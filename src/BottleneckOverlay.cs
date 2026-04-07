using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;

namespace ProjectHospital.AutoLabBalancer
{
    internal sealed class BottleneckSnapshot
    {
        public bool Ready;
        public string Warning;
        public int Departments;
        public int BusyDepartments;
        public int Patients;
        public int HighRiskPatients;
        public int PatientsWithPlannedMedication;
        public int Doctors;
        public int FreeDoctors;
        public int Nurses;
        public int FreeNurses;
        public int LabSpecialists;
        public int FreeLabSpecialists;
        public int Janitors;
        public int FreeJanitors;
        public int IdleLabProcedures;
        public int PlannedSurgeries;
        public int CriticalSurgeryPatients;
        public int WaitingSurgeryDepartments;
        public int SurgeryWaitingForRoom;
        public int SurgeryWaitingForStaff;
        public int SurgeryWaitingForTransport;
        public int SurgeryWaitingForCriticalPatients;
        public int WaitingForExamTransport;
        public int WaitingForTreatmentTransport;
        public int OutsideRoomChainedPatients;
        public int RadiologyPlannedExaminations;
        public int RadiologyCtExaminations;
        public int RadiologyMriExaminations;
        public int RadiologyXrayExaminations;
        public int RadiologyUsgExaminations;
        public int RadiologyAngioExaminations;
        public int CardiologyExaminations;
        public int NeurologyExaminations;
        public int HematologyExaminations;
        public int MicrobiologyExaminations;
        public int HistologyExaminations;
        public int OfficeExaminations;
        public int OtherExaminations;
        public int IntakeClinicPatients;
        public int IntakeClinicCapacity;
        public int IntakeAmbulancePatients;
        public int IntakeAmbulanceCapacity;
        public int IntakeOutpatientDoctorCapacity;
        public string SurgeryReadinessDetails;
    }

    internal static class BottleneckOverlayService
    {
        public static BottleneckSnapshot CreateSnapshot()
        {
            var snapshot = new BottleneckSnapshot();

            try
            {
                var hospital = Lopital.Hospital.Instance;
                if (hospital == null)
                {
                    snapshot.Warning = "Hospital.Instance is null.";
                    return snapshot;
                }

                snapshot.Ready = true;
                CountDepartments(hospital, snapshot);
                CountCharacters(hospital, snapshot);
                CountIdleLabProcedures(snapshot);
                CountIntake(snapshot);
            }
            catch (Exception ex)
            {
                snapshot.Warning = ex.GetType().Name + ": " + ex.Message;
            }

            return snapshot;
        }

        private static void CountDepartments(object hospital, BottleneckSnapshot snapshot)
        {
            foreach (var department in ReflectionHelpers.GetEnumerableField(hospital, "m_departments"))
            {
                snapshot.Departments++;
                if (IsDepartmentBusy(department))
                {
                    snapshot.BusyDepartments++;
                }

                if (ReflectionHelpers.InvokeBool(department, "HasWaitingSurgery"))
                {
                    snapshot.WaitingSurgeryDepartments++;
                }
            }
        }

        private static void CountCharacters(object hospital, BottleneckSnapshot snapshot)
        {
            foreach (var character in ReflectionHelpers.GetEnumerableField(hospital, "m_characters"))
            {
                var doctor = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.BehaviorDoctor");
                var nurse = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.BehaviorNurse");
                var lab = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.BehaviorLabSpecialist");
                var janitor = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.BehaviorJanitor");
                var patient = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.BehaviorPatient");

                if (doctor != null)
                {
                    snapshot.Doctors++;
                    if (IsFreeBehavior(doctor))
                    {
                        snapshot.FreeDoctors++;
                    }
                }

                if (nurse != null)
                {
                    snapshot.Nurses++;
                    if (IsFreeBehavior(nurse))
                    {
                        snapshot.FreeNurses++;
                    }
                }

                if (lab != null)
                {
                    snapshot.LabSpecialists++;
                    if (IsFreeBehavior(lab))
                    {
                        snapshot.FreeLabSpecialists++;
                    }
                }

                if (janitor != null)
                {
                    snapshot.Janitors++;
                    if (IsFreeBehavior(janitor))
                    {
                        snapshot.FreeJanitors++;
                    }
                }

                if (patient != null)
                {
                    snapshot.Patients++;
                    if (IsHighRiskPatient(character, patient))
                    {
                        snapshot.HighRiskPatients++;
                    }

                    if (HasPlannedMedication(character))
                    {
                        snapshot.PatientsWithPlannedMedication++;
                    }

                    if (HasPlannedSurgery(character))
                    {
                        snapshot.PlannedSurgeries++;
                        AddSurgeryReadinessDetails(hospital, character, snapshot);
                    }

                    CountPlannedExaminations(character, snapshot);

                    if (ReflectionHelpers.InvokeBool(patient, "HasCriticalSurgeryPlanned"))
                    {
                        snapshot.CriticalSurgeryPatients++;
                    }

                    CountHospitalizationStatus(character, snapshot);
                }
            }
        }

