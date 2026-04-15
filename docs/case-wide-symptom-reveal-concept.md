# Concept: Case-Wide Symptom Reveal During Diagnostics

## Goal

When a doctor, nurse, lab specialist, or monitoring flow performs diagnostics on a patient with a multi-problem `PatientCase`, the completed diagnostic step should reveal **all symptoms from all case problems** that are discoverable by that step **right now**, not only symptoms from the current compatibility diagnosis.

This revision also has to stay compatible with:

- the single active vanilla `MedicalCondition`
- the plugin's queue/task orchestrator and scheduler layer
- queue-based helper modules such as nurse-check discharge, equipment referral, overlay counters, and medication planning

## Non-Negotiable Principles

1. **Evidence belongs to the whole patient case.**
2. **Execution ownership belongs to one department cluster at a time.**
3. **The rewrite arbitration layer is the only writer to vanilla planned examinations/treatments for multi-case patients.**
4. **Scheduler, dispatcher, referral helpers, and overlays see only materialized vanilla work, not hidden case backlog.**
5. **Discharge, referral, and department-routing gates are decided at case level, not by one projected diagnosis.**
6. **Diagnostic events are processed exactly once.**
7. **If a vanilla arbitration method is overridden, the rewrite owns the whole decision and vanilla must not continue with a conflicting partial outcome.**

The most important correction compared to the previous draft is:

- `DiagnosticEvent` processing must update the case ledger only
- it must **not** immediately rebuild or churn the vanilla queue inside the examination-result hook itself

## Problem In Vanilla

Vanilla symptom reveal is single-condition only:

- `ProcedureComponent.Update(...)`
  - on exam completion calls `BehaviorPatient.UncoverSymptomsFromLastExamination(...)`
- `BehaviorPatient.UncoverSymptomsFromLastExamination(...)`
  - iterates only `m_state.m_medicalCondition.m_symptoms`
  - reveals hidden symptoms whose `symptom.Examinations` contains the finished examination
- `ProcedureComponent.FinishLabProceduresWithResultsReady(...)`
  - updates diagnoses after lab results, but still through the current `MedicalCondition`
- `ProcedureScriptExaminationDoctorsInterview`
  - reveals complained-about hidden symptoms only inside the current `MedicalCondition`

So even if the patient conceptually has several illnesses, vanilla will only uncover symptoms on whichever disease is currently projected into the single active `MedicalCondition`.

## Required Design Shift

Diagnostics should no longer write "reveal symptom on active disease".

They should emit a **diagnostic evidence event**:

- interview happened
- reception triage happened
- exam `EXM_XRAY` finished
- lab result `EXM_BLOOD` became ready
- monitoring tick discovered monitored symptom

Then the rewrite processes that event against the **entire `PatientCase`**.

But that processing must be split into two phases:

1. **Evidence phase**
   - reveal symptoms across the whole case
   - update statuses, certainty, timeline, blockers
   - set dirty flags
2. **Materialization phase**
   - performed later by the queue/task orchestrator at a safe checkpoint
   - chooses one executable slice
   - updates vanilla queue and compatibility projection with minimal churn

## Rewrite Scope

This concept assumes a full BepInEx/Harmony rewrite is allowed where needed.

That means the mod may:

- intercept and cancel vanilla arbitration methods for multi-case patients
- replace vanilla discharge/referral/routing verdicts with case-authored verdicts
- keep vanilla procedure scripts, reservations, transport, and room/equipment execution only as low-level executors

Preferred rule:

- override high-level decision points instead of mirroring dozens of downstream side effects

Typical rewrite-owned arbitration points:

- `BehaviorPatient.TryToScheduleExamination(bool)`
- `BehaviorPatient.TryToStartScheduledExamination()`
- `BehaviorPatient.SelectNextProcedure()`
- `BehaviorPatient.Diagnose(int, bool)`
- `HospitalizationComponent.SelectNextStep(float)`
- `HospitalizationComponent.IsHospitalizationOver()`
- `HospitalizationComponent.ReleaseFromObservation()`
- `BehaviorPatient.Leave(...)`
- `BehaviorPatient.BelongsToDepartment()`
- `BehaviorPatient.DepartmentIsUnclear()`

Not every implementation pass has to override all of them on day one.
But the architecture must allow the rewrite to take full ownership of these gates when compatibility shims alone are not enough.

## Phased Ownership Rule

During rollout, every high-level vanilla gate must have exactly one declared owner for rewrite-owned patients:

- `RewritePatch`
- `CompatibilityProjection`
- `LegacyAdapter`

