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

    internal static class PerformanceOptimizationService
    {
        private static readonly Dictionary<string, TimedCacheEntry<TileObject>> ObjectSearchCache = new Dictionary<string, TimedCacheEntry<TileObject>>();
        private static readonly Dictionary<string, TimedCacheEntry<ProcedureSceneAvailability>> ReservationFailureCache = new Dictionary<string, TimedCacheEntry<ProcedureSceneAvailability>>();
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
            Prune(SelectNextStepBackoff, now);
            Prune(NurseIdleBackoff, now);
            Prune(WaitingSittingBackoff, now);
        }

        public static bool TryGetCachedObjectSearch(MethodBase method, object[] args, ref TileObject result)
        {
            if (!Enabled || !RuntimeSettings.Config.EnableObjectSearchCache.Value)
            {
                return false;
            }

            TimedCacheEntry<TileObject> entry;
            if (!ObjectSearchCache.TryGetValue(BuildKey(method, args), out entry) || Time.realtimeSinceStartup >= entry.ExpiresAt)
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
            if (!Enabled || !RuntimeSettings.Config.EnableObjectSearchCache.Value || !IsValidFreeObject(result))
            {
                return;
            }

            ObjectSearchCache[BuildKey(method, args)] = new TimedCacheEntry<TileObject>
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
            if (isFree && !reserved)
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
            var key = (method == null ? "unknown" : method.DeclaringType.FullName + "." + method.Name + "#" + method.GetParameters().Length);
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
    }

    /*
     * Disabled for 0.13.1: HarmonyX rejects this multi-target patch when the
     * patch method asks for __args. The rest of the optimization layer should
     * still load, so object search caching will be reintroduced via explicit
     * per-overload patches instead of PatchAll attributes.
     */
    internal static class ObjectSearchCachePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName("Lopital.MapScriptInterface");
            if (type == null)
            {
                yield break;
            }

            foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if ((method.Name == "FindClosestFreeObjectWithTags" || method.Name == "FindClosestFreeObjectWithTag")
                    && method.ReturnType == typeof(TileObject))
                {
                    yield return method;
                }
            }
        }

        private static bool Prefix(MethodBase __originalMethod, object[] __args, ref TileObject __result)
        {
            return !PerformanceOptimizationService.TryGetCachedObjectSearch(__originalMethod, __args, ref __result);
        }

        private static void Postfix(MethodBase __originalMethod, object[] __args, TileObject __result)
        {
            PerformanceOptimizationService.StoreObjectSearch(__originalMethod, __args, __result);
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
