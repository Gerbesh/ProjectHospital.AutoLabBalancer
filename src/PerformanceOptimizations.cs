using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using GLib;
using HarmonyLib;
using Lopital;
using UnityEngine;

namespace ProjectHospital.AutoLabBalancer
{
    internal sealed class TimedCacheEntry<T>
    {
        public T Value;
        public float ExpiresAt;
    }

    internal sealed class NurseTaskBoardSnapshot
    {
        public float ExpiresAt;
        public int Score;
        public int Critical;
        public int Surgery;
        public int HospitalizedProcedures;
        public int WaitingPatients;
        public int Medicine;
        public int Food;
        public int Transport;
        public int Care;
    }

    internal sealed class BackoffState
    {
        public float NextAt;
        public float Delay;
    }

    internal sealed class ReservationBrokerCountersSnapshot
    {
        public long Hits;
        public long Misses;
        public long Stores;
    }

    internal static class ReservationBrokerService
    {
        private static readonly Dictionary<string, TimedCacheEntry<ProcedureSceneAvailability>> Failures = new Dictionary<string, TimedCacheEntry<ProcedureSceneAvailability>>();
        private static long _hits;
        private static long _misses;
        private static long _stores;

        public static bool TryGet(MethodBase method, object[] args, ref ProcedureSceneAvailability result)
        {
            if (!PerformanceOptimizationService.Enabled
                || RuntimeSettings.Config == null
                || !RuntimeSettings.Config.EnableReservationBroker.Value)
            {
                return false;
            }

            var key = BuildReservationKey(method, args);
            TimedCacheEntry<ProcedureSceneAvailability> entry;
            if (!Failures.TryGetValue(key, out entry) || Time.realtimeSinceStartup >= entry.ExpiresAt)
            {
                _misses++;
                return false;
            }

            result = entry.Value;
            _hits++;
            return true;
        }

        public static void Store(MethodBase method, object[] args, ProcedureSceneAvailability result)
        {
            if (!PerformanceOptimizationService.Enabled
                || RuntimeSettings.Config == null
                || !RuntimeSettings.Config.EnableReservationBroker.Value)
            {
                return;
            }

            var key = BuildReservationKey(method, args);
            if (result == ProcedureSceneAvailability.AVAILABLE)
            {
                Failures.Remove(key);
                return;
            }

            Failures[key] = new TimedCacheEntry<ProcedureSceneAvailability>
            {
                Value = result,
                ExpiresAt = Time.realtimeSinceStartup + Mathf.Max(0.05f, RuntimeSettings.Config.ReservationBrokerTtlSeconds.Value)
            };
            _stores++;
        }

        public static void Tick(float now)
        {
            var expired = new List<string>();
            foreach (var pair in Failures)
            {
                if (now >= pair.Value.ExpiresAt)
                {
                    expired.Add(pair.Key);
                }
            }

            foreach (var key in expired)
            {
                Failures.Remove(key);
            }
        }

        public static ReservationBrokerCountersSnapshot GetCounters()
        {
            return new ReservationBrokerCountersSnapshot
            {
                Hits = _hits,
                Misses = _misses,
                Stores = _stores
            };
        }

        public static void ResetCounters()
        {
            _hits = 0;
            _misses = 0;
            _stores = 0;
        }

        private static string BuildReservationKey(MethodBase method, object[] args)
        {
            var key = method == null ? "unknown" : method.Name;
            if (args == null)
            {
                return key;
            }

            for (var i = 0; i < args.Length; i++)
            {
                key += "|" + BuildReservationPart(args[i]);
            }

            return key;
        }

        private static string BuildReservationPart(object value)
        {
            if (value == null)
            {
                return "null";
            }

            var type = value.GetType();
            var locId = ReflectionHelpers.GetStringProperty(value, "LocID");
            if (!string.IsNullOrEmpty(locId))
            {
                return type.Name + ":" + locId;
            }

            var id = ReflectionHelpers.GetField(value, "ID") ?? ReflectionHelpers.GetField(value, "m_entityID");
            if (id != null)
            {
                return type.Name + ":" + id;
            }

            return type.Name + "#" + ReferenceEqualityComparer.Instance.GetHashCode(value);
        }
    }

    internal static class PerformanceOptimizationService
    {
        private static readonly Dictionary<string, TimedCacheEntry<TileObject>> ObjectSearchCache = new Dictionary<string, TimedCacheEntry<TileObject>>();
        private static readonly Dictionary<string, TimedCacheEntry<TileObject>> CenterObjectSearchCache = new Dictionary<string, TimedCacheEntry<TileObject>>();
        private static readonly Dictionary<string, TimedCacheEntry<Entity>> EntitySearchCache = new Dictionary<string, TimedCacheEntry<Entity>>();
        private static readonly Dictionary<object, NurseTaskBoardSnapshot> NurseBoards = new Dictionary<object, NurseTaskBoardSnapshot>();
        private static readonly Dictionary<object, BackoffState> SelectNextStepBackoff = new Dictionary<object, BackoffState>();
        private static readonly Dictionary<object, BackoffState> NurseIdleBackoff = new Dictionary<object, BackoffState>();
        private static readonly Dictionary<object, BackoffState> WaitingSittingBackoff = new Dictionary<object, BackoffState>();
        private static readonly Dictionary<object, BackoffState> PatientDoctorSearchBackoff = new Dictionary<object, BackoffState>();
        private static readonly Dictionary<object, float> PersonalNeedsIdleNextCheck = new Dictionary<object, float>(ReferenceEqualityComparer.Instance);
        private static float _nextCleanupAt;