No gate may be left in a "best effort" shared mode.

Fallback rules:

- if `BelongsToDepartment()` / `DepartmentIsUnclear()` stay vanilla in `v1`, `ProjectedDepartmentId`, queue head, and active cluster ownership must all resolve to the same department
- if `ReleaseFromObservation()` stays vanilla in `v1`, the rewrite must still publish a clinic-compatible target department and disposition verdict that match what vanilla will do next
- if `Leave(...)` stays vanilla in `v1`, every helper that can trigger departure must call the rewrite gate first and use `Leave(...)` only as the final side-effect executor

## Revised Core Model

### 1. PatientCase

`PatientCase` is the source of truth for the whole patient.

It keeps:

- `CaseId`
- `PatientEntityId`
- `Problems[]`
- `Clusters[]`
- `Intents[]`
- `MaterializedSlice`
- `CompatibilityProjection`
- `ProcessedDiagnosticEventJournal`
- `DirtyFlags`
- `TimelineEntries[]`
- `DispositionState`

`PatientCase` owns truth.
Vanilla owns only execution of the currently materialized slice.

### 2. CaseProblem

Each `CaseProblem` keeps:

- `ProblemId`
- `DiagnosisId`
- `OwningClusterId`
- `Status`
- `Certainty`
- `Symptoms[]`
- `KnownSymptomIds[]`
- `RevealedByEventIds[]`
- `RequiresHospitalization`
- `BlocksDischarge`

Department ownership is not stored separately on the problem.
`CaseProblem` points only at `OwningClusterId`, and department identity is derived through the owning cluster.

Each symptom entry should keep:

- `SymptomId`
- `Spawned`
- `Hidden`
- `PatientKnowsAndComplains`
- `Active`
- `Suppressed`
- `RevealSources[]`
  - mapped from DB examination links, interview-capable rules, monitoring, collapse, surgery complications

### 3. CareCluster

`CareCluster` is the execution owner for a set of problems handled by one department flow.

It keeps:

- `ClusterId`
- `DepartmentId`
- `ProblemIds[]`
- `ExecutionState`
  - `Dormant`
  - `Candidate`
  - `Active`
  - `Blocked`
  - `WaitingTransfer`
  - `Completed`
  - `ReferredOut`
- `NeedsHospitalization`
- `Priority`
- `Blockers[]`

This is critical for multi-department compatibility:

- evidence can advance any problem in the case
- but only one cluster at a time should own actual executable work in vanilla
- `CareCluster.DepartmentId` is the only canonical department owner for execution

Terminal aggregation rules:

- if any member problem is still non-terminal, the cluster cannot be `Completed` or `ReferredOut`
- `ReferredOut` means all terminal member problems ended as `ReferredOut` and no local executable intents remain
- `Completed` means all member problems are terminal and at least one ended locally as `Resolved`
- if terminal outcomes are mixed (`Resolved` + `ReferredOut`), the cluster still aggregates to `Completed`; referral summaries must read per-problem outcomes, not only the cluster flag
- `Blocked` means unresolved member problems remain, but routing cannot safely materialize more local work until a transfer/resource/return condition clears

### 4. CaseIntent

The previous draft overloaded `CaseOrder`.
That is not safe enough for scheduler compatibility.

Use `CaseIntent` as the latent case-level order:

- `IntentId`
- `Kind`
  - `Examination`
  - `Treatment`
  - `Hospitalization`
  - `Transfer`
  - `Observation`
- `ProcedureId`
- `ReasonProblemIds[]`
- `OwningClusterId`
- `Status`
  - `Latent`
  - `ReadyToMaterialize`
  - `Materialized`
  - `Running`
  - `Completed`
  - `Cancelled`
  - `Blocked`
- `Blockers[]`
- `Priority`

Department identity is derived from `OwningClusterId -> CareCluster.DepartmentId`.
`CaseIntent` must not carry an independent department owner.

Important rule:

- `ReasonProblemIds[]` explain **why** the intent exists
- they do **not** define the full set of symptoms or problems that the result may reveal

So an MRI ordered because of trauma may still reveal a neuro symptom.
That evidence fan-out does not change MRI ownership retroactively.

### 5. MaterializedSlice

`MaterializedSlice` is the only part of case work that becomes visible to vanilla queue-based systems.

It keeps:

