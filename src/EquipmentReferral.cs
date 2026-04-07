using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace ProjectHospital.AutoLabBalancer
{
    internal static class EquipmentReferralService
    {
        public static bool TryReferCurrentPlannedExamination(Lopital.BehaviorPatient patient)
        {
            if (!IsEnabled() || patient == null)
            {
                return false;
            }

            try
            {
                var procedureComponent = GetProcedureComponent(patient);
                var examination = GetFirstPlannedExamination(procedureComponent);
                if (examination == null)
                {
                    return false;
                }

                var availability = GetBestAvailability(patient, procedureComponent, examination);
                if (!IsMissingEquipmentBlock(availability))
                {
                    return false;
                }

                return ReferPatient(patient, examination, availability, "planned examination blocked");
            }
            catch (Exception ex)
            {
                LogError("Equipment referral check for planned examination failed: " + ex);
                return false;
            }
        }

        public static void TryReferAfterSchedulingFailure(Lopital.BehaviorPatient patient, bool schedulingSucceeded)
        {
            if (schedulingSucceeded || !IsEnabled() || patient == null)
            {
                return;
            }

            try
            {
                var procedureComponent = GetProcedureComponent(patient);
                var state = ReflectionHelpers.GetField(patient, "m_state");
                var medicalCondition = ReflectionHelpers.GetField(state, "m_medicalCondition") as Lopital.MedicalCondition;
                if (procedureComponent == null || medicalCondition == null || HasDiagnosis(medicalCondition))
                {
                    return;
                }

                var map = procedureComponent.UpdateAllExaminationsForMedicalCondition(medicalCondition, 9999, false);
                if (map == null || map.Count == 0)
                {
                    return;
                }

                GameDBExamination blockedExamination = null;
                Lopital.ProcedureSceneAvailability blockedAvailability = Lopital.ProcedureSceneAvailability.UNKNOWN;
                for (var i = 0; i < map.Count; i++)
                {
                    var examination = map.KeyAt(i);
                    var availability = map.ValueAt(i);

                    if (Lopital.ProcedureScene.IsProcedureAvailable(availability)
                        || availability == Lopital.ProcedureSceneAvailability.STAFF_BUSY
                        || availability == Lopital.ProcedureSceneAvailability.EQUIPMENT_BUSY)
                    {
                        return;
                    }

                    if (blockedExamination == null && IsMissingEquipmentBlock(availability))
                    {
                        blockedExamination = examination;
                        blockedAvailability = availability;
                    }
                }

                if (blockedExamination != null)
                {
                    ReferPatient(patient, blockedExamination, blockedAvailability, "no schedulable examination because equipment/room is missing");
                }
            }
            catch (Exception ex)
            {
                LogError("Equipment referral check after scheduling failure failed: " + ex);
            }
        }

        public static bool TryPayManualUntreatedReferral(object controller, bool wasTreated)
        {
            if (wasTreated
                || RuntimeSettings.Config == null
                || !RuntimeSettings.Config.Enabled.Value
                || !RuntimeSettings.Config.EnableManualReferralPayment.Value
                || controller == null)
            {
                return false;
            }

            try
            {
                var patient = GetControllerPatient(controller);
                if (patient == null)
                {
                    return false;
                }

                var payment = PayReferralShare(patient, RuntimeSettings.Config.ManualReferralPaymentPercent.Value);
                if (payment <= 0)
                {
                    return false;
                }

                RuntimeCounters.ManualReferralPayments++;
                RuntimeCounters.ManualReferralIncome += payment;
                Debug("Paid manual untreated referral share. Payment=" + payment + ".");
                return true;
            }
            catch (Exception ex)
            {
                LogError("Manual referral payment failed: " + ex);
                return false;
            }
        }

        public static bool TryReferUnsupportedDiagnosedPatient(Lopital.BehaviorPatient patient)
        {
            if (patient == null
                || RuntimeSettings.Config == null
                || !RuntimeSettings.Config.Enabled.Value
                || !RuntimeSettings.Config.EnableUnsupportedDiagnosisReferral.Value)
            {
                return false;
            }

            try
            {
                var state = ReflectionHelpers.GetField(patient, "m_state");
                if (state == null
                    || Equals(ReflectionHelpers.GetField(state, "m_sentAway"), true)
                    || Equals(ReflectionHelpers.GetField(state, "m_sentHome"), true)
                    || Equals(ReflectionHelpers.GetField(state, "m_deathTriggered"), true))
                {
                    return false;
                }

                var entity = ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
                if (IsHospitalized(entity))
                {
                    return false;
                }

                var medicalCondition = ReflectionHelpers.GetField(state, "m_medicalCondition") as Lopital.MedicalCondition;
                var diagnosedCondition = GetDiagnosedMedicalCondition(medicalCondition);
                if (diagnosedCondition == null)
                {
                    return false;
                }

                var profileDepartmentType = GetDiagnosisDepartmentType(diagnosedCondition);
                if (profileDepartmentType == null || Lopital.MapScriptInterface.Instance == null)
                {
                    return false;
                }

                var profileDepartment = Lopital.MapScriptInterface.Instance.GetDepartmentOfType(profileDepartmentType);
                string reason = null;
                if (RuntimeSettings.Config.ReferUnsupportedIfDepartmentMissing.Value
                    && (profileDepartment == null || !ReflectionHelpers.InvokeBool(profileDepartment, "HasWorkingClinic")))
                {
                    reason = "profile department unavailable";
                }
                else if (RuntimeSettings.Config.ReferUnsupportedIfNoProfileDoctor.Value
                    && profileDepartment != null
                    && !HasAvailableProfileDoctor(profileDepartment))
                {
                    reason = "no available profile doctor";
                }

                if (reason == null)
                {
                    return false;
                }

                return ReferUnsupportedPatient(patient, diagnosedCondition, reason);
            }
            catch (Exception ex)
            {
                LogError("Unsupported diagnosis referral check failed: " + ex);
                return false;
            }
        }

        private static bool ReferPatient(
            Lopital.BehaviorPatient patient,
            GameDBExamination blockedExamination,
            Lopital.ProcedureSceneAvailability availability,
            string reason)
        {
            var state = ReflectionHelpers.GetField(patient, "m_state");
            if (state == null
                || Equals(ReflectionHelpers.GetField(state, "m_sentAway"), true)
                || Equals(ReflectionHelpers.GetField(state, "m_deathTriggered"), true))
            {
                return false;
            }

            var entity = ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            var department = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_department")) as Lopital.Department;
            var hospitalized = IsHospitalized(entity);
            if (hospitalized)
            {
                Debug("Skipped equipment referral for hospitalized patient; vanilla hospitalization state must own patient departure.");
                return false;
            }

            var payment = CalculateReferralPayment(state, RuntimeSettings.Config == null ? 20 : RuntimeSettings.Config.EquipmentReferralPaymentPercent.Value);
            if (payment > 0)
            {
                Pay(entity, department, hospitalized, payment);
                RuntimeCounters.EquipmentReferralIncome += payment;
            }

            SetField(state, "m_sentAway", true);
            SetField(state, "m_sentHome", false);
            SetField(state, "m_untreated", false);
            SetField(state, "m_waitingForPlayer", false);
            SetField(state, "m_bookmarked", false);
            TryClearBookmark(entity);

            RuntimeCounters.EquipmentReferrals++;
            Debug("Referred equipment-blocked patient to another hospital. Payment=" + payment
                + ", availability=" + availability
                + ", examination=" + DescribeExamination(blockedExamination)
                + ", reason=" + reason + ".");

            InvokeLeave(patient, hospitalized);
            return true;
        }

        private static bool ReferUnsupportedPatient(Lopital.BehaviorPatient patient, GameDBMedicalCondition diagnosedCondition, string reason)
        {
            var state = ReflectionHelpers.GetField(patient, "m_state");
            if (state == null)
            {
                return false;
            }

            var entity = ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            var department = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_department")) as Lopital.Department;
            var payment = CalculateReferralPayment(state, RuntimeSettings.Config.UnsupportedDiagnosisReferralPaymentPercent.Value);
            if (payment > 0)
            {
                Pay(entity, department, false, payment);
                RuntimeCounters.UnsupportedDiagnosisReferralIncome += payment;
            }

            SetField(state, "m_sentAway", true);
            SetField(state, "m_sentHome", false);
            SetField(state, "m_untreated", false);
            SetField(state, "m_waitingForPlayer", false);
            SetField(state, "m_bookmarked", false);
            TryClearBookmark(entity);

            RuntimeCounters.UnsupportedDiagnosisReferrals++;
            Debug("Referred unsupported diagnosed outpatient to another hospital. Payment=" + payment
                + ", diagnosis=" + DescribeEntry(diagnosedCondition)
                + ", reason=" + reason + ".");

            InvokeLeave(patient, false);
            return true;
        }

        private static int PayReferralShare(Lopital.BehaviorPatient patient, int percent)
        {
            var state = ReflectionHelpers.GetField(patient, "m_state");
            var entity = ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            var department = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_department")) as Lopital.Department;
            var payment = CalculateReferralPayment(state, percent);
            if (payment <= 0)
            {
                return 0;
            }

            Pay(entity, department, IsHospitalized(entity), payment);
            return payment;
        }

        private static void Pay(GLib.Entity patientEntity, Lopital.Department department, bool hospitalized, int payment)
        {
            var category = hospitalized ? Lopital.PaymentCategory.INSURANCE_HOSPITALIZED : Lopital.PaymentCategory.INSURANCE_CLINIC;
            if (department != null)
            {
                department.Pay(payment, category, patientEntity);
            }
            else if (Lopital.Hospital.Instance != null)
            {
                Lopital.Hospital.Instance.Pay(payment, category);
            }
        }

        private static Lopital.ProcedureSceneAvailability GetBestAvailability(
            Lopital.BehaviorPatient patient,
            Lopital.ProcedureComponent procedureComponent,
            GameDBExamination examination)
        {
            if (procedureComponent == null || examination == null || examination.Procedure == null)
            {
                return Lopital.ProcedureSceneAvailability.UNKNOWN;
            }

            var entity = ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            var primaryDepartment = GetProcedureDepartment(patient, examination.Procedure, false);
            var fallbackDepartment = GetProcedureDepartment(patient, examination.Procedure, true);
            var primaryAvailability = GetDepartmentAvailability(procedureComponent, examination.Procedure, entity, primaryDepartment);
            if (Lopital.ProcedureScene.IsProcedureAvailable(primaryAvailability)
                || primaryAvailability == Lopital.ProcedureSceneAvailability.STAFF_BUSY
                || primaryAvailability == Lopital.ProcedureSceneAvailability.EQUIPMENT_BUSY)
            {
                return primaryAvailability;
            }

            if (fallbackDepartment != null && !ReferenceEquals(fallbackDepartment, primaryDepartment))
            {
                var fallbackAvailability = GetDepartmentAvailability(procedureComponent, examination.Procedure, entity, fallbackDepartment);
                if (Lopital.ProcedureScene.IsProcedureAvailable(fallbackAvailability)
                    || fallbackAvailability == Lopital.ProcedureSceneAvailability.STAFF_BUSY
                    || fallbackAvailability == Lopital.ProcedureSceneAvailability.EQUIPMENT_BUSY)
                {
                    return fallbackAvailability;
                }

                if (IsMissingEquipmentBlock(fallbackAvailability))
                {
                    return fallbackAvailability;
                }
            }

            return primaryAvailability;
        }

        private static Lopital.ProcedureSceneAvailability GetDepartmentAvailability(
            Lopital.ProcedureComponent procedureComponent,
            GameDBProcedure procedure,
            GLib.Entity patientEntity,
            Lopital.Department department)
        {
            if (procedureComponent == null || procedure == null || patientEntity == null || department == null)
            {
                return Lopital.ProcedureSceneAvailability.UNKNOWN;
            }

            return procedureComponent.GetProcedureAvailabilty(
                procedure,
                patientEntity,
                department,
                Lopital.AccessRights.PATIENT_PROCEDURE,
                Lopital.EquipmentListRules.ANY);
        }

        private static Lopital.Department GetProcedureDepartment(Lopital.BehaviorPatient patient, GameDBProcedure procedure, bool fallback)
        {
            if (procedure == null)
            {
                return null;
            }

            var departmentRef = fallback ? procedure.FallbackLabDepartmentRef : procedure.DetachedDepartmentRef;
            if (departmentRef != null && Lopital.MapScriptInterface.Instance != null)
            {
                return Lopital.MapScriptInterface.Instance.GetDepartmentOfType(departmentRef.Entry);
            }

            var state = ReflectionHelpers.GetField(patient, "m_state");
            return ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_department")) as Lopital.Department;
        }

        private static GameDBExamination GetFirstPlannedExamination(Lopital.ProcedureComponent procedureComponent)
        {
            var state = ReflectionHelpers.GetField(procedureComponent, "m_state");
            var queue = ReflectionHelpers.GetField(state, "m_procedureQueue");
            var plannedStates = ReflectionHelpers.GetField(queue, "m_plannedExaminationStates") as IList;
            if (plannedStates == null || plannedStates.Count == 0)
            {
                return null;
            }

            return ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(plannedStates[0], "m_examination")) as GameDBExamination;
        }

        private static Lopital.ProcedureComponent GetProcedureComponent(Lopital.BehaviorPatient patient)
        {
            var entity = ReflectionHelpers.GetField(patient, "m_entity");
            return ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.ProcedureComponent") as Lopital.ProcedureComponent;
        }

        private static bool IsMissingEquipmentBlock(Lopital.ProcedureSceneAvailability availability)
        {
            return (availability & Lopital.ProcedureSceneAvailability.EQUIPMENT_UNAVAILABLE) != 0
                || (availability & Lopital.ProcedureSceneAvailability.NO_ROOM) != 0;
        }

        private static bool HasDiagnosis(Lopital.MedicalCondition medicalCondition)
        {
            return ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(medicalCondition, "m_diagnosedMedicalCondition")) != null;
        }

        private static GameDBMedicalCondition GetDiagnosedMedicalCondition(Lopital.MedicalCondition medicalCondition)
        {
            return ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(medicalCondition, "m_diagnosedMedicalCondition")) as GameDBMedicalCondition;
        }

        private static GameDBDepartment GetDiagnosisDepartmentType(GameDBMedicalCondition diagnosis)
        {
            var property = diagnosis == null ? null : diagnosis.GetType().GetProperty("DepartmentRef", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var departmentRef = property == null ? null : property.GetValue(diagnosis, null);
            return ReflectionHelpers.ResolvePointer(departmentRef) as GameDBDepartment;
        }

        private static bool HasAvailableProfileDoctor(Lopital.Department department)
        {
            if (department == null || Lopital.Hospital.Instance == null)
            {
                return false;
            }

            foreach (var character in ReflectionHelpers.GetEnumerableField(Lopital.Hospital.Instance, "m_characters"))
            {
                var doctor = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.BehaviorDoctor");
                var employee = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.EmployeeComponent");
                if (doctor == null || employee == null)
                {
                    continue;
                }

                var employeeDepartment = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(ReflectionHelpers.GetField(employee, "m_state"), "m_department"));
                if (ReferenceEquals(employeeDepartment, department)
                    && ReflectionHelpers.InvokeBool(employee, "IsAvailable"))
                {
                    return true;
                }
            }

            return false;
        }

        private static int CalculateReferralPayment(object patientState, int percent)
        {
            if (percent <= 0)
            {
                return 0;
            }

            if (percent > 100)
            {
                percent = 100;
            }

            var medicalCondition = ReflectionHelpers.GetField(patientState, "m_medicalCondition");
            var gameDbMedicalCondition = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(medicalCondition, "m_gameDBMedicalCondition")) as GameDBMedicalCondition;
            if (gameDbMedicalCondition == null)
            {
                return 0;
            }

            return Math.Max(0, gameDbMedicalCondition.InsurancePayment * percent / 100);
        }

        private static Lopital.BehaviorPatient GetControllerPatient(object controller)
        {
            var character = ReflectionHelpers.GetField(controller, "m_character");
            var entityMethod = character == null ? null : character.GetType().GetMethod("GetEntity", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            var entity = entityMethod == null ? null : entityMethod.Invoke(character, null);
            return ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.BehaviorPatient") as Lopital.BehaviorPatient;
        }

        private static bool IsHospitalized(GLib.Entity entity)
        {
            var hospitalization = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.HospitalizationComponent") as Lopital.HospitalizationComponent;
            return hospitalization != null && hospitalization.IsHospitalized();
        }

        private static void InvokeLeave(Lopital.BehaviorPatient patient, bool hospitalized)
        {
            var method = AccessTools.Method(typeof(Lopital.BehaviorPatient), "Leave", new[] { typeof(bool), typeof(bool), typeof(bool) });
            if (method != null)
            {
                method.Invoke(patient, new object[] { false, false, hospitalized });
            }
        }

        private static void TryClearBookmark(GLib.Entity entity)
        {
            if (entity == null)
            {
                return;
            }

            var managerType = AccessTools.TypeByName("Lopital.BookmarkedCharacterManager") ?? AccessTools.TypeByName("BookmarkedCharacterManager");
            var instance = managerType == null ? null : AccessTools.Property(managerType, "Instance");
            var manager = instance == null ? null : instance.GetValue(null, null);
            var method = manager == null ? null : AccessTools.Method(manager.GetType(), "RemoveCharacter", new[] { typeof(GLib.Entity) });
            if (method != null)
            {
                method.Invoke(manager, new object[] { entity });
            }
        }

        private static void SetField(object instance, string fieldName, object value)
        {
            var field = instance == null ? null : AccessTools.Field(instance.GetType(), fieldName);
            if (field != null)
            {
                field.SetValue(instance, value);
            }
        }

        private static string DescribeExamination(GameDBExamination examination)
        {
            return examination == null ? "<unknown>" : examination.DatabaseID.ToString();
        }

        private static string DescribeEntry(object entry)
        {
            var property = entry == null ? null : entry.GetType().GetProperty("DatabaseID", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var value = property == null ? null : property.GetValue(entry, null);
            return value == null ? "<unknown>" : value.ToString();
        }

        private static bool IsEnabled()
        {
            return RuntimeSettings.Config != null
                && RuntimeSettings.Config.Enabled.Value
                && RuntimeSettings.Config.EnableEquipmentReferral.Value;
        }

        private static void Debug(string message)
        {
            if (RuntimeSettings.Config != null
                && RuntimeSettings.Config.EquipmentReferralDebugLog.Value
                && RuntimeSettings.Logger != null)
            {
                RuntimeSettings.Logger.LogInfo(ModText.Log(message));
            }
        }

        private static void LogError(string message)
        {
            if (RuntimeSettings.Logger != null)
            {
                RuntimeSettings.Logger.LogError(ModText.Log(message));
            }
        }
    }

    [HarmonyPatch(typeof(Lopital.BehaviorPatient), "TryToStartScheduledExamination")]
    internal static class EquipmentReferralStartScheduledExaminationPatch
    {
        private static bool Prefix(Lopital.BehaviorPatient __instance)
        {
            return !EquipmentReferralService.TryReferCurrentPlannedExamination(__instance);
        }
    }

    [HarmonyPatch(typeof(Lopital.BehaviorPatient), "TryToScheduleExamination")]
    internal static class EquipmentReferralScheduleExaminationPatch
    {
        private static void Postfix(Lopital.BehaviorPatient __instance, bool __result)
        {
            EquipmentReferralService.TryReferAfterSchedulingFailure(__instance, __result);
        }
    }

    [HarmonyPatch(typeof(Lopital.BehaviorPatient), "Diagnose", new[] { typeof(int), typeof(bool) })]
    internal static class UnsupportedDiagnosisReferralDiagnosePatch
    {
        private static void Postfix(Lopital.BehaviorPatient __instance)
        {
            EquipmentReferralService.TryReferUnsupportedDiagnosedPatient(__instance);
        }
    }

    [HarmonyPatch(typeof(Lopital.BehaviorPatient), "DiagnoseNow")]
    internal static class UnsupportedDiagnosisReferralDiagnoseNowPatch
    {
        private static void Postfix(Lopital.BehaviorPatient __instance)
        {
            EquipmentReferralService.TryReferUnsupportedDiagnosedPatient(__instance);
        }
    }

    [HarmonyPatch]
    internal static class ManualReferralPaymentPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("CharacterPanelPatientPanelController");
            return type == null ? null : AccessTools.Method(type, "SendPatientToAnotherHospital");
        }

        private static void Prefix(object __instance, ref bool __state)
        {
            try
            {
                var patient = GetPatient(__instance);
                var method = __instance == null ? null : __instance.GetType().GetMethod("IsPatientTreated", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                __state = patient != null && method != null && Equals(method.Invoke(__instance, new object[] { patient }), true);
            }
            catch
            {
                __state = true;
            }
        }

        private static void Postfix(object __instance, bool __state)
        {
            EquipmentReferralService.TryPayManualUntreatedReferral(__instance, __state);
        }

        private static Lopital.BehaviorPatient GetPatient(object controller)
        {
            var character = ReflectionHelpers.GetField(controller, "m_character");
            var entityMethod = character == null ? null : character.GetType().GetMethod("GetEntity", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            var entity = entityMethod == null ? null : entityMethod.Invoke(character, null);
            return ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.BehaviorPatient") as Lopital.BehaviorPatient;
        }
    }
}
