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

    internal sealed class BackoffState
    {
        public float NextAt;
        public float Delay;
    }

    internal sealed class RouteRequestState
    {
        public PerformanceCacheKey Key;
        public float ExpiresAt;
    }

    internal struct PerformanceCacheKey : IEquatable<PerformanceCacheKey>
    {
        public string MethodKey;
        public int Count;
        public int P0;
        public int P1;
        public int P2;
        public int P3;
        public int P4;
        public int P5;
        public int P6;
        public int P7;
        public int P8;
        public int Hash;

        public bool Equals(PerformanceCacheKey other)
        {
            return Count == other.Count
                && Hash == other.Hash
                && string.Equals(MethodKey, other.MethodKey, StringComparison.Ordinal)
                && P0 == other.P0
                && P1 == other.P1
                && P2 == other.P2
                && P3 == other.P3
                && P4 == other.P4
                && P5 == other.P5
                && P6 == other.P6
                && P7 == other.P7
                && P8 == other.P8;
        }

        public override bool Equals(object obj)
        {
            return obj is PerformanceCacheKey && Equals((PerformanceCacheKey)obj);
        }

        public override int GetHashCode()
        {
            return Hash;
        }
    }

    internal sealed class ReservationBrokerCountersSnapshot
    {
        public long Hits;
        public long Misses;
        public long Stores;
        public long AvailableDrops;
        public long Disabled;
        public long StaffUnavailableStores;
        public long RoomUnavailableStores;
        public long EquipmentUnavailableStores;
        public long OtherFailureStores;
    }

    internal sealed class PerformanceOptimizationCountersSnapshot
    {
        public long ObjectSearchHits;
        public long ObjectSearchMisses;
        public long ObjectSearchInvalidHits;
        public long StaffSearchHits;
        public long StaffSearchMisses;
        public long StaffSearchInvalidHits;
        public long RouteRequests;
        public long RouteRepeatedRequests;
        public long ReflectionFallbacks;
        public long MissingTargets;
    }

    internal static class ReservationBrokerService
    {
        private static readonly Dictionary<PerformanceCacheKey, TimedCacheEntry<ProcedureSceneAvailability>> Failures = new Dictionary<PerformanceCacheKey, TimedCacheEntry<ProcedureSceneAvailability>>();
        private static readonly List<PerformanceCacheKey> ExpiredKeys = new List<PerformanceCacheKey>();
        private static long _hits;
        private static long _misses;
        private static long _stores;
        private static long _availableDrops;
        private static long _disabled;
        private static long _staffUnavailableStores;
        private static long _roomUnavailableStores;
        private static long _equipmentUnavailableStores;
        private static long _otherFailureStores;

        public static bool TryGet(MethodBase method, object[] args, ref ProcedureSceneAvailability result)
        {
            if (!PerformanceOptimizationService.Enabled
                || RuntimeSettings.Config == null
                || !RuntimeSettings.Config.EnablePerformanceOptimizations.Value)
            {
                _disabled++;
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
                || !RuntimeSettings.Config.EnablePerformanceOptimizations.Value)
            {
                _disabled++;
                return;
            }

            var key = BuildReservationKey(method, args);
            if (result == ProcedureSceneAvailability.AVAILABLE)
            {
                Failures.Remove(key);
                _availableDrops++;
                return;
            }

            Failures[key] = new TimedCacheEntry<ProcedureSceneAvailability>
            {
                Value = result,
                ExpiresAt = Time.realtimeSinceStartup + Mathf.Max(0.05f, RuntimeSettings.Config.ReservationBrokerTtlSeconds.Value)
            };
            _stores++;
            RecordFailureReason(result);
        }

        public static void Tick(float now)
        {
            ExpiredKeys.Clear();
            foreach (var pair in Failures)
            {
                if (now >= pair.Value.ExpiresAt)
                {
                    ExpiredKeys.Add(pair.Key);
                }
            }

            for (var i = 0; i < ExpiredKeys.Count; i++)
            {
                Failures.Remove(ExpiredKeys[i]);
            }

            ExpiredKeys.Clear();
        }

        public static ReservationBrokerCountersSnapshot GetCounters()
        {
            return new ReservationBrokerCountersSnapshot
            {
                Hits = _hits,
                Misses = _misses,
                Stores = _stores,
                AvailableDrops = _availableDrops,
                Disabled = _disabled,
                StaffUnavailableStores = _staffUnavailableStores,
                RoomUnavailableStores = _roomUnavailableStores,
                EquipmentUnavailableStores = _equipmentUnavailableStores,
                OtherFailureStores = _otherFailureStores
            };
        }

        public static void ResetCounters()
        {
            _hits = 0;
            _misses = 0;
            _stores = 0;
            _availableDrops = 0;
            _disabled = 0;
            _staffUnavailableStores = 0;
            _roomUnavailableStores = 0;
            _equipmentUnavailableStores = 0;
            _otherFailureStores = 0;
        }

        private static PerformanceCacheKey BuildReservationKey(MethodBase method, object[] args)
        {
            return PerformanceOptimizationService.BuildCacheKey(method == null ? "reservation#unknown" : "reservation#" + method.Name, args);
        }

        private static void RecordFailureReason(ProcedureSceneAvailability result)
        {
            var text = result.ToString();
            if (text.IndexOf("STAFF", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("EMPLOYEE", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("DOCTOR", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("NURSE", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _staffUnavailableStores++;
            }
            else if (text.IndexOf("ROOM", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _roomUnavailableStores++;
            }
            else if (text.IndexOf("EQUIP", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("OBJECT", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _equipmentUnavailableStores++;
            }
            else
            {
                _otherFailureStores++;
            }
        }
    }

    internal static class PerformanceOptimizationService
    {
        private static readonly Dictionary<PerformanceCacheKey, TimedCacheEntry<TileObject>> ObjectSearchCache = new Dictionary<PerformanceCacheKey, TimedCacheEntry<TileObject>>();
        private static readonly Dictionary<PerformanceCacheKey, TimedCacheEntry<TileObject>> CenterObjectSearchCache = new Dictionary<PerformanceCacheKey, TimedCacheEntry<TileObject>>();
        private static readonly Dictionary<PerformanceCacheKey, TimedCacheEntry<Entity>> EntitySearchCache = new Dictionary<PerformanceCacheKey, TimedCacheEntry<Entity>>();
        private static readonly Dictionary<PerformanceCacheKey, TimedCacheEntry<LabProcedure>> FirstIdleLabProcedureCache = new Dictionary<PerformanceCacheKey, TimedCacheEntry<LabProcedure>>();
        private static readonly Dictionary<PerformanceCacheKey, TimedCacheEntry<List<LabProcedure>>> IdleLabProcedureListCache = new Dictionary<PerformanceCacheKey, TimedCacheEntry<List<LabProcedure>>>();
        private static readonly Dictionary<object, BackoffState> SelectNextStepBackoff = new Dictionary<object, BackoffState>();
        private static readonly Dictionary<object, BackoffState> NurseIdleBackoff = new Dictionary<object, BackoffState>();
        private static readonly Dictionary<object, BackoffState> WaitingSittingBackoff = new Dictionary<object, BackoffState>();
        private static readonly Dictionary<object, BackoffState> PatientDoctorSearchBackoff = new Dictionary<object, BackoffState>();
        private static readonly Dictionary<object, float> PersonalNeedsIdleNextCheck = new Dictionary<object, float>(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<object, RouteRequestState> RouteRequestThrottle = new Dictionary<object, RouteRequestState>(ReferenceEqualityComparer.Instance);
        private static readonly List<PerformanceCacheKey> ExpiredPerformanceKeys = new List<PerformanceCacheKey>();
        private static readonly List<object> ExpiredObjectKeys = new List<object>();
        private static float _nextCleanupAt;
        private static long _objectSearchHits;
        private static long _objectSearchMisses;
        private static long _objectSearchInvalidHits;
        private static long _staffSearchHits;
        private static long _staffSearchMisses;
        private static long _staffSearchInvalidHits;
        private static long _routeRequests;
        private static long _routeRepeatedRequests;
        private static long _reflectionFallbacks;
        private static long _missingTargets;

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
            Prune(FirstIdleLabProcedureCache, now);
            Prune(IdleLabProcedureListCache, now);
            ReservationBrokerService.Tick(now);
            Prune(SelectNextStepBackoff, now);
            Prune(NurseIdleBackoff, now);
            Prune(WaitingSittingBackoff, now);
            Prune(PatientDoctorSearchBackoff, now);
            PruneRouteRequests(now);
        }

        public static bool ShouldSkipRepeatedRouteRequest(object walkComponent, object destination, int floorIndex, object movementType)
        {
            if (!Enabled
                || RuntimeSettings.Config == null
                || walkComponent == null
                || destination == null)
            {
                return false;
            }

            _routeRequests++;

            var key = BuildCacheKey("WalkComponent.SetDestination", new[] { destination, (object)floorIndex, movementType });
            var now = Time.realtimeSinceStartup;
            RouteRequestState state;
            if (RouteRequestThrottle.TryGetValue(walkComponent, out state)
                && state.Key.Equals(key)
                && now < state.ExpiresAt)
            {
                _routeRepeatedRequests++;

                return true;
            }

            RouteRequestThrottle[walkComponent] = new RouteRequestState
            {
                Key = key,
                ExpiresAt = now + Mathf.Clamp(RuntimeSettings.Config.RouteRequestThrottleSeconds.Value, 0.05f, 1.0f)
            };
            return false;
        }

        public static PerformanceOptimizationCountersSnapshot GetCounters()
        {
            return new PerformanceOptimizationCountersSnapshot
            {
                ObjectSearchHits = _objectSearchHits,
                ObjectSearchMisses = _objectSearchMisses,
                ObjectSearchInvalidHits = _objectSearchInvalidHits,
                StaffSearchHits = _staffSearchHits,
                StaffSearchMisses = _staffSearchMisses,
                StaffSearchInvalidHits = _staffSearchInvalidHits,
                RouteRequests = _routeRequests,
                RouteRepeatedRequests = _routeRepeatedRequests,
                ReflectionFallbacks = _reflectionFallbacks,
                MissingTargets = _missingTargets
            };
        }

        public static void ResetCounters()
        {
            _objectSearchHits = 0;
            _objectSearchMisses = 0;
            _objectSearchInvalidHits = 0;
            _staffSearchHits = 0;
            _staffSearchMisses = 0;
            _staffSearchInvalidHits = 0;
            _routeRequests = 0;
            _routeRepeatedRequests = 0;
            _reflectionFallbacks = 0;
            _missingTargets = 0;
        }

        public static void RecordMissingTarget()
        {
            _missingTargets++;
        }

        public static bool TryGetCachedObjectSearch(MethodBase method, object[] args, ref TileObject result)
        {
            return TryGetCachedObjectSearch(method == null ? "unknown" : method.DeclaringType.FullName + "." + method.Name + "#" + method.GetParameters().Length, args, ref result);
        }

        public static bool TryGetCachedObjectSearch(string methodKey, object[] args, ref TileObject result)
        {
            return TryGetCachedObjectSearch(BuildCacheKey(methodKey, args), ref result);
        }

        public static bool TryGetCachedObjectSearch(PerformanceCacheKey key, ref TileObject result)
        {
            if (!Enabled)
            {
                return false;
            }

            TimedCacheEntry<TileObject> entry;
            if (!ObjectSearchCache.TryGetValue(key, out entry) || Time.realtimeSinceStartup >= entry.ExpiresAt)
            {
                _objectSearchMisses++;
                return false;
            }

            if (!IsValidFreeObject(entry.Value))
            {
                ObjectSearchCache.Remove(key);
                _objectSearchInvalidHits++;
                return false;
            }

            result = entry.Value;
            _objectSearchHits++;
            return true;
        }

        public static void StoreObjectSearch(MethodBase method, object[] args, TileObject result)
        {
            StoreObjectSearch(method == null ? "unknown" : method.DeclaringType.FullName + "." + method.Name + "#" + method.GetParameters().Length, args, result);
        }

        public static void StoreObjectSearch(string methodKey, object[] args, TileObject result)
        {
            StoreObjectSearch(BuildCacheKey(methodKey, args), result);
        }

        public static void StoreObjectSearch(PerformanceCacheKey key, TileObject result)
        {
            if (!Enabled || !IsValidFreeObject(result))
            {
                return;
            }

            ObjectSearchCache[key] = new TimedCacheEntry<TileObject>
            {
                Value = result,
                ExpiresAt = Time.realtimeSinceStartup + Mathf.Max(0.1f, RuntimeSettings.Config.ObjectSearchCacheTtlSeconds.Value)
            };
        }

        public static bool TryGetCachedCenterObjectSearch(string methodKey, object[] args, ref TileObject result)
        {
            if (!Enabled)
            {
                return false;
            }

            TimedCacheEntry<TileObject> entry;
            var key = BuildCacheKey(methodKey, args);
            if (!CenterObjectSearchCache.TryGetValue(key, out entry) || Time.realtimeSinceStartup >= entry.ExpiresAt)
            {
                _objectSearchMisses++;
                return false;
            }

            if (!IsValidTileObject(entry.Value))
            {
                CenterObjectSearchCache.Remove(key);
                _objectSearchInvalidHits++;
                return false;
            }

            result = entry.Value;
            _objectSearchHits++;
            return true;
        }

        public static void StoreCenterObjectSearch(string methodKey, object[] args, TileObject result)
        {
            if (!Enabled || !IsValidTileObject(result))
            {
                return;
            }

            CenterObjectSearchCache[BuildCacheKey(methodKey, args)] = new TimedCacheEntry<TileObject>
            {
                Value = result,
                ExpiresAt = Time.realtimeSinceStartup + Mathf.Max(0.05f, RuntimeSettings.Config.ObjectSearchCacheTtlSeconds.Value * 0.5f)
            };
        }

        public static bool TryGetCachedEntitySearch(MethodBase method, object[] args, ref Entity result)
        {
            if (!Enabled)
            {
                return false;
            }

            var key = BuildCacheKey(method, args);
            TimedCacheEntry<Entity> entry;
            if (!EntitySearchCache.TryGetValue(key, out entry) || Time.realtimeSinceStartup >= entry.ExpiresAt)
            {
                _staffSearchMisses++;
                return false;
            }

            if (!IsValidStaffEntity(entry.Value, IsFreeStaffSearch(method, args)))
            {
                EntitySearchCache.Remove(key);
                _staffSearchInvalidHits++;
                return false;
            }

            result = entry.Value;
            _staffSearchHits++;
            return true;
        }

        public static void StoreEntitySearch(MethodBase method, object[] args, Entity result)
        {
            if (!Enabled || !IsValidStaffEntity(result, IsFreeStaffSearch(method, args)))
            {
                return;
            }

            EntitySearchCache[BuildCacheKey(method, args)] = new TimedCacheEntry<Entity>
            {
                Value = result,
                ExpiresAt = Time.realtimeSinceStartup + Mathf.Max(0.05f, RuntimeSettings.Config.DoctorSearchCacheTtlSeconds.Value)
            };
        }

        public static bool TryGetFirstIdleLabProcedure(object[] args, ref LabProcedure result)
        {
            if (!Enabled)
            {
                return false;
            }

            var key = BuildCacheKey("LabProcedureManager.GetFirstIdleLabProcedure", args);
            TimedCacheEntry<LabProcedure> entry;
            if (!FirstIdleLabProcedureCache.TryGetValue(key, out entry) || Time.realtimeSinceStartup >= entry.ExpiresAt)
            {
                return false;
            }

            if (!IsIdleLabProcedure(entry.Value))
            {
                FirstIdleLabProcedureCache.Remove(key);
                return false;
            }

            result = entry.Value;
            return true;
        }

        public static void StoreFirstIdleLabProcedure(object[] args, LabProcedure result)
        {
            if (!Enabled || !IsIdleLabProcedure(result))
            {
                return;
            }

            FirstIdleLabProcedureCache[BuildCacheKey("LabProcedureManager.GetFirstIdleLabProcedure", args)] = new TimedCacheEntry<LabProcedure>
            {
                Value = result,
                ExpiresAt = Time.realtimeSinceStartup + 0.20f
            };
        }

        public static bool TryGetIdleLabProcedureList(object[] args, ref List<LabProcedure> result)
        {
            if (!Enabled)
            {
                return false;
            }

            var key = BuildCacheKey("LabProcedureManager.GetIdleLabProcedures", args);
            TimedCacheEntry<List<LabProcedure>> entry;
            if (!IdleLabProcedureListCache.TryGetValue(key, out entry) || Time.realtimeSinceStartup >= entry.ExpiresAt)
            {
                return false;
            }

            for (var i = 0; i < entry.Value.Count; i++)
            {
                if (!IsIdleLabProcedure(entry.Value[i]))
                {
                    IdleLabProcedureListCache.Remove(key);
                    return false;
                }
            }

            result = new List<LabProcedure>(entry.Value);
            return true;
        }

        public static void StoreIdleLabProcedureList(object[] args, List<LabProcedure> result)
        {
            if (!Enabled || result == null || result.Count == 0)
            {
                return;
            }

            for (var i = 0; i < result.Count; i++)
            {
                if (!IsIdleLabProcedure(result[i]))
                {
                    return;
                }
            }

            IdleLabProcedureListCache[BuildCacheKey("LabProcedureManager.GetIdleLabProcedures", args)] = new TimedCacheEntry<List<LabProcedure>>
            {
                Value = new List<LabProcedure>(result),
                ExpiresAt = Time.realtimeSinceStartup + 0.20f
            };
        }

        public static void ClearLabProcedureQueryCache()
        {
            FirstIdleLabProcedureCache.Clear();
            IdleLabProcedureListCache.Clear();
        }

        private static bool IsIdleLabProcedure(LabProcedure procedure)
        {
            return procedure != null && ReflectionHelpers.InvokeBool(procedure, "IsIdle");
        }

        public static bool ShouldSkipSelectNextStep(object hospitalization, ref bool result)
        {
            if (!Enabled || !RuntimeSettings.Config.EnableSelectNextStepBackoff.Value || hospitalization == null)
            {
                return false;
            }

            if (ShouldAlwaysRunSelectNextStep(hospitalization))
            {
                SelectNextStepBackoff.Remove(hospitalization);
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

        private static bool ShouldAlwaysRunSelectNextStep(object hospitalization)
        {
            var state = ReflectionHelpers.GetField(hospitalization, "m_state");
            if (state == null)
            {
                return false;
            }

            var reservationStatus = ReflectionHelpers.GetField(state, "m_procedureReservationStatus");
            if (reservationStatus != null && !string.Equals(reservationStatus.ToString(), "NONE", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (ReflectionHelpers.InvokeBool(hospitalization, "WillCollapse"))
            {
                return true;
            }

            var entity = ReflectionHelpers.GetField(hospitalization, "m_entity");
            var patient = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.BehaviorPatient");
            return HasCriticalDoctorBinding(patient, entity);
        }

        private static bool HasCriticalDoctorBinding(object patient, object patientEntity)
        {
            if (patient == null || patientEntity == null)
            {
                return false;
            }

            if (ReflectionHelpers.InvokeBool(patient, "HasCriticalSurgeryPlanned"))
            {
                return true;
            }

            var state = ReflectionHelpers.GetField(patient, "m_state");
            var doctorEntity = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_doctor"));
            if (doctorEntity == null)
            {
                return false;
            }

            var doctor = ReflectionHelpers.GetComponentByTypeName(doctorEntity, "Lopital.BehaviorDoctor");
            var currentPatient = GetPropertyOrField(doctor, "CurrentPatient");
            return ReferenceEquals(currentPatient, patientEntity);
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
            if (!Enabled || nurse == null)
            {
                return false;
            }

            if (TryForceCriticalPersonalNeeds(nurse, "nurse"))
            {
                return true;
            }

            var department = GetNurseDepartment(nurse);
            if (HasDepartmentSurgeryDemand(department))
            {
                NurseIdleBackoff.Remove(nurse);
                SchedulingEngineService.RecordNurseGating(false);
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

            if (department == null)
            {
                return false;
            }

            SchedulingDepartmentBoard schedulingBoard;
            if (SchedulingEngineService.TryGetDepartmentBoard(department, out schedulingBoard))
            {
                if (schedulingBoard.NurseScore > 0)
                {
                    NurseIdleBackoff.Remove(nurse);
                    SchedulingEngineService.RecordNurseGating(false);
                    return false;
                }

                var skip = ShouldSkipShortBackoff(nurse, NurseIdleBackoff);
                SchedulingEngineService.RecordNurseGating(skip);
                return skip;
            }

            return false;
        }

        public static bool ShouldSkipDoctorIdle(object doctor)
        {
            return TryForceCriticalPersonalNeeds(doctor, "doctor");
        }

        public static bool ShouldSkipLabSpecialistIdle(object labSpecialist)
        {
            return TryForceCriticalPersonalNeeds(labSpecialist, "lab");
        }

        public static bool ShouldSkipJanitorAdminIdle(object janitor)
        {
            if (TryForceCriticalPersonalNeeds(janitor, "janitor"))
            {
                return true;
            }

            bool dispatcherDecision;
            return TryGetDispatcherIdleDecision(janitor, "janitor", out dispatcherDecision) && !dispatcherDecision;
        }

        public static void StoreNurseIdleResult(object nurse)
        {
            if (!Enabled || nurse == null)
            {
                return;
            }

            var isFree = ReflectionHelpers.InvokeBool(nurse, "IsFree");
            var reserved = ReflectionHelpers.InvokeBool(nurse, "GetReserved");
            var department = GetNurseDepartment(nurse);
            SchedulingDepartmentBoard board;
            var hasWork = HasDepartmentSurgeryDemand(department)
                || (department != null && SchedulingEngineService.TryGetDepartmentBoard(department, out board) && board.NurseScore > 0);
            if (isFree && !reserved && !hasWork)
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
            SchedulingDepartmentBoard board;
            if (SchedulingEngineService.TryGetPatientDepartmentBoard(patient, out board))
            {
                if (HasVisibleDoctorWork(board) || board.FreeDoctors > 0 || board.FreeLabSpecialists > 0)
                {
                    WaitingSittingBackoff.Remove(patient);
                    PatientDoctorSearchBackoff.Remove(patient);
                    SchedulingEngineService.RecordOutpatientGating(false);
                    return false;
                }

                var skip = ShouldSkipShortBackoff(patient, WaitingSittingBackoff);
                SchedulingEngineService.RecordOutpatientGating(skip);
                return skip;
            }

            return false;
        }

        public static void StoreWaitingSittingResult(object patient)
        {
            if (!Enabled || patient == null)
            {
                return;
            }

            SetAdaptiveBackoff(WaitingSittingBackoff, patient, RuntimeSettings.Config.OutpatientQueueBackoffSeconds.Value, RuntimeSettings.Config.OutpatientQueueBackoffMaxSeconds.Value);
        }

        public static bool ShouldSkipPatientDoctorSearch(object patient)
        {
            SchedulingDepartmentBoard board;
            if (SchedulingEngineService.TryGetPatientDepartmentBoard(patient, out board))
            {
                if (HasVisibleDoctorWork(board) || board.FreeDoctors > 0 || board.FreeLabSpecialists > 0)
                {
                    PatientDoctorSearchBackoff.Remove(patient);
                    WaitingSittingBackoff.Remove(patient);
                    SchedulingEngineService.RecordDoctorSearchGating(false);
                    return false;
                }

                var skip = ShouldSkipShortBackoff(patient, PatientDoctorSearchBackoff);
                SchedulingEngineService.RecordDoctorSearchGating(skip);
                return skip;
            }

            return false;
        }

        public static void StorePatientDoctorSearchResult(object patient)
        {
            if (!Enabled || patient == null)
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

        private static bool ShouldSkipShortBackoff(object instance, Dictionary<object, BackoffState> backoff)
        {
            if (!Enabled || instance == null)
            {
                return false;
            }

            BackoffState state;
            return backoff.TryGetValue(instance, out state) && Time.realtimeSinceStartup < state.NextAt;
        }

        private static bool HasVisibleDoctorWork(SchedulingDepartmentBoard board)
        {
            return board != null
                && (board.DoctorScore > 0
                    || board.WaitingPatients > 0
                    || board.CriticalPatients > 0
                    || board.PlannedSurgeryPatients > 0);
        }

        private static bool TryForceCriticalPersonalNeeds(object behavior, string role)
        {
            if (!Enabled || behavior == null || !IsIdleCandidate(behavior) || !HasCriticalNeed(behavior))
            {
                return false;
            }

            var department = GetEmployeeDepartment(behavior);
            SchedulingDepartmentBoard board;
            if (department != null
                && SchedulingEngineService.TryGetDepartmentBoard(department, out board)
                && board.CollapseCareTasks > 0)
            {
                return false;
            }

            if (HasDepartmentSurgeryDemand(department) && IsSurgeryStaff(behavior, role))
            {
                return false;
            }

            var accessRights = AccessRights.STAFF_ONLY;
            if (!InvokeCheckNeeds(behavior, role, accessRights))
            {
                return false;
            }

            InvokeNoArg(behavior, "CancelBrowsing");
            InvokeNoArg(behavior, "CancelUsingComputer");
            var entity = ReflectionHelpers.GetField(behavior, "m_entity");
            var speech = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.SpeechComponent");
            InvokeNoArg(speech, "HideBubble");
            SwitchToFulfillingNeeds(behavior, role);
            PersonalNeedsIdleNextCheck[behavior] = Time.realtimeSinceStartup + 10f;
            return true;
        }

        private static bool HasCriticalNeed(object behavior)
        {
            var entity = ReflectionHelpers.GetField(behavior, "m_entity");
            var mood = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.MoodComponent");
            var state = ReflectionHelpers.GetField(mood, "m_state");
            foreach (var need in ReflectionHelpers.GetEnumerableField(state, "m_needs"))
            {
                var value = ReflectionHelpers.GetField(need, "m_currentValue");
                if (value == null)
                {
                    continue;
                }

                try
                {
                    if (Convert.ToSingle(value) >= 95f)
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool InvokeCheckNeeds(object behavior, string role, AccessRights accessRights)
        {
            MethodInfo method;
            object[] args;
            if (string.Equals(role, "nurse", StringComparison.OrdinalIgnoreCase))
            {
                method = AccessTools.Method(behavior.GetType(), "CheckNeeds", new[] { typeof(AccessRights), typeof(bool) });
                args = new object[] { accessRights, false };
            }
            else if (string.Equals(role, "janitor", StringComparison.OrdinalIgnoreCase))
            {
                method = AccessTools.Method(behavior.GetType(), "CheckNeeds", Type.EmptyTypes);
                args = null;
            }
            else
            {
                method = AccessTools.Method(behavior.GetType(), "CheckNeeds", new[] { typeof(AccessRights) });
                args = new object[] { accessRights };
            }

            if (method == null)
            {
                RecordMissingTarget();
                return false;
            }

            try
            {
                return Equals(method.Invoke(behavior, args), true);
            }
            catch
            {
                return false;
            }
        }

        private static void SwitchToFulfillingNeeds(object behavior, string role)
        {
            var stateTypeName = string.Equals(role, "lab", StringComparison.OrdinalIgnoreCase)
                ? "Lopital.LabSpecialistState"
                : string.Equals(role, "nurse", StringComparison.OrdinalIgnoreCase)
                    ? "Lopital.NurseState"
                    : string.Equals(role, "janitor", StringComparison.OrdinalIgnoreCase)
                        ? "Lopital.BehaviorJanitorState"
                        : "Lopital.DoctorState";
            var stateName = string.Equals(role, "doctor", StringComparison.OrdinalIgnoreCase)
                ? "FulfilingNeeds"
                : "FulfillingNeeds";
            var stateType = AccessTools.TypeByName(stateTypeName);
            if (stateType == null)
            {
                RecordMissingTarget();
                return;
            }

            try
            {
                var state = Enum.Parse(stateType, stateName);
                var method = AccessTools.Method(behavior.GetType(), "SwitchState", new[] { stateType });
                if (method == null)
                {
                    RecordMissingTarget();
                    return;
                }

                method.Invoke(behavior, new[] { state });
            }
            catch
            {
            }
        }

        private static void InvokeNoArg(object instance, string methodName)
        {
            if (instance == null)
            {
                return;
            }

            var method = AccessTools.Method(instance.GetType(), methodName, Type.EmptyTypes);
            if (method == null)
            {
                return;
            }

            try
            {
                method.Invoke(instance, null);
            }
            catch
            {
            }
        }

        private static bool TryGetDispatcherIdleDecision(object behavior, string role, out bool allowed)
        {
            allowed = false;
            if (!Enabled
                || RuntimeSettings.Config == null
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

        private static object GetNurseDepartment(object nurse)
        {
            return GetEmployeeDepartment(nurse);
        }

        private static bool HasDepartmentSurgeryDemand(object department)
        {
            return department != null
                && (ReflectionHelpers.InvokeBool(department, "HasWaitingSurgery")
                    || ReflectionHelpers.InvokeBool(department, "HasAnyCriticalSurgeryScheduled"));
        }

        private static bool IsSurgeryStaff(object behavior, string role)
        {
            var entity = ReflectionHelpers.GetField(behavior, "m_entity");
            var employee = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.EmployeeComponent");
            if (employee == null)
            {
                return false;
            }

            if (string.Equals(role, "nurse", StringComparison.OrdinalIgnoreCase))
            {
                return HasEmployeeRole(employee, "EMPL_ROLE_SURGERY_NURSE");
            }

            if (string.Equals(role, "doctor", StringComparison.OrdinalIgnoreCase))
            {
                return HasEmployeeRole(employee, "EMPL_ROLE_SURGERY")
                    || HasEmployeeRole(employee, "EMPL_ROLE_SURGERY_ANESTHESIOLOGY")
                    || HasEmployeeRole(employee, "EMPL_ROLE_SURGERY_ASSIST");
            }

            return false;
        }

        private static bool HasEmployeeRole(object employee, string roleId)
        {
            try
            {
                var role = Database.Instance.GetEntry<GameDBEmployeeRole>(roleId);
                var method = employee == null || role == null
                    ? null
                    : employee.GetType().GetMethod("HasRole", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(GameDBEmployeeRole) }, null);
                return method != null && Equals(method.Invoke(employee, new object[] { role }), true);
            }
            catch
            {
                return false;
            }
        }

        private static object GetEmployeeDepartment(object behavior)
        {
            var entity = ReflectionHelpers.GetField(behavior, "m_entity");
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

        public static PerformanceCacheKey BuildCacheKey(MethodBase method, object[] args)
        {
            return BuildCacheKey(method == null ? "unknown" : method.DeclaringType.FullName + "." + method.Name + "#" + method.GetParameters().Length, args);
        }

        public static PerformanceCacheKey BuildCacheKey(string methodKey, object[] args)
        {
            var key = new PerformanceCacheKey
            {
                MethodKey = methodKey ?? "unknown",
                Count = args == null ? 0 : args.Length
            };

            unchecked
            {
                var hash = 17;
                hash = (hash * 31) + key.MethodKey.GetHashCode();
                hash = (hash * 31) + key.Count;
                for (var i = 0; args != null && i < args.Length && i < 9; i++)
                {
                    var part = BuildArgHash(args[i]);
                    switch (i)
                    {
                        case 0:
                            key.P0 = part;
                            break;
                        case 1:
                            key.P1 = part;
                            break;
                        case 2:
                            key.P2 = part;
                            break;
                        case 3:
                            key.P3 = part;
                            break;
                        case 4:
                            key.P4 = part;
                            break;
                        case 5:
                            key.P5 = part;
                            break;
                        case 6:
                            key.P6 = part;
                            break;
                        case 7:
                            key.P7 = part;
                            break;
                        case 8:
                            key.P8 = part;
                            break;
                    }

                    hash = (hash * 31) + part;
                }

                key.Hash = hash;
            }

            return key;
        }

        public static PerformanceCacheKey BuildCacheKey(string methodKey, object p0, object p1, object p2, object p3, object p4, object p5)
        {
            var key = CreateCacheKey(methodKey, 6);
            AddCacheKeyPart(ref key, 0, p0);
            AddCacheKeyPart(ref key, 1, p1);
            AddCacheKeyPart(ref key, 2, p2);
            AddCacheKeyPart(ref key, 3, p3);
            AddCacheKeyPart(ref key, 4, p4);
            AddCacheKeyPart(ref key, 5, p5);
            return key;
        }

        public static PerformanceCacheKey BuildCacheKey(string methodKey, object p0, object p1, object p2, object p3, object p4, object p5, object p6, object p7, object p8)
        {
            var key = CreateCacheKey(methodKey, 9);
            AddCacheKeyPart(ref key, 0, p0);
            AddCacheKeyPart(ref key, 1, p1);
            AddCacheKeyPart(ref key, 2, p2);
            AddCacheKeyPart(ref key, 3, p3);
            AddCacheKeyPart(ref key, 4, p4);
            AddCacheKeyPart(ref key, 5, p5);
            AddCacheKeyPart(ref key, 6, p6);
            AddCacheKeyPart(ref key, 7, p7);
            AddCacheKeyPart(ref key, 8, p8);
            return key;
        }

        private static PerformanceCacheKey CreateCacheKey(string methodKey, int count)
        {
            unchecked
            {
                var key = new PerformanceCacheKey
                {
                    MethodKey = methodKey ?? "unknown",
                    Count = count
                };
                key.Hash = ((17 * 31) + key.MethodKey.GetHashCode()) * 31 + key.Count;
                return key;
            }
        }

        private static void AddCacheKeyPart(ref PerformanceCacheKey key, int index, object value)
        {
            var part = BuildArgHash(value);
            switch (index)
            {
                case 0:
                    key.P0 = part;
                    break;
                case 1:
                    key.P1 = part;
                    break;
                case 2:
                    key.P2 = part;
                    break;
                case 3:
                    key.P3 = part;
                    break;
                case 4:
                    key.P4 = part;
                    break;
                case 5:
                    key.P5 = part;
                    break;
                case 6:
                    key.P6 = part;
                    break;
                case 7:
                    key.P7 = part;
                    break;
                case 8:
                    key.P8 = part;
                    break;
            }

            unchecked
            {
                key.Hash = (key.Hash * 31) + part;
            }
        }

        private static int BuildArgHash(object value)
        {
            if (value == null)
            {
                return 0;
            }

            var array = value as IEnumerable;
            if (array != null && !(value is string))
            {
                unchecked
                {
                    var hash = 23;
                    foreach (var item in array)
                    {
                        hash = (hash * 31) + BuildArgHash(item);
                    }

                    return hash;
                }
            }

            var resolved = ReflectionHelpers.ResolvePointer(value);
            if (resolved != null && !ReferenceEquals(resolved, value))
            {
                return ReferenceEqualityComparer.Instance.GetHashCode(resolved);
            }

            var type = value.GetType();
            if (type.FullName != null && type.FullName.StartsWith("GLib.Vector", StringComparison.Ordinal))
            {
                unchecked
                {
                    return (type.Name.GetHashCode() * 397)
                        ^ ((ReflectionHelpers.GetField(value, "m_x") ?? 0).GetHashCode() * 31)
                        ^ (ReflectionHelpers.GetField(value, "m_y") ?? 0).GetHashCode();
                }
            }

            if (type.IsEnum || type.IsPrimitive || value is string)
            {
                return value.GetHashCode();
            }

            var locId = ReflectionHelpers.GetStringProperty(value, "LocID");
            if (!string.IsNullOrEmpty(locId))
            {
                return locId.GetHashCode();
            }

            var id = ReflectionHelpers.GetField(value, "ID") ?? ReflectionHelpers.GetField(value, "m_entityID");
            return id == null ? ReferenceEqualityComparer.Instance.GetHashCode(value) : id.GetHashCode();
        }

        private static void Prune<T>(Dictionary<PerformanceCacheKey, TimedCacheEntry<T>> cache, float now)
        {
            ExpiredPerformanceKeys.Clear();
            foreach (var pair in cache)
            {
                if (now >= pair.Value.ExpiresAt)
                {
                    ExpiredPerformanceKeys.Add(pair.Key);
                }
            }

            for (var i = 0; i < ExpiredPerformanceKeys.Count; i++)
            {
                cache.Remove(ExpiredPerformanceKeys[i]);
            }

            ExpiredPerformanceKeys.Clear();
        }

        private static void Prune(Dictionary<object, BackoffState> cache, float now)
        {
            ExpiredObjectKeys.Clear();
            foreach (var pair in cache)
            {
                if (now >= pair.Value.NextAt)
                {
                    ExpiredObjectKeys.Add(pair.Key);
                }
            }

            for (var i = 0; i < ExpiredObjectKeys.Count; i++)
            {
                cache.Remove(ExpiredObjectKeys[i]);
            }

            ExpiredObjectKeys.Clear();
        }

        private static void PruneRouteRequests(float now)
        {
            ExpiredObjectKeys.Clear();
            foreach (var pair in RouteRequestThrottle)
            {
                if (now >= pair.Value.ExpiresAt)
                {
                    ExpiredObjectKeys.Add(pair.Key);
                }
            }

            for (var i = 0; i < ExpiredObjectKeys.Count; i++)
            {
                RouteRequestThrottle.Remove(ExpiredObjectKeys[i]);
            }

            ExpiredObjectKeys.Clear();
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
            var key = PerformanceOptimizationService.BuildCacheKey(Key, character, owner, position, room, tags, accessRights, allowObjectsWithAttachments, roomTypes, allowedOutsideOfRoom);
            return !PerformanceOptimizationService.TryGetCachedObjectSearch(key, ref __result);
        }

        private static void Postfix(Entity character, Entity owner, Vector2i position, Room room, string[] tags, AccessRights accessRights, bool allowObjectsWithAttachments, DatabaseEntryRef<GameDBRoomType>[] roomTypes, bool allowedOutsideOfRoom, TileObject __result)
        {
            var key = PerformanceOptimizationService.BuildCacheKey(Key, character, owner, position, room, tags, accessRights, allowObjectsWithAttachments, roomTypes, allowedOutsideOfRoom);
            PerformanceOptimizationService.StoreObjectSearch(key, __result);
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
            var key = PerformanceOptimizationService.BuildCacheKey(Key, position, floorIndex, department, tags, accessRights, roomType);
            return !PerformanceOptimizationService.TryGetCachedObjectSearch(key, ref __result);
        }

        private static void Postfix(Vector2i position, int floorIndex, Department department, string[] tags, AccessRights accessRights, GameDBRoomType roomType, TileObject __result)
        {
            var key = PerformanceOptimizationService.BuildCacheKey(Key, position, floorIndex, department, tags, accessRights, roomType);
            PerformanceOptimizationService.StoreObjectSearch(key, __result);
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
            var key = PerformanceOptimizationService.BuildCacheKey(Key, position, floorIndex, department, tags, accessRights, roomTags);
            return !PerformanceOptimizationService.TryGetCachedObjectSearch(key, ref __result);
        }

        private static void Postfix(Vector2i position, int floorIndex, Department department, string[] tags, AccessRights accessRights, string[] roomTags, TileObject __result)
        {
            var key = PerformanceOptimizationService.BuildCacheKey(Key, position, floorIndex, department, tags, accessRights, roomTags);
            PerformanceOptimizationService.StoreObjectSearch(key, __result);
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
            var key = PerformanceOptimizationService.BuildCacheKey(Key, character, owner, position, room, tag, accessRights, allowObjectsWithAttachments, roomTypes, allowedOutsideOfRoom);
            return !PerformanceOptimizationService.TryGetCachedObjectSearch(key, ref __result);
        }

        private static void Postfix(Entity character, Entity owner, Vector2i position, Room room, string tag, AccessRights accessRights, bool allowObjectsWithAttachments, DatabaseEntryRef<GameDBRoomType>[] roomTypes, bool allowedOutsideOfRoom, TileObject __result)
        {
            var key = PerformanceOptimizationService.BuildCacheKey(Key, character, owner, position, room, tag, accessRights, allowObjectsWithAttachments, roomTypes, allowedOutsideOfRoom);
            PerformanceOptimizationService.StoreObjectSearch(key, __result);
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
    internal static class RouteRequestThrottleVector2iPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.WalkComponent");
            var vector2i = AccessTools.TypeByName("GLib.Vector2i");
            var movementType = AccessTools.TypeByName("Lopital.MovementType");
            return type == null || vector2i == null || movementType == null
                ? null
                : AccessTools.Method(type, "SetDestination", new[] { vector2i, typeof(int), movementType });
        }

        private static bool Prefix(object __instance, object destinationTile, int floorIndex, object movementType)
        {
            return !PerformanceOptimizationService.ShouldSkipRepeatedRouteRequest(__instance, destinationTile, floorIndex, movementType);
        }
    }

    [HarmonyPatch]
    internal static class RouteRequestThrottleVector2fPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.WalkComponent");
            var vector2f = AccessTools.TypeByName("GLib.Vector2f");
            var movementType = AccessTools.TypeByName("Lopital.MovementType");
            return type == null || vector2f == null || movementType == null
                ? null
                : AccessTools.Method(type, "SetDestination", new[] { vector2f, typeof(int), movementType });
        }

        private static bool Prefix(object __instance, object destination, int floorIndex, object movementType)
        {
            return !PerformanceOptimizationService.ShouldSkipRepeatedRouteRequest(__instance, destination, floorIndex, movementType);
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

    [HarmonyPatch]
    internal static class LabProcedureFirstIdleQueryCachePatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.LabProcedureManager");
            return type == null ? null : AccessTools.Method(type, "GetFirstIdleLabProcedure", new[] { typeof(Department) });
        }

        private static bool Prefix(object[] __args, ref LabProcedure __result)
        {
            return !PerformanceOptimizationService.TryGetFirstIdleLabProcedure(__args, ref __result);
        }

        private static void Postfix(object[] __args, LabProcedure __result)
        {
            PerformanceOptimizationService.StoreFirstIdleLabProcedure(__args, __result);
        }
    }

    [HarmonyPatch]
    internal static class LabProcedureIdleListQueryCachePatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.LabProcedureManager");
            return type == null ? null : AccessTools.Method(type, "GetIdleLabProcedures", new[] { typeof(Department), typeof(Room), typeof(bool), typeof(bool) });
        }

        private static bool Prefix(object[] __args, ref List<LabProcedure> __result)
        {
            return !PerformanceOptimizationService.TryGetIdleLabProcedureList(__args, ref __result);
        }

        private static void Postfix(object[] __args, List<LabProcedure> __result)
        {
            PerformanceOptimizationService.StoreIdleLabProcedureList(__args, __result);
        }
    }

    [HarmonyPatch]
    internal static class LabProcedureQueryCacheInvalidationPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName("Lopital.LabProcedureManager");
            if (type == null)
            {
                yield break;
            }

            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name == "AddLabProcedure" || method.Name == "Reset")
                {
                    yield return method;
                }
            }
        }

        private static void Postfix()
        {
            PerformanceOptimizationService.ClearLabProcedureQueryCache();
        }
    }
}