- `ActiveClusterId`
- `MaterializedIntentIds[]`
- `ReservedIntentIds[]`
- `RunningIntentIds[]`
- `ActiveLabIntentIds[]`
- `ActiveTreatmentIntentIds[]`
- `ExecutionBindings[]`
- `VanillaPlannedExaminations[]`
- `VanillaPlannedTreatments[]`
- `Version`

The scheduler, dispatcher, referral helpers, and queue-based overlays should operate only on this slice.

`MaterializedSlice` is a derived projection, not authoritative state.

`ExecutionBindings[]` map intent IDs to the live vanilla runtime surface currently carrying them.

Typical binding kinds:

- `PlannedExamination`
- `PlannedTreatment`
- `ReservedProcedure`
- `ActiveExamination`
- `ActiveTreatment`
- `LabProcedure`
- `HospitalizationTransition`

Invariants:

- every ID in `MaterializedIntentIds[]` must refer to an intent with `Status == Materialized` or `Status == Running`
- every ID in `ReservedIntentIds[]` must refer to an examination or treatment intent that already owns a vanilla reservation / reserved procedure script
- every ID in `RunningIntentIds[]` must refer to an examination or treatment intent whose vanilla execution already started
- every ID in `ActiveLabIntentIds[]` must refer to a lab intent currently represented by a live vanilla `LabProcedure`
- every ID in `ActiveTreatmentIntentIds[]` must refer to a treatment intent currently represented by `m_activeTreatmentStates` or an equivalent hospitalization runtime step
- every live execution surface in vanilla (`m_plannedExaminationStates`, `m_plannedTreatmentStates`, `m_activeExamination`, `m_activeTreatmentStates`, `m_labProcedures`, reserved procedure script, active hospitalization transition) must map back to exactly one slice binding
- every materialized intent must belong to `ActiveClusterId`
- `Running` means vanilla execution already started; `Materialized` means the intent is visible in the vanilla planned queue but has not started yet
- hospitalization and observation transitions count as running work even when the original hospitalization treatment is no longer sitting in the planned queue
- if an intent leaves the slice without completion, its `CaseIntent.Status` must first change back to `ReadyToMaterialize` or `Blocked`
- discharge and routing gates must consider planned treatment, active treatment, reserved/running examination, lab, and hospitalization-transition bindings, not only planned queue lists

### 6. CompatibilityProjection

There is still only one active vanilla-style projection.

It keeps:

- `ProjectedProblemId`
- `ProjectedDepartmentId`
- `ProjectedSymptoms[]`
- `ProjectedHazard`
- `ProjectedDiagnosisState`
- `BlocksVanillaDischarge`

It exists only so vanilla AI/runtime continues to work.
It must not become the source of truth.

Derived-state rules:

- `ProjectedDepartmentId` is derived from `MaterializedSlice.ActiveClusterId`
- `BlocksVanillaDischarge` is derived from `EvaluateCaseDisposition(...)`
- projection never infers discharge or ownership from stale cached projection state
- not every vanilla reader has to use the projection; high-level gates may be patched directly to case truth instead
- projection is for residual compatibility surfaces, not a mandate to keep all vanilla reasoning unpatched
- if `BelongsToDepartment()` / `DepartmentIsUnclear()` are still unpatched in a rollout stage, `ProjectedDepartmentId` becomes the required fallback department owner for those vanilla readers too
- if `Mode == TransferToClinic` and `ReleaseFromObservation()` is still vanilla, `ProjectedDepartmentId` must already mirror `TargetDepartmentId` before the release path runs

### 7. DiagnosticEvent

Introduce a transient event structure:

- `EventId`
- `PatientEntityId`
- `Kind`
  - `ReceptionInterview`
  - `DoctorInterview`
- `PhysicalExam`
- `EquipmentExam`
- `LabResult`
- `Monitoring`
- `ExaminationId`
- `SourceDepartmentId`
- `DoctorId`
- `SkillContext`
- `Timestamp`

`SourceDepartmentId` is event context only.
It records where the evidence came from, not who owns the case or the resulting work.

### 8. Dirty Flags

Use explicit dirty flags so case updates do not immediately churn the queue:

- `EvidenceDirty`
- `RoutingDirty`
- `IntentsDirty`
- `MaterializationDirty`
- `ProjectionDirty`
- `DispositionDirty`

## Symptom Fan-Out Rule

On every diagnostic event:

1. verify `EventId` has not already been processed
2. collect all open case problems
3. skip `Resolved` and `ReferredOut`
4. for each hidden symptom in each problem:
   - if symptom is not spawned, skip
   - if already known, skip
   - if current event is not a valid reveal source, skip
   - if reveal source is not valid for the current patient state, skip
   - otherwise reveal it