        public static bool Enabled
        {
            get
            {
                return RuntimeSettings.Config != null
                    && RuntimeSettings.Config.Enabled.Value
                    && RuntimeSettings.Config.EnablePerformanceOptimizations.Value;
            }
        }

        public static void Tick(float now)
        {
            if (!Enabled || now < _nextCleanupAt)
            {
                return;
            }

            _nextCleanupAt = now + 5f;
            Prune(ObjectSearchCache, now);
            Prune(CenterObjectSearchCache, now);
            Prune(EntitySearchCache, now);
            ReservationBrokerService.Tick(now);
            PruneNurseBoards(now);
            Prune(SelectNextStepBackoff, now);
            Prune(NurseIdleBackoff, now);
            Prune(WaitingSittingBackoff, now);
            Prune(PatientDoctorSearchBackoff, now);
        }

        public static bool TryGetCachedObjectSearch(MethodBase method, object[] args, ref TileObject result)
        {
            return TryGetCachedObjectSearch(method == null ? "unknown" : method.DeclaringType.FullName + "." + method.Name + "#" + method.GetParameters().Length, args, ref result);
        }

        public static bool TryGetCachedObjectSearch(string methodKey, object[] args, ref TileObject result)
        {
            if (!Enabled || !RuntimeSettings.Config.EnableObjectSearchCache.Value)
            {
                return false;
            }

            TimedCacheEntry<TileObject> entry;
            if (!ObjectSearchCache.TryGetValue(BuildKey(methodKey, args), out entry) || Time.realtimeSinceStartup >= entry.ExpiresAt)
            {
                return false;
            }

            if (!IsValidFreeObject(entry.Value))
            {
                return false;
            }

            result = entry.Value;
            return true;
        }

        public static void StoreObjectSearch(MethodBase method, object[] args, TileObject result)
        {
            StoreObjectSearch(method == null ? "unknown" : method.DeclaringType.FullName + "." + method.Name + "#" + method.GetParameters().Length, args, result);
        }

        public static void StoreObjectSearch(string methodKey, object[] args, TileObject result)
        {
            if (!Enabled || !RuntimeSettings.Config.EnableObjectSearchCache.Value || !IsValidFreeObject(result))
            {
                return;
            }

            ObjectSearchCache[BuildKey(methodKey, args)] = new TimedCacheEntry<TileObject>
            {
                Value = result,
                ExpiresAt = Time.realtimeSinceStartup + Mathf.Max(0.1f, RuntimeSettings.Config.ObjectSearchCacheTtlSeconds.Value)
            };
        }

        public static bool TryGetCachedCenterObjectSearch(string methodKey, object[] args, ref TileObject result)
        {
            if (!Enabled || !RuntimeSettings.Config.EnableObjectSearchCache.Value)
            {
                return false;
            }

            TimedCacheEntry<TileObject> entry;
            if (!CenterObjectSearchCache.TryGetValue(BuildKey(methodKey, args), out entry) || Time.realtimeSinceStartup >= entry.ExpiresAt)
            {
                return false;
            }

            if (!IsValidTileObject(entry.Value))
            {
                CenterObjectSearchCache.Remove(BuildKey(methodKey, args));
                return false;
            }

            result = entry.Value;
            return true;
        }

        public static void StoreCenterObjectSearch(string methodKey, object[] args, TileObject result)
        {
            if (!Enabled || !RuntimeSettings.Config.EnableObjectSearchCache.Value || !IsValidTileObject(result))
            {
                return;
            }

            CenterObjectSearchCache[BuildKey(methodKey, args)] = new TimedCacheEntry<TileObject>
            {
                Value = result,
                ExpiresAt = Time.realtimeSinceStartup + Mathf.Max(0.05f, RuntimeSettings.Config.ObjectSearchCacheTtlSeconds.Value * 0.5f)
            };
        }

        public static bool TryGetCachedEntitySearch(MethodBase method, object[] args, ref Entity result)
        {
            if (!Enabled || !RuntimeSettings.Config.EnableDoctorSearchCache.Value)
            {
                return false;
            }

            var key = BuildKey(method, args);
            TimedCacheEntry<Entity> entry;
            if (!EntitySearchCache.TryGetValue(key, out entry) || Time.realtimeSinceStartup >= entry.ExpiresAt)
            {
                return false;
            }

            if (!IsValidStaffEntity(entry.Value, IsFreeStaffSearch(method, args)))
            {
                EntitySearchCache.Remove(key);
                return false;
            }

            result = entry.Value;
            return true;
        }

        public static void StoreEntitySearch(MethodBase method, object[] args, Entity result)
        {
            if (!Enabled || !RuntimeSettings.Config.EnableDoctorSearchCache.Value || !IsValidStaffEntity(result, IsFreeStaffSearch(method, args)))
            {
                return;
            }

            EntitySearchCache[BuildKey(method, args)] = new TimedCacheEntry<Entity>
            {
                Value = result,
                ExpiresAt = Time.realtimeSinceStartup + Mathf.Max(0.05f, RuntimeSettings.Config.DoctorSearchCacheTtlSeconds.Value)
            };
        }