        private static void CountIdleLabProcedures(BottleneckSnapshot snapshot)
        {
            var manager = Lopital.LabProcedureManager.Instance;
            if (manager == null)
            {
                return;
            }

            foreach (var procedure in ReflectionHelpers.GetEnumerableField(manager, "m_labProcedures"))
            {
                if (ReflectionHelpers.InvokeBool(procedure, "IsIdle"))
                {
                    snapshot.IdleLabProcedures++;
                }
            }
        }

        private static bool IsFreeBehavior(object behavior)
        {
            if (behavior == null || !ReflectionHelpers.InvokeBool(behavior, "IsFree") || ReflectionHelpers.InvokeBool(behavior, "GetReserved"))
            {
                return false;
            }

            var employee = ReflectionHelpers.GetComponentByTypeName(GetEntityFromComponent(behavior), "Lopital.EmployeeComponent");
            return employee == null || !ReflectionHelpers.InvokeBool(employee, "IsPerformingAProcedure");
        }

        private static bool IsHighRiskPatient(object character, object behaviorPatient)
        {
            if (ReflectionHelpers.InvokeBool(behaviorPatient, "HasCriticalSurgeryPlanned"))
            {
                return true;
            }

            var hazard = InvokeObject(behaviorPatient, "GetWorstKnownHazard");
            if (hazard != null && string.Equals(hazard.ToString(), "High", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var hospitalization = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.HospitalizationComponent");
            return hospitalization != null && ReflectionHelpers.InvokeBool(hospitalization, "WillCollapse");
        }

        private static bool HasPlannedMedication(object character)
        {
            var procedureComponent = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.ProcedureComponent");
            var state = ReflectionHelpers.GetField(procedureComponent, "m_state");
            var queue = ReflectionHelpers.GetField(state, "m_procedureQueue");
            foreach (var treatmentState in ReflectionHelpers.GetEnumerableField(queue, "m_plannedTreatmentStates"))
            {
                var treatment = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(treatmentState, "m_treatment")) as GameDBTreatment;
                if (treatment != null && (treatment.TreatmentType == TreatmentType.PRESCRIPTION || treatment.TreatmentType == TreatmentType.RECEIPT))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasPlannedSurgery(object character)
        {
            return GetPlannedSurgeryTreatments(character).Count > 0;
        }

        private static List<GameDBTreatment> GetPlannedSurgeryTreatments(object character)
        {
            var result = new List<GameDBTreatment>();
            var procedureComponent = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.ProcedureComponent");
            var state = ReflectionHelpers.GetField(procedureComponent, "m_state");
            var queue = ReflectionHelpers.GetField(state, "m_procedureQueue");
            foreach (var treatmentState in ReflectionHelpers.GetEnumerableField(queue, "m_plannedTreatmentStates"))
            {
                var treatment = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(treatmentState, "m_treatment")) as GameDBTreatment;
                if (treatment != null && treatment.TreatmentType == TreatmentType.SURGERY)
                {
                    result.Add(treatment);
                }
            }

            return result;
        }

        private static void AddSurgeryReadinessDetails(object hospital, object patientEntity, BottleneckSnapshot snapshot)
        {
            if (CountLines(snapshot.SurgeryReadinessDetails) >= 6)
            {
                return;
            }

            var department = GetPatientDepartment(patientEntity);
            var surgeries = GetPlannedSurgeryTreatments(patientEntity);
            for (var i = 0; i < surgeries.Count && CountLines(snapshot.SurgeryReadinessDetails) < 6; i++)
            {
                var detail = BuildSurgeryReadinessLine(hospital, department, surgeries[i]);
                if (!string.IsNullOrEmpty(detail))
                {
                    snapshot.SurgeryReadinessDetails = AppendLine(snapshot.SurgeryReadinessDetails, detail);
                }
            }
        }

        private static string BuildSurgeryReadinessLine(object hospital, object department, GameDBTreatment treatment)
        {
            var procedure = treatment == null ? null : treatment.Procedure;
            if (procedure == null)
            {
                return null;
            }

            var shortages = new List<string>();
            AddRoleShortages(hospital, department, procedure.RequiredDoctorRoles, shortages);
            AddRoleShortages(hospital, department, procedure.RequiredNurseRoles, shortages);

            if (shortages.Count == 0)
            {
                return GetDatabaseId(treatment) + ": staff ready";
            }

            return GetDatabaseId(treatment) + ": missing " + string.Join(", ", shortages.ToArray());
        }

        private static void AddRoleShortages(object hospital, object department, IEnumerable roleRefs, List<string> shortages)
        {
            var required = new Dictionary<string, RoleRequirement>();
            if (roleRefs == null)
            {
                return;
            }

            foreach (var roleRef in roleRefs)
            {
                var role = ReflectionHelpers.ResolvePointer(roleRef);
                if (role == null)
                {
                    continue;
                }

                var id = GetDatabaseId(role);
                if (string.IsNullOrEmpty(id))
                {
                    id = role.ToString();
                }

                RoleRequirement requirement;
                if (!required.TryGetValue(id, out requirement))
                {
                    requirement = new RoleRequirement(role);
                    required[id] = requirement;
                }

                requirement.Required++;
            }

            foreach (var pair in required)
            {
                var ready = CountReadyEmployeesWithRole(hospital, department, pair.Value.Role);
                if (ready < pair.Value.Required)
                {
                    shortages.Add(pair.Key + " " + ready + "/" + pair.Value.Required);
                }
            }
        }

        private static int CountReadyEmployeesWithRole(object hospital, object department, object role)
        {
            var count = 0;
            foreach (var character in ReflectionHelpers.GetEnumerableField(hospital, "m_characters"))
            {
                var employee = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.EmployeeComponent");
                if (employee == null || !ReferenceEquals(GetEmployeeDepartment(employee), department))
                {
                    continue;
                }

                if (HasRole(employee, role) && IsEmployeeReadyNow(character, employee))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool HasRole(object employee, object role)
        {
            var method = employee == null || role == null ? null : employee.GetType().GetMethod("HasRole", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new[] { role.GetType() }, null);
            return method != null && Equals(method.Invoke(employee, new[] { role }), true);
        }

        private static bool IsEmployeeReadyNow(object character, object employee)
        {
            if (!ReflectionHelpers.InvokeBool(employee, "IsAvailable") || ReflectionHelpers.InvokeBool(employee, "IsPerformingAProcedure"))
            {
                return false;
            }

            var doctor = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.BehaviorDoctor");
            var nurse = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.BehaviorNurse");
            var lab = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.BehaviorLabSpecialist");
            var janitor = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.BehaviorJanitor");
            var behavior = doctor ?? nurse ?? lab ?? janitor;
            return behavior == null || IsFreeBehavior(behavior);
        }

        private static object GetPatientDepartment(object patientEntity)
        {
            var patient = ReflectionHelpers.GetComponentByTypeName(patientEntity, "Lopital.BehaviorPatient");
            var state = ReflectionHelpers.GetField(patient, "m_state");
            return ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_department"));
        }

        private static object GetEmployeeDepartment(object employee)
        {
            var state = ReflectionHelpers.GetField(employee, "m_state");
            return ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_department"));
        }

        private static string GetDatabaseId(object entry)
        {
            var id = ReflectionHelpers.GetStringProperty(entry, "_DatabaseIDSurrogate");
            if (!string.IsNullOrEmpty(id))
            {
                return id;
            }

            var property = entry == null ? null : AccessTools.Property(entry.GetType(), "DatabaseID");
            var value = property == null ? null : property.GetValue(entry, null);
            return value == null ? null : value.ToString();
        }

        private static string AppendLine(string existing, string line)
        {
            if (string.IsNullOrEmpty(existing))
            {
                return line;
            }

            return existing + "\n" + line;
        }

        private static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            var count = 1;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    count++;
                }
            }

            return count;
        }

        private static bool HasPlannedExamination(object character)
        {
            var procedureComponent = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.ProcedureComponent");
            var state = ReflectionHelpers.GetField(procedureComponent, "m_state");
            var queue = ReflectionHelpers.GetField(state, "m_procedureQueue");
            var planned = ReflectionHelpers.GetField(queue, "m_plannedExaminationStates") as IList;
            return planned != null && planned.Count > 0;
        }

        private static void CountPlannedExaminations(object character, BottleneckSnapshot snapshot)
        {
            var procedureComponent = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.ProcedureComponent");
            var state = ReflectionHelpers.GetField(procedureComponent, "m_state");
            var queue = ReflectionHelpers.GetField(state, "m_procedureQueue");
            foreach (var plannedState in ReflectionHelpers.GetEnumerableField(queue, "m_plannedExaminationStates"))
            {
                var examination = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(plannedState, "m_examination")) as GameDBExamination;
                var type = GetExaminationType(examination);
                if (type == ExaminationType.None)
                {
                    continue;
                }

                if (IsImagingType(type))
                {
                    snapshot.RadiologyPlannedExaminations++;
                }

                if (type == ExaminationType.Ct)
                {
                    snapshot.RadiologyCtExaminations++;
                }
                else if (type == ExaminationType.Mri)
                {
                    snapshot.RadiologyMriExaminations++;
                }
                else if (type == ExaminationType.Xray)
                {
                    snapshot.RadiologyXrayExaminations++;
                }
                else if (type == ExaminationType.Usg)
                {
                    snapshot.RadiologyUsgExaminations++;
                }
                else if (type == ExaminationType.Angio)
                {
                    snapshot.RadiologyAngioExaminations++;
                }
                else if (type == ExaminationType.Cardiology)
                {
                    snapshot.CardiologyExaminations++;
                }
                else if (type == ExaminationType.Neurology)
                {
                    snapshot.NeurologyExaminations++;
                }
                else if (type == ExaminationType.Hematology)
                {
                    snapshot.HematologyExaminations++;
                }
                else if (type == ExaminationType.Microbiology)
                {
                    snapshot.MicrobiologyExaminations++;
                }
                else if (type == ExaminationType.Histology)
                {
                    snapshot.HistologyExaminations++;
                }
                else if (type == ExaminationType.Office)
                {
                    snapshot.OfficeExaminations++;
                }
                else
                {
                    snapshot.OtherExaminations++;
                }
            }
        }

