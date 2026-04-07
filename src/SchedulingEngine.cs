using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace ProjectHospital.AutoLabBalancer
{
    internal sealed class SchedulingDepartmentBoard
    {
        public object Department;
        public int Score;
        public int CriticalPatients;
        public int WaitingPatients;
        public int PlannedSurgeryPatients;
        public int HospitalizedScheduledProcedures;
        public int MedicineTasks;
        public int FoodTasks;
        public int TransportTasks;
        public int CollapseCareTasks;
        public int FreeDoctors;
        public int FreeNurses;
        public int FreeLabSpecialists;
        public int FreeJanitors;

        public int TotalTasks
        {
            get
            {
                return CriticalPatients
                    + WaitingPatients
                    + PlannedSurgeryPatients
                    + HospitalizedScheduledProcedures
                    + MedicineTasks
                    + FoodTasks
                    + TransportTasks
                    + CollapseCareTasks;
            }
        }
    }

    internal sealed class SchedulingSnapshot
    {
        public bool Ready;
        public string Warning;
        public float BuiltAt;
        public double RebuildMs;
        public int Departments;
        public int DepartmentBoards;
        public int TotalTasks;
        public int CriticalTasks;
        public int SurgeryTasks;
        public int MedicineTasks;
        public int TransportTasks;
        public int WaitingPatientTasks;
        public int Staff;
        public int FreeStaff;
        public int Patients;
        public string TopBoardSummary;
        public readonly Dictionary<object, SchedulingDepartmentBoard> Boards = new Dictionary<object, SchedulingDepartmentBoard>(ReferenceEqualityComparer.Instance);
    }

    internal static class SchedulingEngineService
    {
        private static readonly object Sync = new object();
        private static SchedulingSnapshot _snapshot;
        private static float _nextRebuildAt;
        private static readonly double TickToMs = 1000.0 / Stopwatch.Frequency;

        public static SchedulingSnapshot Snapshot
        {
            get
            {
                lock (Sync)
                {
                    return _snapshot;
                }
            }
        }

        public static bool Enabled
        {
            get
            {
                return RuntimeSettings.Config != null
                    && RuntimeSettings.Config.Enabled.Value
                    && RuntimeSettings.Config.EnableSchedulingEngine.Value;
            }
        }

        public static void Tick(float now)
        {
            if (!Enabled || now < _nextRebuildAt)
            {
                return;
            }

            _nextRebuildAt = now + Mathf.Max(0.1f, RuntimeSettings.Config.SchedulingEngineIntervalSeconds.Value);
            Rebuild(now);
        }

        public static bool TryGetDepartmentBoard(object department, out SchedulingDepartmentBoard board)
        {
            board = null;
            if (!Enabled || department == null)
            {
                return false;
            }

            lock (Sync)
            {
                if (_snapshot == null || !_snapshot.Ready)
                {
                    return false;
                }

                if (Time.realtimeSinceStartup - _snapshot.BuiltAt > Mathf.Max(0.25f, RuntimeSettings.Config.SchedulingEngineMaxSnapshotAgeSeconds.Value))
                {
                    return false;
                }

                return _snapshot.Boards.TryGetValue(department, out board);
            }
        }

        public static bool TryGetPatientDepartmentBoard(object patient, out SchedulingDepartmentBoard board)
        {
            board = null;
            if (!Enabled || patient == null)
            {
                return false;
            }

            return TryGetDepartmentBoard(GetPatientDepartment(patient), out board);
        }

        private static void Rebuild(float now)
        {
            var start = Stopwatch.GetTimestamp();
            var snapshot = new SchedulingSnapshot { BuiltAt = now };

            try
            {
                var hospital = Lopital.Hospital.Instance;
                if (hospital == null)
                {
                    snapshot.Warning = "Hospital.Instance is null.";
                }
                else
                {
                    BuildDepartments(hospital, snapshot);
                    BuildCharacters(hospital, snapshot);
                    FinalizeSnapshot(snapshot);
                    snapshot.Ready = true;
                }
            }
            catch (Exception ex)
            {
                snapshot.Warning = ex.GetType().Name + ": " + ex.Message;
            }

            snapshot.RebuildMs = (Stopwatch.GetTimestamp() - start) * TickToMs;
            lock (Sync)
            {
                _snapshot = snapshot;
            }

            if (RuntimeSettings.Config != null
                && RuntimeSettings.Config.SchedulingEngineDebugLog.Value
                && RuntimeSettings.Logger != null)
            {
                RuntimeSettings.Logger.LogInfo("[SchedulingEngine] "
                    + "ready=" + snapshot.Ready
                    + " departments=" + snapshot.Departments
                    + " tasks=" + snapshot.TotalTasks
                    + " freeStaff=" + snapshot.FreeStaff + "/" + snapshot.Staff
                    + " rebuildMs=" + snapshot.RebuildMs.ToString("0.00")
                    + (string.IsNullOrEmpty(snapshot.Warning) ? string.Empty : " warning=" + snapshot.Warning));
            }
        }

        private static void BuildDepartments(object hospital, SchedulingSnapshot snapshot)
        {
            foreach (var department in ReflectionHelpers.GetEnumerableField(hospital, "m_departments"))
            {
                if (department == null)
                {
                    continue;
                }

                snapshot.Departments++;
                snapshot.Boards[department] = new SchedulingDepartmentBoard { Department = department };
            }
        }

        private static void BuildCharacters(object hospital, SchedulingSnapshot snapshot)
        {
            foreach (var character in ReflectionHelpers.GetEnumerableField(hospital, "m_characters"))
            {
                if (character == null)
                {
                    continue;
                }

                CountStaff(character, snapshot);
                CountPatient(character, snapshot);
            }
        }

        private static void CountStaff(object character, SchedulingSnapshot snapshot)
        {
            var doctor = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.BehaviorDoctor");
            var nurse = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.BehaviorNurse");
            var lab = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.BehaviorLabSpecialist");
            var janitor = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.BehaviorJanitor");
            var behavior = doctor ?? nurse ?? lab ?? janitor;
            if (behavior == null)
            {
                return;
            }

            snapshot.Staff++;
            var free = IsFreeBehavior(behavior);
            if (free)
            {
                snapshot.FreeStaff++;
            }

            var department = GetEmployeeDepartment(character);
            SchedulingDepartmentBoard board;
            if (department == null || !snapshot.Boards.TryGetValue(department, out board) || !free)
            {
                return;
            }

            if (doctor != null)
            {
                board.FreeDoctors++;
            }
            else if (nurse != null)
            {
                board.FreeNurses++;
            }
            else if (lab != null)
            {
                board.FreeLabSpecialists++;
            }
            else if (janitor != null)
            {
                board.FreeJanitors++;
            }
        }

        private static void CountPatient(object character, SchedulingSnapshot snapshot)
        {
            var patient = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.BehaviorPatient");
            if (patient == null)
            {
                return;
            }

            snapshot.Patients++;
            var department = GetPatientDepartment(patient);
            SchedulingDepartmentBoard board;
            if (department == null || !snapshot.Boards.TryGetValue(department, out board))
            {
                return;
            }

            if (ReflectionHelpers.InvokeBool(patient, "HasCriticalSurgeryPlanned"))
            {
                board.PlannedSurgeryPatients++;
                board.Score += 800;
            }

            var hazard = InvokeObject(patient, "GetWorstKnownHazard");
            if (hazard != null && string.Equals(hazard.ToString(), "High", StringComparison.OrdinalIgnoreCase))
            {
                board.CriticalPatients++;
                board.Score += 1200;
            }

            var hospitalization = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.HospitalizationComponent");
            if (hospitalization != null)
            {
                CountHospitalizedTasks(hospitalization, board);
                return;
            }

            var state = ReflectionHelpers.GetField(patient, "m_state");
            if (state != null)
            {
                var patientState = ReflectionHelpers.GetField(state, "m_patientState");
                if (patientState != null && patientState.ToString().IndexOf("Waiting", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    board.WaitingPatients++;
                    board.Score += 60;
                }
            }
        }

        private static void CountHospitalizedTasks(object hospitalization, SchedulingDepartmentBoard board)
        {
            if (ReflectionHelpers.InvokeBool(hospitalization, "WillCollapse"))
            {
                board.CollapseCareTasks++;
                board.Score += 1500;
            }

            if (ReflectionHelpers.InvokeBool(hospitalization, "HasAnyScheduledProcedures"))
            {
                board.HospitalizedScheduledProcedures++;
                board.Score += 250;
            }

            var state = ReflectionHelpers.GetField(hospitalization, "m_state");
            if (state == null)
            {
                return;
            }

            if (Equals(ReflectionHelpers.GetField(state, "m_medicinePrescribed"), true)
                && !Equals(ReflectionHelpers.GetField(state, "m_medicineReceived"), true))
            {
                board.MedicineTasks++;
                board.Score += 160;
            }

            if (Equals(ReflectionHelpers.GetField(state, "m_lunchReady"), true)
                && !Equals(ReflectionHelpers.GetField(state, "m_lunchEaten"), true))
            {
                board.FoodTasks++;
                board.Score += 40;
            }

            if (Equals(ReflectionHelpers.GetField(state, "m_oustideRoom"), true))
            {
                board.TransportTasks++;
                board.Score += 180;
            }
        }

        private static void FinalizeSnapshot(SchedulingSnapshot snapshot)
        {
            SchedulingDepartmentBoard top = null;
            foreach (var pair in snapshot.Boards)
            {
                var board = pair.Value;
                snapshot.DepartmentBoards++;
                snapshot.TotalTasks += board.TotalTasks;
                snapshot.CriticalTasks += board.CriticalPatients + board.CollapseCareTasks;
                snapshot.SurgeryTasks += board.PlannedSurgeryPatients;
                snapshot.MedicineTasks += board.MedicineTasks;
                snapshot.TransportTasks += board.TransportTasks;
                snapshot.WaitingPatientTasks += board.WaitingPatients;
                if (top == null || board.Score > top.Score)
                {
                    top = board;
                }
            }

            if (top != null && top.Score > 0)
            {
                snapshot.TopBoardSummary = "score=" + top.Score
                    + " tasks=" + top.TotalTasks
                    + " critical=" + (top.CriticalPatients + top.CollapseCareTasks)
                    + " surgery=" + top.PlannedSurgeryPatients
                    + " meds=" + top.MedicineTasks
                    + " transport=" + top.TransportTasks
                    + " freeNurses=" + top.FreeNurses;
            }
            else
            {
                snapshot.TopBoardSummary = "none";
            }
        }

        private static bool IsFreeBehavior(object behavior)
        {
            if (behavior == null || !ReflectionHelpers.InvokeBool(behavior, "IsFree") || ReflectionHelpers.InvokeBool(behavior, "GetReserved"))
            {
                return false;
            }

            var entity = ReflectionHelpers.GetField(behavior, "m_entity");
            var employee = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.EmployeeComponent");
            return employee == null || !ReflectionHelpers.InvokeBool(employee, "IsPerformingAProcedure");
        }

        private static object GetEmployeeDepartment(object entity)
        {
            var employee = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.EmployeeComponent");
            var state = ReflectionHelpers.GetField(employee, "m_state");
            return ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_department"));
        }

        private static object GetPatientDepartment(object patient)
        {
            var state = ReflectionHelpers.GetField(patient, "m_state");
            return ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_department"));
        }

        private static object InvokeObject(object instance, string methodName)
        {
            if (instance == null)
            {
                return null;
            }

            var method = AccessTools.Method(instance.GetType(), methodName, Type.EmptyTypes);
            if (method == null)
            {
                return null;
            }

            try
            {
                return method.Invoke(instance, null);
            }
            catch
            {
                return null;
            }
        }
    }
}
