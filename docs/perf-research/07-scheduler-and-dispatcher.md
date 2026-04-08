# Performance Research 07: Scheduler And Dispatcher

Scope: `src/SchedulingEngine.cs`, `src/PerformanceOptimizations.cs`, `src/AutoLabBalancerPlugin.cs`, `docs/performance-investigation.md`, and decompiled `Assembly-CSharp.dll` via `ilspycmd` for `Lopital.BehaviorDoctor`, `Lopital.BehaviorNurse`, `Lopital.BehaviorLabSpecialist`, and `Lopital.BehaviorJanitor`.

This pass is about the mod-side scheduling index, dispatch recommendation layer, and the gating/apply counters that decide whether idle AI branches should do their normal expensive search work.

## What Is Already Good

The current design has a few useful properties that are worth preserving:

- The scheduler is short-lived, not a permanent world model. `SchedulingEngineService.Tick(...)` rebuilds on an interval, and `TryGetDepartmentBoard(...)` refuses to use stale data once the snapshot age exceeds the configured limit.
- The board is read-mostly. Callers only ask for a department board or a staff decision; they do not mutate the snapshot in place.
- The dispatcher is role-aware at the callsite. Doctors, nurses, lab specialists, and janitors each have their own idle patch entry points, so the gate can be applied only where it matters.
- The code already records useful diagnostics:
  - board hits / misses / stale
  - nurse gating checks / skips
  - outpatient gating checks / skips
  - doctor-search gating checks / skips
  - dispatcher apply checks / allows / skips
- The fallback behavior is conservative. If the board is missing, stale, or does not contain a recommendation for the current staff member, the mod falls back to vanilla behavior instead of hard-blocking AI.
- The decompiled vanilla behavior confirms that these hot paths are the right targets:
  - `BehaviorDoctor.UpdateStateIdle(float)`
  - `BehaviorNurse.UpdateStateIdle(float)`
  - `BehaviorLabSpecialist.UpdateStateIdle(float)`
  - `BehaviorJanitor` idle/admin selection

That matters because the vanilla idle branches already do a lot of repeated map/search/state work right before they mutate reservations, destinations, procedures, and staff state.

## What Is Legacy Or Excessive

The current scheduler is carrying too many responsibilities in one place.

1. `SchedulingDepartmentBoard` is both an index and a scorecard.
   - It stores tasks.
   - It stores free staff candidates.
   - It stores role scores.
   - It stores summary counts.
   - It stores dispatch recommendations.

   That makes the board hard to reason about because the same object is doing collection, scoring, filtering, and debug reporting.

2. `SchedulingTask.ExpiresAt` is dead weight right now.
   - It is written when the task is created.
   - It is not consulted anywhere in the recommendation pass.
   - Actual lifetime control is driven by the snapshot TTL, not by per-task expiry.

3. `TaskId` is mostly debug glue.
   - It is useful for logs and summaries.
   - It is not a real validation key.
   - It does not protect against stale state.

4. The scheduler still uses string-role logic in hot paths.
   - `doctor`, `nurse`, `lab`, and `janitor` are represented as strings.
   - The dispatcher and role-score code then branch on those strings repeatedly.
   - That is workable for a prototype, but it is the wrong shape for a stable task board.

5. The nurse task board in `PerformanceOptimizations.cs` overlaps the scheduling engine.
   - `GetNurseBoard(...)` and `BuildNurseBoard(...)` build a second short-lived board.
   - `ShouldSkipNurseIdle(...)` can still fall back to that board when the central scheduler is disabled or unavailable.
   - This is acceptable as a bridge, but it is now duplicated logic and should be collapsed into the v2 board model.

## Architectural Bugs To Treat As Real

### 1. Task priority is not task ownership

`BuildDispatchRecommendations(...)` greedily picks the highest-priority task for each unused staff candidate, but it does not mark the task itself as claimed.

That creates two problems:

- the same task can be recommended to multiple staff members if it still looks best in the greedy pass
- the final top-dispatch summary only reports the numerically highest priority recommendation, not a stable per-role claim

In other words, priority is being used as a ranking hint, not as a safe claim mechanism.

### 2. `PersonalNeeds` is too cheap and too broad

`AddPersonalStaffTasks(...)` creates a `PersonalNeeds` task for every free staff member and adds a score of `2`.

That is a weak signal, but it still has side effects:

- it keeps the board non-empty even when there is no patient work
- it inflates role scores, especially the nurse score path used by gating
- it makes the board look “active” when it is only reporting that staff have personal needs

Then `TryGetDispatcherIdleDecision(...)` adds a 5-second cooldown when a staff member is allowed for `PersonalNeeds`, which means the board is mixing scheduling intent with a repeated backoff policy.

This is the wrong level of abstraction. Personal needs should be a separate low-priority queue, not a generic task that also drives dispatch gating.

### 3. Role scoring is not a clean demand signal

The current role score is a mixture of:

- patient demand
- hospitalized demand
- janitor cleaning demand
- personal needs
- free-staff presence

