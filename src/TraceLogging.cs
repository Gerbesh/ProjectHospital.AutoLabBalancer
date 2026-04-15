using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;
using Lopital;
using UnityEngine;

namespace ProjectHospital.AutoLabBalancer
{
    internal static class TraceLoggingService
    {
        private sealed class CaseTraceSnapshot
        {
            public string Scope;
            public string Payload;
        }

        internal sealed class CaseTraceQueueState
        {
            public string ActiveExaminationId;
            public readonly List<string> PlannedExaminationIds = new List<string>();
            public readonly List<string> ActiveTreatmentIds = new List<string>();
            public readonly List<string> PlannedTreatmentIds = new List<string>();
            public readonly List<string> LabProcedureIds = new List<string>();
        }

        private sealed class CaseTraceDecision
        {
            public string MethodName;
            public bool Result;
            public string BlockerOrReason;
            public string Details;
        }

        private sealed class CaseTraceAction
        {
            public string MethodName;
            public string Outcome;
            public string Details;
        }

        private const float PollIntervalSeconds = 0.10f;
        private const float FlushIntervalSeconds = 0.50f;

        private static readonly Dictionary<uint, string> PatientSnapshots = new Dictionary<uint, string>();
        private static readonly Dictionary<uint, string> DoctorSnapshots = new Dictionary<uint, string>();
        private static readonly Dictionary<uint, string> NurseSnapshots = new Dictionary<uint, string>();
        private static readonly Dictionary<uint, string> LabSnapshots = new Dictionary<uint, string>();
        private static readonly Dictionary<uint, string> JanitorSnapshots = new Dictionary<uint, string>();
        private static readonly Dictionary<string, string> UiSnapshots = new Dictionary<string, string>();
        private static readonly Dictionary<string, float> RateLimitTimestamps = new Dictionary<string, float>();

        private static StreamWriter _writer;
        private static string _currentScopeKey;
        private static string _currentLogPath;
        private static string _sessionStamp;
        private static float _nextPollAt;
        private static float _nextFlushAt;
        private static bool _failedToOpenWriter;

        public static string CurrentLogPath
        {
            get { return _currentLogPath ?? string.Empty; }
        }

        private static bool Enabled
        {
            get
            {
                return RuntimeSettings.Config != null
                    && RuntimeSettings.Config.Enabled.Value
                    && RuntimeSettings.Config.EnableDeepTraceLog.Value;
            }
        }

        public static void Tick(float now)
        {
            if (!Enabled)
            {
                Shutdown();
                return;
            }

            EnsureWriter();
            if (_writer == null || now < _nextPollAt)
            {
                return;
            }

            _nextPollAt = now + PollIntervalSeconds;
            ScanHospital();

            if (now >= _nextFlushAt)
            {
                _nextFlushAt = now + FlushIntervalSeconds;
                TryFlush();
            }
        }

        public static void LogCaseTimeline(PatientCase patientCase, string text)
        {
            if (!Enabled || patientCase == null || string.IsNullOrEmpty(text))
            {
                return;
            }

            EnsureWriter();
            if (_writer == null)
            {
                return;
            }

            WriteLine("CASE", patientCase.PatientEntityId, patientCase.PatientName, text);
        }

        public static void LogPatientEvent(BehaviorPatient patient, string category, string text)
        {
            if (!Enabled || patient == null || string.IsNullOrEmpty(text))
            {
                return;
            }

            var entity = GetEntityFromObject(patient);
            var entityId = entity == null ? 0u : entity.GetEntityID();
            WriteLine(category, entityId, GetEntityName(entity), text);
        }

        public static void LogRateLimitedPatientEvent(BehaviorPatient patient, string category, string text, float minIntervalSeconds)
        {
            if (!Enabled || patient == null || string.IsNullOrEmpty(text))
            {
                return;
            }

            var entity = GetEntityFromObject(patient);
            var entityId = entity == null ? 0u : entity.GetEntityID();
            var key = category + "|" + entityId + "|" + text;
            var now = Time.realtimeSinceStartup;
            float previous;
            if (RateLimitTimestamps.TryGetValue(key, out previous) && now - previous < minIntervalSeconds)
            {
                return;
            }

            RateLimitTimestamps[key] = now;
            WriteLine(category, entityId, GetEntityName(entity), text);
        }

        public static void LogPatientDecision(BehaviorPatient patient, string methodName, bool result, string blockerOrReason, string details)
        {
            if (!Enabled || patient == null || string.IsNullOrEmpty(methodName))
            {
                return;
            }

            var decision = new CaseTraceDecision
            {
                MethodName = methodName,
                Result = result,
                BlockerOrReason = blockerOrReason,
                Details = details
            };

            var entity = GetEntityFromObject(patient);
            WriteLine("DECISION", entity == null ? 0u : entity.GetEntityID(), GetEntityName(entity), FormatDecision(decision));
        }

