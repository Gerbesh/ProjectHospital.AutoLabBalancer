using System;
using System.Collections.Generic;
using HarmonyLib;
using Lopital;
using UnityEngine;

namespace ProjectHospital.AutoLabBalancer
{
    internal sealed class IntakeSnapshot
    {
        public int CurrentClinicPatients;
        public int CurrentAmbulancePatients;
        public int ClinicCapacity;
        public int AmbulanceCapacity;
        public int OutpatientDoctors;
        public int DynamicDepartmentChoices;
        public int DirectDepartmentReferrals;
    }

    internal sealed class IntakeDepartmentChoice
    {
        public Department Department;
        public int BaseWeight;
        public int Doctors;
        public int DailyCapacity;
        public int AssignedToday;
        public float TargetHour;
        public int Weight;
    }

    internal static class IntakeControlService
    {
        private const int DefaultPatientsPerDoctorPerDay = 20;
        private const int MinDynamicWeightPercent = 65;
        private const int MaxDynamicWeightPercent = 145;

        [ThreadStatic]
        private static PatientGenerationContext sm_generationContext;

        private static readonly Dictionary<object, int> AssignedMobilePatientsByDepartment = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);

        private static int sm_assignedDay = -1;
        private static long sm_dynamicDepartmentChoices;
        private static long sm_directDepartmentReferrals;

        public static IntakeSnapshot CreateSnapshot()
        {
            var snapshot = new IntakeSnapshot();
            snapshot.OutpatientDoctors = CountOutpatientDoctors();
            snapshot.ClinicCapacity = CalculateClinicCapacity(snapshot.OutpatientDoctors);
            snapshot.AmbulanceCapacity = CalculateAmbulanceCapacity(snapshot.OutpatientDoctors);
            CountInsurancePatients(snapshot);
            snapshot.DynamicDepartmentChoices = (int)Math.Min(int.MaxValue, sm_dynamicDepartmentChoices);
            snapshot.DirectDepartmentReferrals = (int)Math.Min(int.MaxValue, sm_directDepartmentReferrals);
            return snapshot;
        }