        private static ExaminationType GetExaminationType(GameDBExamination examination)
        {
            var id = GetDatabaseId(examination);
            if (string.IsNullOrEmpty(id))
            {
                return ExaminationType.None;
            }

            if (id.StartsWith("EXM_CT", StringComparison.OrdinalIgnoreCase))
            {
                return ExaminationType.Ct;
            }

            if (id.StartsWith("EXM_MRI", StringComparison.OrdinalIgnoreCase))
            {
                return ExaminationType.Mri;
            }

            if (id.IndexOf("X_RAY", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ExaminationType.Xray;
            }

            if (id == "EXM_USG" || id == "EXM_FAST" || id.IndexOf("USG", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ExaminationType.Usg;
            }

            if (id.IndexOf("ANGIO", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ExaminationType.Angio;
            }

            if (id == "EXM_ECG" || id == "EXM_ECHO" || id == "EXM_CC_ECHO" || id == "EXM_HEART_MONITORING" || id == "EXM_URGENT_BLOOD_ANALYSIS")
            {
                return ExaminationType.Cardiology;
            }

            if (id == "EXM_EEG" || id == "EXM_EMG" || id == "EXM_PERIMETRY" || id == "EXM_TONOMETRY")
            {
                return ExaminationType.Neurology;
            }

            if (ContainsAny(id, "BLOOD", "CBC", "PCR", "SEROLOGY", "ELISA", "FECAL_ELASTASE"))
            {
                return ExaminationType.Hematology;
            }

            if (ContainsAny(id, "BACTERIA", "FUNGAL"))
            {
                return ExaminationType.Microbiology;
            }

            if (ContainsAny(id, "BIOPSY", "SPINAL_FLUID", "STOOL_ANALYSIS", "URINE_SAMPLE", "PERITONEAL_FLUID"))
            {
                return ExaminationType.Histology;
            }

            if (ContainsAny(id, "INTERVIEW", "OBSERVATION", "PALPATION", "LISTENING", "EXAMINATION", "EVALUATION", "TEMPERATURE", "PRESSURE", "PULSE", "SPIROMETRY", "AUDIOMETRY", "GASTROSCOPY", "LARYNGOSCOPY"))
            {
                return ExaminationType.Office;
            }

            return ExaminationType.Other;
        }

        private static bool ContainsAny(string text, params string[] values)
        {
            for (var i = 0; i < values.Length; i++)
            {
                if (text.IndexOf(values[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsImagingType(ExaminationType type)
        {
            return type == ExaminationType.Ct
                || type == ExaminationType.Mri
                || type == ExaminationType.Xray
                || type == ExaminationType.Usg
                || type == ExaminationType.Angio;
        }

        private static void CountIntake(BottleneckSnapshot snapshot)
        {
            var intake = IntakeControlService.CreateSnapshot();
            snapshot.IntakeClinicPatients = intake.CurrentClinicPatients;
            snapshot.IntakeClinicCapacity = intake.ClinicCapacity;
            snapshot.IntakeAmbulancePatients = intake.CurrentAmbulancePatients;
            snapshot.IntakeAmbulanceCapacity = intake.AmbulanceCapacity;
            snapshot.IntakeOutpatientDoctorCapacity = intake.OutpatientDoctors;
        }

        private static void CountHospitalizationStatus(object character, BottleneckSnapshot snapshot)
        {
            var hospitalization = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.HospitalizationComponent");
            var state = ReflectionHelpers.GetField(hospitalization, "m_state");
            var status = ReflectionHelpers.GetField(state, "m_procedureReservationStatus");
            if (status != null)
            {
                var name = status.ToString();
                if (string.Equals(name, "WAITING_FOR_ROOM_SURG", StringComparison.OrdinalIgnoreCase))
                {
                    snapshot.SurgeryWaitingForRoom++;
                }
                else if (string.Equals(name, "WAITING_FOR_STAFF_SURG", StringComparison.OrdinalIgnoreCase))
                {
                    snapshot.SurgeryWaitingForStaff++;
                }
                else if (string.Equals(name, "WAITING_FOR_TRANSPORT_SURG", StringComparison.OrdinalIgnoreCase))
                {
                    snapshot.SurgeryWaitingForTransport++;
                }
                else if (string.Equals(name, "WAITING_FOR_CRITICAL_PATIENTS", StringComparison.OrdinalIgnoreCase))
                {
                    snapshot.SurgeryWaitingForCriticalPatients++;
                }
                else if (string.Equals(name, "WAITING_FOR_TRANSPORT_EXM", StringComparison.OrdinalIgnoreCase))
                {
                    snapshot.WaitingForExamTransport++;
                }
                else if (string.Equals(name, "WAITING_FOR_TRANSPORT_TRT", StringComparison.OrdinalIgnoreCase))
                {
                    snapshot.WaitingForTreatmentTransport++;
                }
            }

            var outsideRoom = ReflectionHelpers.GetField(state, "m_oustideRoom");
            if (outsideRoom is bool && (bool)outsideRoom && HasPlannedExamination(character))
            {
                snapshot.OutsideRoomChainedPatients++;
            }
        }

        private static bool IsDepartmentBusy(object department)
        {
            return ReflectionHelpers.InvokeBool(department, "HasAnyCriticalPatients")
                || ReflectionHelpers.InvokeBool(department, "HasWaitingSurgery")
                || ReflectionHelpers.InvokeBool(department, "HasAnyCriticalSurgeryScheduled")
                || ReflectionHelpers.InvokeBool(department, "HasAnyHospitalizedPatientsWithScheduledProcedures")
                || ReflectionHelpers.InvokeBool(department, "HasAnyWaitingPatients");
        }

        private static object GetEntityFromComponent(object component)
        {
            return ReflectionHelpers.GetField(component, "m_entity");
        }

        private static object InvokeObject(object instance, string methodName)
        {
            var method = instance == null ? null : AccessTools.Method(instance.GetType(), methodName);
            return method == null ? null : method.Invoke(instance, null);
        }

        private sealed class RoleRequirement
        {
            public readonly object Role;
            public int Required;

            public RoleRequirement(object role)
            {
                Role = role;
            }
        }

        private enum ExaminationType
        {
            None,
            Ct,
            Mri,
            Xray,
            Usg,
            Angio,
            Cardiology,
            Neurology,
            Hematology,
            Microbiology,
            Histology,
            Office,
            Other
        }
    }
}
