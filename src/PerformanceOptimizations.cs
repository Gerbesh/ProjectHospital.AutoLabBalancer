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

    internal static class PerformanceOptimizationService
    {
        private static readonly Dictionary<string, TimedCacheEntry<TileObject>> ObjectSearchCache = new Dictionary<string, TimedCacheEntry<TileObject>>();
        private static readonly Dictionary<string, TimedCacheEntry<ProcedureSceneAvailability>> ReservationFailureCache = new Dictionary<string, TimedCacheEntry<ProcedureSceneAvailability>>();
        private static readonly Dictionary<object, NurseTaskBoardSnapshot> NurseBoards = new Dictionary<object, NurseTaskBoardSnapshot>();
        private static readonly Dictionary<object, float> SelectNextStepBackoff = new Dictionary<object, float>();
        private static readonly Dictionary<object, float> NurseIdleBackoff = new Dictionary<object, float>();
        private static readonly Dictionary<object, float> WaitingSittingBackoff = new Dictionary<object, float>();
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
            Prune(ReservationFailureCache, now);
            PruneNurseBoards(now);
            Prune(SelectNextStepBackoff, now);
            Prune(NurseIdleBackoff, now);
            Prune(WaitingSittingBackoff, now);
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

        public static bool ShouldSkipSelectNextStep(object hospitalization, ref bool result)
        {
            if (!Enabled || !RuntimeSettings.Config.EnableSelectNextStepBackoff.Value || hospitalization == null)
            {
                return false;
            }

            float nextAt;
            if (SelectNextStepBackoff.TryGetValue(hospitalization, out nextAt) && Time.realtimeSinceStartup < nextAt)
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

            SelectNextStepBackoff[hospitalization] = Time.realtimeSinceStartup + Mathf.Max(0.05f, RuntimeSettings.Config.SelectNextStepBackoffSeconds.Value);
        }

        public static bool TryGetReservationFailure(MethodBase method, object[] args, ref ProcedureSceneAvailability result)
        {
            if (!Enabled || !RuntimeSettings.Config.EnableReservationNegativeCache.Value)
            {
                return false;
            }

            TimedCacheEntry<ProcedureSceneAvailability> entry;
            if (!ReservationFailureCache.TryGetValue(BuildKey(method, args), out entry) || Time.realtimeSinceStartup >= entry.ExpiresAt)
            {
                return false;
            }

            result = entry.Value;
            return true;
        }

        public static void StoreReservationResult(MethodBase method, object[] args, ProcedureSceneAvailability result)
        {
            if (!Enabled || !RuntimeSettings.Config.EnableReservationNegativeCache.Value)
            {
                return;
            }

            var key = BuildKey(method, args);
            if (result == ProcedureSceneAvailability.AVAILABLE)
            {
                ReservationFailureCache.Remove(key);
                return;
            }

            ReservationFailureCache[key] = new TimedCacheEntry<ProcedureSceneAvailability>
            {
                Value = result,
                ExpiresAt = Time.realtimeSinceStartup + Mathf.Max(0.05f, RuntimeSettings.Config.ReservationNegativeCacheTtlSeconds.Value)
            };
        }

        public static bool ShouldSkipNurseIdle(object nurse)
        {
            if (!Enabled || !RuntimeSettings.Config.EnableNurseTaskBoard.Value || nurse == null)
            {
                return false;
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

            var board = GetNurseBoard(department);
            if (board.Score > 0)
            {
                NurseIdleBackoff.Remove(nurse);
                return false;
            }

            return ShouldSkipShortBackoff(nurse, NurseIdleBackoff, RuntimeSettings.Config.EnableNurseIdleBackoff.Value);
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
                NurseIdleBackoff[nurse] = Time.realtimeSinceStartup + Mathf.Max(0.02f, RuntimeSettings.Config.NurseIdleBackoffSeconds.Value);
            }
        }

        public static bool ShouldSkipWaitingSitting(object patient)
        {
            return ShouldSkipShortBackoff(patient, WaitingSittingBackoff, RuntimeSettings.Config.EnableOutpatientQueueBackoff.Value);
        }

        public static void StoreWaitingSittingResult(object patient)
        {
            if (!Enabled || !RuntimeSettings.Config.EnableOutpatientQueueBackoff.Value || patient == null)
            {
                return;
            }

            WaitingSittingBackoff[patient] = Time.realtimeSinceStartup + Mathf.Max(0.02f, RuntimeSettings.Config.OutpatientQueueBackoffSeconds.Value);
        }

        private static bool ShouldSkipShortBackoff(object instance, Dictionary<object, float> backoff, bool enabled)
        {
            if (!Enabled || !enabled || instance == null)
            {
                return false;
            }

            float nextAt;
            return backoff.TryGetValue(instance, out nextAt) && Time.realtimeSinceStartup < nextAt;
        }

        private static bool IsNurseIdleCandidate(object nurse)
        {
            if (!ReflectionHelpers.InvokeBool(nurse, "IsFree") || ReflectionHelpers.InvokeBool(nurse, "GetReserved"))
            {
                return false;
            }

            if (GetPropertyOrField(nurse, "CurrentPatient") != null)
            {
                return false;
            }

            var entity = ReflectionHelpers.GetField(nurse, "m_entity");
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

            var property = AccessTools.Property(instance.GetType(), name);
            if (property != null)
            {
                return property.GetValue(instance, null);
            }

            return ReflectionHelpers.GetField(instance, name);
        }

        private static bool IsValidFreeObject(TileObject tileObject)
        {
            if (tileObject == null)
            {
                return false;
            }

            try
            {
                if (!tileObject.IsValid() || tileObject.User != null || tileObject.Owner != null)
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

        private static void Prune(Dictionary<object, float> cache, float now)
        {
            var expired = new List<object>();
            foreach (var pair in cache)
            {
                if (now >= pair.Value)
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

        private static bool Prefix(Entity ignoredUser, Entity ignoredOwner, Vector2i position, Room room, string[] tags, AccessRights accessRights, bool needsToBeFree, DatabaseEntryRef<GameDBRoomType>[] allowedRoomTypes, bool onlyComposite, ref TileObject __result)
        {
            return !PerformanceOptimizationService.TryGetCachedObjectSearch(Key, new object[] { ignoredUser, ignoredOwner, position, room, tags, accessRights, needsToBeFree, allowedRoomTypes, onlyComposite }, ref __result);
        }

        private static void Postfix(Entity ignoredUser, Entity ignoredOwner, Vector2i position, Room room, string[] tags, AccessRights accessRights, bool needsToBeFree, DatabaseEntryRef<GameDBRoomType>[] allowedRoomTypes, bool onlyComposite, TileObject __result)
        {
            PerformanceOptimizationService.StoreObjectSearch(Key, new object[] { ignoredUser, ignoredOwner, position, room, tags, accessRights, needsToBeFree, allowedRoomTypes, onlyComposite }, __result);
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

        private static bool Prefix(Entity ignoredUser, Entity ignoredOwner, Vector2i position, Room room, string tag, AccessRights accessRights, bool needsToBeFree, DatabaseEntryRef<GameDBRoomType>[] allowedRoomTypes, bool onlyComposite, ref TileObject __result)
        {
            return !PerformanceOptimizationService.TryGetCachedObjectSearch(Key, new object[] { ignoredUser, ignoredOwner, position, room, tag, accessRights, needsToBeFree, allowedRoomTypes, onlyComposite }, ref __result);
        }

        private static void Postfix(Entity ignoredUser, Entity ignoredOwner, Vector2i position, Room room, string tag, AccessRights accessRights, bool needsToBeFree, DatabaseEntryRef<GameDBRoomType>[] allowedRoomTypes, bool onlyComposite, TileObject __result)
        {
            PerformanceOptimizationService.StoreObjectSearch(Key, new object[] { ignoredUser, ignoredOwner, position, room, tag, accessRights, needsToBeFree, allowedRoomTypes, onlyComposite }, __result);
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
}