5. record reveal in timeline and per-problem state
6. recompute certainty and status for affected problems
7. set dirty flags
8. record `EventId` into the processed-event journal

That means:

- one completed `EXM_CT` can reveal CT-visible symptoms from several different problems
- one interview can reveal complained-about symptoms from several different problems
- one blood panel can reveal lab-visible findings from several different problems

## "Available Right Now" Constraint

Reveal only symptoms that are discoverable **at the current moment**.

A symptom is eligible only if all are true:

1. the symptom exists in the case problem
2. it is spawned already
3. it is still hidden
4. the finished diagnostic step is one of its valid reveal sources
5. the reveal source is currently valid for this patient state

Examples:

- Interview:
  - reveal only symptoms with `PatientKnowsAndComplains == true`
  - do not reveal silent/internal findings
- Reception fast exam:
  - reveal only complained-about hazardous symptoms, same as vanilla logic, but case-wide
- Physical/equipment exam:
  - reveal symptoms whose DB exam list contains the finished exam
- Lab result:
  - reveal symptoms linked to that lab exam
- Monitoring:
  - reveal symptoms marked discoverable by monitoring

## Confirmation Stays Conservative

The event may reveal symptoms across all problems, but it must **not** auto-confirm all diagnoses.

Reveal fan-out should be broad.
Diagnosis confirmation should stay conservative.

Recommended status progression:

- `Hidden -> Observed`
  - first relevant evidence exists
- `Observed -> Suspected`
  - enough evidence to keep the problem visible in the common case list
- `Suspected -> Confirmable`
  - rewrite believes confirmation is now possible
- `Confirmable -> Confirmed`
  - only by a separate diagnosis-commit step in the owning cluster
- `Confirmed -> Treating -> Controlled -> Resolved`
  - through execution and outcome checks
- `ReferredOut`
  - only by routing/referral logic

Confirmation still depends on:

- certainty threshold
- doctor approach
- enough evidence for this problem
- department ownership
- optional player decision if the case is player-controlled

So one CT can expose symptoms from cardio + ortho + neuro, but the game does not instantly mark all three diagnoses as final.

## Status Compatibility Map

The status set is only useful if every module interprets it the same way.

Use this compatibility mapping:

- `Hidden`
  - unresolved
  - invisible in diagnosis list
  - no executable work on its own
- `Observed`
  - unresolved
  - visible in case ledger
  - no forced transfer
- `Suspected`
  - unresolved
  - visible in case ledger
  - may create latent intents
- `Confirmable`
  - unresolved
  - may request an owning-cluster confirmation action
  - still not auto-materialized cross-department
- `Confirmed`
  - active problem
  - can own executable intents if its cluster is active
- `Treating`
  - active problem under current execution
- `Controlled`
  - still open, still blocks discharge if configured
- `Resolved`
  - closed
- `ReferredOut`
  - closed locally, but not treated by this hospital

The important compatibility consequence is:

- `Hidden`, `Observed`, `Suspected`, and `Confirmable` must not automatically flood the vanilla queue

Visibility note:

- problem visibility in the ledger and diagnosis-label visibility are separate policies
- `Observed` and `Suspected` may already be visible in the ledger while the diagnosis label remains masked
- UI panels must not infer diagnosis-name visibility directly from `Status`

## Separation Of Knowledge, Intent, And Materialized Work

This is the main architectural correction.

### Knowledge

Knowledge is:

- revealed symptoms
- certainty
- statuses
- timeline
- blockers

Knowledge belongs to `PatientCase`.

### Intent

Intent is:

- "this exam/treatment/hospitalization/transfer is needed eventually"

Intent belongs to `CaseIntent`.
It may exist without being visible to vanilla queue systems.

### Materialized Work

Materialized work is:

- the currently executable slice reflected into vanilla planned queues
- the reservation / running / lab bindings that prove which intents already own live vanilla execution state

Materialized work belongs to `MaterializedSlice`.
Only this slice should be visible to:

- `SchedulingEngine`
- dispatcher recommendations
- equipment referral logic
- medication planning helpers
- bottleneck overlays
- nurse idle gating and other queue-based support systems

## Queue/Task Orchestrator Contract

The queue/task orchestrator must become the **only** writer to the vanilla queue for multi-case logic.

For rewrite-owned patients it must also become the only authority that may:

- allow or deny final discharge / clinic release / referral disposition
- allow or deny referral
- choose active department ownership
- decide whether vanilla arbitration should run at all

