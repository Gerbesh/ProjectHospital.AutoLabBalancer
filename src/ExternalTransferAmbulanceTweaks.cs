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
    internal static class ExternalTransferAmbulanceTweaksService
    {
        private const float DefaultMultiplier = 3f;

        public static bool QueueBrokerEnabled
        {
            get
            {
                return RuntimeSettings.Config != null
                    && RuntimeSettings.Config.Enabled.Value
                    && RuntimeSettings.Config.EnableExternalTransferQueueBroker.Value;
            }
        }

        public static bool ParamedicSpeedEnabled
        {
            get
            {
                return QueueBrokerEnabled
                    && RuntimeSettings.Config.EnableExternalTransferParamedicSpeed.Value;
            }
        }

        public static float Multiplier
        {
            get { return RuntimeSettings.Config == null ? DefaultMultiplier : Mathf.Max(1f, RuntimeSettings.Config.ExternalTransferAmbulanceSpeedMultiplier.Value); }
        }

        public static void ApplyExternalAmbulanceTimeScale(object ambulance, ref float timeStep)
        {
            // Intentionally no-op. The external ambulance state machine owns exactly one
            // patient/paramedic and desynchronizes if its timeStep is accelerated.
        }

        public static void ApplyParamedicMovementExtraSteps(object walkComponent, int updateCount, float deltaTime)
        {
            if (!ParamedicSpeedEnabled || walkComponent == null || updateCount <= 0 || deltaTime <= 0f)
            {
                return;
            }

            var entity = ReflectionHelpers.GetField(walkComponent, "m_entity");
            var paramedic = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.BehaviorParamedic");
            if (!IsTransferParamedic(paramedic))
            {
                return;
            }

            var desiredExtraSteps = Math.Max(0, (int)Math.Floor((Multiplier - 1f) * updateCount));
            var extraSteps = Math.Min(desiredExtraSteps, 24);
            if (extraSteps <= 0)
            {
                return;
            }

            var updateMovement = AccessTools.Method(walkComponent.GetType(), "UpdateMovement");
            var routeField = AccessTools.Field(walkComponent.GetType(), "m_route");
            var floor = ReflectionHelpers.GetField(walkComponent, "m_floor");
            if (updateMovement == null || routeField == null || floor == null)
            {
                return;
            }

            var movementDeltaTime = Mathf.Min(deltaTime, 0.05f);
            for (var i = 0; i < extraSteps && routeField.GetValue(walkComponent) != null; i++)
            {
                updateMovement.Invoke(walkComponent, new[] { floor, (object)movementDeltaTime });
            }
        }

        public static void ApplyParamedicAnimationTimeScale(object animModelComponent, ref float deltaTime)
        {
            if (!ParamedicSpeedEnabled || animModelComponent == null || deltaTime <= 0f || deltaTime > 0.05f)
            {
                return;
            }

            var entity = ReflectionHelpers.GetField(animModelComponent, "m_entity");
            var paramedic = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.BehaviorParamedic");
            if (IsTransferParamedic(paramedic))
            {
                deltaTime *= Multiplier;
            }
        }

        public static void TryCreateParallelExternalAmbulance(object manager, ref Ambulance result)
        {
            // Disabled intentionally. Hidden duplicate external ambulances can desynchronize
            // vanilla AmbulancePersistentData and freeze transfer flow.
            return;
        }

        public static void HideSecondaryExternalAmbulance(object ambulance)
        {
            // Intentionally no-op. Older builds tried to hide duplicate external ambulance jobs;
            // that can freeze vanilla transfer flow, so the broker no longer creates/hides jobs.
        }

        private static bool IsTransferParamedic(object paramedic)
        {
            var state = ReflectionHelpers.GetField(paramedic, "m_state");
            var patient = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_currentPatient"));
            var behaviorPatient = ReflectionHelpers.GetComponentByTypeName(patient, "Lopital.BehaviorPatient");
            var patientState = ReflectionHelpers.GetField(behaviorPatient, "m_state");
            return Equals(ReflectionHelpers.GetField(patientState, "m_sentAway"), true);
        }

        private static bool IsExternalTransferAmbulance(object ambulance)
        {
            var state = ReflectionHelpers.GetField(ambulance, "m_state");
            return Equals(ReflectionHelpers.GetField(state, "m_external"), true)
                && !Equals(ReflectionHelpers.GetField(state, "m_isHelicopter"), true);
        }
    }

    internal sealed class ExternalTransferQueueSnapshot
    {
        public bool Ready;
        public string Warning;
        public float BuiltAt;
        public int SentAwayPatients;
        public int WaitingTransfers;
        public int ExternalAmbulances;
        public int ActiveTransfers;
        public int ActiveParamedics;
        public int StuckTransfers;
        public float MaxActiveStateAge;
        public string ActiveState;
    }

    internal static class ExternalTransferQueueBrokerService
    {
        private static readonly object Sync = new object();
        private static ExternalTransferQueueSnapshot _snapshot;
        private static float _nextRebuildAt;

        public static ExternalTransferQueueSnapshot Snapshot
        {
            get
            {
                lock (Sync)
                {
                    return _snapshot;
                }
            }
        }

        public static void Tick(float now)
        {
            if (!ExternalTransferAmbulanceTweaksService.QueueBrokerEnabled || now < _nextRebuildAt)
            {
                return;
            }

            _nextRebuildAt = now + 1.0f;
            Rebuild(now);
        }

        private static void Rebuild(float now)
        {
            var snapshot = new ExternalTransferQueueSnapshot { BuiltAt = now };
            try
            {
                var activePatients = new HashSet<object>(ReferenceEqualityComparer.Instance);
                CountAmbulances(snapshot, activePatients);
                CountSentAwayPatients(snapshot, activePatients);
                snapshot.Ready = true;
            }
            catch (Exception ex)
            {
                snapshot.Warning = ex.GetType().Name + ": " + ex.Message;
            }

            lock (Sync)
            {
                _snapshot = snapshot;
            }
        }

        private static void CountSentAwayPatients(ExternalTransferQueueSnapshot snapshot, HashSet<object> activePatients)
        {
            var hospital = Lopital.Hospital.Instance;
            if (hospital == null)
            {
                snapshot.Warning = "Hospital.Instance is null.";
                return;
            }

            foreach (var character in ReflectionHelpers.GetEnumerableField(hospital, "m_characters"))
            {
                var behaviorPatient = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.BehaviorPatient");
                if (!IsSentAwayPatient(behaviorPatient))
                {
                    continue;
                }

                snapshot.SentAwayPatients++;
                if (!activePatients.Contains(character))
                {
                    snapshot.WaitingTransfers++;
                }
            }
        }

        private static void CountAmbulances(ExternalTransferQueueSnapshot snapshot, HashSet<object> activePatients)
        {
            var manager = AmbulanceManager.Instance;
            var ambulances = ReflectionHelpers.GetField(manager, "m_ambulances") as IList;
            if (ambulances == null)
            {
                return;
            }

            foreach (var ambulance in ambulances)
            {
                var state = ReflectionHelpers.GetField(ambulance, "m_state");
                if (!Equals(ReflectionHelpers.GetField(state, "m_external"), true)
                    || Equals(ReflectionHelpers.GetField(state, "m_isHelicopter"), true))
                {
                    continue;
                }

                snapshot.ExternalAmbulances++;
                var patient = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_patient"));
                if (patient != null)
                {
                    activePatients.Add(patient);
                }

                var ambulanceState = Convert.ToString(ReflectionHelpers.GetField(state, "m_state"));
                var isActive = patient != null || !string.Equals(ambulanceState, "PARKED", StringComparison.OrdinalIgnoreCase);
                if (!isActive)
                {
                    continue;
                }

                snapshot.ActiveTransfers++;
                var timeInState = ToFloat(ReflectionHelpers.GetField(state, "m_timeInState"));
                if (timeInState > snapshot.MaxActiveStateAge)
                {
                    snapshot.MaxActiveStateAge = timeInState;
                    snapshot.ActiveState = ambulanceState;
                }

                if (timeInState >= GetStuckWarningSeconds())
                {
                    snapshot.StuckTransfers++;
                }

                var paramedic = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_paramedic"));
                if (paramedic != null)
                {
                    snapshot.ActiveParamedics++;
                }
            }
        }

        private static bool IsSentAwayPatient(object behaviorPatient)
        {
            var state = ReflectionHelpers.GetField(behaviorPatient, "m_state");
            return Equals(ReflectionHelpers.GetField(state, "m_sentAway"), true)
                && !Equals(ReflectionHelpers.GetField(state, "m_sentHome"), true)
                && !Equals(ReflectionHelpers.GetField(state, "m_deathTriggered"), true);
        }

        private static float GetStuckWarningSeconds()
        {
            return RuntimeSettings.Config == null ? 120f : Mathf.Max(10f, RuntimeSettings.Config.ExternalTransferStuckWarningSeconds.Value);
        }

        private static float ToFloat(object value)
        {
            if (value == null)
            {
                return 0f;
            }

            try
            {
                return Convert.ToSingle(value);
            }
            catch
            {
                return 0f;
            }
        }
    }

    [HarmonyPatch(typeof(AmbulanceManager), "GetFreeExternalAmbulance")]
    internal static class ExternalTransferAmbulanceParallelPatch
    {
        private static void Postfix(object __instance, ref Ambulance __result)
        {
            ExternalTransferAmbulanceTweaksService.TryCreateParallelExternalAmbulance(__instance, ref __result);
        }
    }

    [HarmonyPatch]
    internal static class ExternalTransferAmbulanceSpeedPatch
    {
        private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName("Lopital.Ambulance");
            foreach (var methodName in new[] { "UpdateExternalAmbulanceComingBackWithPatient", "UpdateExternalAmbulanceMovingOutOfMap" })
            {
                var method = type == null ? null : AccessTools.Method(type, methodName, new[] { typeof(float) });
                if (method != null)
                {
                    yield return method;
                }
            }
        }

        private static void Prefix(object __instance, ref float timeStep)
        {
            ExternalTransferAmbulanceTweaksService.ApplyExternalAmbulanceTimeScale(__instance, ref timeStep);
        }
    }

    [HarmonyPatch]
    internal static class ExternalTransferAmbulanceVisibilityPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.Ambulance");
            return type == null ? null : AccessTools.Method(type, "Update", new[] { typeof(int), typeof(float) });
        }

        private static void Postfix(object __instance)
        {
            ExternalTransferAmbulanceTweaksService.HideSecondaryExternalAmbulance(__instance);
        }
    }

    [HarmonyPatch]
    internal static class ExternalTransferParamedicMovementPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.WalkComponent");
            return type == null ? null : AccessTools.Method(type, "MultiUpdate", new[] { typeof(int), typeof(float) });
        }

        private static void Postfix(object __instance, int updateCount, float deltaTime)
        {
            ExternalTransferAmbulanceTweaksService.ApplyParamedicMovementExtraSteps(__instance, updateCount, deltaTime);
        }
    }

    [HarmonyPatch]
    internal static class ExternalTransferParamedicAnimationPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.AnimModelComponent");
            return type == null ? null : AccessTools.Method(type, "Update", new[] { typeof(float) });
        }

        private static void Prefix(object __instance, ref float deltaTime)
        {
            ExternalTransferAmbulanceTweaksService.ApplyParamedicAnimationTimeScale(__instance, ref deltaTime);
        }
    }
}
