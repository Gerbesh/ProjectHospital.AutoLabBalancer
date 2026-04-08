# ТЗ: полное исследование производительности Project Hospital и мода

## Цель

Провести глубокое исследование runtime-процессов Project Hospital и мода `ProjectHospital.AutoLabBalancer` на предмет:

- статтеров, рывков камеры и просадок симуляции;
- неэффективных vanilla-алгоритмов и hot path методов;
- лишних сканов, резерваций, поиска пути и повторного выбора задач;
- лишнего Harmony/reflection overhead, лог-спама и аллокаций;
- возможностей безопасной оптимизации через BepInEx/Harmony без повреждения сейвов.

Результат должен быть не “общие мысли”, а набор Markdown-отчётов по направлениям и итоговая дорожная карта конкретных патчей.

## Входные данные

Рабочая директория:

```text
C:\Users\gerbe\OneDrive\Документы\Playground\ProjectHospital.AutoLabBalancer
```

Игра:

```text
C:\Program Files (x86)\Steam\steamapps\common\Project Hospital
```

Ключевые файлы:

```text
C:\Program Files (x86)\Steam\steamapps\common\Project Hospital\ProjectHospital_Data\Managed\Assembly-CSharp.dll
C:\Program Files (x86)\Steam\steamapps\common\Project Hospital\BepInEx\LogOutput.log
C:\Program Files (x86)\Steam\steamapps\common\Project Hospital\BepInEx\config\local.projecthospital.autolabbalancer.cfg
```

Исходники мода:

```text
src\*.cs
tests\ReflectionContract.Tests.ps1
docs\performance-investigation.md
```

Инструменты:

```powershell
rg
ilspycmd
dotnet build .\src\ProjectHospital.AutoLabBalancer.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\ReflectionContract.Tests.ps1
```

## Ограничения

- Не делать destructive git-команды.
- Не менять поведение игры во время исследования, если задача явно не перешла в фазу реализации.
- Не распараллеливать Unity/entity state machine напрямую.
- Worker threads допустимы только для DTO/read-only scoring.
- Любые предложения по прямому patch/apply должны включать safety gates и kill switch.
- Отдельно отмечать, если оптимизация рискованная для сейва, резерваций, pathfinding, attach/transport или hospitalization state.

## Главная гипотеза

Рывки камеры и статтеры вызваны не только pathfinding. По текущим замерам сильные источники:

- `BehaviorNurse.UpdateStateIdle`;
- patient/examination selection:
  - `BehaviorPatient.SelectNextProcedure`;
  - `BehaviorPatient.TryToScheduleExamination`;
  - `ProcedureComponent.SelectExaminationForMedicalCondition`;
- pathfinding:
  - `Pathfinder.FindRoute`;
  - repeated `WalkComponent.SetDestination`;
  - `FindClosestCenterObjectWithTagShortestPath`;
- частые `MapScriptInterface` поиски;
- reflection/Harmony/logging overhead при максимальном профилировщике;
- возможные повторные full scans вместо индексов/очередей.

Нужно подтвердить или опровергнуть это по коду, логам и decompiled vanilla flow.

## Требуемый формат работы Codex

Codex должен запустить параллельно 10 sub-agents. Каждый sub-agent получает отдельное направление и пишет отдельный Markdown-отчёт в:

```text
docs\perf-research\
```

Имена файлов:

```text
01-update-loop-map.md
02-pathfinding-and-movement.md
03-nurse-ai-and-task-selection.md
04-patient-flow-and-hospitalization.md
05-procedures-exams-reservations.md
06-map-object-staff-searches.md
07-scheduler-and-dispatcher.md
08-janitor-lab-and-support-ai.md
09-render-camera-frame-pacing.md
10-mod-overhead-logging-reflection.md
00-summary-roadmap.md
```

После завершения sub-agents основной Codex должен собрать `00-summary-roadmap.md`:

