# Performance Research 05: Procedures, Examinations, Reservations

Scope:

- `ProcedureManager.Update`
- `ProcedureComponent.ReserveExamination`
- `ProcedureComponent.ReserveProcedure`
- `ProcedureComponent.SelectExaminationForMedicalCondition`
- `ProcedureSceneFactory`
- `ProcedureQueue`
- current `ReservationBrokerService`

## What is expensive and why

### `ProcedureManager.Update`

Vanilla `ProcedureManager.Update(int)` iterates the full `m_scriptEntities` list every tick, calls `ScriptUpdate(Time.deltaTime * updateCount)` for every live procedure script, and then performs cleanup for abandoned or missing-patient scripts.

The expensive parts are not just the per-script update call:

- the loop touches every active script on the main thread;
- abandoned-script cleanup releases doctors, nurses, and equipment ownership one entity at a time;
- removals happen after the scan, so a bad frame can turn into a burst of reservation cleanup.

This is not a place to parallelize. It is a mutation-heavy state machine over live entities, reservations, and script ownership.

### `ProcedureQueue`

`ProcedureQueue` is mostly linear scans over lists:

- `AddPlannedExamination` / `AddPlannedTreatment` dedupe by full scan;
- `HasPlannedExamination` / `HasPlannedTreatment` are full scans;
- `RemovePlannedExamination` / `RemovePlannedTreatment` are remove-in-loop scans;
- `GetFirstLabSampleToDeliver` and `GetFirstLabProcedureWithResultReady` walk the queue again.

That matters because `SelectExaminationForMedicalCondition` and the scheduling flow repeatedly query the queue while deciding whether to schedule or reserve. The cost is therefore not one scan, but many scans layered on top of each other.

### `ProcedureSceneFactory`

`ProcedureSceneFactory.CreateProcedureScene(...)` is the actual cost center behind both examination and procedure reservation checks.

It does not just validate a single boolean. It rebuilds a scene against live world state:

- patient;
- department;
- room / patient room;
- access rights;
- staff selection rules;
- equipment list rules;
- hospitalization state;
- current shift;
- current doctor/nurse/lab assignment;
- room validity;
- free/busy status of staff and equipment.

For `QUERY` scenes it reuses a mutable static scratch scene, which keeps allocations down, but the search work is still live and stateful. For `INSTANTIATION` it creates a new scene and then mutates real reservations if the result is available.

That is why the scene factory is not cache-friendly at the success boundary.

### `ProcedureComponent.ReserveProcedure` / `ReserveExamination`

`ReserveProcedure` builds a scene, and if the scene is `AVAILABLE` it immediately mutates the world:

- instantiates a `ProcedureScript`;
- stores it in `m_reservedProcedureScript`;
- reserves each selected doctor, nurse, lab specialist, and indistinct employee;
- transfers equipment ownership to the reserved script when needed.

`ReserveExamination` is only a thin wrapper over `ReserveProcedure`, plus one extra assignment for `m_stateData.m_examination`.

That means a positive result is not a pure answer. It is the start of a transaction.

### `ProcedureComponent.SelectExaminationForMedicalCondition`

This is the main source of avg/spike behavior in the examination path.

The method first calls `UpdateAllExaminationsForMedicalCondition(...)`, which:

- rebuilds availability for all candidate examinations;
- does work in chunks of 5 when the list size is stable;
- still touches every examination candidate over time;
- repeatedly reads hospitalization level, doctor skill, department state, queue state, planned/finished exams, and fallback department state.

Then `SelectExaminationForMedicalCondition` iterates the cached availability map again and applies:

- alternative-examination rules;
- queue de-duplication rules;
- availability category filtering.

So the cost is:

1. rebuild availability for the full candidate set;
2. scan the result again to choose one exam;
3. immediately hand that choice to `TryToScheduleExamination`, which may invoke `ProcedureSceneFactory` again.

That is a textbook source of average-frame cost plus spikes when the dirty set changes or when the patient/doctor/department context flips.

### `BehaviorPatient.TryToScheduleExamination`

`TryToScheduleExamination` is the user-visible trigger for the above work.

It calls `SelectExaminationForMedicalCondition` every time it decides the patient should look for an examination. If that returns a candidate, it then:

- checks observation rules;
- tries room-based availability;
- retries through a fallback department if needed;
- may switch state or department if the exam cannot be placed.

This is why it shows up as a hot path even though the real cost is split across selection, scene creation, and state change.

### `BehaviorPatient.TryToStartScheduledExamination`

