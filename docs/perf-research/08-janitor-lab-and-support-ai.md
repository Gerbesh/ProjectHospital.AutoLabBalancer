# Janitor, Lab And Support AI

Scope:

- `src/ProductivityTweaks.cs`
- `src/PerformanceOptimizations.cs`
- `src/SchedulingEngine.cs`
- `src/ExternalTransferAmbulanceTweaks.cs`
- `Assembly-CSharp.dll` decompile of `Lopital.BehaviorJanitor`
- `Assembly-CSharp.dll` decompile of `Lopital.BehaviorLabSpecialist`
- `Assembly-CSharp.dll` decompile of `Lopital.LabProcedureManager`
- `Assembly-CSharp.dll` decompile of `Lopital.LabProcedure`

This pass is about three different things that should not be mixed:

1. janitor lifecycle and the home/go-home boundary,
2. lab specialist idle gating and procedure execution,
3. support flow around the external ambulance / paramedic path.

## Why janitors go home

Vanilla janitor home behavior is not random. It is driven by a small set of explicit state transitions:

- `BehaviorJanitor.UpdateStateCleaning()` finishes the current cleaning action and then calls `SelectNextAction()`.
- `SelectNextAction()` sends the janitor back through `GoReturnCart()` when the janitor has no home room or the home room is a `janitor_admin_workplace`.
- `GoReturnCart()` falls back to `GoHome()` when the cart is gone or no longer placeable.
- `UpdateStateAdminIdle()` sends the janitor home when any of these become true:
  - shift mismatch,
  - fired,
  - department closed,
  - `m_noWorkSpaceForLongTime`.

That is the correct "go home" envelope. The mod should not try to keep a janitor on duty outside that envelope, because vanilla uses those same flags to protect the state machine from stale workplace and shift data.

## Correct standby conditions

The current standby redirect in `ProductivityTweaks` is the right shape:

- enabled only when `EnableJanitorStandbyAfterCleaning` is on,
- only for a janitor that is still in the current shift,
- only if the employee is not fired,
- only if the department still exists and is open,
- only if `m_noWorkSpaceForLongTime` is not set.

When those conditions hold, the redirect should:

1. stop the walk component,
2. clear `m_finished`,
3. prefer `GoToWorkPlace()` for janitor admin workplaces,
4. otherwise try `CheckNeeds()` / `CheckFreetime()`,
5. otherwise force the janitor back into `Cleaning`.

That is the safe boundary. Anything broader will keep a janitor active after the vanilla logic has already decided the shift or workplace is no longer valid.

## What must not be deep-profiled in lab states

`BehaviorLabSpecialist.Update()` is a large state machine. The expensive part is not just `UpdateStateIdle()`, but the fact that idle can flow into many executor states that mutate live procedure objects, reservations, and patient links.

Do not deep-profile these states as if they were read-only:

- `UpdateStateUsingEquipment()`
- `UpdateStateStoppedUsingEquipment()`
- `SelectNextLabProcedureStep()`
- `UpdeteSafetyReleaseCheck()`
- the `OverridenReservedForProcedure` cleanup path

Those paths do real work:

- they reserve and free equipment,
- they write `m_currentLabProcedure`,
- they switch `LabProcedureState`,
- they clear deleted or abandoned patient references,
- they finish results-ready procedures.

Profiling inside those paths is high-risk because you can distort timing-sensitive transitions and you will almost certainly double-count work that already happens in the idle selector or in `LabProcedure.Update()`.

## Safe lab specialist gating

`PerformanceOptimizations.ShouldSkipLabSpecialistIdle()` is safe because it is only a dispatcher gate. It does not replace lab behavior; it only decides whether `UpdateStateIdle()` should run for a free lab specialist.

The safe conditions are:

- `EnableSchedulingDispatcherApply` must be on,
- `EnableSchedulingEngineGating` must be on,
- the behavior must be an idle candidate,
- the dispatcher must have a current decision for the exact behavior and role `"lab"`.

