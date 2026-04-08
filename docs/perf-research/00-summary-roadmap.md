# Итоговая дорожная карта performance research

Дата: 2026-04-08.

Основано на отчётах `01`-`10`, `docs/performance-investigation.md`, исходниках мода и decompile-срезе `Assembly-CSharp.dll`.

## Executive summary

Рывки камеры и статтеры с высокой вероятностью идут не из одного места. Главный контур: `MapEditorController.MainUpdate -> Hospital.Update / ProcedureManager.Update / LabProcedureManager.UpdateLabProcedures / DayTime.MultiUpdate`, дальше нагрузка расходится в `Behavior*`, `HospitalizationComponent`, `ProcedureComponent`, `WalkComponent`, `MapScriptInterface` и процедуру/резервации.

Главные направления оптимизации:

1. Уменьшить повторные idle/scanning проходы через scheduler/task board v2.
2. Дедуплицировать route/reservation requests, но не кэшировать успешные резервации и полноценные маршруты без строгой валидации.
3. Заменить широкие `MapScriptInterface` full scans на короткие TTL-кэши и затем на индексы по department/floor/tag/role.
4. Сократить overhead самого мода: reflection handles, string keys, temporary arrays, LINQ/reporting, scope профайлера.
5. Оставить camera/frame pacing как стабилизатор cadence, но не считать его заменой устранению main-thread spikes.

## Top 20 bottlenecks

| # | Method / area | Source | Likely cause | Estimated impact | Risk |
|---|---|---|---|---|---|
| 1 | `BehaviorNurse.UpdateStateIdle` | vanilla + mod patches | повторные ветки medicine/food/checkup/needs/free-time и availability probes | very high | medium |
| 2 | `BehaviorPatient.SelectNextProcedure` | vanilla | повторная arbitration по diagnosis/procedure/treatment/hospitalization | very high | high |
| 3 | `BehaviorPatient.TryToScheduleExamination` | vanilla | `SelectExaminationForMedicalCondition` + availability fallback | very high | high |
| 4 | `ProcedureComponent.SelectExaminationForMedicalCondition` | vanilla | rebuild/scan examination availability map | high | medium |
| 5 | `ProcedureComponent.ReserveExamination` / `ReserveProcedure` | vanilla | `ProcedureSceneFactory` live world scan + mutation on success | high | high |
| 6 | `ProcedureSceneFactory.CreateProcedureScene` | vanilla | staff/equipment/room selection, reservation availability | high | high |
| 7 | `WalkComponent.SetDestination` -> `SetupJob` | vanilla + mod throttle | repeated destination requests and `PathfinderJob` creation | high | medium |
| 8 | `Pathfinder.FindRoute` | vanilla | A* over live floor/access/object graph | high | high |
| 9 | `FindClosestCenterObjectWithTagShortestPath` | vanilla | candidate scan plus shortest-path scoring | high | high |
| 10 | `FindClosestFreeObjectWithTag(s)` | vanilla | repeated room/department object scans | high | medium |
| 11 | `FindClosestDoctorWithQualification` / free variants | vanilla | repeated department staff scans | high | medium |
| 12 | `FindLabSpecialist...LowestWorkload` | vanilla | assigned staff scan plus workload selection | medium-high | medium |
| 13 | `FindDirtiestTileIn...` | vanilla | tile-grid scans for janitor decisions | medium-high | medium |
| 14 | `HospitalizationComponent.SelectNextStep` | vanilla + mod backoff | repeated inpatient procedure/transport/discharge arbitration | high | high |
| 15 | `HospitalizationComponent.UpdateStateInBed` | vanilla | bed/room/stabilization/discharge checks with nested scans | medium-high | high |
| 16 | `ProcedureManager.Update` | vanilla | main-thread `ScriptUpdate` over active procedure scripts and cleanup | medium-high | high |
| 17 | `LabProcedureManager.GetIdleLabProcedures` | vanilla | repeated filtered list query from lab idle | medium | medium |
| 18 | `BehaviorJanitor.SelectNextAction` | vanilla + mod standby | dirty-room/tile scans, cart handling, reservations | medium | medium |
| 19 | `PerformanceOptimizationService.BuildKey` / cache call sites | mod | string keys, `object[]` allocations, reflection in validation | medium | low |
| 20 | `PerformanceProfiler` broad Harmony scope | mod | instrumentation overhead can perturb target timings | medium | low |

