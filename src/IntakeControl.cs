using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ProjectHospital.AutoLabBalancer
{
    internal sealed class IntakeSnapshot
    {
        public int CurrentClinicPatients;
        public int CurrentAmbulancePatients;
        public int ClinicCapacity;
        public int AmbulanceCapacity;
        public int OutpatientDoctors;
    }

    internal static class IntakeControlService
    {
        public static IntakeSnapshot CreateSnapshot()
        {
            var snapshot = new IntakeSnapshot();
            CountInsurancePatients(snapshot);
            snapshot.OutpatientDoctors = CountOutpatientDoctors();
            snapshot.ClinicCapacity = CalculateClinicCapacity(snapshot.OutpatientDoctors);
            snapshot.AmbulanceCapacity = CalculateAmbulanceCapacity(snapshot.OutpatientDoctors);
            return snapshot;
        }

        public static void ApplyDailyCap()
        {
            if (RuntimeSettings.Config == null || !RuntimeSettings.Config.Enabled.Value)
            {
                return;
            }

            var snapshot = CreateSnapshot();
            if (!RuntimeSettings.Config.EnableIntakeControl.Value)
            {
                Debug("Intake analytics: clinic=" + snapshot.CurrentClinicPatients + "/" + snapshot.ClinicCapacity
                    + ", ambulance=" + snapshot.CurrentAmbulancePatients + "/" + snapshot.AmbulanceCapacity
                    + ", outpatientDoctors=" + snapshot.OutpatientDoctors);
                return;
            }

            var clinicTarget = ClampTarget(snapshot.CurrentClinicPatients, snapshot.ClinicCapacity);
            var ambulanceTarget = ClampTarget(snapshot.CurrentAmbulancePatients, snapshot.AmbulanceCapacity);
            if (clinicTarget < snapshot.CurrentClinicPatients)
            {
                ClampInsuranceField("m_currentPatients", clinicTarget);
            }

            if (ambulanceTarget < snapshot.CurrentAmbulancePatients)
            {
                ClampInsuranceField("m_currentImmobilePatients", ambulanceTarget);
            }

            if (clinicTarget < snapshot.CurrentClinicPatients || ambulanceTarget < snapshot.CurrentAmbulancePatients)
            {
                Debug("Intake capped: clinic=" + snapshot.CurrentClinicPatients + "->" + clinicTarget
                    + ", ambulance=" + snapshot.CurrentAmbulancePatients + "->" + ambulanceTarget
                    + ", outpatientDoctors=" + snapshot.OutpatientDoctors);
            }
        }

        private static void CountInsurancePatients(IntakeSnapshot snapshot)
        {
            foreach (var insuranceCompany in GetInsuranceCompanies())
            {
                if (!ReflectionHelpers.InvokeBool(insuranceCompany, "IsContracted"))
                {
                    continue;
                }

                snapshot.CurrentClinicPatients += GetIntField(insuranceCompany, "m_currentPatients");
                snapshot.CurrentAmbulancePatients += GetIntField(insuranceCompany, "m_currentImmobilePatients");
            }
        }

        private static int CalculateClinicCapacity(int outpatientDoctors)
        {
            var hardCap = RuntimeSettings.Config == null ? 0 : Math.Max(0, RuntimeSettings.Config.MaxClinicPatientsPerDay.Value);
            var dynamicCap = RuntimeSettings.Config == null || !RuntimeSettings.Config.EnableDynamicIntakeByDoctors.Value
                ? 0
                : ApplyReserve(outpatientDoctors * Math.Max(1, RuntimeSettings.Config.ClinicPatientsPerDoctorPerShift.Value));
            return CombineCaps(hardCap, dynamicCap);
        }

        private static int CalculateAmbulanceCapacity(int outpatientDoctors)
        {
            var hardCap = RuntimeSettings.Config == null ? 0 : Math.Max(0, RuntimeSettings.Config.MaxAmbulancePatientsPerDay.Value);
            var dynamicCap = RuntimeSettings.Config == null || !RuntimeSettings.Config.EnableDynamicIntakeByDoctors.Value
                ? 0
                : ApplyReserve(outpatientDoctors * Math.Max(1, RuntimeSettings.Config.AmbulancePatientsPerDoctorPerShift.Value));
            return CombineCaps(hardCap, dynamicCap);
        }

        private static int ApplyReserve(int capacity)
        {
            if (RuntimeSettings.Config == null || capacity <= 0)
            {
                return capacity;
            }

            var reserve = Math.Max(0, Math.Min(90, RuntimeSettings.Config.ReserveEmergencyCapacityPercent.Value));
            return Math.Max(0, capacity * (100 - reserve) / 100);
        }

        private static int CombineCaps(int hardCap, int dynamicCap)
        {
            if (hardCap > 0 && dynamicCap > 0)
            {
                return Math.Min(hardCap, dynamicCap);
            }

            return hardCap > 0 ? hardCap : dynamicCap;
        }

        private static int ClampTarget(int current, int capacity)
        {
            if (capacity <= 0)
            {
                return current;
            }

            return Math.Max(0, Math.Min(current, capacity));
        }

        private static void ClampInsuranceField(string fieldName, int targetTotal)
        {
            var companies = new List<object>();
            var currentTotal = 0;
            foreach (var insuranceCompany in GetInsuranceCompanies())
            {
                if (!ReflectionHelpers.InvokeBool(insuranceCompany, "IsContracted"))
                {
                    continue;
                }

                var current = GetIntField(insuranceCompany, fieldName);
                if (current <= 0)
                {
                    continue;
                }

                companies.Add(insuranceCompany);
                currentTotal += current;
            }

            if (currentTotal <= targetTotal)
            {
                return;
            }

            var remaining = targetTotal;
            for (var i = 0; i < companies.Count; i++)
            {
                var company = companies[i];
                var current = GetIntField(company, fieldName);
                var next = i == companies.Count - 1 ? remaining : current * targetTotal / currentTotal;
                next = Math.Max(0, Math.Min(current, next));
                SetField(company, fieldName, next);
                remaining -= next;
            }
        }

        private static int CountOutpatientDoctors()
        {
            var hospital = Lopital.Hospital.Instance;
            if (hospital == null)
            {
                return 0;
            }

            var count = 0;
            foreach (var character in ReflectionHelpers.GetEnumerableField(hospital, "m_characters"))
            {
                var doctor = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.BehaviorDoctor");
                var employee = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.EmployeeComponent");
                if (doctor == null || employee == null)
                {
                    continue;
                }

                var department = GetEmployeeDepartment(employee);
                if (department != null && ReflectionHelpers.InvokeBool(department, "AcceptsOutpatients"))
                {
                    count++;
                }
            }

            return count;
        }

        private static IEnumerable<object> GetInsuranceCompanies()
        {
            var manager = Lopital.InsuranceManager.Instance;
            var state = ReflectionHelpers.GetField(manager, "m_state");
            return ReflectionHelpers.GetEnumerableField(state, "m_insuranceCompanies");
        }

        private static object GetEmployeeDepartment(object employee)
        {
            var state = ReflectionHelpers.GetField(employee, "m_state");
            return ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_department"));
        }

        private static int GetIntField(object instance, string fieldName)
        {
            var value = ReflectionHelpers.GetField(instance, fieldName);
            return value == null ? 0 : Convert.ToInt32(value);
        }

        private static void SetField(object instance, string fieldName, object value)
        {
            var field = instance == null ? null : AccessTools.Field(instance.GetType(), fieldName);
            if (field != null)
            {
                field.SetValue(instance, value);
            }
        }

        private static void Debug(string message)
        {
            if (RuntimeSettings.Config != null && RuntimeSettings.Config.IntakeDebugLog.Value && RuntimeSettings.Logger != null)
            {
                RuntimeSettings.Logger.LogInfo("[IntakeControl] " + message);
            }
        }
    }

    [HarmonyPatch(typeof(Lopital.InsuranceManager), "CalculatePatientCounts")]
    internal static class IntakeControlCalculatePatientCountsPatch
    {
        private static void Postfix()
        {
            try
            {
                IntakeControlService.ApplyDailyCap();
            }
            catch (Exception ex)
            {
                if (RuntimeSettings.Logger != null)
                {
                    RuntimeSettings.Logger.LogError(ModText.Log("Intake control failed: ") + ex);
                }
            }
        }
    }
}