If those conditions are not met, the prefix falls back to vanilla and `UpdateStateIdle()` runs.

Inside vanilla idle, the important branches are:

- shift / fired / closed / no-workspace -> `GoingHome`,
- training / needs / free-time transitions,
- room recovery and workplace re-anchoring,
- stat-lab scan through `LabProcedureManager.GetIdleLabProcedures(...)`.

So the right optimization boundary is "skip a full idle pass when the dispatcher already knows there is no useful work", not "profile the entire lab AI".

## What can be cached in LabProcedureManager

`LabProcedureManager` is small enough that the safe optimization target is the query shape, not the execution state.

Good cache candidates:

- `GetIdleLabProcedures(department, statLab, clinic, hospitalized)`
- `GetFirstIdleLabProcedure(department)`

The cache key should be based on:

- department,
- stat lab room,
- clinic / hospitalized flags,
- a short frame-local or very short TTL window.

The cache must be invalidated on any state mutation that can change the result set:

- `AddLabProcedure(...)`
- `UpdateLabProcedures(...)` removing finished procedures
- `LabProcedure.SwitchState(...)`
- `LabProcedure.Finish()`
- `LabProcedure.FreeEquipment()`
- patient leaving or being deleted
- room reassignment in `FindRoom(...)`
- any change to `m_statLab`, `m_equipmentIndex`, `m_labProcedureState`, or `m_abandoned`

Do not cache `IsIdle()` for long periods. In vanilla, `IsIdle()` depends on:

- `m_labProcedureState`,
- `m_timeInState`,
- equipment ownership,
- whether the patient or equipment still exists.

That means long-lived caching on the boolean itself is unsafe. Cache the filtered list, not the final truth value, unless the cache is invalidated immediately on every mutation above.

## Support flow note: external ambulance and paramedics

The external transfer path is separate from janitor and lab AI. It should stay separate in profiling and caching decisions.

Relevant facts:

- `ApplyExternalAmbulanceTimeScale()` is intentionally a no-op.
- only external-transfer paramedic movement / animation get extra speed,
- the ambulance state machine itself must not be accelerated,
- the queue broker is read-only and is used for diagnostics and parallel job creation only.

That means this path is not a candidate for janitor or lab deep profiling. If a future perf pass touches it, it should stay isolated to the transfer broker and paramedic movement hooks.

## Safety tests

Janitor:

1. Janitor with a valid shift and no admin workplace finishes cleaning and stays on duty when `EnableJanitorStandbyAfterCleaning` is enabled.
2. Janitor with shift mismatch still goes home.
3. Janitor with a closed department still goes home.
4. Fired janitor still goes home.
5. Janitor with `m_noWorkSpaceForLongTime` still goes home.
6. Janitor with a `janitor_admin_workplace` home room goes through `GoToWorkPlace()` instead of getting stuck in the cleaning loop.

Lab:

1. Free lab specialist in a stat lab with idle procedures can still enter vanilla idle and select work.
2. Free lab specialist in a non-stat-lab room does not get forced into deep lab procedure scanning.
3. Deleting the patient while the specialist is in `UsingEquipment` or `StoppedUsingEquipment` still clears the procedure cleanly.
4. Finishing a procedure still transitions through `ResultsReady` and `FinishedProcedure` without stale reservations.
5. `LabProcedureManager` cache invalidation happens after add, finish, abandon, or room reassignment.
6. `ShouldSkipLabSpecialistIdle()` never suppresses a non-idle or reserved behavior.

## Bottom line

Janitors go home for explicit vanilla reasons: shift, fired, closed department, missing workspace, or the cart-return/home-room path. The mod should only keep them on duty when all of those are still valid and the current shift still matches.

For lab specialists, the safe optimization boundary is the idle gate and the procedure list query. Do not deep-profile the whole state machine; it mutates live equipment, patient, and procedure state. Cache the filtered idle procedure list with tight invalidation, and leave execution states vanilla.