That means:

- reveal hooks do not rebuild vanilla queue
- reveal hooks do not reorder queue heads
- reveal hooks do not create cross-department planned examinations directly
- legacy helper modules do not independently mutate queue/discharge/referral state for these patients
- if a vanilla arbitration method is intercepted and cancelled, it must re-enter through rewrite-owned case logic, not through helper-specific side paths

Instead, the orchestrator reconciles case state at safe checkpoints.

### Safe Checkpoints

Reconciliation should happen only when the current mutation-heavy procedure step is already committed.

Examples:

- after exam completion is fully recorded
- after lab results are fully committed
- at patient routing checkpoints
- at hospitalization routing checkpoints
- after transfer completion
- before a scheduler snapshot is rebuilt, if dirty flags require it

### Reconciliation Responsibilities

At a safe checkpoint the orchestrator may:

1. refresh latent intents from current case knowledge
2. recompute routing, cluster priorities, blockers, and active-cluster choice
3. refresh case-level disposition state from case truth
4. materialize only the executable slice for the chosen cluster
5. delta-apply changes to vanilla planned examinations/treatments
6. refresh reserved/running/lab/treatment/hospitalization execution bindings against current vanilla runtime state
7. refresh compatibility projection from the materialized slice plus a direct `EvaluateCaseDisposition(...)` read
8. if materialization changed active work, rerun or invalidate the cached `DispositionState` before leaving the checkpoint
9. if `MaterializedSlice.Version` changed, trigger one centralized `OnMaterializedSliceVersionChanged(...)` invalidation pass

This keeps the existing scheduler and reservation stabilization logic viable because queue mutation happens in one controlled place.

## Department Independence Of Evidence

This remains the key conceptual choice:

- **evidence belongs to the patient case**
- **execution belongs to the current department cluster**

So if cardio orders an MRI and that MRI also reveals a neuro symptom, that is valid.

But the result must behave like this:

- the neuro problem advances in the case ledger
- neuro may gain or reprioritize a latent intent
- neuro does **not** immediately inject work into vanilla queue unless the orchestrator activates the neuro cluster or explicit urgent handoff rules say so

This is what keeps the design compatible with the current department-local scheduler board.

## Shared Orders Redefined

The previous `CaseOrder` idea is split into two layers:

1. `CaseIntent`
   - latent case-level need and ownership
2. `MaterializedSlice`
   - actual vanilla-visible queue entries

This is stronger and safer than one shared order object.

Why:

- ownership needs stability for scheduler/referral/task identity
- evidence fan-out is intentionally broad and can affect non-owning problems
- those two concerns should not mutate the same object in conflicting ways

## Compatibility With Existing Queue-Based Modules

### Slice Version Invalidation Contract

Use one explicit compatibility callback:

```csharp
void OnMaterializedSliceVersionChanged(BehaviorPatient patient, PatientCase patientCase, SliceDiff diff)
```

Required responsibilities:

- invalidate or rebuild any scheduler snapshot that still reflects the previous visible queue shape or department owner
- clear patient / hospitalization backoff caches that assume the old queue head is still current
- refresh trace / overlay / debug caches that mirror queue state
- notify legacy adapters so they stop acting on stale queue, referral, or discharge assumptions
- run once per committed version bump, not once per individual low-level queue mutation

### Scheduler And Dispatcher

`SchedulingEngine` reads current patient department board and vanilla planned queue state.
It should continue to do that.

Compatibility rule:

- scheduler sees only the materialized slice
- hidden case backlog stays out of scheduler input in `v1`
- scheduler snapshot must be invalidated or rebuilt through `OnMaterializedSliceVersionChanged(...)`

### Reservation Broker And Backoff

Backoff and reservation-failure caching assume queue shape does not churn multiple times inside the same result-processing window.

Compatibility rule:

- materialize only at safe checkpoints
- if queue head truly changes, invalidate or refresh the relevant recommendation/backoff state once through the centralized invalidation callback
- `SelectNextStep` and patient-search backoff caches must be reset when the rewrite starts new work, swaps active slice ownership, or changes treatment-vs-exam priority inside the slice

### Equipment Referral

Equipment referral looks at the first planned examination.

Compatibility rule:

- only the active slice may produce queue-head examinations
- latent cross-department intents must not appear as fake blocked queue heads
- referral decisions should move from vanilla scheduling-failure heuristics to case routing verdicts / explicit referral intents

### Bottleneck / Overlay / Medication Helpers

These modules read vanilla planned examination/treatment lists directly.