        public static bool ShouldSkipSelectNextStep(object hospitalization, ref bool result)
        {
            if (!Enabled || !RuntimeSettings.Config.EnableSelectNextStepBackoff.Value || hospitalization == null)
            {
                return false;
            }

            BackoffState state;
            if (SelectNextStepBackoff.TryGetValue(hospitalization, out state) && Time.realtimeSinceStartup < state.NextAt)
            {
                result = false;
                return true;
            }

            return false;
        }

        public static void StoreSelectNextStepResult(object hospitalization, bool result)
        {
            if (!Enabled || !RuntimeSettings.Config.EnableSelectNextStepBackoff.Value || hospitalization == null)
            {
                return;
            }

            if (result)
            {
                SelectNextStepBackoff.Remove(hospitalization);
                return;
            }

            SetAdaptiveBackoff(SelectNextStepBackoff, hospitalization, RuntimeSettings.Config.SelectNextStepBackoffSeconds.Value, RuntimeSettings.Config.SelectNextStepBackoffMaxSeconds.Value);
        }

        public static bool TryGetReservationFailure(MethodBase method, object[] args, ref ProcedureSceneAvailability result)
        {
            return ReservationBrokerService.TryGet(method, args, ref result);
        }

        public static void StoreReservationResult(MethodBase method, object[] args, ProcedureSceneAvailability result)
        {
            ReservationBrokerService.Store(method, args, result);
        }

        public static bool ShouldSkipNurseIdle(object nurse)
        {
            if (!Enabled || !RuntimeSettings.Config.EnableNurseTaskBoard.Value || nurse == null)
            {
                return false;
            }

            bool dispatcherDecision;
            if (TryGetDispatcherIdleDecision(nurse, "nurse", out dispatcherDecision))
            {
                return !dispatcherDecision;
            }

            if (!IsNurseIdleCandidate(nurse))
            {
                NurseIdleBackoff.Remove(nurse);
                return false;
            }

            var department = GetNurseDepartment(nurse);
            if (department == null)
            {
                return false;
            }

            if (RuntimeSettings.Config.EnableSchedulingEngineGating.Value)
            {
                SchedulingDepartmentBoard schedulingBoard;
                if (SchedulingEngineService.TryGetDepartmentBoard(department, out schedulingBoard))
                {
                    if (schedulingBoard.NurseScore > 0)
                    {
                        NurseIdleBackoff.Remove(nurse);
                        SchedulingEngineService.RecordNurseGating(false);
                        return false;
                    }

                    var skip = ShouldSkipShortBackoff(nurse, NurseIdleBackoff, RuntimeSettings.Config.EnableNurseIdleBackoff.Value);
                    SchedulingEngineService.RecordNurseGating(skip);
                    return skip;
                }
            }

            var board = GetNurseBoard(department);
            if (board.Score > 0)
            {
                NurseIdleBackoff.Remove(nurse);
                return false;
            }

            return ShouldSkipShortBackoff(nurse, NurseIdleBackoff, RuntimeSettings.Config.EnableNurseIdleBackoff.Value);
        }

        public static bool ShouldSkipDoctorIdle(object doctor)
        {
            bool dispatcherDecision;
            return TryGetDispatcherIdleDecision(doctor, "doctor", out dispatcherDecision) && !dispatcherDecision;
        }

        public static bool ShouldSkipLabSpecialistIdle(object labSpecialist)
        {
            bool dispatcherDecision;
            return TryGetDispatcherIdleDecision(labSpecialist, "lab", out dispatcherDecision) && !dispatcherDecision;
        }

        public static bool ShouldSkipJanitorAdminIdle(object janitor)
        {
            bool dispatcherDecision;
            return TryGetDispatcherIdleDecision(janitor, "janitor", out dispatcherDecision) && !dispatcherDecision;
        }

        public static void StoreNurseIdleResult(object nurse)
        {
            if (!Enabled || !RuntimeSettings.Config.EnableNurseIdleBackoff.Value || nurse == null)
            {
                return;
            }

            var isFree = ReflectionHelpers.InvokeBool(nurse, "IsFree");
            var reserved = ReflectionHelpers.InvokeBool(nurse, "GetReserved");
            var department = GetNurseDepartment(nurse);
            var board = department == null ? null : GetNurseBoard(department);
            if (isFree && !reserved && (board == null || board.Score <= 0))
            {
                SetAdaptiveBackoff(NurseIdleBackoff, nurse, RuntimeSettings.Config.NurseIdleBackoffSeconds.Value, RuntimeSettings.Config.NurseIdleBackoffMaxSeconds.Value);
            }
            else
            {
                NurseIdleBackoff.Remove(nurse);
            }
        }

        public static bool ShouldSkipWaitingSitting(object patient)
        {
            if (RuntimeSettings.Config.EnableSchedulingEngineGating.Value)
            {
                SchedulingDepartmentBoard board;
                if (SchedulingEngineService.TryGetPatientDepartmentBoard(patient, out board) && (board.FreeDoctors > 0 || board.FreeLabSpecialists > 0))
                {
                    WaitingSittingBackoff.Remove(patient);
                    PatientDoctorSearchBackoff.Remove(patient);
                    SchedulingEngineService.RecordOutpatientGating(false);
                    return false;
                }

                var skip = ShouldSkipShortBackoff(patient, WaitingSittingBackoff, RuntimeSettings.Config.EnableOutpatientQueueBackoff.Value);
                SchedulingEngineService.RecordOutpatientGating(skip);
                return skip;
            }

            return ShouldSkipShortBackoff(patient, WaitingSittingBackoff, RuntimeSettings.Config.EnableOutpatientQueueBackoff.Value);
        }

