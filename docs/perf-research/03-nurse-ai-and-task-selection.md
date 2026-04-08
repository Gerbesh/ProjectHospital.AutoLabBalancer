# Nurse AI And Task Selection

Scope:

- `Assembly-CSharp.dll` decompile of `Lopital.BehaviorNurse`
- `docs/performance-investigation.md`
- `src/PerformanceOptimizations.cs`
- `src/SchedulingEngine.cs`
- `src/ProductivityTweaks.cs`

The goal here is to separate:

1. what vanilla nurse idle actually scans,
2. what our scheduling/task-board layer already precomputes,
3. what must remain vanilla executor logic because it mutates live state.

## Vanilla idle selection order

`BehaviorNurse.Update(float)` dispatches `UpdateStateIdle(deltaTime)` only when the nurse is in `NurseState.Idle`. The method is the real selector, while the `UpdateStateDelivering*`, `UpdateStatePatientCheckUp`, `UpdateStateFulfillingNeeds`, and `UpdateStateFillingFreeTime` methods are executors for a state already chosen.

| Order | Vanilla branch / gate | What it scans or mutates | Task class | Can task board replace the scan? | Keep vanilla executor? |
| --- | --- | --- | --- | --- | --- |
| 1 | `deltaTime <= 0` | Immediate return | none | no | yes |
| 2 | shift/fired/closed/no-workspace gate | Sends nurse home, clears desk lights, resets flags | none | no | yes |
| 3 | `ShouldGoToTraining()` | Calls `CheckNeeds(...)` first, then `GoToTraining(...)` | personal needs / training | partial | yes |
| 4 | surgery-nurse gate | Checks surgery role, shift validity, `HasWaitingSurgery()` or `HasAnyCriticalSurgeryScheduled()` | surgery coverage | yes, as a coarse board signal | yes |
| 5 | scheduled medicine delivery | `FindUnreservedRoomWithTagInDepartmentForMedicine`, then `FindClosestFreeObjectWithTag(..., "doc_equipment")`, then starts `CONTROL_PROCEDURE_MEDICINE_DELIVERY_NURSE` | medicine | yes | yes |
| 6 | scheduled food delivery | `FindUnreservedRoomWithTagInDepartmentForLunch`, then `FindClosestFreeObjectWithTag(..., "lunch_cart")`, then starts `CONTROL_PROCEDURE_FOOD_DELIVERY_NURSE` | food | yes | yes |
| 7 | patient checkup | `FindRoomWithTagInDepartmentForPatientCheckUp("patient_medicine")`, `GetFirstHospitalizedPatientForCheckUp`, then assigns nurse and starts `CONTROL_PROCEDURE_PATIENT_CARE_NURSE` | checkup | yes | yes |
| 8 | critical-patient gate | `HasAnyCriticalPatients()` blocks lunch/free-time and also blocks `CheckNeeds(...)` | critical | yes | yes |
| 9 | hospitalized scheduled-procedure gate | `HasAnyHospitalizedPatientsWithScheduledProcedures()` blocks lunch/free-time for the first in-state hour | surgery transport / scheduled inpatient work | yes | yes |
| 10 | staff lunch | `CONTROL_PROCEDURE_STAFF_LUNCH` if scheduled time, available, not had lunch | needs | no | yes |
| 11 | `CheckNeeds(AccessRights.STAFF_ONLY, flag)` | Scans `MoodComponent.GetNeedsSortedFromMostCritical()` and calls `GetProcedureAvailabilty(...)` for each need over 50 | personal needs | no, only coarse gating | yes |
| 12 | free-time fallback | `CONTROL_PROCEDURE_NURSE_FREE_TIME` if available, debug setting allows it, and the nurse is not a hard worker | free time | no | yes |
| 13 | workplace recovery | If workplace is set but nurse is not at it, it tries to re-anchor to the current room or common room | none | no | yes |

Notes:

- `CheckNeeds(...)` is the most expensive generic selector in the idle path because it does a sorted needs scan and a procedure-availability probe per candidate need.
- `flag` in the source is the surgery-nurse gate. When it is true, the nurse skips most free-time branches and the personal-needs check is delayed by the `surgeryScheduled` minimum in `CheckNeeds(...)`.
- There is no direct vanilla `CollapseCare` branch in `BehaviorNurse.UpdateStateIdle`. Collapse care is modeled in our scheduler from `HospitalizationComponent.WillCollapse`, not in the nurse's own idle selector.

## Task-board and dispatcher branches

This is the layer that can short-circuit or throttle idle selection before vanilla code runs.