Compatibility rule:

- keep them pointed at the materialized slice only
- show the full case backlog in a separate case/debug UI, not by polluting vanilla queue

### Intake And Dynamic Department Choice

`IntakeControl` may continue choosing a generation-time clinic target.

Compatibility rule:

- generated diagnosis department is only a spawn-time hint, not a long-term owner for rewrite-owned patients
- once the case rewrite starts, active department truth comes only from cluster routing / materialized slice ownership
- intake counters such as direct department referrals may stay analytics-only, but they must not later be reused as routing truth

## Rewrite Integration With Current Plugin Modules

The existing plugin already has several helper modules that write into flow control.
For this concept they must be reclassified.

### `EquipmentReferral`

Current behavior:

- reacts to vanilla planned-exam blockage or vanilla scheduling failure
- may send the patient away immediately

Rewrite rule:

- it must stop owning referral truth for multi-case patients
- it should read explicit case routing / referral verdicts from the rewrite
- if kept as a helper, it becomes only an adapter that performs the final vanilla departure side effects after the rewrite already decided referral

### Nurse-Check Discharge

Current behavior:

- discharges or downgrades after vanilla nurse check if vanilla-style conditions look satisfied

Rewrite rule:

- it must become an adapter around `EvaluateCaseDisposition(...)` and case routing truth
- it may preserve `SendHome()` / `StopMonitoring()` / `SwitchState(Leaving)` as side-effect helpers only after the case gate authorizes release
- ICU downgrade or profile-department reassignment after nurse check must read the rewrite-owned target cluster/department, not `BelongsToDepartment()` independently

### Chained Hospitalized Examinations

Current behavior:

- keeps patients outside the room and re-enters `SelectNextStep(...)`

Rewrite rule:

- it may only continue same-cluster / same-slice follow-up work
- it must not consume latent cross-department work as if it were already materialized
- if the active slice changes, the helper must yield back to rewrite-owned routing instead of forcing another vanilla inpatient step

### `SchedulingEngine` And Performance Gating

Current behavior:

- reads current vanilla queue state and uses throttled snapshots/backoff

Rewrite rule:

- it remains a reader of materialized work only
- version changes in `MaterializedSlice` must invalidate snapshot-driven gating through one centralized callback
- no staff gating decision may assume the queue is stable across a rewrite reconciliation that already bumped the slice version

### Trace / Overlay / Debug UI

Rewrite rule:

- queue trace, overlay counters, and debug cards must report materialized work plus execution bindings
- trace readers must use live queue surfaces such as `m_plannedExaminationStates`, `m_plannedTreatmentStates`, `m_activeTreatmentStates`, and execution bindings instead of legacy serialized lists like `m_plannedExaminations`
- they must not infer hidden backlog from one compatibility diagnosis or one patient department field

### Developer Tools / Forced Leave

Rewrite rule:

- direct debug helpers that call `Leave(...)` must either run in an explicit bypass/debug mode or first archive the case through rewrite-owned cleanup
- even in tooling, `Leave(...)` should be treated as a side-effect executor, not as the source of case teardown truth

## Hospitalization And Chained Diagnostics

Hospitalized patients outside the room are a special edge case.

While a patient is `outsideRoom`:

- same-cluster follow-up diagnostics that are already compatible with the current hospitalization flow may be materialized immediately
- cross-department follow-up must stay latent until the patient returns to a stable routing checkpoint

This avoids oscillation between:

- retry transport reservation
- `SendBackToRoom`
- re-materialize different department work
- re-enter `SelectNextStep` under backoff pressure

## Case-Level Disposition Gate

Discharge cannot stay tied to one projected diagnosis.
Observation release, clinic transfer, external referral, and final discharge must share one case-authoritative verdict.

Introduce a case-authoritative gate:

```csharp
CaseDispositionDecision EvaluateCaseDisposition(PatientCase patientCase, DispositionContext context)
```

Recommended decision modes:

- `StayInCurrentCluster`
- `TransferToClinic`
- `ReferOut`
- `LeaveHospital`

Recommended decision payload:

- `Mode`
- `TargetClusterId`
- `TargetDepartmentId`
- `Reason`

It should decide from case truth:

- `StayInCurrentCluster` if any open problem still blocks release, any executable slice work remains, or unresolved hazard / hospitalization need remains
- `TransferToClinic` when hospitalization may end but the case still has local clinic-owned work that should continue inside another cluster or department flow
- `ReferOut` only when remaining work cannot be safely served locally and the case routing layer explicitly authorizes external exit
- `LeaveHospital` only when remaining open items are compatible with `Resolved` or `ReferredOut` and no planned/running treatment, examination, lab, or hospitalization-transition work remains