        public static void LogPatientAction(BehaviorPatient patient, string methodName, string outcome, string details)
        {
            if (!Enabled || patient == null || string.IsNullOrEmpty(methodName))
            {
                return;
            }

            var action = new CaseTraceAction
            {
                MethodName = methodName,
                Outcome = outcome,
                Details = details
            };

            var entity = GetEntityFromObject(patient);
            WriteLine("ACTION", entity == null ? 0u : entity.GetEntityID(), GetEntityName(entity), FormatAction(action));
        }

        public static void LogPatientAnomaly(BehaviorPatient patient, string anomalyName, string details)
        {
            if (!Enabled || patient == null || string.IsNullOrEmpty(anomalyName))
            {
                return;
            }

            var entity = GetEntityFromObject(patient);
            WriteLine("ANOMALY", entity == null ? 0u : entity.GetEntityID(), GetEntityName(entity), AppendDetails(BuildStructuredPayload("event", "anomaly", "kind", anomalyName), details));
        }

        public static void LogCaseAnomaly(PatientCase patientCase, string anomalyName, string details)
        {
            if (!Enabled || patientCase == null || string.IsNullOrEmpty(anomalyName))
            {
                return;
            }

            WriteLine("ANOMALY", patientCase.PatientEntityId, patientCase.PatientName, AppendDetails(BuildStructuredPayload("event", "anomaly", "kind", anomalyName), details));
        }

        public static void LogQueueEvent(BehaviorPatient patient, string queueEventName, object staff, string queueItemId, string sourceMethod, string reason, string outcome)
        {
            if (!Enabled || patient == null || string.IsNullOrEmpty(queueEventName))
            {
                return;
            }

            var entity = GetEntityFromObject(patient);
            var staffEntity = GetEntityFromObject(staff);
            var details = new StringBuilder(192);
            AppendField(details, "staff_id", staffEntity == null ? "-" : staffEntity.GetEntityID().ToString(CultureInfo.InvariantCulture));
            AppendField(details, "staff_name", staffEntity == null ? "-" : GetEntityName(staffEntity));
            AppendField(details, "queue_item_id", queueItemId);
            AppendField(details, "source_method", sourceMethod);
            AppendField(details, "reason", reason);
            AppendField(details, "outcome", outcome);
            WriteLine("ACTION", entity == null ? 0u : entity.GetEntityID(), GetEntityName(entity), AppendDetails(BuildStructuredPayload("event", "queue", "kind", queueEventName), details.ToString()));
        }

        public static void LogUiSnapshot(BehaviorPatient patient, string source)
        {
            if (!Enabled || patient == null || string.IsNullOrEmpty(source))
            {
                return;
            }

            var entity = GetEntityFromObject(patient);
            if (entity == null)
            {
                return;
            }

            var snapshotPayload = BuildUiSnapshot(patient, source);
            if (string.IsNullOrEmpty(snapshotPayload))
            {
                return;
            }

            var storageKey = source + "|" + entity.GetEntityID().ToString(CultureInfo.InvariantCulture);
            string previous;
            if (UiSnapshots.TryGetValue(storageKey, out previous) && string.Equals(previous, snapshotPayload, StringComparison.Ordinal))
            {
                return;
            }

            UiSnapshots[storageKey] = snapshotPayload;
            var snapshot = new CaseTraceSnapshot
            {
                Scope = "ui",
                Payload = snapshotPayload
            };
            WriteLine("SNAPSHOT", entity.GetEntityID(), GetEntityName(entity), FormatSnapshot(snapshot, "STATE"));

            var uiDesyncReason = MedicalCaseRewriteService.GetUiRuntimeDesyncReason(patient);
            if (!string.IsNullOrEmpty(uiDesyncReason))
            {
                LogPatientAnomaly(patient, "ui_runtime_desync", "source=" + NormalizeValue(source) + ";reason=" + NormalizeValue(uiDesyncReason));
            }
        }