        public static void StoreWaitingSittingResult(object patient)
        {
            if (!Enabled || !RuntimeSettings.Config.EnableOutpatientQueueBackoff.Value || patient == null)
            {
                return;
            }

            SetAdaptiveBackoff(WaitingSittingBackoff, patient, RuntimeSettings.Config.OutpatientQueueBackoffSeconds.Value, RuntimeSettings.Config.OutpatientQueueBackoffMaxSeconds.Value);
        }

        public static bool ShouldSkipPatientDoctorSearch(object patient)
        {
            if (RuntimeSettings.Config.EnableSchedulingEngineGating.Value)
            {
                SchedulingDepartmentBoard board;
                if (SchedulingEngineService.TryGetPatientDepartmentBoard(patient, out board) && (board.FreeDoctors > 0 || board.FreeLabSpecialists > 0))
                {
                    PatientDoctorSearchBackoff.Remove(patient);
                    WaitingSittingBackoff.Remove(patient);
                    SchedulingEngineService.RecordDoctorSearchGating(false);
                    return false;
                }

                var skip = ShouldSkipShortBackoff(patient, PatientDoctorSearchBackoff, RuntimeSettings.Config.EnableOutpatientQueueBackoff.Value);
                SchedulingEngineService.RecordDoctorSearchGating(skip);
                return skip;
            }

            return ShouldSkipShortBackoff(patient, PatientDoctorSearchBackoff, RuntimeSettings.Config.EnableOutpatientQueueBackoff.Value);
        }

        public static void StorePatientDoctorSearchResult(object patient)
        {
            if (!Enabled || !RuntimeSettings.Config.EnableOutpatientQueueBackoff.Value || patient == null)
            {
                return;
            }

            if (HasAssignedClinician(patient))
            {
                PatientDoctorSearchBackoff.Remove(patient);
                WaitingSittingBackoff.Remove(patient);
                return;
            }

            SetAdaptiveBackoff(PatientDoctorSearchBackoff, patient, RuntimeSettings.Config.OutpatientQueueBackoffSeconds.Value, RuntimeSettings.Config.OutpatientQueueBackoffMaxSeconds.Value);
        }

        private static bool ShouldSkipShortBackoff(object instance, Dictionary<object, BackoffState> backoff, bool enabled)
        {
            if (!Enabled || !enabled || instance == null)
            {
                return false;
            }

            BackoffState state;
            return backoff.TryGetValue(instance, out state) && Time.realtimeSinceStartup < state.NextAt;
        }

        private static bool TryGetDispatcherIdleDecision(object behavior, string role, out bool allowed)
        {
            allowed = false;
            if (!Enabled
                || RuntimeSettings.Config == null
                || !RuntimeSettings.Config.EnableSchedulingDispatcherApply.Value
                || !RuntimeSettings.Config.EnableSchedulingEngineGating.Value
                || behavior == null)
            {
                return false;
            }

            if (!IsIdleCandidate(behavior))
            {
                return false;
            }

            SchedulingDispatchRecommendation recommendation;
            if (!SchedulingEngineService.TryGetStaffDispatcherDecision(behavior, role, out allowed, out recommendation))
            {
                return false;
            }

            if (allowed && recommendation != null && recommendation.Task != null && recommendation.Task.Type == SchedulingTaskType.PersonalNeeds)
            {
                var now = Time.realtimeSinceStartup;
                float nextAt;
                if (PersonalNeedsIdleNextCheck.TryGetValue(behavior, out nextAt) && now < nextAt)
                {
                    allowed = false;
                }
                else
                {
                    PersonalNeedsIdleNextCheck[behavior] = now + 5f;
                }
            }
            else if (allowed)
            {
                PersonalNeedsIdleNextCheck.Remove(behavior);
            }

            SchedulingEngineService.RecordDispatcherApply(allowed);
            return true;
        }

        private static void SetAdaptiveBackoff(Dictionary<object, BackoffState> backoff, object instance, float baseDelay, float maxDelay)
        {
            if (instance == null)
            {
                return;
            }

            BackoffState state;
            if (!backoff.TryGetValue(instance, out state))
            {
                state = new BackoffState();
                backoff[instance] = state;
            }

            var minDelay = Mathf.Max(0.02f, baseDelay);
            var cap = Mathf.Max(minDelay, maxDelay);
            state.Delay = state.Delay <= 0f ? minDelay : Mathf.Min(cap, state.Delay * 1.75f);
            state.NextAt = Time.realtimeSinceStartup + state.Delay;
        }

        private static bool HasAssignedClinician(object patient)
        {
            var state = ReflectionHelpers.GetField(patient, "m_state");
            if (state == null)
            {
                return false;
            }

            return ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_doctor")) != null
                || ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_labSpecialist")) != null;
        }

        private static bool IsNurseIdleCandidate(object nurse)
        {
            return IsIdleCandidate(nurse);
        }