        public static void ApplyDailyCap()
        {
            if (RuntimeSettings.Config == null
                || !RuntimeSettings.Config.Enabled.Value
                || !RuntimeSettings.Config.EnableIntakeControl.Value)
            {
                return;
            }

            var snapshot = CreateSnapshot();
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

        public static void BeginPatientGeneration(int index, int patientCounter, PatientMobility mobility, bool smoothDistribution)
        {
            sm_generationContext = new PatientGenerationContext
            {
                Active = true,
                Index = index,
                PatientCounter = patientCounter,
                Mobility = mobility,
                SmoothDistribution = smoothDistribution,
                EstimatedVisitHour = EstimateVisitHour(index, patientCounter, smoothDistribution)
            };
        }

        public static void EndPatientGeneration()
        {
            sm_generationContext = null;
        }

        public static bool TryChooseDynamicDepartment(PatientMobility mobility, ref Department result)
        {
            if (!IsDynamicDepartmentIntakeEnabled()
                || mobility != PatientMobility.MOBILE
                || sm_generationContext == null
                || !sm_generationContext.Active
                || sm_generationContext.Mobility != PatientMobility.MOBILE)
            {
                return true;
            }

            var hospital = Hospital.Instance;
            if (hospital == null)
            {
                return true;
            }

            ResetAssignedCountsIfDayChanged();

            var targetHour = sm_generationContext.EstimatedVisitHour;
            var candidates = new List<IntakeDepartmentChoice>();
            var totalWeight = 0;
            foreach (var department in hospital.m_departments)
            {
                var choice = CreateDepartmentChoice(department, targetHour);
                if (choice == null)
                {
                    continue;
                }

                candidates.Add(choice);
                totalWeight += choice.Weight;
            }

            if (candidates.Count == 0 || totalWeight <= 0)
            {
                return true;
            }

            var roll = UnityEngine.Random.Range(0, totalWeight);
            var cursor = 0;
            for (var i = 0; i < candidates.Count; i++)
            {
                var choice = candidates[i];
                cursor += choice.Weight;
                if (roll >= cursor)
                {
                    continue;
                }

                result = choice.Department;
                sm_generationContext.DynamicDepartment = choice.Department;
                sm_dynamicDepartmentChoices++;
                Debug("Dynamic department choice: " + GetDepartmentId(choice.Department)
                    + " hour=" + choice.TargetHour.ToString("0.0")
                    + " weight=" + choice.Weight
                    + " doctors=" + choice.Doctors
                    + " assigned=" + choice.AssignedToday + "/" + choice.DailyCapacity);
                return false;
            }

            return true;
        }

        public static void TryApplyDirectProfileReferral(object insuranceCompany, int index, PatientMobility mobility)
        {
            if (!IsDynamicDepartmentIntakeEnabled()
                || mobility != PatientMobility.MOBILE
                || sm_generationContext == null
                || sm_generationContext.DynamicDepartment == null)
            {
                return;
            }

            try
            {
                var patient = GetSpawnedPatient(insuranceCompany, index, "m_patientsSpawnedOutpatients") as GLib.Entity;
                if (patient == null)
                {
                    return;
                }

                var behavior = patient.GetComponent<BehaviorPatient>();
                if (behavior == null || behavior.m_state == null || behavior.m_state.m_medicalCondition == null)
                {
                    return;
                }

                var diagnosis = behavior.m_state.m_medicalCondition.m_gameDBMedicalCondition.Entry;
                var profileDepartmentType = diagnosis == null || diagnosis.DepartmentRef == null ? null : diagnosis.DepartmentRef.Entry;
                var emergency = Database.Instance.GetEntry<GameDBDepartment>("DPT_EMERGENCY");
                if (profileDepartmentType == null || profileDepartmentType == emergency)
                {
                    return;
                }

                var profileDepartment = MapScriptInterface.Instance == null ? null : MapScriptInterface.Instance.GetDepartmentOfType(profileDepartmentType);
                if (!CanAcceptDynamicClinicPatient(profileDepartment, sm_generationContext.EstimatedVisitHour))
                {
                    return;
                }

                if (!ReferenceEquals(profileDepartment, sm_generationContext.DynamicDepartment))
                {
                    return;
                }

                behavior.m_state.m_fromReferral = true;
                behavior.ResolveComplainedAboutSymptoms();
                behavior.m_state.m_medicalCondition.UpdatePossibleDiagnoses(patient);
                behavior.ChangeDepartment(profileDepartment, checkHospitalizationPlace: false);
                behavior.m_state.m_finishedAtReception = false;
                behavior.m_state.m_fVisitTime = GetVisitTime(insuranceCompany, index, sm_generationContext.PatientCounter, sm_generationContext.SmoothDistribution, profileDepartment);
                IncrementAssigned(profileDepartment);
                sm_directDepartmentReferrals++;
                Debug("Direct profile referral: " + GetDepartmentId(profileDepartment) + " diagnosis=" + diagnosis.DatabaseID);
            }
            catch (Exception ex)
            {
                Debug("Direct profile referral failed: " + ex.Message);
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
                : ApplyReserve(outpatientDoctors * GetPatientsPerDoctorPerDay());
            return CombineCaps(hardCap, dynamicCap);
        }

        private static bool IsDynamicDepartmentIntakeEnabled()
        {
            return RuntimeSettings.Config != null
                && RuntimeSettings.Config.Enabled.Value
                && RuntimeSettings.Config.EnableIntakeControl.Value
                && RuntimeSettings.Config.EnableDynamicIntakeByDoctors.Value;
        }

        private static float EstimateVisitHour(int index, int patientCounter, bool smoothDistribution)
        {
            if (patientCounter <= 0)
            {
                return GetCurrentHour();
            }

            var totalPatients = Math.Max(1f, patientCounter);
            if (smoothDistribution)
            {
                return ClampHour(1f + index * (22f / totalPatients));
            }

            var firstWave = totalPatients / 2f;
            if (firstWave <= 0.01f || index < firstWave)
            {
                return ClampHour(8f + index * (2f / Math.Max(1f, firstWave)));
            }

            return ClampHour(10f + (index - firstWave) * (5f / Math.Max(1f, firstWave)));
        }

        private static float ClampHour(float hour)
        {
            if (hour < 0f)
            {
                return 0f;
            }

            return hour > 23.99f ? 23.99f : hour;
        }

        private static float GetCurrentHour()
        {
            return DayTime.Instance == null ? 8f : DayTime.Instance.GetDayTimeHours();
        }

        private static IntakeDepartmentChoice CreateDepartmentChoice(Department department, float targetHour)
        {
            if (!CanAcceptDynamicClinicPatient(department, targetHour))
            {
                return null;
            }

            var departmentType = department.GetDepartmentType();
            var baseWeight = Math.Max(1, departmentType.PatientGenerationWeight);
            if (WorldEventManager.Instance != null && WorldEventManager.Instance.IsDepartmentModifierActive(departmentType))
            {
                baseWeight = Math.Max(1, (int)(baseWeight * WorldEventManager.Instance.GetDepartmentModifier()));
            }

            var doctors = GetOutpatientDoctorsForHour(department, targetHour);
            var capacity = GetDailyClinicCapacity(department);
            var assigned = GetAssigned(department);
            if (assigned >= capacity)
            {
                return null;
            }

            var hourlyPercent = GetHourlyWeightPercent(departmentType.DatabaseID.ToString(), targetHour);
            var capacityFactor = 0.75f + Math.Min(2.5f, (float)Math.Sqrt(doctors)) * 0.25f;
            var weight = Math.Max(1, (int)(baseWeight * capacityFactor * hourlyPercent / 100f));
            return new IntakeDepartmentChoice
            {
                Department = department,
                BaseWeight = baseWeight,
                Doctors = doctors,
                DailyCapacity = capacity,
                AssignedToday = assigned,
                TargetHour = targetHour,
                Weight = weight
            };
        }

        private static bool CanAcceptDynamicClinicPatient(Department department, float targetHour)
        {
            if (department == null || department.IsClosed() || !department.AcceptsOutpatients())
            {
                return false;
            }

            var departmentType = department.GetDepartmentType();
            return departmentType != null
                && departmentType.DiagnosisRandomizerMobile != null
                && departmentType.DiagnosisRandomizerMobile.Count > 0
                && GetOutpatientDoctorsForHour(department, targetHour) > 0
                && GetAssigned(department) < GetDailyClinicCapacity(department);
        }

        private static int GetDailyClinicCapacity(Department department)
        {
            var validity = department.m_departmentPersistentData.m_departmentValidity;
            var doctors = Math.Max(validity.m_outpatientDoctors, 0) + Math.Max(validity.m_outpatientDoctorsNight, 0);
            if (doctors <= 0)
            {
                doctors = GetOutpatientDoctorsForHour(department, GetCurrentHour());
            }

            return Math.Max(1, doctors) * GetPatientsPerDoctorPerDay();
        }

        private static int GetOutpatientDoctorsForHour(Department department, float targetHour)
        {
            var validity = department.m_departmentPersistentData.m_departmentValidity;
            var night = targetHour < 7f || targetHour >= 20f;
            return Math.Max(0, night ? validity.m_outpatientDoctorsNight : validity.m_outpatientDoctors);
        }

        private static int GetPatientsPerDoctorPerDay()
        {
            if (RuntimeSettings.Config == null)
            {
                return DefaultPatientsPerDoctorPerDay;
            }

            return Math.Max(1, RuntimeSettings.Config.OutpatientPatientsPerDoctorPerDay.Value);
        }

        private static float GetVisitTime(object insuranceCompany, int index, int patientCounter, bool smoothDistribution, Department department)
        {
            var method = insuranceCompany == null
                ? null
                : AccessTools.Method(insuranceCompany.GetType(), "GetVisitTime", new[] { typeof(float), typeof(float), typeof(bool), typeof(Department) });
            if (method == null)
            {
                return EstimateVisitHour(index, patientCounter, smoothDistribution);
            }

            try
            {
                return Convert.ToSingle(method.Invoke(insuranceCompany, new object[] { (float)index, (float)patientCounter, smoothDistribution, department }));
            }
            catch (Exception ex)
            {
                Debug("GetVisitTime reflection failed: " + ex.Message);
                return EstimateVisitHour(index, patientCounter, smoothDistribution);
            }
        }

        private static int GetHourlyWeightPercent(string departmentId, float targetHour)
        {
            var day = DayTime.Instance == null ? 0 : DayTime.Instance.GetDay();
            var hour = Math.Max(0, Math.Min(23, (int)Math.Floor(targetHour)));
            unchecked
            {
                var hash = 17;
                hash = hash * 31 + day;
                hash = hash * 31 + hour;
                for (var i = 0; i < departmentId.Length; i++)
                {
                    hash = hash * 31 + departmentId[i];
                }

                var range = MaxDynamicWeightPercent - MinDynamicWeightPercent + 1;
                var value = Math.Abs(hash % range);
                return MinDynamicWeightPercent + value;
            }
        }

        private static void ResetAssignedCountsIfDayChanged()
        {
            var day = DayTime.Instance == null ? 0 : DayTime.Instance.GetDay();
            if (day == sm_assignedDay)
            {
                return;
            }

            AssignedMobilePatientsByDepartment.Clear();
            sm_assignedDay = day;
        }

        private static int GetAssigned(object department)
        {
            int value;
            return department != null && AssignedMobilePatientsByDepartment.TryGetValue(department, out value) ? value : 0;
        }

        private static void IncrementAssigned(object department)
        {
            if (department == null)
            {
                return;
            }

            ResetAssignedCountsIfDayChanged();
            AssignedMobilePatientsByDepartment[department] = GetAssigned(department) + 1;
        }

        private static object GetSpawnedPatient(object insuranceCompany, int index, string listFieldName)
        {
            var list = ReflectionHelpers.GetField(insuranceCompany, listFieldName) as System.Collections.IList;
            if (list == null || index < 0 || index >= list.Count)
            {
                return null;
            }

            var slot = list[index];
            var pointer = ReflectionHelpers.GetField(slot, "m_patient");
            return ReflectionHelpers.ResolvePointer(pointer);
        }

        private static string GetDepartmentId(Department department)
        {
            var departmentType = department == null ? null : department.GetDepartmentType();
            return departmentType == null ? "unknown" : departmentType.DatabaseID.ToString();
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

    internal sealed class PatientGenerationContext
    {
        public bool Active;
        public int Index;
        public int PatientCounter;
        public PatientMobility Mobility;
        public bool SmoothDistribution;
        public float EstimatedVisitHour;
        public Department DynamicDepartment;
    }

    [HarmonyPatch]
    internal static class DynamicIntakeDepartmentChoicePatch
    {
        private static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Hospital), "GetRandomDepartmentWithDiagnoses", new[] { typeof(PatientMobility) });
        }

        private static bool Prefix(PatientMobility patientMobility, ref Department __result)
        {
            return IntakeControlService.TryChooseDynamicDepartment(patientMobility, ref __result);
        }
    }

    [HarmonyPatch]
    internal static class DynamicIntakePatientGenerationPatch
    {
        private static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(InsuranceCompany), "AddGeneratedPatient", new[] { typeof(int), typeof(int), typeof(PatientMobility), typeof(bool) });
        }

        private static void Prefix(int index, int patientCounter, PatientMobility mobility, bool smoothDistribution)
        {
            IntakeControlService.BeginPatientGeneration(index, patientCounter, mobility, smoothDistribution);
        }

        private static void Postfix(object __instance, int index, PatientMobility mobility)
        {
            IntakeControlService.TryApplyDirectProfileReferral(__instance, index, mobility);
        }

        private static void Finalizer()
        {
            IntakeControlService.EndPatientGeneration();
        }
    }

}