This gate should be used by:

- nurse-check discharge
- inpatient discharge checks
- `ReleaseFromObservation()`
- any rewrite interception of `Leave`
- compatibility projection refresh

The compatibility projection must keep vanilla discharge blocked whenever case truth says `Mode != LeaveHospital`.

`RefreshCaseDispositionState(...)` may cache the latest verdict on `PatientCase`, but discharge gates, observation-release gates, and compatibility projection must be allowed to call `EvaluateCaseDisposition(...)` directly when they need the authoritative answer for the current checkpoint.

If a release/referral path is fully patched, it should call `EvaluateCaseDisposition(...)` or the case routing verdict directly instead of inferring readiness from compatibility projection fields.

## Exact-Once Diagnostic Event Handling

The previous "do not reveal the same symptom twice" rule is not enough.

We also need exactly-once handling for the event itself.

Use a bounded `ProcessedDiagnosticEventJournal` in `PatientCase`.

Rules:

- if an event ID was already processed, skip the whole event
- only one canonical emitter may allocate an event ID for a given event family
- repeated Harmony postfixes must become no-ops
- repeated callbacks may still happen, but they must not allocate a second event ID or retrigger routing/materialization
- non-canonical hooks may gather context, suppress vanilla behavior, or request deferred reconciliation, but they must not emit a new event
- the journal must be retained as a bounded per-family ring buffer and pruned when the case reaches discharge/referral archive state

Typical event key shape:

- interview: patient + script instance + completion stamp
- exam result: patient + finished procedure/exam instance
- lab result: patient + lab procedure instance + result-ready stamp
- monitoring: patient + monitored symptom + monitoring window

## Recommended Internal API

The old immediate rebuild flow is removed.

### Evidence Processing

```csharp
CaseDiagnosticResult ProcessDiagnosticEvent(PatientCase patientCase, DiagnosticEvent evt)
```

Returns:

- `RevealedSymptoms[]`
- `AffectedProblemIds[]`
- `ProblemsPromotedToObserved[]`
- `ProblemsPromotedToSuspected[]`
- `ProblemsPromotedToConfirmable[]`
- `DirtyFlags`

### Deferred Reconciliation

```csharp
void ReconcileCaseAtCheckpoint(BehaviorPatient patient, PatientCase patientCase, CaseCheckpoint checkpoint)
```

Responsibilities:

```csharp
RefreshLatentIntents(patientCase);
RecomputeCaseRouting(patientCase);
RefreshCaseDispositionState(patient, patientCase);
MaterializeActiveSlice(patient, patientCase);
RefreshExecutionBindings(patient, patientCase);
RefreshCompatibilityProjection(patient, patientCase);
if (TryCommitSliceVersion(patientCase, out diff))
{
    OnMaterializedSliceVersionChanged(patient, patientCase, diff);
}
```

Ordering rule:

- routing reads the latest intent set
- disposition is evaluated from case truth before projection is rebuilt
- execution bindings are refreshed after materialization so discharge/referral/helpers see the same running examination, treatment, lab, and hospitalization state the vanilla runtime sees
- projection must use the current disposition verdict, not infer it from stale state
- if `MaterializeActiveSlice(...)` changed the executable slice, the cached disposition state must be refreshed again or explicitly treated as stale until the next safe checkpoint
- centralized invalidation runs after the new version is committed and only once per committed slice bump

This is the key correction:

- `ProcessDiagnosticEvent(...)` updates knowledge
- `ReconcileCaseAtCheckpoint(...)` updates vanilla-visible execution

## Patch Points

The patch plan should be split into three layers.

### 1. Event Emitters

These hooks emit `DiagnosticEvent` and process case-wide evidence:

- `ProcedureScriptExaminationDoctorsInterview.UpdateStatePatientTalking()`
- `ProcedureScriptExaminationReceptionFast.UpdateStatePatientTalking()`
- one chosen post-commit equipment/non-lab examination boundary hook
- `ProcedureComponent.FinishLabProceduresWithResultsReady(...)`
- monitoring symptom-unhide path

Canonical-emitter rule:

