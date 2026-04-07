# Performance investigation

This document tracks the first read-only pass over Project Hospital runtime ticks and the mod hot paths. The goal is to identify what can be measured and optimized safely before attempting any parallel work.

## Current findings

### Movement

Primary type: `Lopital.WalkComponent`.

Important methods:

- `MultiUpdate(int updateCount, float deltaTime)`
- `UpdateMovement(Floor floor, float deltaTime)`
- `UpdateMovementSingle(Floor floor)`
- `UpdateDestinationSet()`
- `UpdateLookingForPath()`
- `SetDestination(...)`

Observed behavior:

- `MultiUpdate` updates timers, calls `Update(deltaTime * updateCount)`, and loops `updateCount` vanilla movement steps.
- `UpdateMovement` mutates route state, current/next/world position, route index, floor dirt/blood, sitting/standing/changing-floor state, and animation state.
- `UpdateMovementSingle` opens doors, updates direction, and touches floor state.
- Pathfinding is already job-like: `SetupJob(...)` creates a `PathfinderJob`; `UpdateDestinationSet()` calls `TryToStart(0)`; `UpdateLookingForPath()` polls `IsRunning` / `IsDone` and then applies the result on the main thread.

Threading classification:

- Do not move `UpdateMovement`, `UpdateMovementSingle`, `SetDestination`, `UpdateLookingForPath`, or any `PathfinderJob` result application to a worker thread.
- Potential optimization is profiling, reducing extra mod-driven movement steps when unnecessary, and avoiding any mod scans that repeatedly call path/room lookup from unrelated code.
- Path scoring and route requests may already be offloaded by vanilla `PathfinderJob`; we should not replace it until a profiler proves it is the bottleneck.

### Procedure scripts

Primary type: `Lopital.ProcedureManager`.

Important method:

- `Update(int updateCount)`

Observed behavior:

- Iterates `m_scriptEntities`.
- Checks missing patients and abandoned idle procedure scripts.
- Calls `scriptEntity.ScriptUpdate(Time.deltaTime * updateCount)`.
- Catches and reports exceptions.
- Removes abandoned scripts and resets equipment/doctor/nurse/lab reservations.

Threading classification:

- Do not run `ScriptUpdate` on worker threads. Procedure scripts mutate patient, staff, room, equipment, reservations, and current procedure scene.
- Good profiler target: `ProcedureManager.Update` and selected concrete script `ScriptUpdate` methods:
  - `ProcedureScriptTreatmentSurgery`
  - `ProcedureScriptExaminationRadiology`
  - `ProcedureScriptExaminationStatLab`
  - `ProcedureScriptTreatmentProcedure`
  - `ProcedureScriptTreatmentReceipt`
  - `ProcedureScriptTreatmentPills`
- Safe optimization path: measure first, then reduce mod overhead and excessive additional updates from upgrades.

### Hospitalization

Primary type: `Lopital.HospitalizationComponent`.

Important methods:

- `Update(float deltaTime)`
- `SwitchState(HospitalizationState state)`
- `UpdateStateInBed(float deltaTime)`
- `UpdateStateAfterExaminationCheck(float deltaTime)`
- `SelectNextStep(float deltaTime)`
- `UpdateState...` state handlers

Observed behavior:

- `Update` increments hospitalization timers, validates patient/hospitalization state consistency, counts hospitalization time, handles infectious/outbreak checks, and dispatches a large state machine.
- The state machine mutates patient state, beds, rooms, monitoring, transport, planned procedures, collapse/death handling, and notifications.
- The vanilla code already has self-repair branches for inconsistent states, which means concurrent modifications would be especially dangerous.

Threading classification:

- Main-thread only.
- Profiler target: `HospitalizationComponent.Update`, plus the chained diagnostics and nurse-check discharge mod patches.
- Optimization path: avoid full-hospital scans from overlay/analytics every frame; use throttled snapshots.

### Staff AI

Primary types:

- `Lopital.BehaviorDoctor`
- `Lopital.BehaviorNurse`
- `Lopital.BehaviorJanitor`
- `Lopital.BehaviorLabSpecialist`

Observed behavior:

- `BehaviorDoctor.Update` and `BehaviorNurse.Update` dispatch state machines and call `UpdateStateIdle` while idle.
- Idle logic performs many `MapScriptInterface` queries and then immediately mutates state: reserves patients/rooms/objects, starts procedures, changes staff state, calls `SetDestination`.
- `BehaviorJanitor.SelectNextAction` finds dirty tiles/rooms, reserves rooms/tiles, cleans tiles, manages carts, and changes destinations.

Threading classification:

