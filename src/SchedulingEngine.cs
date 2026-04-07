using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Collections;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace ProjectHospital.AutoLabBalancer
{
    internal enum SchedulingTaskType
    {
        CriticalCare,
        WaitingPatient,
        PlannedSurgery,
        HospitalizedProcedure,
        Medicine,
        Food,
        Transport,
        CollapseCare,
        Examination,
        Treatment
    }

    internal sealed class SchedulingTask
    {
        public string TaskId;
        public object Patient;
        public object Department;
        public string RequiredRole;
        public SchedulingTaskType Type;
        public int Priority;
        public object TargetProcedure;
        public float ExpiresAt;
    }

    internal sealed class SchedulingStaffCandidate
    {
        public object Staff;
        public object Department;
        public string Role;
    }

    internal sealed class SchedulingDispatchRecommendation
    {
        public object Staff;
        public SchedulingTask Task;
        public string StaffRole;
    }

    internal sealed class SchedulingDepartmentBoard
    {
        public object Department;
        public int Score;
        public int NurseScore;
        public int DoctorScore;
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
        public int NurseDryRunDispatches;
        public int DoctorDryRunDispatches;
        public readonly List<SchedulingTask> Tasks = new List<SchedulingTask>();
        public readonly List<SchedulingStaffCandidate> StaffCandidates = new List<SchedulingStaffCandidate>();
        public readonly List<SchedulingDispatchRecommendation> DispatchRecommendations = new List<SchedulingDispatchRecommendation>();

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

        public int NurseTasks
        {
            get
            {
                return CriticalPatients
                    + PlannedSurgeryPatients
                    + HospitalizedScheduledProcedures
                    + MedicineTasks
                    + FoodTasks
                    + TransportTasks
                    + CollapseCareTasks;
            }
        }

        public int DoctorTasks
        {
            get { return CriticalPatients + WaitingPatients + PlannedSurgeryPatients; }
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
        public int NurseTasks;
        public int DoctorTasks;
        public int NurseDryRunDispatches;
        public int DoctorDryRunDispatches;
        public int Staff;
        public int FreeStaff;
        public int Patients;
        public int TaskObjects;
        public int DispatchRecommendations;
        public string TopBoardSummary;
        public string TopDispatchSummary;
        public readonly Dictionary<object, SchedulingDepartmentBoard> Boards = new Dictionary<object, SchedulingDepartmentBoard>(ReferenceEqualityComparer.Instance);
    }

    internal sealed class SchedulingCountersSnapshot
    {
        public long Rebuilds;
        public double AverageRebuildMs;
        public double MaxRebuildMs;
        public long BoardHits;
        public long BoardMisses;
        public long BoardStale;
        public long NurseGatingChecks;
        public long NurseGatingSkips;
        public long OutpatientGatingChecks;
        public long OutpatientGatingSkips;
        public long DoctorSearchGatingChecks;
        public long DoctorSearchGatingSkips;
        public long ReservationBrokerHits;
        public long ReservationBrokerMisses;
        public long ReservationBrokerStores;
        public long DispatcherRecommendations;
        public long DispatcherApplyChecks;
        public long DispatcherApplyAllows;
        public long DispatcherApplySkips;
    }

    internal static class SchedulingEngineService
    {
        private static readonly object Sync = new object();
        private static SchedulingSnapshot _snapshot;
        private static float _nextRebuildAt;
        private static readonly double TickToMs = 1000.0 / Stopwatch.Frequency;
        private static long _rebuilds;
        private static double _totalRebuildMs;
        private static double _maxRebuildMs;
        private static long _boardHits;
        private static long _boardMisses;
        private static long _boardStale;
        private static long _nurseGatingChecks;
        private static long _nurseGatingSkips;
        private static long _outpatientGatingChecks;
        private static long _outpatientGatingSkips;
        private static long _doctorSearchGatingChecks;
        private static long _doctorSearchGatingSkips;
        private static long _dispatcherApplyChecks;
        private static long _dispatcherApplyAllows;
        private static long _dispatcherApplySkips;

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
                Interlocked.Increment(ref _boardMisses);
                return false;
            }

            lock (Sync)
            {
                if (_snapshot == null || !_snapshot.Ready)
                {
                    Interlocked.Increment(ref _boardMisses);
                    return false;
                }

                if (Time.realtimeSinceStartup - _snapshot.BuiltAt > Mathf.Max(0.25f, RuntimeSettings.Config.SchedulingEngineMaxSnapshotAgeSeconds.Value))
                {
                    Interlocked.Increment(ref _boardStale);
                    return false;
                }

                if (_snapshot.Boards.TryGetValue(department, out board))
                {
                    Interlocked.Increment(ref _boardHits);
                    return true;
                }

                Interlocked.Increment(ref _boardMisses);
                return false;
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

        public static bool TryGetStaffRecommendation(object staffOrBehavior, string role, out SchedulingDispatchRecommendation recommendation)
        {
            recommendation = null;
            if (!Enabled || staffOrBehavior == null || string.IsNullOrEmpty(role))
            {
                return false;
            }

            var staff = ReflectionHelpers.GetField(staffOrBehavior, "m_entity") ?? staffOrBehavior;
            var department = GetEmployeeDepartment(staff);
            SchedulingDepartmentBoard board;
            if (!TryGetDepartmentBoard(department, out board))
            {
                return false;
            }

            for (var i = 0; i < board.DispatchRecommendations.Count; i++)
            {
                var candidate = board.DispatchRecommendations[i];
                if (ReferenceEquals(candidate.Staff, staff)
                    && string.Equals(candidate.StaffRole, role, StringComparison.OrdinalIgnoreCase))
                {
                    recommendation = candidate;
                    return true;
                }
            }

            return false;
        }

        public static void RecordDispatcherApply(bool allowed)
        {
            Interlocked.Increment(ref _dispatcherApplyChecks);
            if (allowed)
            {
                Interlocked.Increment(ref _dispatcherApplyAllows);
            }
            else
            {
                Interlocked.Increment(ref _dispatcherApplySkips);
            }
        }

        public static void RecordNurseGating(bool skipped)
        {
            Interlocked.Increment(ref _nurseGatingChecks);
            if (skipped)
            {
                Interlocked.Increment(ref _nurseGatingSkips);
            }
        }

        public static void RecordOutpatientGating(bool skipped)
        {
            Interlocked.Increment(ref _outpatientGatingChecks);
            if (skipped)
            {
                Interlocked.Increment(ref _outpatientGatingSkips);
            }
        }

        public static void RecordDoctorSearchGating(bool skipped)
        {
            Interlocked.Increment(ref _doctorSearchGatingChecks);
            if (skipped)
            {
                Interlocked.Increment(ref _doctorSearchGatingSkips);
            }
        }

        public static SchedulingCountersSnapshot GetCounters()
        {
            var broker = ReservationBrokerService.GetCounters();
            lock (Sync)
            {
                return new SchedulingCountersSnapshot
                {
                    Rebuilds = _rebuilds,
                    AverageRebuildMs = _rebuilds <= 0 ? 0.0 : _totalRebuildMs / _rebuilds,
                    MaxRebuildMs = _maxRebuildMs,
                    BoardHits = Interlocked.Read(ref _boardHits),
                    BoardMisses = Interlocked.Read(ref _boardMisses),
                    BoardStale = Interlocked.Read(ref _boardStale),
                    NurseGatingChecks = Interlocked.Read(ref _nurseGatingChecks),
                    NurseGatingSkips = Interlocked.Read(ref _nurseGatingSkips),
                    OutpatientGatingChecks = Interlocked.Read(ref _outpatientGatingChecks),
                    OutpatientGatingSkips = Interlocked.Read(ref _outpatientGatingSkips),
                    DoctorSearchGatingChecks = Interlocked.Read(ref _doctorSearchGatingChecks),
                    DoctorSearchGatingSkips = Interlocked.Read(ref _doctorSearchGatingSkips),
                    ReservationBrokerHits = broker.Hits,
                    ReservationBrokerMisses = broker.Misses,
                    ReservationBrokerStores = broker.Stores,
                    DispatcherRecommendations = _snapshot == null ? 0 : _snapshot.DispatchRecommendations,
                    DispatcherApplyChecks = Interlocked.Read(ref _dispatcherApplyChecks),
                    DispatcherApplyAllows = Interlocked.Read(ref _dispatcherApplyAllows),
                    DispatcherApplySkips = Interlocked.Read(ref _dispatcherApplySkips)
                };
            }
        }

        public static void ResetCounters()
        {
            lock (Sync)
            {
                _rebuilds = 0;
                _totalRebuildMs = 0.0;
                _maxRebuildMs = 0.0;
                Interlocked.Exchange(ref _boardHits, 0);
                Interlocked.Exchange(ref _boardMisses, 0);
                Interlocked.Exchange(ref _boardStale, 0);
                Interlocked.Exchange(ref _nurseGatingChecks, 0);
                Interlocked.Exchange(ref _nurseGatingSkips, 0);
                Interlocked.Exchange(ref _outpatientGatingChecks, 0);
                Interlocked.Exchange(ref _outpatientGatingSkips, 0);
                Interlocked.Exchange(ref _doctorSearchGatingChecks, 0);
                Interlocked.Exchange(ref _doctorSearchGatingSkips, 0);
                Interlocked.Exchange(ref _dispatcherApplyChecks, 0);
                Interlocked.Exchange(ref _dispatcherApplyAllows, 0);
                Interlocked.Exchange(ref _dispatcherApplySkips, 0);
                ReservationBrokerService.ResetCounters();
            }
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
                _rebuilds++;
                _totalRebuildMs += snapshot.RebuildMs;
                if (snapshot.RebuildMs > _maxRebuildMs)
                {
                    _maxRebuildMs = snapshot.RebuildMs;
                }
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
                AddStaffCandidate(board, character, "doctor");
            }
            else if (nurse != null)
            {
                board.FreeNurses++;
                AddStaffCandidate(board, character, "nurse");
            }
            else if (lab != null)
            {
                board.FreeLabSpecialists++;
                AddStaffCandidate(board, character, "lab");
            }
            else if (janitor != null)
            {
                board.FreeJanitors++;
                AddStaffCandidate(board, character, "janitor");
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
                board.NurseScore += 350;
                board.DoctorScore += 450;
                AddTask(board, patient, "nurse", SchedulingTaskType.PlannedSurgery, 350, null);
                AddTask(board, patient, "doctor", SchedulingTaskType.PlannedSurgery, 450, null);
            }

            var hazard = InvokeObject(patient, "GetWorstKnownHazard");
            if (hazard != null && string.Equals(hazard.ToString(), "High", StringComparison.OrdinalIgnoreCase))
            {
                board.CriticalPatients++;
                board.Score += 1200;
                board.NurseScore += 600;
                board.DoctorScore += 600;
                AddTask(board, patient, "nurse", SchedulingTaskType.CriticalCare, 600, null);
                AddTask(board, patient, "doctor", SchedulingTaskType.CriticalCare, 600, null);
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
                    board.DoctorScore += 60;
                    AddTask(board, patient, "doctor", SchedulingTaskType.WaitingPatient, 60, null);
                }
            }
        }

        private static void CountHospitalizedTasks(object hospitalization, SchedulingDepartmentBoard board)
        {
            var patient = ReflectionHelpers.GetComponentByTypeName(ReflectionHelpers.GetField(hospitalization, "m_entity"), "Lopital.BehaviorPatient");
            if (ReflectionHelpers.InvokeBool(hospitalization, "WillCollapse"))
            {
                board.CollapseCareTasks++;
                board.Score += 1500;
                board.NurseScore += 1200;
                board.DoctorScore += 300;
                AddTask(board, patient, "nurse", SchedulingTaskType.CollapseCare, 1200, null);
                AddTask(board, patient, "doctor", SchedulingTaskType.CollapseCare, 300, null);
            }

            if (ReflectionHelpers.InvokeBool(hospitalization, "HasAnyScheduledProcedures"))
            {
                board.HospitalizedScheduledProcedures++;
                board.Score += 250;
                board.NurseScore += 250;
                AddTask(board, patient, "nurse", SchedulingTaskType.HospitalizedProcedure, 250, null);
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
                board.NurseScore += 160;
                AddTask(board, patient, "nurse", SchedulingTaskType.Medicine, 160, null);
            }

            if (Equals(ReflectionHelpers.GetField(state, "m_lunchReady"), true)
                && !Equals(ReflectionHelpers.GetField(state, "m_lunchEaten"), true))
            {
                board.FoodTasks++;
                board.Score += 40;
                board.NurseScore += 40;
                AddTask(board, patient, "nurse", SchedulingTaskType.Food, 40, null);
            }

            if (Equals(ReflectionHelpers.GetField(state, "m_oustideRoom"), true))
            {
                board.TransportTasks++;
                board.Score += 180;
                board.NurseScore += 180;
                AddTask(board, patient, "nurse", SchedulingTaskType.Transport, 180, null);
            }

            CountProcedureQueueTasks(patient, board);
        }

        private static void CountProcedureQueueTasks(object patient, SchedulingDepartmentBoard board)
        {
            var entity = ReflectionHelpers.GetField(patient, "m_entity");
            var procedure = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.ProcedureComponent");
            var state = ReflectionHelpers.GetField(procedure, "m_state");
            var queue = ReflectionHelpers.GetField(state, "m_procedureQueue");
            if (queue == null)
            {
                return;
            }

            foreach (var planned in ReflectionHelpers.GetEnumerableField(queue, "m_plannedExaminationStates"))
            {
                AddTask(board, patient, "nurse", SchedulingTaskType.Examination, 120, ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(planned, "m_examination")));
            }

            foreach (var planned in ReflectionHelpers.GetEnumerableField(queue, "m_plannedTreatmentStates"))
            {
                AddTask(board, patient, "nurse", SchedulingTaskType.Treatment, 120, ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(planned, "m_treatment")));
            }
        }

        private static void AddTask(SchedulingDepartmentBoard board, object patient, string requiredRole, SchedulingTaskType type, int priority, object targetProcedure)
        {
            if (board == null || patient == null)
            {
                return;
            }

            board.Tasks.Add(new SchedulingTask
            {
                TaskId = BuildTaskId(patient, requiredRole, type, targetProcedure),
                Patient = patient,
                Department = board.Department,
                RequiredRole = requiredRole,
                Type = type,
                Priority = priority,
                TargetProcedure = targetProcedure,
                ExpiresAt = Time.realtimeSinceStartup + 2f
            });
        }

        private static void AddStaffCandidate(SchedulingDepartmentBoard board, object staff, string role)
        {
            if (board == null || staff == null)
            {
                return;
            }

            board.StaffCandidates.Add(new SchedulingStaffCandidate
            {
                Staff = staff,
                Department = board.Department,
                Role = role
            });
        }

        private static string BuildTaskId(object patient, string requiredRole, SchedulingTaskType type, object targetProcedure)
        {
            return GetObjectKey(patient) + ":" + requiredRole + ":" + type + ":" + GetObjectKey(targetProcedure);
        }

        private static void FinalizeSnapshot(SchedulingSnapshot snapshot)
        {
            SchedulingDepartmentBoard top = null;
            SchedulingDispatchRecommendation topDispatch = null;
            foreach (var pair in snapshot.Boards)
            {
                var board = pair.Value;
                BuildDispatchRecommendations(board);
                snapshot.DepartmentBoards++;
                snapshot.TotalTasks += board.TotalTasks;
                snapshot.CriticalTasks += board.CriticalPatients + board.CollapseCareTasks;
                snapshot.SurgeryTasks += board.PlannedSurgeryPatients;
                snapshot.MedicineTasks += board.MedicineTasks;
                snapshot.TransportTasks += board.TransportTasks;
                snapshot.WaitingPatientTasks += board.WaitingPatients;
                snapshot.NurseTasks += board.NurseTasks;
                snapshot.DoctorTasks += board.DoctorTasks;
                snapshot.TaskObjects += board.Tasks.Count;
                board.NurseDryRunDispatches = CountRecommendations(board, "nurse");
                board.DoctorDryRunDispatches = CountRecommendations(board, "doctor") + CountRecommendations(board, "lab");
                snapshot.NurseDryRunDispatches += board.NurseDryRunDispatches;
                snapshot.DoctorDryRunDispatches += board.DoctorDryRunDispatches;
                snapshot.DispatchRecommendations += board.DispatchRecommendations.Count;
                if (top == null || board.Score > top.Score)
                {
                    top = board;
                }

                for (var i = 0; i < board.DispatchRecommendations.Count; i++)
                {
                    var recommendation = board.DispatchRecommendations[i];
                    if (topDispatch == null || recommendation.Task.Priority > topDispatch.Task.Priority)
                    {
                        topDispatch = recommendation;
                    }
                }
            }

            if (top != null && top.Score > 0)
            {
                snapshot.TopBoardSummary = "score=" + top.Score
                    + " tasks=" + top.TotalTasks
                    + " taskObjects=" + top.Tasks.Count
                    + " critical=" + (top.CriticalPatients + top.CollapseCareTasks)
                    + " surgery=" + top.PlannedSurgeryPatients
                    + " meds=" + top.MedicineTasks
                    + " transport=" + top.TransportTasks
                    + " nurseScore=" + top.NurseScore
                    + " doctorScore=" + top.DoctorScore
                    + " dryRun(nurse/doctor)=" + top.NurseDryRunDispatches + "/" + top.DoctorDryRunDispatches
                    + " freeNurses=" + top.FreeNurses
                    + " freeDoctors=" + top.FreeDoctors;
            }
            else
            {
                snapshot.TopBoardSummary = "none";
            }

            snapshot.TopDispatchSummary = topDispatch == null
                ? "none"
                : topDispatch.StaffRole + " -> " + topDispatch.Task.Type + " priority=" + topDispatch.Task.Priority + " task=" + topDispatch.Task.TaskId;
        }

        private static void BuildDispatchRecommendations(SchedulingDepartmentBoard board)
        {
            if (board == null || board.Tasks.Count == 0 || board.StaffCandidates.Count == 0)
            {
                return;
            }

            var usedStaff = new HashSet<object>(ReferenceEqualityComparer.Instance);
            for (var pick = 0; pick < board.StaffCandidates.Count; pick++)
            {
                SchedulingTask bestTask = null;
                SchedulingStaffCandidate bestStaff = null;
                for (var s = 0; s < board.StaffCandidates.Count; s++)
                {
                    var staff = board.StaffCandidates[s];
                    if (usedStaff.Contains(staff.Staff))
                    {
                        continue;
                    }

                    for (var t = 0; t < board.Tasks.Count; t++)
                    {
                        var task = board.Tasks[t];
                        if (!CanHandleTask(staff, task))
                        {
                            continue;
                        }

                        if (bestTask == null || task.Priority > bestTask.Priority)
                        {
                            bestTask = task;
                            bestStaff = staff;
                        }
                    }
                }

                if (bestTask == null || bestStaff == null)
                {
                    return;
                }

                usedStaff.Add(bestStaff.Staff);
                board.DispatchRecommendations.Add(new SchedulingDispatchRecommendation
                {
                    Staff = bestStaff.Staff,
                    StaffRole = bestStaff.Role,
                    Task = bestTask
                });
            }
        }

        private static bool CanHandleTask(SchedulingStaffCandidate staff, SchedulingTask task)
        {
            if (staff == null || task == null)
            {
                return false;
            }

            if (string.Equals(task.RequiredRole, staff.Role, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(task.RequiredRole, "doctor", StringComparison.OrdinalIgnoreCase)
                && string.Equals(staff.Role, "lab", StringComparison.OrdinalIgnoreCase)
                && task.Type == SchedulingTaskType.WaitingPatient;
        }

        private static int CountRecommendations(SchedulingDepartmentBoard board, string role)
        {
            var count = 0;
            for (var i = 0; i < board.DispatchRecommendations.Count; i++)
            {
                if (string.Equals(board.DispatchRecommendations[i].StaffRole, role, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }

            return count;
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

        private static string GetObjectKey(object value)
        {
            if (value == null)
            {
                return "null";
            }

            var entityId = ReflectionHelpers.GetField(value, "ID") ?? ReflectionHelpers.GetField(value, "m_entityID");
            return entityId == null ? value.GetType().Name + "#" + ReferenceEqualityComparer.Instance.GetHashCode(value) : Convert.ToString(entityId);
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
