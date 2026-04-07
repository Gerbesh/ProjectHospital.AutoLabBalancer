using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ProjectHospital.AutoLabBalancer
{
    internal static class MedicationPlanningTweaksService
    {
        public static void PlanKnownActiveSymptomMedication(
            Lopital.ProcedureComponent procedureComponent,
            Lopital.MedicalCondition medicalCondition,
            ref Lopital.TreatmentPlanningResult result)
        {
            if (!IsEnabled() || procedureComponent == null || medicalCondition == null)
            {
                return;
            }

            try
            {
                if (!HasDiagnosis(medicalCondition))
                {
                    return;
                }

                var candidateTreatments = CollectKnownActiveSymptomMedication(medicalCondition);
                if (candidateTreatments.Count == 0)
                {
                    return;
                }

                var queue = GetProcedureQueue(procedureComponent);
                if (queue == null)
                {
                    return;
                }

                var existingMedicationCount = CountMedicationTreatmentStates(queue);
                var patientLimit = RuntimeSettings.Config.MaxPlannedMedicationsPerPatient.Value;
                if (patientLimit > 0 && existingMedicationCount >= patientLimit)
                {
                    if (RuntimeSettings.ProductivityDebug && RuntimeSettings.Logger != null)
                    {
                        RuntimeSettings.Logger.LogDebug(ModText.Log("Aggressive medication planning skipped: patient medication limit reached (" + existingMedicationCount + "/" + patientLimit + ")."));
                    }

                    return;
                }

                var availabilityMap = procedureComponent.GetAllTreatmentsForMedicalCondition(
                    medicalCondition,
                    Lopital.TreatmentPlanningMode.ALL_SYMPTOMS);

                var added = 0;
                var passLimit = RuntimeSettings.Config.MaxAutoMedicationsPerPlan.Value;
                for (var i = 0; i < availabilityMap.Count; i++)
                {
                    if ((passLimit > 0 && added >= passLimit)
                        || (patientLimit > 0 && existingMedicationCount + added >= patientLimit))
                    {
                        break;
                    }

                    var treatment = availabilityMap.KeyAt(i);
                    if (treatment == null || !candidateTreatments.Contains(treatment))
                    {
                        continue;
                    }

                    if (!Lopital.ProcedureScene.IsProcedureAvailable(availabilityMap.ValueAt(i)))
                    {
                        continue;
                    }

                    if (HasAnyTreatmentState(queue, treatment) || IsAlreadyDone(procedureComponent, treatment))
                    {
                        continue;
                    }

                    AddPlannedTreatment(queue, treatment);
                    added++;
                }

                if (added > 0)
                {
                    result = Lopital.TreatmentPlanningResult.PLANNED;
                    RuntimeCounters.MedicationsAutoPlanned += added;
                    if (RuntimeSettings.ProductivityDebug && RuntimeSettings.Logger != null)
                    {
                        RuntimeSettings.Logger.LogInfo(ModText.Log("Aggressive medication planning added " + added + " treatment(s) for known active symptoms."));
                    }
                }
            }
            catch (Exception ex)
            {
                if (RuntimeSettings.Logger != null)
                {
                    RuntimeSettings.Logger.LogError(ModText.Log("Aggressive medication planning failed: " + ex));
                }
            }
        }

        private static bool IsEnabled()
        {
            return RuntimeSettings.Config != null
                && RuntimeSettings.Config.Enabled.Value
                && RuntimeSettings.Config.EnableAggressiveMedicationPlanning.Value;
        }

        private static bool HasDiagnosis(Lopital.MedicalCondition medicalCondition)
        {
            var diagnosed = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(medicalCondition, "m_diagnosedMedicalCondition"));
            return diagnosed != null;
        }

        private static HashSet<GameDBTreatment> CollectKnownActiveSymptomMedication(Lopital.MedicalCondition medicalCondition)
        {
            var result = new HashSet<GameDBTreatment>();
            foreach (var symptomObject in ReflectionHelpers.GetEnumerableField(medicalCondition, "m_symptoms"))
            {
                if (!Equals(ReflectionHelpers.GetField(symptomObject, "m_active"), true)
                    || Equals(ReflectionHelpers.GetField(symptomObject, "m_hidden"), true))
                {
                    continue;
                }

                var symptom = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(symptomObject, "m_symptom")) as GameDBSymptom;
                if (symptom == null || symptom.Treatments == null)
                {
                    continue;
                }

                foreach (var treatmentRef in symptom.Treatments)
                {
                    var treatment = ReflectionHelpers.ResolvePointer(treatmentRef) as GameDBTreatment;
                    if (IsMedicationTreatment(treatment))
                    {
                        result.Add(treatment);
                    }
                }
            }

            return result;
        }

        private static bool IsMedicationTreatment(GameDBTreatment treatment)
        {
            if (treatment == null)
            {
                return false;
            }

            return treatment.TreatmentType == TreatmentType.PRESCRIPTION
                || treatment.TreatmentType == TreatmentType.RECEIPT;
        }

        private static object GetProcedureQueue(Lopital.ProcedureComponent procedureComponent)
        {
            var state = ReflectionHelpers.GetField(procedureComponent, "m_state");
            return ReflectionHelpers.GetField(state, "m_procedureQueue");
        }

        private static bool HasAnyTreatmentState(object queue, GameDBTreatment treatment)
        {
            return InvokeQueueBool(queue, "HasPlannedTreatment", treatment)
                || InvokeQueueBool(queue, "HasActiveTreatment", treatment)
                || InvokeQueueBool(queue, "HasFinishedTreatment", treatment);
        }

        private static int CountMedicationTreatmentStates(object queue)
        {
            return CountMedicationTreatmentStates(queue, "m_plannedTreatmentStates")
                + CountMedicationTreatmentStates(queue, "m_activeTreatmentStates");
        }

        private static int CountMedicationTreatmentStates(object queue, string fieldName)
        {
            var count = 0;
            foreach (var state in ReflectionHelpers.GetEnumerableField(queue, fieldName))
            {
                var treatment = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_treatment")) as GameDBTreatment;
                if (IsMedicationTreatment(treatment))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool IsAlreadyDone(Lopital.ProcedureComponent procedureComponent, GameDBTreatment treatment)
        {
            var method = typeof(Lopital.ProcedureComponent).GetMethod("IsAlreadyDone", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return method != null && Equals(method.Invoke(procedureComponent, new object[] { treatment }), true);
        }

        private static bool InvokeQueueBool(object queue, string methodName, GameDBTreatment treatment)
        {
            if (queue == null || treatment == null)
            {
                return false;
            }

            var method = queue.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return method != null && Equals(method.Invoke(queue, new object[] { treatment }), true);
        }

        private static void AddPlannedTreatment(object queue, GameDBTreatment treatment)
        {
            var method = queue.GetType().GetMethod("AddPlannedTreatment", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (method != null)
            {
                method.Invoke(queue, new object[] { treatment });
            }
        }
    }

    [HarmonyPatch]
    internal static class AggressiveMedicationPlanningPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.ProcedureComponent");
            return type == null ? null : AccessTools.Method(type, "PlanAllTreatments");
        }

        private static void Postfix(
            Lopital.ProcedureComponent __instance,
            Lopital.MedicalCondition medicalCondition,
            ref Lopital.TreatmentPlanningResult __result)
        {
            MedicationPlanningTweaksService.PlanKnownActiveSymptomMedication(__instance, medicalCondition, ref __result);
        }
    }
}
