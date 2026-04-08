# Performance Research 10: Mod Overhead, Reflection, Logging, Allocations

Scope: read-only pass over `src/*.cs`, `README.md`, `docs/performance-investigation.md`, and `C:\Program Files (x86)\Steam\steamapps\common\Project Hospital\BepInEx\LogOutput.log`.

## Log Signal

The sampled `LogOutput.log` tail shows one startup-only issue, not a runtime spam pattern:

- two repeated Harmony warnings for missing `Lopital.ProcedureScriptPersistentData.ScriptUpdate(float)` targets.

I did not see sustained warning/error spam in the sampled log tail. That makes the overhead story mostly about mod-side reflection, repeated allocations, and the profiler/reporting layer itself.

## Overhead Hotspots

| Hotspot | What is expensive | What to do |
|---|---|---|
| `src/PerformanceProfiler.cs` `TargetMethods()` + `AddDetailedMethods()` | Broad Harmony patch sweep plus reflection over many type buckets; the profiler can become part of the overhead it tries to measure. | Keep the profiler default-off, shrink the target set to the small explicit list, and exclude broad type sweeps unless you are actively sampling one subsystem. |
| `src/PerformanceProfiler.cs` `Tick()` / `GetTopSamples()` / `FormatSample()` | Low-frequency, but still allocates via LINQ, `ToArray()`, cloning, sorting, and long string joins. | Keep reporting summary-only, and move repeated presentation into counters or a prebuilt buffer instead of per-log LINQ. |
| `src/PerformanceOptimizations.cs` `BuildKey()` / `BuildArgKey()` | String key construction uses `+=`, recursive enumerable walking, vector field reads, and pointer-hash signatures. | Replace string-built cache keys with typed keys or prehashed structs; at minimum stop rebuilding the same canonical text on every lookup. |
| `src/PerformanceOptimizations.cs` cache call sites | Many cache lookups/stores allocate `new object[]` and `new[] { ... }` argument packs for every call. | Add typed overloads for the fixed signatures that are hit most often, so hot patches do not allocate argument arrays. |
| `src/PerformanceOptimizations.cs` `Prune()` / `PruneRouteRequests()` / `PruneNurseBoards()` | Each prune pass allocates a fresh `List<T>` for expired keys. | Reuse scratch lists or prune in-place with a manual pass when the cache is large enough to matter. |
| `src/ProductivityTweaks.cs` `ApplyEmergencyRunningExtraSteps()` | Repeated `AccessTools.Field` / `AccessTools.Method` lookups and `MethodInfo.Invoke` inside a movement hot path. | Cache `FieldInfo` / `MethodInfo` per walk type, or use direct accessors/delegates if the member is public and stable. |
| `src/ExternalTransferAmbulanceTweaks.cs` `ApplyParamedicMovementExtraSteps()` | Same pattern as above: reflection lookup plus repeated invocation in a per-movement path. | Cache the method/field handles once, and keep the loop free of reflection work. |
| `src/HospitalUpgrades.cs` `ApplyEmergencyRunningExtraSteps()` | Same repeated reflection pattern in another movement path. | Cache the route/floor/update handles; do not resolve them every tick. |
| `src/ProductivityTweaks.cs` `TryHandleAfterExaminationCheck()` / `TryDischargeAfterNurseCheck()` / `CanDischargeAfterNurseCheck()` | Repeated `AccessTools.Method` / `GetMethod` calls for fixed signatures, plus reflective `Invoke`. | Cache those `MethodInfo`s statically. If the signature is known and public, call directly instead. |
| `src/EquipmentReferral.cs` / `src/MedicationPlanningTweaks.cs` | Repeated `GetMethod` / `GetProperty` / `Invoke` on fixed members during referral/planning decisions. | Cache the reflection handles once per type, and prefer direct access for public members such as `DatabaseID`, `Instance`, or known public methods. |
| `src/BottleneckOverlay.cs` / `src/SchedulingEngine.cs` | Large read-only scans, temporary lists, `string.Join`, and repeated reflection across characters/departments. | Keep these as throttled snapshots only; translate recurring status into counters instead of repeated per-frame logs. |
| `src/ProductivityTweaks.cs` `PruneCleanupRooms()` | `HighPriorityCleanupRooms.ToList()` allocates on every prune pass. | Iterate the dictionary with a manual two-pass removal or a scratch buffer. |