        private static bool IsIdleCandidate(object behavior)
        {
            if (!ReflectionHelpers.InvokeBool(behavior, "IsFree") || ReflectionHelpers.InvokeBool(behavior, "GetReserved"))
            {
                return false;
            }

            if (GetPropertyOrField(behavior, "CurrentPatient") != null)
            {
                return false;
            }

            var entity = ReflectionHelpers.GetField(behavior, "m_entity");
            var employee = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.EmployeeComponent");
            return employee == null || !ReflectionHelpers.InvokeBool(employee, "IsPerformingAProcedure");
        }

        private static NurseTaskBoardSnapshot GetNurseBoard(object department)
        {
            NurseTaskBoardSnapshot snapshot;
            var now = Time.realtimeSinceStartup;
            if (NurseBoards.TryGetValue(department, out snapshot) && now < snapshot.ExpiresAt)
            {
                return snapshot;
            }

            snapshot = BuildNurseBoard(department, now);
            NurseBoards[department] = snapshot;
            return snapshot;
        }

        private static NurseTaskBoardSnapshot BuildNurseBoard(object department, float now)
        {
            var snapshot = new NurseTaskBoardSnapshot
            {
                ExpiresAt = now + Mathf.Max(0.1f, RuntimeSettings.Config.NurseTaskBoardTtlSeconds.Value)
            };

            if (ReflectionHelpers.InvokeBool(department, "HasAnyCriticalPatients"))
            {
                snapshot.Critical += 1;
                snapshot.Score += 1000;
            }

            if (ReflectionHelpers.InvokeBool(department, "HasWaitingSurgery") || ReflectionHelpers.InvokeBool(department, "HasAnyCriticalSurgeryScheduled"))
            {
                snapshot.Surgery += 1;
                snapshot.Score += 500;
            }

            if (ReflectionHelpers.InvokeBool(department, "HasAnyHospitalizedPatientsWithScheduledProcedures"))
            {
                snapshot.HospitalizedProcedures += 1;
                snapshot.Score += 200;
            }

            if (ReflectionHelpers.InvokeBool(department, "HasAnyWaitingPatients"))
            {
                snapshot.WaitingPatients += 1;
                snapshot.Score += 25;
            }

            CountPatientNurseTasks(department, snapshot);
            return snapshot;
        }

        private static void CountPatientNurseTasks(object department, NurseTaskBoardSnapshot snapshot)
        {
            var hospital = Lopital.Hospital.Instance;
            if (hospital == null)
            {
                return;
            }

            foreach (var character in ReflectionHelpers.GetEnumerableField(hospital, "m_characters"))
            {
                var patient = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.BehaviorPatient");
                if (patient == null || !ReferenceEquals(GetPatientDepartment(patient), department))
                {
                    continue;
                }

                var hospitalization = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.HospitalizationComponent");
                var state = hospitalization == null ? null : ReflectionHelpers.GetField(hospitalization, "m_state");
                if (state == null)
                {
                    continue;
                }

                if (Equals(ReflectionHelpers.GetField(state, "m_medicinePrescribed"), true)
                    && !Equals(ReflectionHelpers.GetField(state, "m_medicineReceived"), true))
                {
                    snapshot.Medicine++;
                    snapshot.Score += 100;
                }

                if (Equals(ReflectionHelpers.GetField(state, "m_lunchReady"), true)
                    && !Equals(ReflectionHelpers.GetField(state, "m_lunchEaten"), true))
                {
                    snapshot.Food++;
                    snapshot.Score += 25;
                }

                if (Equals(ReflectionHelpers.GetField(state, "m_oustideRoom"), true))
                {
                    snapshot.Transport++;
                    snapshot.Score += 100;
                }

                if (ReflectionHelpers.InvokeBool(hospitalization, "WillCollapse"))
                {
                    snapshot.Care++;
                    snapshot.Score += 1000;
                }
            }
        }

        private static object GetNurseDepartment(object nurse)
        {
            var entity = ReflectionHelpers.GetField(nurse, "m_entity");
            var employee = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.EmployeeComponent");
            var state = ReflectionHelpers.GetField(employee, "m_state");
            return ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_department"));
        }

        private static object GetPatientDepartment(object patient)
        {
            var state = ReflectionHelpers.GetField(patient, "m_state");
            return ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_department"));
        }

        private static object GetPropertyOrField(object instance, string name)
        {
            if (instance == null)
            {
                return null;
            }

            var property = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null)
            {
                return property.GetValue(instance, null);
            }

            return ReflectionHelpers.GetField(instance, name);
        }