        internal static CaseTraceQueueState CaptureQueueState(BehaviorPatient patient)
        {
            if (patient == null)
            {
                return null;
            }

            var procedure = patient.GetComponent<ProcedureComponent>();
            var queue = procedure == null || procedure.m_state == null ? null : procedure.m_state.m_procedureQueue;
            if (queue == null)
            {
                return null;
            }

            var state = new CaseTraceQueueState();
            state.ActiveExaminationId = SafeDatabaseId(ReflectionHelpers.ResolvePointer(queue.m_activeExamination) as GameDBExamination);
            CopyIdentifiers(queue.m_plannedExaminations, null, state.PlannedExaminationIds);
            CopyIdentifiers(queue.m_activeTreatmentStates, "m_treatment", state.ActiveTreatmentIds);
            CopyIdentifiers(queue.m_plannedTreatmentStates, "m_treatment", state.PlannedTreatmentIds);
            CopyLabIdentifiers(queue.m_labProcedures, state.LabProcedureIds);
            return state;
        }

        internal static void LogQueueDiff(BehaviorPatient patient, CaseTraceQueueState before, CaseTraceQueueState after, string sourceMethod, string reason)
        {
            if (!Enabled || patient == null)
            {
                return;
            }

            LogListDiff(patient, before == null ? null : before.PlannedExaminationIds, after == null ? null : after.PlannedExaminationIds, "queue_exam_add", "queue_exam_remove", sourceMethod, reason);
            LogListDiff(patient, before == null ? null : before.PlannedTreatmentIds, after == null ? null : after.PlannedTreatmentIds, "queue_treatment_add", "queue_treatment_remove", sourceMethod, reason);
            LogListDiff(patient, before == null ? null : before.LabProcedureIds, after == null ? null : after.LabProcedureIds, "queue_lab_add", "queue_lab_remove", sourceMethod, reason);
        }

        public static void LogEntityEvent(object subject, string category, string text)
        {
            if (!Enabled || subject == null || string.IsNullOrEmpty(text))
            {
                return;
            }

            var entity = GetEntityFromObject(subject);
            var entityId = entity == null ? 0u : entity.GetEntityID();
            WriteLine(category, entityId, GetEntityName(entity), text);
        }

        private static void ScanHospital()
        {
            var hospital = Hospital.Instance;
            if (hospital == null)
            {
                return;
            }

            var seenPatients = new HashSet<uint>();
            var seenDoctors = new HashSet<uint>();
            var seenNurses = new HashSet<uint>();
            var seenLabs = new HashSet<uint>();
            var seenJanitors = new HashSet<uint>();

            foreach (var character in ReflectionHelpers.GetEnumerableField(hospital, "m_characters"))
            {
                var entity = character as GLib.Entity;
                if (entity == null)
                {
                    continue;
                }

                var patient = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.BehaviorPatient") as BehaviorPatient;
                if (patient != null)
                {
                    TrackSnapshot(PatientSnapshots, seenPatients, entity.GetEntityID(), GetEntityName(entity), "patient", BuildPatientSnapshot(patient));
                }

                var doctor = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.BehaviorDoctor");
                if (doctor != null)
                {
                    TrackSnapshot(DoctorSnapshots, seenDoctors, entity.GetEntityID(), GetEntityName(entity), "doctor", BuildStaffSnapshot(doctor));
                }

                var nurse = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.BehaviorNurse");
                if (nurse != null)
                {
                    TrackSnapshot(NurseSnapshots, seenNurses, entity.GetEntityID(), GetEntityName(entity), "nurse", BuildStaffSnapshot(nurse));
                }

                var lab = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.BehaviorLabSpecialist");
                if (lab != null)
                {
                    TrackSnapshot(LabSnapshots, seenLabs, entity.GetEntityID(), GetEntityName(entity), "lab_specialist", BuildStaffSnapshot(lab));
                }

                var janitor = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.BehaviorJanitor");
                if (janitor != null)
                {
                    TrackSnapshot(JanitorSnapshots, seenJanitors, entity.GetEntityID(), GetEntityName(entity), "janitor", BuildStaffSnapshot(janitor));
                }
            }

            RemoveMissingSnapshots(PatientSnapshots, seenPatients, "patient");
            RemoveMissingSnapshots(DoctorSnapshots, seenDoctors, "doctor");
            RemoveMissingSnapshots(NurseSnapshots, seenNurses, "nurse");
            RemoveMissingSnapshots(LabSnapshots, seenLabs, "lab_specialist");
            RemoveMissingSnapshots(JanitorSnapshots, seenJanitors, "janitor");
        }