This path repeats the same live availability work for the first queued exam, and in the lab-specialist case it may call `FindLabSpecialist`, `GetProcedureAvailabilty`, and then remove the queue head.

When the queue head is invalid, the method retries against the current live state. That is correct, but it means repeated failures can cluster into a spike until the world state changes.

## Current `ReservationBrokerService`

The current broker in `src\PerformanceOptimizations.cs` is a short-lived negative cache only.

Behavior:

- it is enabled only when performance optimizations and `EnableReservationBroker` are on;
- it stores only failed reservation results;
- `AVAILABLE` is treated as a cache eviction, not a stored result;
- entries expire by TTL, with a minimum of 0.05 seconds;
- cleanup runs from `PerformanceOptimizationService.Tick(...)`.

Current key shape:

- `MethodBase.Name` only;
- all arguments appended in order;
- each argument is reduced by `LocID`, then `ID`, then `m_entityID`, then a reference-hash fallback.

That is enough to dedupe some repeated failures, but it is not a strong semantic key.

## What reservation failures can be cached

Cacheable for a short TTL, if the key is specific enough:

- `STAFF_BUSY`
- `STAFF_UNAVAILABLE`
- `STAFF_UNAVAILABLE_LAB`
- `STAFF_UNAVAILABLE_LAB_ROLES`
- `EQUIPMENT_BUSY`
- `EQUIPMENT_UNAVAILABLE`
- `NO_ROOM`
- `ONLY_CLINIC`
- `PATIENT_CAN_NOT_TALK`
- `DOCTOR_CAN_NOT_PRESCRIBE`
- `NOT_PRESCRIBABLE_DEPARTMENT`

These are all failures that come from a current-world snapshot and are commonly repeated by the same caller within a few frames.

Not cacheable as a success path:

- `AVAILABLE`

Not safe to cache as a long-lived failure without explicit invalidation:

- any failure that depends on the exact doctor/lab assignment;
- any failure that depends on room closure or room validity;
- any failure that depends on queue head changes;
- any failure that depends on equipment ownership or reservation release;
- any failure that depends on a department move or hospitalization transition.

## Why positive reservation results cannot be cached

`AVAILABLE` is not just a query result. In this code path it means "the reservation can be materialized right now."

Caching that as a reusable positive answer would be wrong because:

- `ReserveProcedure` has side effects that must run exactly once;
- the scene reserves staff and equipment immediately after success;
- `ProcedureManager.Update` later expects those reservations to exist and to be released only when the script finishes or is cleaned up;
- the result can flip to unavailable a moment later if another script or state update consumes the same staff/equipment.

So success is not cacheable in the same way a pure function result is. At most, the broker can memoize a transient `AVAILABLE` verdict inside one call chain, but it cannot reuse that verdict across frames or callers.

## ReservationBroker v2 plan

The current broker should be replaced with an explicit request key and explicit invalidation rules.

Recommended key fields:

- request kind: `ReserveExamination` vs `ReserveProcedure`;
- patient entity ID;
- exam/procedure database ID;
- department entity ID;
- room entity ID, or a sentinel for "no room";
- access rights;
- urgency / priority / queue context;
- optional staff-selection context if the caller path changes the reservation shape.

Recommended behavior:

1. Cache only negative reservation results.
2. Store the failure reason plus the typed request key.
3. Keep TTL short by default, but allow slightly longer TTLs for structural failures like missing room or department mismatch.
4. Invalidate on:
   - patient results ready;
   - procedure start/finish;
   - staff reserve/free;
   - equipment owner change;
   - room validity or room-close change;
   - department change;
   - hospitalization transition;
   - queue-head change.
5. Do not use reflection-hash fallback as the primary identity model.
6. Include counters by failure class so the broker can prove it is removing repeated misses, not just hiding work.

The practical v2 goal is simple: dedupe repeated failed attempts to reserve the same exam/procedure in the same live context, while never turning a one-time success transaction into a stale cached answer.

## Source notes

- Current broker implementation: [`src\PerformanceOptimizations.cs`](C:\Users\gerbe\OneDrive\Документы\Playground\ProjectHospital.AutoLabBalancer\src\PerformanceOptimizations.cs)
- Broker config: [`src\AutoLabBalancerPlugin.cs`](C:\Users\gerbe\OneDrive\Документы\Playground\ProjectHospital.AutoLabBalancer\src\AutoLabBalancerPlugin.cs)
- Scheduling/exam call sites: [`src\EquipmentReferral.cs`](C:\Users\gerbe\OneDrive\Документы\Playground\ProjectHospital.AutoLabBalancer\src\EquipmentReferral.cs)