        private static bool IsValidTileObject(TileObject tileObject)
        {
            if (tileObject == null)
            {
                return false;
            }

            try
            {
                if (!tileObject.IsValid())
                {
                    return false;
                }

                var state = ReflectionHelpers.GetField(tileObject, "m_state");
                var error = ReflectionHelpers.GetField(state, "m_error");
                return error == null || Convert.ToInt32(error) <= 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsValidFreeObject(TileObject tileObject)
        {
            if (!IsValidTileObject(tileObject))
            {
                return false;
            }

            try
            {
                return tileObject.User == null && tileObject.Owner == null;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsValidStaffEntity(Entity entity, bool mustBeFree)
        {
            if (entity == null)
            {
                return false;
            }

            try
            {
                if (!ReflectionHelpers.InvokeBool(entity, "IsValid"))
                {
                    return false;
                }

                if (!mustBeFree)
                {
                    return true;
                }

                foreach (var typeName in new[]
                {
                    "Lopital.BehaviorDoctor",
                    "Lopital.BehaviorNurse",
                    "Lopital.BehaviorLabSpecialist",
                    "Lopital.BehaviorJanitor"
                })
                {
                    var behavior = ReflectionHelpers.GetComponentByTypeName(entity, typeName);
                    if (behavior == null)
                    {
                        continue;
                    }

                    if (!mustBeFree)
                    {
                        return true;
                    }

                    return ReflectionHelpers.InvokeBool(behavior, "IsFree") && !ReflectionHelpers.InvokeBool(behavior, "GetReserved");
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsFreeStaffSearch(MethodBase method, object[] args)
        {
            if (method != null && method.Name.IndexOf("Free", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (args == null)
            {
                return false;
            }

            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] is bool && Equals(args[i], true))
                {
                    return true;
                }
            }

            return false;
        }

        private static string BuildKey(MethodBase method, object[] args)
        {
            return BuildKey(method == null ? "unknown" : method.DeclaringType.FullName + "." + method.Name + "#" + method.GetParameters().Length, args);
        }

        private static string BuildKey(string methodKey, object[] args)
        {
            var key = methodKey ?? "unknown";
            if (args == null)
            {
                return key;
            }

            for (var i = 0; i < args.Length; i++)
            {
                key += "|" + BuildArgKey(args[i]);
            }

            return key;
        }

        private static string BuildArgKey(object value)
        {
            if (value == null)
            {
                return "null";
            }

            var array = value as IEnumerable;
            if (array != null && !(value is string))
            {
                var text = "[";
                foreach (var item in array)
                {
                    text += BuildArgKey(item) + ",";
                }

                return text + "]";
            }

            var resolved = ReflectionHelpers.ResolvePointer(value);
            if (resolved != null && !ReferenceEquals(resolved, value))
            {
                return "ptr:" + resolved.GetHashCode();
            }

            var type = value.GetType();
            if (type.FullName != null && type.FullName.StartsWith("GLib.Vector", StringComparison.Ordinal))
            {
                return type.Name + ":" + ReflectionHelpers.GetField(value, "m_x") + "," + ReflectionHelpers.GetField(value, "m_y");
            }

            if (type.IsEnum || type.IsPrimitive || value is string)
            {
                return value.ToString();
            }

            return type.Name + "@" + value.GetHashCode();
        }

        private static void Prune<T>(Dictionary<string, TimedCacheEntry<T>> cache, float now)
        {
            var expired = new List<string>();
            foreach (var pair in cache)
            {
                if (now >= pair.Value.ExpiresAt)
                {
                    expired.Add(pair.Key);
                }
            }

            foreach (var key in expired)
            {
                cache.Remove(key);
            }
        }

        private static void Prune(Dictionary<object, BackoffState> cache, float now)
        {
            var expired = new List<object>();
            foreach (var pair in cache)
            {
                if (now >= pair.Value.NextAt)
                {
                    expired.Add(pair.Key);
                }
            }

            foreach (var key in expired)
            {
                cache.Remove(key);
            }
        }

        private static void PruneNurseBoards(float now)
        {
            var expired = new List<object>();
            foreach (var pair in NurseBoards)
            {
                if (now >= pair.Value.ExpiresAt)
                {
                    expired.Add(pair.Key);
                }
            }

            foreach (var key in expired)
            {
                NurseBoards.Remove(key);
            }
        }
    }

    [HarmonyPatch]
    internal static class ObjectSearchCacheTagsEntityPatch
    {
        private const string Key = "MapScriptInterface.FindClosestFreeObjectWithTags#entity";

        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.MapScriptInterface");
            return type == null ? null : AccessTools.Method(type, "FindClosestFreeObjectWithTags", new[]
            {
                typeof(Entity),
                typeof(Entity),
                typeof(Vector2i),
                typeof(Room),
                typeof(string[]),
                typeof(AccessRights),
                typeof(bool),
                typeof(DatabaseEntryRef<GameDBRoomType>[]),
                typeof(bool)
            });
        }

        private static bool Prefix(Entity character, Entity owner, Vector2i position, Room room, string[] tags, AccessRights accessRights, bool allowObjectsWithAttachments, DatabaseEntryRef<GameDBRoomType>[] roomTypes, bool allowedOutsideOfRoom, ref TileObject __result)
        {
            return !PerformanceOptimizationService.TryGetCachedObjectSearch(Key, new object[] { character, owner, position, room, tags, accessRights, allowObjectsWithAttachments, roomTypes, allowedOutsideOfRoom }, ref __result);
        }

        private static void Postfix(Entity character, Entity owner, Vector2i position, Room room, string[] tags, AccessRights accessRights, bool allowObjectsWithAttachments, DatabaseEntryRef<GameDBRoomType>[] roomTypes, bool allowedOutsideOfRoom, TileObject __result)
        {
            PerformanceOptimizationService.StoreObjectSearch(Key, new object[] { character, owner, position, room, tags, accessRights, allowObjectsWithAttachments, roomTypes, allowedOutsideOfRoom }, __result);
        }
    }

    [HarmonyPatch]
    internal static class ObjectSearchCacheTagsDepartmentPatch
    {
        private const string Key = "MapScriptInterface.FindClosestFreeObjectWithTags#department";

        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.MapScriptInterface");
            return type == null ? null : AccessTools.Method(type, "FindClosestFreeObjectWithTags", new[]
            {
                typeof(Vector2i),
                typeof(int),
                typeof(Department),
                typeof(string[]),
                typeof(AccessRights),
                typeof(GameDBRoomType)
            });
        }

        private static bool Prefix(Vector2i position, int floorIndex, Department department, string[] tags, AccessRights accessRights, GameDBRoomType roomType, ref TileObject __result)
        {
            return !PerformanceOptimizationService.TryGetCachedObjectSearch(Key, new object[] { position, floorIndex, department, tags, accessRights, roomType }, ref __result);
        }

        private static void Postfix(Vector2i position, int floorIndex, Department department, string[] tags, AccessRights accessRights, GameDBRoomType roomType, TileObject __result)
        {
            PerformanceOptimizationService.StoreObjectSearch(Key, new object[] { position, floorIndex, department, tags, accessRights, roomType }, __result);
        }
    }

    [HarmonyPatch]
    internal static class ObjectSearchCacheTagsRoomTagsPatch
    {
        private const string Key = "MapScriptInterface.FindClosestFreeObjectWithTagsAndRoomTags#department";

        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.MapScriptInterface");
            return type == null ? null : AccessTools.Method(type, "FindClosestFreeObjectWithTagsAndRoomTags", new[]
            {
                typeof(Vector2i),
                typeof(int),
                typeof(Department),
                typeof(string[]),
                typeof(AccessRights),
                typeof(string[])
            });
        }

        private static bool Prefix(Vector2i position, int floorIndex, Department department, string[] tags, AccessRights accessRights, string[] roomTags, ref TileObject __result)
        {
            return !PerformanceOptimizationService.TryGetCachedObjectSearch(Key, new object[] { position, floorIndex, department, tags, accessRights, roomTags }, ref __result);
        }

        private static void Postfix(Vector2i position, int floorIndex, Department department, string[] tags, AccessRights accessRights, string[] roomTags, TileObject __result)
        {
            PerformanceOptimizationService.StoreObjectSearch(Key, new object[] { position, floorIndex, department, tags, accessRights, roomTags }, __result);
        }
    }

    [HarmonyPatch]
    internal static class ObjectSearchCacheTagEntityPatch
    {
        private const string Key = "MapScriptInterface.FindClosestFreeObjectWithTag#entity";

        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.MapScriptInterface");
            return type == null ? null : AccessTools.Method(type, "FindClosestFreeObjectWithTag", new[]
            {
                typeof(Entity),
                typeof(Entity),
                typeof(Vector2i),
                typeof(Room),
                typeof(string),
                typeof(AccessRights),
                typeof(bool),
                typeof(DatabaseEntryRef<GameDBRoomType>[]),
                typeof(bool)
            });
        }

        private static bool Prefix(Entity character, Entity owner, Vector2i position, Room room, string tag, AccessRights accessRights, bool allowObjectsWithAttachments, DatabaseEntryRef<GameDBRoomType>[] roomTypes, bool allowedOutsideOfRoom, ref TileObject __result)
        {
            return !PerformanceOptimizationService.TryGetCachedObjectSearch(Key, new object[] { character, owner, position, room, tag, accessRights, allowObjectsWithAttachments, roomTypes, allowedOutsideOfRoom }, ref __result);
        }

        private static void Postfix(Entity character, Entity owner, Vector2i position, Room room, string tag, AccessRights accessRights, bool allowObjectsWithAttachments, DatabaseEntryRef<GameDBRoomType>[] roomTypes, bool allowedOutsideOfRoom, TileObject __result)
        {
            PerformanceOptimizationService.StoreObjectSearch(Key, new object[] { character, owner, position, room, tag, accessRights, allowObjectsWithAttachments, roomTypes, allowedOutsideOfRoom }, __result);
        }
    }

    [HarmonyPatch]
    internal static class CenterObjectSearchCachePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName("Lopital.MapScriptInterface");
            if (type == null)
            {
                yield break;
            }

            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if ((method.Name == "FindClosestCenterObjectWithTag"
                        || method.Name == "FindClosestCenterObjectWithTagShortestPath"
                        || method.Name == "FindClosestObjectWithTag"
                        || method.Name == "FindClosestObjectWithTags")
                    && method.ReturnType == typeof(TileObject))
                {
                    yield return method;
                }
            }
        }

        private static bool Prefix(MethodBase __originalMethod, object[] __args, ref TileObject __result)
        {
            var key = __originalMethod == null
                ? "MapScriptInterface.CenterObject#unknown"
                : "MapScriptInterface." + __originalMethod.Name + "#" + __originalMethod.GetParameters().Length;
            return !PerformanceOptimizationService.TryGetCachedCenterObjectSearch(key, __args, ref __result);
        }

        private static void Postfix(MethodBase __originalMethod, object[] __args, TileObject __result)
        {
            var key = __originalMethod == null
                ? "MapScriptInterface.CenterObject#unknown"
                : "MapScriptInterface." + __originalMethod.Name + "#" + __originalMethod.GetParameters().Length;
            PerformanceOptimizationService.StoreCenterObjectSearch(key, __args, __result);
        }
    }

    [HarmonyPatch]
    internal static class SelectNextStepBackoffPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.HospitalizationComponent");
            return type == null ? null : AccessTools.Method(type, "SelectNextStep", new[] { typeof(float) });
        }

        private static bool Prefix(object __instance, ref bool __result)
        {
            return !PerformanceOptimizationService.ShouldSkipSelectNextStep(__instance, ref __result);
        }

        private static void Postfix(object __instance, bool __result)
        {
            PerformanceOptimizationService.StoreSelectNextStepResult(__instance, __result);
        }
    }

    [HarmonyPatch]
    internal static class ReservationNegativeCachePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName("Lopital.ProcedureComponent");
            if (type == null)
            {
                yield break;
            }

            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if ((method.Name == "ReserveExamination" || method.Name == "ReserveProcedure")
                    && method.ReturnType == typeof(ProcedureSceneAvailability))
                {
                    yield return method;
                }
            }
        }

        private static bool Prefix(MethodBase __originalMethod, object[] __args, ref ProcedureSceneAvailability __result)
        {
            return !PerformanceOptimizationService.TryGetReservationFailure(__originalMethod, __args, ref __result);
        }

        private static void Postfix(MethodBase __originalMethod, object[] __args, ProcedureSceneAvailability __result)
        {
            PerformanceOptimizationService.StoreReservationResult(__originalMethod, __args, __result);
        }
    }

    [HarmonyPatch]
    internal static class NurseIdleBackoffPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.BehaviorNurse");
            return type == null ? null : AccessTools.Method(type, "UpdateStateIdle", new[] { typeof(float) });
        }

        private static bool Prefix(object __instance)
        {
            return !PerformanceOptimizationService.ShouldSkipNurseIdle(__instance);
        }

        private static void Postfix(object __instance)
        {
            PerformanceOptimizationService.StoreNurseIdleResult(__instance);
        }
    }

    [HarmonyPatch]
    internal static class DoctorIdleDispatcherPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.BehaviorDoctor");
            return type == null ? null : AccessTools.Method(type, "UpdateStateIdle", new[] { typeof(float) });
        }

        private static bool Prefix(object __instance)
        {
            return !PerformanceOptimizationService.ShouldSkipDoctorIdle(__instance);
        }
    }

    [HarmonyPatch]
    internal static class LabSpecialistIdleDispatcherPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.BehaviorLabSpecialist");
            return type == null ? null : AccessTools.Method(type, "UpdateStateIdle", new[] { typeof(float) });
        }

        private static bool Prefix(object __instance)
        {
            return !PerformanceOptimizationService.ShouldSkipLabSpecialistIdle(__instance);
        }
    }

    [HarmonyPatch]
    internal static class JanitorAdminIdleDispatcherPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.BehaviorJanitor");
            return type == null ? null : AccessTools.Method(type, "UpdateStateAdminIdle", new[] { typeof(float) });
        }

        private static bool Prefix(object __instance)
        {
            return !PerformanceOptimizationService.ShouldSkipJanitorAdminIdle(__instance);
        }
    }

    [HarmonyPatch]
    internal static class OutpatientQueueBackoffPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.BehaviorPatient");
            return type == null ? null : AccessTools.Method(type, "UpdateStateWaitingSitting", new[] { typeof(float) });
        }

        private static bool Prefix(object __instance)
        {
            return !PerformanceOptimizationService.ShouldSkipWaitingSitting(__instance);
        }

        private static void Postfix(object __instance)
        {
            PerformanceOptimizationService.StoreWaitingSittingResult(__instance);
        }
    }

    [HarmonyPatch]
    internal static class PatientDoctorSearchBackoffPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.BehaviorPatient");
            return type == null ? null : AccessTools.Method(type, "FindDoctorOrLabSpecialist", new[] { typeof(bool) });
        }

        private static bool Prefix(object __instance)
        {
            return !PerformanceOptimizationService.ShouldSkipPatientDoctorSearch(__instance);
        }

        private static void Postfix(object __instance)
        {
            PerformanceOptimizationService.StorePatientDoctorSearchResult(__instance);
        }
    }

    [HarmonyPatch]
    internal static class DoctorSearchCachePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName("Lopital.MapScriptInterface");
            if (type == null)
            {
                yield break;
            }

            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (ShouldCacheStaffSearch(method)
                    && method.ReturnType == typeof(Entity))
                {
                    yield return method;
                }
            }
        }

        private static bool ShouldCacheStaffSearch(MethodInfo method)
        {
            if (method == null)
            {
                return false;
            }

            switch (method.Name)
            {
                case "FindClosestDoctorWithQualification":
                case "FindClosestFreeDoctorWithQualification":
                case "FindClosestNurseWithQualification":
                case "FindClosestFreeNurseWithQualification":
                case "FindClosestFreeMedicalEmployee":
                case "FindLabSpecialistAssingedToARoomTag":
                case "FindLabSpecialistAssingedToARoomTagLowestWorkload":
                case "FindLabSpecialistAssingedToARoomType":
                case "FindLabSpecialistAssingedToRoom":
                case "FindJanitorAssignedAssignedToRoom":
                case "FindJanitorAssignedToARoomTagLowestWorkload":
                case "FindJanitorAssignedToARoomType":
                    return true;
                default:
                    return false;
            }
        }

        private static bool Prefix(MethodBase __originalMethod, object[] __args, ref Entity __result)
        {
            return !PerformanceOptimizationService.TryGetCachedEntitySearch(__originalMethod, __args, ref __result);
        }

        private static void Postfix(MethodBase __originalMethod, object[] __args, Entity __result)
        {
            PerformanceOptimizationService.StoreEntitySearch(__originalMethod, __args, __result);
        }
    }
}