- Main-thread only for actual AI decisions and state transitions.
- Potentially parallelizable only as read-only scoring over a DTO snapshot, followed by main-thread validation and application.
- High value profiler targets:
  - `BehaviorDoctor.Update`
  - `BehaviorDoctor.UpdateStateIdle`
  - `BehaviorNurse.Update`
  - `BehaviorNurse.UpdateStateIdle`
  - `BehaviorJanitor.Update`
  - `BehaviorJanitor.SelectNextAction`

### Map searches

Primary type: `Lopital.MapScriptInterface`.

High-risk/high-value methods seen in AI and cleanup paths:

- `FindClosestDoctorWithQualification(...)`
- `FindClosestNurseWithQualification(...)`
- `FindClosestFreeMedicalEmployee(...)`
- `FindClosestWorkspace(...)`
- `FindClosestFreeObjectWithTag(s)(...)`
- `FindRoomWithTagInDepartmentForDoctorsRounds(...)`
- `FindRoomWithTagInDepartmentForPatientCheckUp(...)`
- `FindDirtiestTileInRoomWithMatchingAssignmentAnyFloor(...)`
- `FindDirtiestTileInAnyUnreservedRoomAnyFloor(...)`
- `FindDirtiestTileInARoom(...)`
- `FindClosestDirtyTileInARoom(...)`
- `FindClosestDirtyIndoorsTile(...)`
- `CleanTile(...)`
- `ReserveTile(...)`
- `MoveObject(...)`

Observed behavior:

- Many methods scan department objects, rooms, floor tile grids, or all hospital characters.
- Some are read-like searches, but several are paired with immediate reservations or use mutable object/user/owner state.

Threading classification:

- Direct calls should stay on main thread.
- Candidate for caching/snapshotting:
  - room/department object lists by tag;
  - dirty room/tile summaries;
  - free staff summaries by role/department/floor;
  - planned procedure counts for overlay.
- Any cached result must be revalidated before use because `User`, `Owner`, reservations, room validity, and patient state can change between frames.

## Mod hot paths to measure

Existing mod code likely to affect frame time:

- `BottleneckOverlayService.CreateSnapshot()`
- `SurgeryAnalytics` logging / snapshot code
- `ProductivityTweaksService.Tick(...)`
- `ProductivityTweaksService.TryHandleNurseAssistedCleanup(...)`
- `HospitalUpgradesService.ApplyMovementBoost(...)`
- `HospitalUpgradesService` procedure/animation speed patches
- `EquipmentReferral` diagnosis/scheduling postfixes
- `MedicationPlanningTweaks` postfix on treatment planning

The overlay and analytics are the safest first optimization target because they should be read-only and already tolerate coarse refresh intervals.

## Safe optimization plan

1. Add an opt-in profiler with low overhead and throttled reporting.
2. Profile only a short target list at first to avoid Harmony overhead becoming the bottleneck.
3. Add F8 `Performance` page with rolling top-N method timings.
4. Use the first profile results to choose between:
   - throttling overlay/analytics;
   - caching reflection metadata and database entries;
   - reducing repeated `MapScriptInterface` scans;
   - time-slicing read-only analytics over multiple frames.
5. Only after that, consider worker threads for DTO-only analytics:
   - collect primitive snapshot on main thread;
   - compute scores on worker thread;
   - apply/report only after main-thread validation.

## Do not parallelize directly

These operations are unsafe off main thread:

- `WalkComponent.SetDestination`, `UpdateMovement`, route application, floor changes.
- Any `Behavior*.Update` state transition.
- `ProcedureScript.ScriptUpdate`.
- `HospitalizationComponent.Update` and `SwitchState`.
- Room, object, tile, bed, stretcher, owner/user, and reservation mutations.
- Unity `GameObject`, `Transform`, UI, animation state, and notification calls.

## Next implementation step

Add `PerformanceProfiler.cs` with config:

```ini
[Performance]
EnableProfiler = false
ProfilerSampleIntervalSeconds = 10
ProfilerTopN = 20
ProfilerSlowCallMs = 5
ProfilerOverlay = true
```

Initial instrumented targets:

- `ProcedureManager.Update(int)`
- `WalkComponent.MultiUpdate(int, float)`
- `HospitalizationComponent.Update(float)`
- `BehaviorDoctor.Update(float)`
- `BehaviorNurse.Update(float)`
- `BehaviorJanitor.Update(float)`
- `BehaviorLabSpecialist.Update(float)`
- `BottleneckOverlayService.CreateSnapshot()`
- `ProductivityTweaksService.Tick(float)`

Keep profiler default-off and use `Stopwatch.GetTimestamp()` to minimize overhead.