## What To Cache

The highest-value cache targets are the reflection handles that are currently re-resolved inside hot paths:

- `WalkComponent.UpdateMovement`, `SetDestination`, `SwitchState`, `SelectNextStep`, `SendBackToRoom`
- `BehaviorPatient.Leave`, `IsPatientTreated`, `HasBeenTreated`, `GetWorstKnownHazard`
- `BehaviorJanitor.SelectNextAction`, `GoHome`, `GetHomeRoomType`
- `MapScriptInterface.FindClosestObjectWithTag`, `FindDirtiestTileInRoomWithMatchingAssignmentAnyFloor`, `CleanTile`
- `BookmarkedCharacterManager.RemoveCharacter`
- `ProcedureComponent.IsAlreadyDone`, `ProcedureQueue.IsAlreadyInQueue`, `ProcedureQueue.AddPlannedTreatment`

Also cache `PropertyInfo` for the fixed public properties that are looked up repeatedly:

- `DatabaseID`
- `Instance`
- `Cost`
- `CurrentPatient` if reflection must stay

`ReflectionHelpers` already caches fields, but the cache key is still a concatenated string. That cache should be treated as a stopgap, not the final shape, if the goal is to reduce allocator pressure in hot loops.

## What To Replace With Direct Accessors

Direct accessor wins are available where the project already references the type and the member is public:

- `DatabaseID`
- `Instance`
- `Cost`
- simple public query methods such as `IsValid`, `IsFree`, `GetReserved`, `IsPerformingAProcedure`, and similar fixed boolean checks

If the member is not public or not safely callable directly, use a cached `MethodInfo` or cached delegate instead of resolving it per call.

## What To Remove From Max Profiler Scope

I did not find a separate `HarmonyMaxProfiler` switch in the codebase. The practical overhead problem is the profiler's own broad patch sweep in `PerformanceProfilerPatch`.

Trim or exclude these from the profiler's default target sweep:

- `Lopital.BehaviorNurse`
- `Lopital.BehaviorDoctor`
- `Lopital.BehaviorJanitor`
- `Lopital.BehaviorPatient`
- `Lopital.HospitalizationComponent`
- `Lopital.ProcedureComponent`
- `Lopital.ProcedureManager`
- `Lopital.ProcedureQueue`
- `Lopital.ProcedureSceneFactory`
- `Lopital.Department`
- `Lopital.MapScriptInterface`
- `Lopital.LabProcedureManager`
- `GLib.PathfinderJob`
- `GLib.ElevatorPathFindingJob`
- `GLib.GridPathFinder`
- `GLib.Pathfinder`

Keep the profiler focused on the manually named methods first. Broad "profile everything that looks like a selector or state machine" sweeps are useful only after the small target set proves insufficient.

## What To Translate Into Counters

The mod already has good examples of counter-based reporting in `RuntimeCounters`. The same shape should be used for repeated diagnostics that do not need a line in the log every time they happen:

- emergency speed boost hits
- nurse cleanup starts and stops
- stuck reservation clears
- transport reservation retries
- referral decisions and referral income
- external transfer fallback creation attempts
- scheduler rebuild counts and snapshot ages

Keep the log for exceptions, startup failures, and occasional summaries. Use counters for repeated decisions and recurring state transitions.

## Bottom Line

The biggest performance risk is not one giant algorithm. It is the combination of:

1. repeated reflection in tick or movement paths,
2. string-built cache keys and temporary arrays,
3. LINQ and `ToList()` in reporting/prune code,
4. and a profiler that is broad enough to perturb the thing it is measuring.

The safest next step is to cache the fixed reflection handles first, then cut profiler scope, then convert repetitive diagnostics into counters.