- interview events are emitted only from the interview/reception scripts
- equipment/non-lab examination results get exactly one post-commit emitter at the finished-exam boundary; implementation may choose `ProcedureComponent.Update(...)` or `BehaviorPatient.UncoverSymptomsFromLastExamination(...)`, but never both
- lab-result events are emitted only from `ProcedureComponent.FinishLabProceduresWithResultsReady(...)`
- `BehaviorPatient.UncoverSymptomsFromLabExamination(...)` and any secondary exam hook may collect context or suppress/adjust vanilla behavior, but they are not emitters

At these hooks:

- emit event
- process case-wide reveal
- mark dirty flags
- do **not** rebuild queue directly

### 2. Reconciliation / Gate Hooks

These hooks reconcile materialized work or block unsafe discharge:

- queue/task orchestrator reconciliation tick
- patient routing checkpoint
- hospitalization routing checkpoint
- nurse-check discharge gate
- any rewrite interception that would otherwise call `Leave`

### 3. Rewrite-Owned Arbitration Hooks

These are the places where the rewrite may fully cancel vanilla logic for multi-case patients:

- `BehaviorPatient.TryToScheduleExamination(bool)`
- `BehaviorPatient.TryToStartScheduledExamination()`
- `BehaviorPatient.SelectNextProcedure()`
- `BehaviorPatient.Diagnose(int, bool)`
- `HospitalizationComponent.SelectNextStep(float)`
- `HospitalizationComponent.IsHospitalizationOver()`
- `HospitalizationComponent.ReleaseFromObservation()`
- `BehaviorPatient.Leave(...)`
- `BehaviorPatient.BelongsToDepartment()`
- `BehaviorPatient.DepartmentIsUnclear()`

Rules:

- prefer prefix + cancel / redirect when vanilla would otherwise perform a conflicting decision
- do not let vanilla produce a routing/discharge/referral verdict first and then try to partially "fix" it afterwards
- if a hook stays unpatched in `v1`, the concept must explicitly say which compatibility projection or adapter owns that remaining vanilla path
- every rewrite-owned arbitration hook must be covered by reflection contract tests before implementation depends on it in production

## Validation And Reflection Contracts

The rewrite depends on brittle private/runtime surfaces.
Validation is part of the architecture, not post-hoc hygiene.

Minimum contract coverage should include:

- routing/disposition gates: `TryToScheduleExamination`, `TryToStartScheduledExamination`, `SelectNextProcedure`, `SelectNextStep`, `IsHospitalizationOver`, `ReleaseFromObservation`, `Leave`, `BelongsToDepartment`, `DepartmentIsUnclear()`
- event boundaries: chosen non-lab exam emitter, `FinishLabProceduresWithResultsReady(...)`, symptom uncover helpers if they are used for context
- queue/runtime surfaces: `m_plannedExaminationStates`, `m_plannedTreatmentStates`, `m_activeTreatmentStates`, `m_labProcedures`, reserved procedure script fields used by execution bindings

If a rollout stage relies on an unpatched vanilla path, that dependency must also be contract-tested explicitly.

## UI Behavior

The patient should still see a single history card.

After one exam:

- new symptoms appear in the common symptom ledger
- diagnosis names remain hidden whenever `DiagnosisLabelVisibility` is still masked, even if the problem already appears in the ledger
- timeline says what was discovered, not necessarily which hidden disease exists

The card should also separate:

- `Case Backlog`
  - latent intents not yet materialized
- `Current Plan`
  - active materialized slice visible to vanilla queue systems

That avoids lying overlays while still exposing full case truth to the user.

Recommended UI policy split:

- `ProblemVisibility`
  - controls whether the problem is shown in the shared case ledger
- `DiagnosisLabelVisibility`
  - controls whether the diagnosis name is shown, hinted, or still masked
- `Suspected` should normally map to visible-in-ledger plus masked-diagnosis by default; stronger label reveal remains a separate policy decision

## Practical Outcome

With this revised design:

- one diagnostic action can produce evidence for several illnesses
- evidence is stored once, at case level
- cross-department findings stay visible without forcing immediate queue churn
- only one active slice is pushed into vanilla queue
- scheduler and helper modules remain compatible because they still read queue-shaped data
- nurse-check discharge and other release paths can be made correct through one case-level discharge gate
- vanilla single-diagnosis runtime is preserved through compatibility projection

## Short Version

The correct mechanic is:

1. exam/interview/lab completion emits a diagnostic event
2. the event fans out over all open case problems
3. every symptom that this event can reveal right now becomes known
4. affected problems recompute certainty/status in the case ledger
5. the queue/task orchestrator later materializes exactly one executable slice
6. scheduler, referral, overlays, and nurse discharge continue to look at that materialized slice, while case truth stays broader underneath