        private static void TrackSnapshot(Dictionary<uint, string> storage, HashSet<uint> seen, uint entityId, string name, string scope, string snapshotPayload)
        {
            if (entityId == 0 || string.IsNullOrEmpty(snapshotPayload))
            {
                return;
            }

            seen.Add(entityId);
            string previous;
            if (storage.TryGetValue(entityId, out previous))
            {
                if (string.Equals(previous, snapshotPayload, StringComparison.Ordinal))
                {
                    return;
                }

                storage[entityId] = snapshotPayload;
                WriteLine("SNAPSHOT", entityId, name, FormatSnapshot(new CaseTraceSnapshot { Scope = scope, Payload = snapshotPayload }, "STATE"));
                return;
            }

            storage[entityId] = snapshotPayload;
            WriteLine("SNAPSHOT", entityId, name, FormatSnapshot(new CaseTraceSnapshot { Scope = scope, Payload = snapshotPayload }, "SPAWN"));
        }

        private static void RemoveMissingSnapshots(Dictionary<uint, string> storage, HashSet<uint> seen, string scope)
        {
            if (storage.Count == 0)
            {
                return;
            }

            var removed = new List<uint>();
            foreach (var pair in storage)
            {
                if (!seen.Contains(pair.Key))
                {
                    removed.Add(pair.Key);
                }
            }

            for (var i = 0; i < removed.Count; i++)
            {
                var entityId = removed[i];
                storage.Remove(entityId);
                WriteLine("SNAPSHOT", entityId, "unknown", FormatSnapshot(new CaseTraceSnapshot { Scope = scope, Payload = string.Empty }, "DESPAWN"));
            }
        }

        private static string BuildPatientSnapshot(BehaviorPatient patient)
        {
            var entity = GetEntityFromObject(patient);
            var state = patient.m_state;
            if (state == null)
            {
                return BuildStructuredPayload("entity_type", "patient", "patient_state", "<null>", "patient_id", entity == null ? "0" : entity.GetEntityID().ToString(CultureInfo.InvariantCulture));
            }

            var procedure = entity == null ? null : ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.ProcedureComponent");
            var queue = GetProcedureQueue(procedure);
            var medicalCondition = ReflectionHelpers.GetField(state, "m_medicalCondition") as MedicalCondition;
            var diagnosedPointer = medicalCondition == null ? null : ReflectionHelpers.GetField(medicalCondition, "m_diagnosedMedicalCondition");
            var diagnosedCondition = ReflectionHelpers.ResolvePointer(diagnosedPointer) as GameDBMedicalCondition;
            var currentCondition = medicalCondition == null || medicalCondition.m_gameDBMedicalCondition == null
                ? null
                : medicalCondition.m_gameDBMedicalCondition.Entry;
            var doctor = ReflectionHelpers.GetField(state, "m_doctor");
            var builder = new StringBuilder(768);
            AppendField(builder, "entity_type", "patient");
            AppendField(builder, "patient_id", entity == null ? "0" : entity.GetEntityID().ToString(CultureInfo.InvariantCulture));
            AppendField(builder, "patient_name", entity == null ? "-" : GetEntityName(entity));
            AppendField(builder, "patient_state", GetPrimaryStateName(patient));
            AppendField(builder, "department", GetPatientDepartmentId(patient));
            AppendField(builder, "active_department", MedicalCaseRewriteService.GetActiveDepartmentIdForTrace(patient));
            AppendField(builder, "doctor", DescribeLinkedObject(doctor));
            AppendField(builder, "compatibility_condition_id", currentCondition == null ? "-" : currentCondition.DatabaseID.ToString());
            AppendField(builder, "compatibility_diagnosed_condition_id", diagnosedCondition == null ? "-" : diagnosedCondition.DatabaseID.ToString());
            AppendField(builder, "sent_home", BoolFlag(ReflectionHelpers.GetField(state, "m_sentHome")));
            AppendField(builder, "sent_away", BoolFlag(ReflectionHelpers.GetField(state, "m_sentAway")));
            AppendField(builder, "death_triggered", BoolFlag(ReflectionHelpers.GetField(state, "m_deathTriggered")));
            AppendField(builder, "queue_summary", DescribeProcedureQueue(queue));
            AppendRawField(builder, "queue_detail", "{" + BuildDetailedQueueSnapshot(patient) + "}");
            AppendField(builder, "case_summary", MedicalCaseRewriteService.GetTraceSummary(patient));
            AppendRawField(builder, "case_detail", "{" + BuildDetailedCaseSnapshot(patient) + "}");
            return builder.ToString();
        }