That means `NurseScore > 0` does not really mean “there is nurse work to do”. It can also mean “there is a free nurse who got a personal-needs task”.

That is visible in `ShouldSkipNurseIdle(...)`, where `NurseScore > 0` prevents the skip path from engaging. The result is that the score behaves more like a noisy activity flag than a demand index.

### 4. Role separation is too shallow

The board stores a single mixed task list and then filters by string role in the recommendation pass.

There is some real separation already:

- nurses get patient care, medicine, food, transport, collapse care, procedures
- doctors get critical care, waiting-patient work, planned surgery
- lab specialists only substitute into `doctor` work for `WaitingPatient`
- janitors get cleaning plus personal needs

But that separation is still implicit. It should be explicit in the data model, not encoded as a string comparison inside a greedy matcher.

### 5. Stale snapshot behavior is fail-open, but not validated enough

The snapshot age check is useful, but it is not enough on its own.

Current behavior:

- if the snapshot is too old, `TryGetDepartmentBoard(...)` returns false
- the caller then falls back to vanilla scanning / legacy board behavior

That is safe, but it is not strong enough for a dispatcher that is supposed to make cheap, repeatable decisions. A snapshot can still be “fresh enough” by wall-clock age and already be wrong because:

- a patient moved departments
- a staff member was reserved, fired, or reassigned
- a procedure state changed
- a room or object became invalid

The board needs versioned state validation, not just age validation.

## What To Keep In V1 Until V2 Replaces It

Keep these pieces until the v2 board is in place:

- global rebuild interval
- stale-age protection
- conservative fallback to vanilla behavior
- gating/apply counters
- per-role idle patch entry points
- short-lived caches for map searches and reservation failures

Do not keep the current mixed board shape as the long-term design.

## Task Board V2 Plan

The v2 board should be built around explicit state validation and per-role queues.

### Core data model

Use an immutable snapshot with:

- `BoardVersion`
- `BuiltAt`
- `ExpiresAt`
- `StateSignature`
- `DepartmentKey`
- per-role queues:
  - `DoctorQueue`
  - `NurseQueue`
  - `LabQueue`
  - `JanitorQueue`

Each queue should contain ranked task entries, not just raw mixed tasks.

### Task identity and validation

Every task should carry:

- stable task signature
- role
- task type
- department key
- target patient / target object / target procedure signature
- expiry
- source version

Validation on apply should require:

1. snapshot version still current
2. state signature still matches
3. task has not expired
4. staff is still free and still in the same department
5. target patient/object/procedure still exists
6. role still matches
7. task has not already been claimed by another staff member

This is the missing piece in the current design. Age alone is not enough.

### One task per staff per interval

The v2 dispatcher should guarantee that a staff member can claim at most one task per scheduling interval.

That means:

- explicit claim state per task
- explicit claim state per staff member
- no duplicate recommendations for the same task
- no “best task” reuse across multiple staff members

The current greedy loop is too permissive because it only tracks used staff, not used tasks.

### Per-role queues

The queue layout should be explicit:

- doctors: critical care, waiting patient, planned surgery
- nurses: collapse care, scheduled procedures, medicine, food, transport
- lab specialists: lab-specific procedure queue plus only the supported doctor fallback
- janitors: cleaning only

Personal needs should not be part of the main queue ordering. It should be a separate low-priority queue with its own cooldown policy.

### Expiry semantics

Make expiry real:

- task expiry should invalidate an individual task
- board expiry should invalidate the whole snapshot
- stale snapshots should be rejected early

The existing `SchedulingTask.ExpiresAt` field already hints at this, but today it is not doing any work.

## Worker-Thread DTO Scoring Plan

The next step after v2 should be a split between collection and scoring.

### Main thread

Collect a compact DTO snapshot on the main thread:

- department ids / pointers
- free staff ids by role
- patient/task ids and minimal state flags
- room/object state needed for ranking
- version and signature inputs

Do not send live Unity objects, mutable behavior components, or reservation handles to the worker.

### Worker thread

Run scoring only on DTOs:

- rank tasks per role
- choose one best task per staff member
- compute board score
- compute dispatcher recommendations

This is the right place to parallelize because it is read-only and can be made deterministic.

### Main-thread validation and apply

When the worker returns:

- revalidate the board version
- revalidate the state signature
- recheck free/reserved state
- recheck department match
- recheck task expiry
- apply only the still-valid recommendations

That keeps the concurrency boundary narrow and avoids trying to make vanilla AI itself thread-safe.

## Bottom Line

The current scheduler is useful as a transitional performance layer, but it is not yet a durable scheduling architecture.

What is worth keeping:

- short-lived snapshots
- conservative gating
- diagnostics counters
- role-specific idle entry points

What should be replaced:

- mixed task/staff/score board
- string-role greedy matching
- implicit task ownership
- `PersonalNeeds` as a generic task
- age-only freshness checks

The v2 direction should be: versioned immutable board, per-role queues, explicit claims, state-signature validation, and worker-thread scoring over DTOs only.
