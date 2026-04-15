# Full Medical Case Rewrite Plan

## Goal

Replace the single-diagnosis gameplay surface with a `PatientCase` model that can hold multiple diagnoses, symptoms, orders, treatments, transfers, and history events. Vanilla `MedicalCondition` may remain as a compatibility bridge, but it must not be the source of truth for medical decisions or UI when the rewrite is enabled.

## Current Implemented Baseline

- `PatientCase` sidecar persistence per save.
- Multi-diagnosis generation behind `EnableMedicalCaseRewrite`.
- Candidate filtering by open feasible departments, including modded departments.
- Hopeless case generation behind `EnableHopelessCases`.
- First `История болезни` IMGUI window via the diagnosis-list button.
- Discharge bridge: if a case has another diagnosis, advance to the next diagnosis instead of letting vanilla discharge immediately.
- Standard patient UI adapters:
  - `Текущие симптомы` aggregates hidden/known symptom counts from `PatientCase`.
  - `Оконч. диагноз` is repurposed into a read-only case status segment.
  - `Возможные диагнозы` is repurposed into a read-only list of opened/confirmed case diagnoses with vanilla diagnosis tooltips.
- Case effects are aggregated into movement/hazard/bleeding/hospitalization adapters without stacking duplicate physical effects.
- Completed examinations/lab results reveal matching symptoms on secondary diagnoses without showing hidden diagnosis names.
- Conservative same-department secondary treatment planner handles known non-surgery/non-hospitalization symptoms and dedupes already planned/active/finished treatments.
- Vanilla examination/treatment availability maps receive conservative same-department secondary case candidates, so the doctor AI can see safe extra orders through the normal availability checks.
- Case-level payment/referral/death/discharge lifecycle adapters are active for rewrite patients.
- Nurse-check discharge now routes through the case checkpoint: all diagnoses treated means discharge, otherwise the next diagnosis is activated/transferred by case priority.
- Case history window lists live blockers: waiting examination, waiting treatment, needed transfer, hospitalization need, unavailable department, missing doctor, and room/equipment availability.
- The rewrite now treats `ActiveDepartmentId` as the current care owner; diagnoses are not single-owner "active disease" anymore. Same-department diagnoses can be suspected/diagnosed/treated together.
- Secondary collapse-capable diagnoses receive a case-level collapse deadline. When the deadline expires, the rewrite bridges it into vanilla collapse via `BehaviorPatient.SetCollapseOnSymptom` and a `MedicalCondition.HasActiveHazardSymptom` adapter.
- Direct vanilla `BehaviorPatient.Leave(...)` calls are intercepted for open cases. If vanilla tries to make a patient leave after only the compatibility diagnosis is treated, the rewrite advances/transfers the case instead of losing the patient.
- Manual patient-panel transfers are marked as case referrals when the full `PatientCase` is still open, preventing an untreated multi-diagnosis case from being recorded as a normal single-diagnosis discharge.

## Remaining Hardening / Runtime Validation

1. **Runtime-Prove Diagnosis Button Replacement**
   - Patch the actual vanilla diagnosis table open path, not only the button delegate.
   - The click on the diagnosis-list icon must always open `История болезни` when rewrite is enabled.
   - If vanilla recreates the delegate after `FillPatientData`, patch the specific open method or table activation path directly.

2. **Runtime-Prove Aggregate Symptoms In Standard UI**
   - The default patient panel `Текущие симптомы` is now adapter-backed by `PatientCase`, but still needs runtime matrix coverage with mixed vanilla/modded diagnosis sets.
   - Hidden diagnosis names may remain hidden, but symptom presence/count must not lie.

3. **Improve `История болезни` UI**
   - Add a visible diagnosis section with statuses: suspected, hidden, diagnosed, treated, plus the current care department.
   - Add an aggregated symptoms section, grouped by diagnosis when debug is enabled and grouped by known/hidden state when debug is disabled.
   - Add current blockers: required department, missing doctor, missing room/equipment, hospitalization need.
   - Add timeline entries for generation, diagnosis updates, treatment planning, transfer, discharge block, and case completion.

4. **Case Effects Aggregator Runtime Validation**
   - The effects adapter is implemented for hazard, mobility/animation, immobile, bleeding, can-not-talk, hospitalization need, and case-level collapse deadlines.
   - Remaining validation: confirm in-game animation/mobility on mixed cases such as cold + broken ankle and double-limp diagnoses.

5. **Case Care Planner**
   - Extend the current conservative same-department secondary treatment planner into a full `CareCluster` model.
   - Dedupe identical exams/treatments across diagnoses.
   - Keep maximum active orders per patient to avoid reservation spam.
   - Sort by collapse/high risk, then same-department treatability, then lower-risk follow-up.
   - Current safety limit: secondary surgery/hospitalization chains still route through active diagnosis/transfer, not direct secondary-map injection.

6. **Transfer And Discharge Rules Runtime Validation**
   - Discharge is blocked while any non-treated diagnosis remains through `SendHome`, direct `Leave(...)`, patient-panel treated-state, and nurse-check discharge adapters.
   - Multi-department cases use vanilla-safe `ChangeDepartment`/hospitalization flow.
   - If any remaining diagnosis can collapse, the next transfer target is chosen from collapse-capable diagnoses first; otherwise the original diagnosis order is used.
   - ICU downgrade and nurse-check discharge respect open `PatientCase` status.

7. **Payments And Statistics**
   - Replace single-diagnosis payment logic for rewrite patients with case-level payment.
   - Apply risk/hopeless bonuses and referral penalties.
   - Avoid double payment when a case advances through multiple vanilla compatibility diagnoses.

8. **Remove MedicalCondition As A Gameplay Dependency**
   - The active gameplay adapters now cover discharge/send-home, direct `Leave(...)`, manual/equipment/unsupported referrals, death, collapse bookkeeping, hospitalization transfer checkpoints, nurse-check discharge, doctor procedure availability, patient-panel treated state/buttons, and case-level payment.
   - `MedicalCondition` remains as a compatibility bridge only where vanilla hard-requires it for procedure and UI internals.
   - Remaining runtime-only risk: vanilla statistics, satisfaction, wrong-diagnosis count, and edge-case surgery/hospitalization paths can still use compatibility data internally. These should be verified in the runtime matrix before enabling rewrite by default.

9. **Runtime Test Matrix**
   - New simple clinic case: two low-risk diagnoses in one department.
   - Same-doctor mixed case: throat/cardiac/minor injury style case.
   - Cross-department case: emergency to orthopedics.
   - Modded department case: only generated if the relevant mod department is open and staffed.
   - UI case: standard symptoms panel count matches `PatientCase` aggregate.
   - Effects case: cold + broken ankle produces ankle-driven limp/mobility, and two limp diagnoses do not double-stack movement impairment.
   - Lifecycle case: discharge, transfer, external hospital referral, and death all resolve from `PatientCase` state and do not rely on a stale active `MedicalCondition`.
   - Hopeless case: rare, labelled, survivable window is fair when upgrades are high enough.

## Safety Rules

- `EnableMedicalCaseRewrite` stays default-off until the full UI/AI/discharge loop is stable.
- Do not generate ordinary cases that the current hospital cannot diagnose or treat.
- Do not mutate vanilla `MedicalCondition` symptom arrays unless no UI adapter path exists.
- Do not enable hopeless cases by default.
- Fail open to vanilla only when the rewrite cannot safely own the state.
- Any remaining `MedicalCondition` dependency must be documented as compatibility-only, not case ownership.