        private static string BuildStaffSnapshot(object behavior)
        {
            var entity = GetEntityFromObject(behavior);
            var employee = entity == null ? null : ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.EmployeeComponent");
            var builder = new StringBuilder(256);
            AppendField(builder, "entity_type", GetBehaviorTypeName(behavior));
            AppendField(builder, "staff_id", entity == null ? "0" : entity.GetEntityID().ToString(CultureInfo.InvariantCulture));
            AppendField(builder, "staff_name", entity == null ? "-" : GetEntityName(entity));
            AppendField(builder, "staff_state", GetPrimaryStateName(behavior));
            AppendField(builder, "department", GetBehaviorDepartmentId(behavior));
            AppendField(builder, "is_free", ReflectionHelpers.InvokeBool(behavior, "IsFree").ToString());
            AppendField(builder, "reserved", ReflectionHelpers.InvokeBool(behavior, "GetReserved").ToString());
            AppendField(builder, "is_available", (employee == null || ReflectionHelpers.InvokeBool(employee, "IsAvailable")).ToString());
            AppendField(builder, "is_performing_procedure", (employee != null && ReflectionHelpers.InvokeBool(employee, "IsPerformingAProcedure")).ToString());
            AppendField(builder, "current_patient", DescribeLinkedObject(GetCurrentPatient(behavior)));
            return builder.ToString();
        }

        private static object GetProcedureQueue(object procedure)
        {
            var state = ReflectionHelpers.GetField(procedure, "m_state");
            return ReflectionHelpers.GetField(state, "m_procedureQueue");
        }

        private static string BuildDetailedCaseSnapshot(BehaviorPatient patient)
        {
            return MedicalCaseRewriteService.BuildDetailedCaseTrace(patient);
        }

        private static string BuildDetailedQueueSnapshot(BehaviorPatient patient)
        {
            var queueState = CaptureQueueState(patient);
            var procedure = patient == null ? null : patient.GetComponent<ProcedureComponent>();
            var reservedProcedure = procedure == null || procedure.m_state == null || procedure.m_state.m_reservedProcedureScript == null || !procedure.m_state.m_reservedProcedureScript.CheckEntity()
                ? "-"
                : procedure.m_state.m_reservedProcedureScript.GetEntity().GetType().Name;
            var requestedHospitalization = MedicalCaseRewriteService.GetRequestedHospitalizationTreatmentIdForTrace(patient);
            var builder = new StringBuilder(320);
            AppendField(builder, "active_examination", queueState == null ? "-" : queueState.ActiveExaminationId);
            AppendField(builder, "planned_examinations", FormatList(queueState == null ? null : queueState.PlannedExaminationIds));
            AppendField(builder, "active_treatments", FormatList(queueState == null ? null : queueState.ActiveTreatmentIds));
            AppendField(builder, "planned_treatments", FormatList(queueState == null ? null : queueState.PlannedTreatmentIds));
            AppendField(builder, "lab_queue", FormatList(queueState == null ? null : queueState.LabProcedureIds));
            AppendField(builder, "reserved_procedure_script", reservedProcedure);
            AppendField(builder, "hospitalization_treatment", requestedHospitalization);
            return builder.ToString();
        }

        private static string BuildUiSnapshot(BehaviorPatient patient, string source)
        {
            return MedicalCaseRewriteService.BuildUiTraceSnapshot(patient, source);
        }

        private static string DescribeProcedureQueue(object queue)
        {
            if (queue == null)
            {
                return "examA0/examP0/lab0/treatA0/treatP0";
            }

            var activeExam = ReflectionHelpers.GetField(queue, "m_activeExamination");
            var plannedExam = ReflectionHelpers.GetField(queue, "m_plannedExaminationStates") as IList;
            var lab = ReflectionHelpers.GetField(queue, "m_labProcedures") as IList;
            var activeTreatment = ReflectionHelpers.GetField(queue, "m_activeTreatmentStates") as IList;
            var plannedTreatment = ReflectionHelpers.GetField(queue, "m_plannedTreatmentStates") as IList;
            return string.Format(CultureInfo.InvariantCulture,
                "examA{0}/examP{1}/lab{2}/treatA{3}/treatP{4}",
                activeExam == null ? 0 : 1,
                plannedExam == null ? 0 : plannedExam.Count,
                lab == null ? 0 : lab.Count,
                activeTreatment == null ? 0 : activeTreatment.Count,
                plannedTreatment == null ? 0 : plannedTreatment.Count);
        }