## Recommended order

### Quick wins

1. Cache fixed reflection handles in hot paths.
2. Replace string-built cache keys for hottest signatures with typed/prehashed keys.
3. Remove or keep disabled broad profiler target sweeps; default to explicit method list.
4. Keep route request throttle and reservation negative broker enabled, but add counters per reason.
5. Keep frame pacing as-is with hard kill switch.

### Scheduler v2

Build an immutable, versioned task board:

- `BoardVersion`, `BuiltAt`, `ExpiresAt`, `StateSignature`.
- Per-role queues: doctor, nurse, lab, janitor.
- Explicit task signatures and task claims.
- One task per staff per scheduling interval.
- `PersonalNeeds` separated into low-priority cooldown queue, not mixed into main demand score.

Kill switches:

- `EnableSchedulingEngine`
- `EnableSchedulingEngineGating`
- `EnableSchedulingDispatcherApply`
- future: `EnableTaskBoardV2`, `EnableTaskBoardV2Claims`

### Route broker

Keep the existing duplicate `SetDestination` throttle as the safe baseline. Add route-request diagnostics before deeper caching:

- per-character request signature;
- solve count vs repeated request count;
- no-path count by destination/floor/access rights.

Do not cache full routes until invalidation covers floor graph, access rights, room restrictions, dynamic objects, stretcher/attached state, lookahead, and movement type.

Kill switches:

- `EnableRouteRequestThrottle`
- future: `EnableRouteRequestBroker`
- future experimental only: `EnableNegativeNoPathCache`

### ReservationBroker v2

Cache only failures. Key should include:

- request kind;
- patient id;
- exam/procedure id;
- department id;
- room id or sentinel;
- access rights;
- urgency / queue context.

Never cache `AVAILABLE` across frames or callers. Success is a transaction that reserves staff/equipment and creates a script.

Kill switches:

- `EnableReservationBroker`
- `ReservationBrokerTtlSeconds`
- future: `EnableReservationBrokerV2`

### Worker-thread DTO scoring

Only move scoring to workers:

- main thread collects DTO snapshot;
- worker ranks tasks and recommendations;
- main thread revalidates version/signature/free state/department/task expiry before applying.

Do not move Unity objects, live behavior components, reservations, `WalkComponent`, `ProcedureScript`, or `HospitalizationComponent` transitions off the main thread.

Kill switches:

- future: `EnableWorkerDtoScoring`
- future: `WorkerDtoScoringMaxAgeSeconds`

## Low-risk fixes

- Cache fixed `MethodInfo` / `FieldInfo` / `PropertyInfo` outside hot loops.
- Use typed cache keys for `FindClosestFreeObjectWithTag(s)`, staff search, and reservation broker.
- Reuse scratch lists in prune/report code.
- Keep overlay/scheduler/profiler reporting on throttled snapshots.
- Add counters instead of per-decision logs.
- Keep `FramePacingService` defaults and restore-on-disable behavior.

## Medium-risk fixes

- Collapse legacy `NurseTaskBoardSnapshot` into central scheduler v2.
- Add department/floor/tag object index with late validity checks.
- Add role/shift/qualification staff index with late validity checks.
- Add dirty-tile room summaries for janitors.
- Add short-TTL `LabProcedureManager.GetIdleLabProcedures` cache with mutation invalidation.
- Add stricter `PersonalNeeds` cooldown and separate queue.