- top bottlenecks по вероятному impact;
- low-risk fixes;
- medium-risk fixes;
- high-risk experimental fixes;
- что уже покрыто текущим модом;
- что нужно удалить как устаревшее;
- какие tests/contract checks добавить;
- рекомендуемый порядок реализации.

## Sub-agent 1: Update Loop Map

Файл отчёта:

```text
docs\perf-research\01-update-loop-map.md
```

Задача:

- Построить карту главного update-loop игры.
- Найти, кто и как часто вызывает:
  - `Behavior*.Update`;
  - `HospitalizationComponent.Update`;
  - `ProcedureManager.Update`;
  - `ProcedureComponent.Update`;
  - `WalkComponent.MultiUpdate`;
  - `DayTime`;
  - `MapScriptInterface`;
  - UI/HospitalManagement controllers.
- Отделить per-frame, multi-update, scheduled/timed updates.

Выход:

- Mermaid diagram update pipeline.
- Таблица методов: method, frequency, state mutation, safe-to-cache, safe-to-profile.
- Список методов, которые нельзя deep-profile из-за риска state-machine bugs.

## Sub-agent 2: Pathfinding And Movement

Файл отчёта:

```text
docs\perf-research\02-pathfinding-and-movement.md
```

Задача:

- Изучить vanilla route flow:
  - `WalkComponent.SetDestination`;
  - `UpdateDestinationSet`;
  - `SetupJob`;
  - `UpdateLookingForPath`;
  - `PathfinderJob`;
  - `Pathfinder.FindRoute`;
  - elevator/stairs/floor transitions.
- Проверить, где возможны repeated route requests к той же цели.
- Проверить риски route cache:
  - этажи;
  - access rights;
  - restricted rooms;
  - elevators/stairs;
  - stretchers/attached patients;
  - dynamic obstacles.

Выход:

- Карта route lifecycle.
- Что можно оптимизировать безопасно:
  - duplicate SetDestination throttle;
  - no-path negative cache;
  - route request queue/batching;
  - candidate prefilter до shortest path.
- Что опасно:
  - замена A*;
  - прямой teleport;
  - route cache без invalidation.
- Конкретные patch target signatures.

## Sub-agent 3: Nurse AI And Task Selection

Файл отчёта:

```text
docs\perf-research\03-nurse-ai-and-task-selection.md
```

Задача:

- Изучить `BehaviorNurse.UpdateStateIdle` и все методы, которые она вызывает.
- Найти, какие задачи сканируются:
  - лекарства;
  - транспорт;
  - еда;
  - checkup;
  - critical/collapse;
  - surgery transport;
  - free time/needs.
- Проверить, как наш `SchedulingEngine`, `PersonalNeeds`, `NurseTaskBoard`, dispatcher apply влияют на vanilla idle.

Выход:

- Таблица vanilla task selection branches.
- Какие branches можно заменить task board.
- Какие branches должны остаться vanilla executor.
- Почему `PersonalNeeds` не должен будить idle executor каждый кадр.
- Рекомендации по nurse task board v2.

## Sub-agent 4: Patient Flow And Hospitalization

Файл отчёта:

```text
docs\perf-research\04-patient-flow-and-hospitalization.md
```

Задача:

- Изучить амбулаторный patient flow:
  - `UpdateStateWaitingSitting`;
  - `FindDoctorOrLabSpecialist`;
  - `CheckScheduledExamination`;
  - `UpdateStateGoingToDoctor`;
  - `SelectNextProcedure`.
- Изучить стационар:
  - `HospitalizationComponent.UpdateStateInBed`;
  - `SelectNextStep`;
  - `TryToStartScheduledExamination`;
  - discharge/release flow.
- Найти повторные сканы, которые можно заменить patient task board / reservation broker.

Выход:

- Отдельно outpatient и inpatient diagrams.
- Список safe throttles/backoff.
- Список событий для invalidation:
  - patient results ready;
  - procedure finished;
  - room/object freed;
  - doctor/lab became free;
  - patient collapsed;
  - department changed.