| Branch | Current implementation | Effect on vanilla idle | Risk if overused |
| --- | --- | --- | --- |
| `ShouldSkipNurseIdle(...)` | `PerformanceOptimizations.cs` prefix on `BehaviorNurse.UpdateStateIdle` | Skips repeated idle scans for free, unreserved nurses when board score is empty or backoff is active | If too aggressive, a nurse can miss a newly opened task window |
| dispatcher apply | `TryGetDispatcherIdleDecision(..., "nurse")` | If the snapshot contains a recommendation for this nurse, it decides whether the idle method should run at all | If the snapshot is stale, the nurse can be gated on old work |
| `PersonalNeedsIdleNextCheck` | 5 second cooldown for `SchedulingTaskType.PersonalNeeds` | Prevents the same nurse from re-entering idle selection every frame for the same personal-needs recommendation | Without it, personal-needs becomes a per-frame idle hot path |
| `NurseTaskBoardSnapshot` | TTL snapshot in `PerformanceOptimizations.cs` | Coarse department-level signal for `HasAnyCriticalPatients`, surgery, hospitalized procedures, waiting patients, medicine, food, transport, and care | Snapshot freshness is only as good as TTL/invalidation |
| `SchedulingEngineService.TryGetDepartmentBoard(...)` | Gating path in `ShouldSkipNurseIdle(...)` | Uses `NurseScore > 0` to keep idle selection alive when real nurse work exists | If the board is stale, the nurse can oscillate between skip and allow |
| `NurseAssistedORCleanupPatch` | `ProductivityTweaks.cs` prefix on `UpdateStateIdle` | Bypasses vanilla selection entirely when an OR cleanup job is active | Must remain exclusive, because it owns the nurse's idle state for that job |

## Vanilla executor branches

These are not task-selection scans. They are the state handlers that execute work after a branch has already been chosen.

| State handler | What it does | Why it should stay vanilla |
| --- | --- | --- |
| `UpdateStateFulfillingNeeds()` | Waits for the procedure to finish, restores workspace, returns to idle | It finalizes a live procedure and resets staff state |
| `UpdateStateFillingFreeTime(float)` | Same pattern for free-time procedures, then returns to idle | It mutates procedure and workspace state |
| `UpdateStateDeliveringMedicine()` | If the procedure is done, sends the nurse back to workplace | It is the executor for medicine delivery |
| `UpdateStateDeliveringFood()` | Same for food delivery | It is the executor for food delivery |
| `UpdateStatePatientCheckUp()` | Same for patient checkup | It owns patient/nurse linkage and hospitalization state transitions |
| `UpdateStateGoingToWorkplace()` | Re-anchors nurse to workplace, updates lights/clothes, cancels browsing, then switches to idle | It repairs workspace state, not task selection |
| `UpdateStateFinishedProcedureWaitWithPatient()` | Walks all hospital characters, clears watching nurses, sends completion message, then returns to idle | It mutates other characters and message flow |

## Why `PersonalNeeds` should not wake idle every frame

This is the main performance trap in the current design.

`SchedulingEngineService` gives `PersonalNeeds` a positive score, so a department with only personal needs still looks non-empty. That is correct as a signal, but it is too coarse to drive the full vanilla idle selector every frame:

- `CheckNeeds(...)` is already a full scan over sorted mood needs plus availability checks.
- `TryGetDispatcherIdleDecision(...)` may allow the nurse to run idle, but if the recommendation type is `PersonalNeeds`, the same nurse can be presented with the same decision again on the very next frame.
- Without `PersonalNeedsIdleNextCheck`, the idle prefix keeps re-opening the selector even when nothing materially changed.

That is wasted work. The right behavior is:

1. detect that a personal-needs task exists,
2. allow one vanilla pass,
3. then suppress re-entry for a short cooldown,
4. and only re-open on real invalidation or timeout.

In other words, `PersonalNeeds` is a low-priority scheduling hint, not a reason to keep re-running the full idle selector each frame.

## Recommendations for nurse task board v2

1. Keep the board coarse, not exact.
   - Track department-level presence for critical, surgery, hospitalized procedures, medicine, food, transport, and personal needs.
   - Do not try to precompute the exact `MoodComponent` need or the exact room/object reservation result in the board.

2. Split selection from execution.
   - The board should decide whether vanilla idle is worth running.
   - The vanilla executor should still perform `StartProcedure`, reservations, room ownership changes, and patient/nurse linking.

3. Treat `PersonalNeeds` as a cooldowned hint.
   - Do not let it continuously wake the idle selector.
   - Keep the 5 second per-nurse recheck guard, or something equivalent, so the selector only runs again after a meaningful interval.

4. Preserve surgery-specific gating.
   - The current surgery-nurse branch is a role/shift gate, not a generic patient task.
   - Board v2 should keep that as a first-class signal so surgery nurses do not get pulled into low-value idle branches.

5. Keep collapse care separate from the idle selector.
   - `WillCollapse` is a scheduler signal and should remain a task-board concern.
   - The nurse executor should only run collapse-related work after a recommendation or another state transition has already committed to it.

6. Use task-board invalidation, not per-frame rescans.
   - Invalidate on patient state changes, procedure completion, room/object reservation changes, nurse availability changes, and department surgery/critical transitions.
   - Let TTL cover short-lived uncertainty, but do not rely on TTL alone for busy departments.

7. Make the dispatcher decision authoritative only for currently visible tasks.
   - If a nurse is not part of the current board or the recommendation is stale, fall back to vanilla idle selection instead of forcing a skip.

## Bottom line

The current stack is already pointing in the right direction:

- `SchedulingEngine` gives a department-level work signal.
- `NurseTaskBoardSnapshot` is the cheap nurse-specific coarse filter.
- dispatcher apply prevents needless idle passes when a recommendation already exists.
- `PersonalNeedsIdleNextCheck` is the critical guard that stops a low-priority branch from becoming a per-frame hot loop.

The safest next step for nurse task board v2 is to keep replacing scans with coarse signals, while leaving all stateful work execution inside vanilla nurse executor methods.