## High-risk experimental fixes

- Full route cache.
- No-path negative cache longer than a very short TTL.
- Positive reservation cache.
- Patching `ProcedureSceneFactory` internals.
- Deep profiling whole `Behavior*`, `HospitalizationComponent`, `ProcedureComponent`, or `ProcedureScript` state machines.
- Camera/render surgery beyond public Unity pacing settings.

## Already covered by current mod

- Default-off `PerformanceProfiler`.
- F8 performance/counter page.
- `FramePacingService` with restore-on-disable.
- Route duplicate `SetDestination` throttle.
- Reservation broker for negative reservation results.
- Short-TTL object, center-object, and staff search caches.
- Nurse idle backoff and task-board gating.
- Outpatient waiting/doctor-search backoff.
- Inpatient `SelectNextStep` miss backoff.
- Scheduling engine snapshot with hit/miss/stale counters.
- Janitor standby-after-cleaning safety gates.
- External transfer queue broker kept separate from ambulance state-machine acceleration.

## Remove or demote as legacy

- `EnableReservationNegativeCache` / `ReservationNegativeCacheTtlSeconds`: documented as legacy no-op, keep only for config compatibility.
- Mixed `SchedulingDepartmentBoard` as long-term architecture.
- `SchedulingTask.ExpiresAt` in v1 if it remains unused; make it real in v2 or remove.
- String role names in scheduler hot paths once v2 exists.
- Legacy `NurseTaskBoardSnapshot` after scheduler v2 has equivalent nurse queues.
- Broad profiler sweep targets as default behavior.

## Tests and contract checks

Add reflection contract tests for:

- scheduler v2 patch targets and config flags;
- route throttle/broker patch signatures;
- `ReservationBroker v2` target signatures and failure enum values;
- `LabProcedureManager.GetIdleLabProcedures` and lab idle target signatures;
- janitor standby patch targets and state enum values;
- camera/controller names only if future camera patching is added.

Add runtime counters for:

- skipped/allowed idle by role and reason;
- task-board stale/version mismatch;
- task claim conflicts;
- route requests, repeated requests, no-path failures;
- reservation broker hit/miss/store by failure reason;
- object/staff/dirty index hits/misses/invalidated hits;
- reflection fallback/missing member warnings.

Add log assertions:

- no repeated Harmony missing-target spam after startup;
- no per-tick debug logs when debug flags are off;
- no repeated stuck-reservation, deleted-patient, or broken-cart warnings introduced by gating.

Manual save-game scenarios:

- busy clinic waiting room with doctors/lab specialists changing shifts;
- hospitalized patient waiting for chained CT/MRI/lab procedures;
- surgery queue with two surgery nurses required;
- janitor cleaning then shift boundary and closed department;
- lab specialist processing sample while patient leaves/deletes;
- stretcher/wheelchair transport across floors;
- broken/blocked room and bed repair;
- high refresh monitor and manual 60 FPS fallback.

## Do not do

- Do not parallelize Unity/entity state machines directly.
- Do not run `Behavior*.Update`, `HospitalizationComponent.Update`, `ProcedureComponent.Update`, `ProcedureScript.ScriptUpdate`, or `WalkComponent` result application on workers.
- Do not cache positive reservation results without immediate validation and one-shot transaction semantics.
- Do not teleport or bypass vanilla route application.
- Do not cache routes across floor/access/elevator/stretcher/dynamic-obstacle changes.
- Do not deep-profile broad state-machine classes by default.
- Do not replace vanilla executors for nurse/lab/janitor tasks; use boards to decide whether to run vanilla selection.

## Final recommendation

Implement in this order: mod overhead cleanup, scheduler/task board v2, reservation broker v2, map/staff/dirty indexes, then route broker diagnostics. Only after those are measured should worker-thread DTO scoring be introduced. Camera/frame pacing should remain a user-facing stabilizer, not the main fix for simulation hitches.