## Sub-agent 5: Procedures, Examinations, Reservations

Файл отчёта:

```text
docs\perf-research\05-procedures-exams-reservations.md
```

Задача:

- Изучить:
  - `ProcedureManager.Update`;
  - `ProcedureComponent.ReserveExamination`;
  - `ProcedureComponent.ReserveProcedure`;
  - `ProcedureComponent.SelectExaminationForMedicalCondition`;
  - `ProcedureSceneFactory`;
  - `ProcedureQueue`.
- Проверить, почему `TryToScheduleExamination` и `SelectExaminationForMedicalCondition` дают большие avg/spikes.
- Проверить текущий `ReservationBrokerService`.

Выход:

- Какие reservation failures можно кэшировать.
- Какой key должен быть у broker:
  - patient;
  - procedure/exam;
  - department;
  - room;
  - access rights;
  - urgency.
- Какие positive reservation results нельзя кэшировать.
- План `ReservationBroker v2`.

## Sub-agent 6: Map/Object/Staff Searches

Файл отчёта:

```text
docs\perf-research\06-map-object-staff-searches.md
```

Задача:

- Изучить `MapScriptInterface` hot methods:
  - `FindClosestFreeObjectWithTag(s)`;
  - `FindClosestCenterObjectWithTagShortestPath`;
  - `FindClosestDoctorWithQualification`;
  - `FindClosestFreeDoctorWithQualification`;
  - nurse/lab/janitor assigned searches;
  - dirty tile searches.
- Проверить текущие caches:
  - object search cache;
  - center object cache;
  - staff search cache.

Выход:

- Какие methods pure-ish/read-only.
- Какие results require validity checks.
- Какие invalidation events нужны.
- Где TTL cache достаточно.
- Где нужен индекс по tag/floor/department.

## Sub-agent 7: Scheduler And Dispatcher

Файл отчёта:

```text
docs\perf-research\07-scheduler-and-dispatcher.md
```

Задача:

- Изучить текущие:
  - `SchedulingEngineService`;
  - `SchedulingTask`;
  - `SchedulingDepartmentBoard`;
  - dispatch recommendations;
  - gating/apply counters.
- Проверить архитектурные баги:
  - task priority;
  - PersonalNeeds priority/cooldown;
  - role scoring;
  - janitor/lab/doctor/nurse separation;
  - stale snapshot behavior.

Выход:

- Что уже хорошо.
- Что избыточно/legacy и нужно удалить.
- Как сделать task board v2:
  - task signatures;
  - expiry/version;
  - state signature validation;
  - per-role queues;
  - one task per staff per interval.
- План worker-thread DTO scoring.

## Sub-agent 8: Janitor, Lab And Support AI

Файл отчёта:

```text
docs\perf-research\08-janitor-lab-and-support-ai.md
```

Задача:

- Изучить:
  - `BehaviorJanitor`;
  - `BehaviorLabSpecialist`;
  - `LabProcedureManager`;
  - external ambulance/paramedic flow if relevant.
- Проверить текущие фиксы:
  - janitor cleaning tasks;
  - janitor standby after cleaning;
  - lab specialist idle gating disabled/safe;
  - external transfer broker/paramedic speed.

Выход:

- Почему janitors уходят домой и какие условия корректны.
- Что нельзя deep-profile в lab states.
- Что можно кэшировать в lab procedure manager.
- Список safety tests для janitor/lab.

## Sub-agent 9: Render, Camera, Frame Pacing

Файл отчёта:

```text
docs\perf-research\09-render-camera-frame-pacing.md
```

Задача:

- Изучить, что можно сделать с камерой/render pacing без Unity source.
- Проверить текущий `FramePacingService`:
  - `Application.targetFrameRate`;
  - `QualitySettings.vSyncCount`;
  - `Time.maximumDeltaTime`;
  - monitor refresh rate.