        private static string GetBehaviorTypeName(object behavior)
        {
            if (behavior == null)
            {
                return "unknown";
            }

            var typeName = behavior.GetType().Name;
            if (typeName.IndexOf("Doctor", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "doctor";
            }

            if (typeName.IndexOf("Nurse", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "nurse";
            }

            if (typeName.IndexOf("Lab", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "lab_specialist";
            }

            if (typeName.IndexOf("Janitor", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "janitor";
            }

            return typeName;
        }

        private static object GetCurrentPatient(object behavior)
        {
            if (behavior == null)
            {
                return null;
            }

            var property = behavior.GetType().GetProperty("CurrentPatient", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null)
            {
                try
                {
                    return property.GetValue(behavior, null);
                }
                catch
                {
                }
            }

            return ReflectionHelpers.GetField(behavior, "CurrentPatient");
        }

        private static string DescribeLinkedObject(object value)
        {
            var entity = GetEntityFromObject(value);
            if (entity == null)
            {
                return "-";
            }

            return GetEntityName(entity) + "#" + entity.GetEntityID().ToString(CultureInfo.InvariantCulture);
        }

        private static string GetPatientDepartmentId(BehaviorPatient patient)
        {
            if (patient == null)
            {
                return "-";
            }

            var department = patient.GetDepartment();
            if (department == null)
            {
                return "-";
            }

            var type = department.GetDepartmentType();
            return type == null ? department.Name : type.DatabaseID.ToString();
        }

        private static string GetBehaviorDepartmentId(object behavior)
        {
            if (behavior is BehaviorPatient)
            {
                return GetPatientDepartmentId((BehaviorPatient)behavior);
            }

            var entity = GetEntityFromObject(behavior);
            var employee = entity == null ? null : ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.EmployeeComponent");
            var state = ReflectionHelpers.GetField(employee, "m_state");
            var department = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_department")) as Department;
            if (department == null)
            {
                return "-";
            }

            var type = department.GetDepartmentType();
            return type == null ? department.Name : type.DatabaseID.ToString();
        }

        private static string GetPrimaryStateName(object behavior)
        {
            var state = ReflectionHelpers.GetField(behavior, "m_state");
            if (state == null)
            {
                return "<null>";
            }

            foreach (var fieldName in new[] { "m_patientState", "m_doctorState", "m_nurseState", "m_labSpecialistState", "m_janitorState", "m_paramedicState" })
            {
                var value = ReflectionHelpers.GetField(state, fieldName);
                if (value != null)
                {
                    return value.ToString();
                }
            }

            return state.GetType().Name;
        }

        private static GLib.Entity GetEntityFromObject(object value)
        {
            if (value == null)
            {
                return null;
            }

            var entity = value as GLib.Entity;
            if (entity != null)
            {
                return entity;
            }

            var resolved = ReflectionHelpers.ResolvePointer(value) as GLib.Entity;
            if (resolved != null)
            {
                return resolved;
            }

            return ReflectionHelpers.GetField(value, "m_entity") as GLib.Entity;
        }

        private static string GetEntityName(GLib.Entity entity)
        {
            if (entity == null)
            {
                return "unknown";
            }

            var name = ReflectionHelpers.GetStringProperty(entity, "Name");
            if (string.IsNullOrEmpty(name))
            {
                name = ReflectionHelpers.GetField(entity, "m_name") as string;
            }

            if (string.IsNullOrEmpty(name))
            {
                name = entity.ToString();
            }

            return Sanitize(name);
        }

        private static string BoolFlag(object value)
        {
            return Equals(value, true) ? "1" : "0";
        }

        private static void WriteLine(string category, uint entityId, string name, string text)
        {
            if (!Enabled || string.IsNullOrEmpty(text))
            {
                return;
            }

            EnsureWriter();
            if (_writer == null)
            {
                return;
            }

            try
            {
                var day = DayTime.Instance == null ? 0 : DayTime.Instance.GetDay();
                var hour = DayTime.Instance == null ? 0f : DayTime.Instance.GetDayTimeHours();
                _writer.WriteLine(
                    "[{0}][D{1} {2:0.0}][F{3}][{4}][{5}:{6}] {7}",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    day,
                    hour,
                    Time.frameCount,
                    category,
                    entityId.ToString(CultureInfo.InvariantCulture),
                    string.IsNullOrEmpty(name) ? "unknown" : Sanitize(name),
                    Sanitize(text));
            }
            catch (Exception ex)
            {
                FailWriter(ex);
            }
        }

        private static void EnsureWriter()
        {
            var scopeKey = RuntimeSettings.SaveSettings != null && RuntimeSettings.SaveSettings.HasActiveScope
                ? RuntimeSettings.SaveSettings.ScopeIdentifier
                : "global";
            if (_writer != null && string.Equals(scopeKey, _currentScopeKey, StringComparison.Ordinal))
            {
                return;
            }

            Shutdown();
            _currentScopeKey = scopeKey;
            _failedToOpenWriter = false;
            _sessionStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

            try
            {
                var displayName = RuntimeSettings.SaveSettings != null && RuntimeSettings.SaveSettings.HasActiveScope
                    ? RuntimeSettings.SaveSettings.ScopeDisplayName
                    : "global";
                var dir = Path.Combine(Paths.ConfigPath, "AutoLabBalancer.TraceLogs");
                Directory.CreateDirectory(dir);
                _currentLogPath = Path.Combine(dir, MakeSafeFileName(displayName) + "-" + _sessionStamp + ".trace.log");
                _writer = new StreamWriter(_currentLogPath, true, new UTF8Encoding(false));
                _writer.AutoFlush = false;
                _nextPollAt = 0f;
                _nextFlushAt = 0f;
                ClearSnapshots();
                _writer.WriteLine("# AutoLabBalancer deep trace");
                _writer.WriteLine("# Started=" + DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
                _writer.WriteLine("# Scope=" + displayName);
                _writer.WriteLine("# Identifier=" + scopeKey);
                _writer.WriteLine("# Session=" + _sessionStamp);
                _writer.WriteLine();
                TryFlush();
            }
            catch (Exception ex)
            {
                FailWriter(ex);
            }
        }

        private static void FailWriter(Exception ex)
        {
            if (_failedToOpenWriter)
            {
                return;
            }

            _failedToOpenWriter = true;
            Shutdown();
            if (RuntimeSettings.Logger != null)
            {
                RuntimeSettings.Logger.LogError("[TraceLogging] Failed to open/write trace log: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void TryFlush()
        {
            try
            {
                if (_writer != null)
                {
                    _writer.Flush();
                }
            }
            catch (Exception ex)
            {
                FailWriter(ex);
            }
        }

        private static void Shutdown()
        {
            try
            {
                if (_writer != null)
                {
                    _writer.Flush();
                    _writer.Dispose();
                }
            }
            catch
            {
            }

            _writer = null;
            _currentScopeKey = null;
            _currentLogPath = null;
            _nextPollAt = 0f;
            _nextFlushAt = 0f;
            ClearSnapshots();
        }

        private static void ClearSnapshots()
        {
            PatientSnapshots.Clear();
            DoctorSnapshots.Clear();
            NurseSnapshots.Clear();
            LabSnapshots.Clear();
            JanitorSnapshots.Clear();
            UiSnapshots.Clear();
            RateLimitTimestamps.Clear();
        }

        private static string FormatSnapshot(CaseTraceSnapshot snapshot, string lifecycle)
        {
            var builder = new StringBuilder(320);
            AppendField(builder, "event", "snapshot");
            AppendField(builder, "lifecycle", lifecycle);
            AppendField(builder, "scope", snapshot == null ? "-" : snapshot.Scope);
            if (snapshot != null && !string.IsNullOrEmpty(snapshot.Payload))
            {
                if (builder.Length > 0)
                {
                    builder.Append(";");
                }

                builder.Append(snapshot.Payload);
            }

            return builder.ToString();
        }

        private static string FormatDecision(CaseTraceDecision decision)
        {
            return AppendDetails(
                BuildStructuredPayload(
                    "event",
                    "decision",
                    "method",
                    decision == null ? "-" : decision.MethodName,
                    "result",
                    decision != null && decision.Result ? "true" : "false",
                    "blocker",
                    decision == null ? "-" : decision.BlockerOrReason),
                decision == null ? null : decision.Details);
        }

        private static string FormatAction(CaseTraceAction action)
        {
            return AppendDetails(
                BuildStructuredPayload(
                    "event",
                    "action",
                    "method",
                    action == null ? "-" : action.MethodName,
                    "outcome",
                    action == null ? "-" : action.Outcome),
                action == null ? null : action.Details);
        }

        private static string BuildStructuredPayload(params string[] parts)
        {
            var builder = new StringBuilder(256);
            if (parts != null)
            {
                for (var i = 0; i + 1 < parts.Length; i += 2)
                {
                    AppendField(builder, parts[i], parts[i + 1]);
                }
            }

            return builder.ToString();
        }

        private static string AppendDetails(string payload, string details)
        {
            if (string.IsNullOrEmpty(details))
            {
                return payload;
            }

            if (string.IsNullOrEmpty(payload))
            {
                return details;
            }

            return payload + ";" + details;
        }

        private static void AppendField(StringBuilder builder, string key, string value)
        {
            if (builder == null || string.IsNullOrEmpty(key))
            {
                return;
            }

            if (builder.Length > 0)
            {
                builder.Append(";");
            }

            builder.Append(key).Append("=").Append(NormalizeValue(value));
        }

        private static void AppendRawField(StringBuilder builder, string key, string value)
        {
            if (builder == null || string.IsNullOrEmpty(key))
            {
                return;
            }

            if (builder.Length > 0)
            {
                builder.Append(";");
            }

            builder.Append(key).Append("=").Append(string.IsNullOrEmpty(value) ? "-" : Sanitize(value));
        }

        private static string NormalizeValue(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "-";
            }

            return Sanitize(value).Replace(";", ",").Replace("[", "(").Replace("]", ")");
        }

        private static string FormatList(IList<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return "[]";
            }

            var builder = new StringBuilder(values.Count * 12);
            builder.Append("[");
            for (var i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append("|");
                }

                builder.Append(NormalizeValue(values[i]));
            }

            builder.Append("]");
            return builder.ToString();
        }

        private static void LogListDiff(BehaviorPatient patient, IList<string> before, IList<string> after, string addEvent, string removeEvent, string sourceMethod, string reason)
        {
            if (patient == null)
            {
                return;
            }

            var beforeSet = new HashSet<string>(StringComparer.Ordinal);
            var afterSet = new HashSet<string>(StringComparer.Ordinal);
            if (before != null)
            {
                for (var i = 0; i < before.Count; i++)
                {
                    if (!string.IsNullOrEmpty(before[i]))
                    {
                        beforeSet.Add(before[i]);
                    }
                }
            }

            if (after != null)
            {
                for (var i = 0; i < after.Count; i++)
                {
                    if (!string.IsNullOrEmpty(after[i]))
                    {
                        afterSet.Add(after[i]);
                    }
                }
            }

            foreach (var item in afterSet)
            {
                if (!beforeSet.Contains(item))
                {
                    LogQueueEvent(patient, addEvent, GetCurrentStaff(patient), item, sourceMethod, reason, "added");
                }
            }

            foreach (var item in beforeSet)
            {
                if (!afterSet.Contains(item))
                {
                    LogQueueEvent(patient, removeEvent, GetCurrentStaff(patient), item, sourceMethod, reason, "removed");
                }
            }
        }

        private static object GetCurrentStaff(BehaviorPatient patient)
        {
            return patient == null || patient.m_state == null ? null : patient.m_state.m_doctor;
        }

        private static void CopyIdentifiers(IList values, string nestedFieldName, IList<string> target)
        {
            if (values == null || target == null)
            {
                return;
            }

            for (var i = 0; i < values.Count; i++)
            {
                var identifier = SafeIdentifier(values[i], nestedFieldName);
                if (!string.IsNullOrEmpty(identifier))
                {
                    target.Add(identifier);
                }
            }
        }

        private static void CopyLabIdentifiers(IList values, IList<string> target)
        {
            if (values == null || target == null)
            {
                return;
            }

            for (var i = 0; i < values.Count; i++)
            {
                var identifier = SafeEntityIdentifier(values[i]);
                if (!string.IsNullOrEmpty(identifier))
                {
                    target.Add(identifier);
                }
            }
        }

        private static string SafeIdentifier(object source, string nestedFieldName)
        {
            if (source == null)
            {
                return null;
            }

            var pointerValue = string.IsNullOrEmpty(nestedFieldName) ? source : ReflectionHelpers.GetField(source, nestedFieldName);
            var resolved = ReflectionHelpers.ResolvePointer(pointerValue);
            var examination = resolved as GameDBExamination;
            if (examination != null)
            {
                return SafeDatabaseId(examination);
            }

            var treatment = resolved as GameDBTreatment;
            if (treatment != null)
            {
                return SafeDatabaseId(treatment);
            }

            return SafeEntityIdentifier(resolved ?? pointerValue);
        }

        private static string SafeEntityIdentifier(object value)
        {
            var entity = GetEntityFromObject(value);
            if (entity != null)
            {
                return entity.GetEntityID().ToString(CultureInfo.InvariantCulture);
            }

            return value == null ? null : NormalizeValue(value.ToString());
        }

        private static string SafeDatabaseId(GameDBExamination examination)
        {
            return examination == null ? null : examination.DatabaseID.ToString();
        }

        private static string SafeDatabaseId(GameDBTreatment treatment)
        {
            return treatment == null ? null : treatment.DatabaseID.ToString();
        }

        private static string MakeSafeFileName(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "global";
            }

            var builder = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                var ch = value[i];
                if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == ' ')
                {
                    builder.Append(ch);
                }
                else
                {
                    builder.Append('_');
                }
            }

            var safe = builder.ToString().Trim();
            return string.IsNullOrEmpty(safe) ? "global" : safe;
        }

        private static string Sanitize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Replace("\r", " ").Replace("\n", " ");
        }
    }
}