- Найти camera controller classes in Assembly-CSharp, если они есть.

Выход:

- Что реально возможно:
  - FPS cap;
  - vSync toggle;
  - maxDeltaTime clamp;
  - camera smoothing if controller patchable;
  - reducing main-thread spikes.
- Что невозможно:
  - fully decouple camera from main thread;
  - fix render stutter while game logic blocks main thread.
- Рекомендованные дополнительные toggles.

## Sub-agent 10: Mod Overhead, Reflection, Logging, Allocations

Файл отчёта:

```text
docs\perf-research\10-mod-overhead-logging-reflection.md
```

Задача:

- Проверить весь мод на overhead:
  - repeated `AccessTools.Field/Property/Method`;
  - reflection in hot paths;
  - string concatenation in hot paths;
  - LINQ in hot paths;
  - logging per tick;
  - Harmony max profiler overhead;
  - allocations in caches/keys.
- Найти warning-spam/error-spam sources.

Выход:

- Таблица overhead hotspots.
- Что кэшировать.
- Что заменить на direct `GetProperty`/cached `MethodInfo`.
- Что убрать из max profiler.
- Что перевести в counters instead of logs.

## Итоговый файл 00-summary-roadmap.md

Должен включать:

1. Executive summary.
2. Top 20 bottlenecks:
   - method;
   - source;
   - likely cause;
   - estimated impact;
   - risk.
3. Рекомендованный порядок работ:
   - quick wins;
   - scheduler v2;
   - route broker;
   - reservation broker v2;
   - worker-thread DTO scoring.
4. Kill switches/config flags для каждой оптимизации.
5. Tests:
   - reflection contract tests;
   - runtime counters;
   - log assertions;
   - manual save-game scenarios.
6. “Do not do” list:
   - unsafe patches;
   - прямой multithreading Unity state;
   - кэш positive reservation results без validation;
   - teleport/route bypass без vanilla route.

## Готовый master prompt для Codex

```text
Ты работаешь в C:\Users\gerbe\OneDrive\Документы\Playground\ProjectHospital.AutoLabBalancer.

Нужно провести полное исследование производительности Project Hospital и мода. Используй Assembly-CSharp.dll, исходники мода, BepInEx LogOutput.log и текущие docs.

Разбей работу на 10 параллельных sub-agents:
1. Update loop map
2. Pathfinding and movement
3. Nurse AI and task selection
4. Patient flow and hospitalization
5. Procedures, examinations, reservations
6. Map/object/staff searches
7. Scheduler and dispatcher
8. Janitor, lab and support AI
9. Render/camera/frame pacing
10. Mod overhead/reflection/logging/allocations

Каждый sub-agent должен написать Markdown-отчёт в docs\perf-research\NN-name.md по своему направлению.
После завершения всех sub-agents собери docs\perf-research\00-summary-roadmap.md с итоговой дорожной картой оптимизаций.

На этом этапе не меняй код игры/мода, кроме создания Markdown-отчётов. Не делай destructive git-команды. Не предлагай распараллеливать Unity/entity state machine напрямую. Для каждой оптимизации указывай safety gates, kill switch, risk level и testing plan.

Используй rg и ilspycmd. Для decompile смотри:
C:\Program Files (x86)\Steam\steamapps\common\Project Hospital\ProjectHospital_Data\Managed\Assembly-CSharp.dll

Особенно проверь:
- BehaviorNurse.UpdateStateIdle
- BehaviorPatient.SelectNextProcedure / TryToScheduleExamination
- ProcedureComponent.SelectExaminationForMedicalCondition
- Pathfinder.FindRoute / WalkComponent.SetDestination / SetupJob
- MapScriptInterface FindClosest* методы
- SchedulingEngineService и dispatcher apply
- logging/reflection warning spam

Финальный ответ должен кратко перечислить созданные файлы и 5-10 главных выводов.
```
