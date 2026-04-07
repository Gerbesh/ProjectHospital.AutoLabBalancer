using System;
using System.Collections;
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

        public static bool Enabled
        {
            get
            {
                return RuntimeSettings.Config != null
                    && RuntimeSettings.Config.Enabled.Value
                    && RuntimeSettings.Config.EnableExternalTransferAmbulanceTweaks.Value;
            }
        }

        public static float Multiplier
        {
            get { return RuntimeSettings.Config == null ? DefaultMultiplier : Mathf.Max(1f, RuntimeSettings.Config.ExternalTransferAmbulanceSpeedMultiplier.Value); }
        }

        public static void ApplyExternalAmbulanceTimeScale(object ambulance, ref float timeStep)
        {
            if (!Enabled || timeStep <= 0f || !IsExternalTransferAmbulance(ambulance))
            {
                return;
            }

            timeStep *= Multiplier;
        }

        public static void ApplyParamedicMovementExtraSteps(object walkComponent, int updateCount, float deltaTime)
        {
            if (!Enabled || walkComponent == null || updateCount <= 0 || deltaTime <= 0f)
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
            if (!Enabled || animModelComponent == null || deltaTime <= 0f || deltaTime > 0.05f)
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
            if (!Enabled || result != null || manager == null || !RuntimeSettings.Config.EnableParallelExternalTransferAmbulances.Value)
            {
                return;
            }

            var ambulances = ReflectionHelpers.GetField(manager, "m_ambulances") as IList;
            if (ambulances == null || !HasBusyExternalAmbulance(ambulances))
            {
                return;
            }

            var freeExisting = FindFreeExternalAmbulance(ambulances);
            if (freeExisting != null)
            {
                result = freeExisting;
                return;
            }

            var externalCount = CountExternalAmbulances(ambulances);
            if (externalCount >= Math.Max(1, RuntimeSettings.Config.MaxParallelExternalTransferAmbulances.Value))
            {
                return;
            }

            var ground = Hospital.Instance == null ? null : Hospital.Instance.GetGroundFloor();
            if (ground == null)
            {
                return;
            }

            var ambulanceType = Database.Instance.GetEntry<GameDBCompositeObject>("COMPOSITE_OBJECT_AMBULANCE_01");
            var y = Math.Max(2, ground.Size.m_y - 10 - (externalCount * 3));
            var ambulanceObject = ground.AddCompositeObject(ambulanceType, new Vector2i(9, y), Direction.NE, Color.black, null, silent: true, noRotation: true);
            if (ambulanceObject == null)
            {
                return;
            }

            var ambulance = new Ambulance(ambulanceObject, external: true, helicopter: false);
            ambulances.Add(ambulance);
            HideAmbulanceObject(ambulance, true);
            result = ambulance;
            RuntimeCounters.ExternalTransferAmbulanceBatches++;
        }

        public static void HideSecondaryExternalAmbulance(object ambulance)
        {
            if (!Enabled || ambulance == null || !IsExternalTransferAmbulance(ambulance))
            {
                return;
            }

            var manager = AmbulanceManager.Instance;
            var ambulances = ReflectionHelpers.GetField(manager, "m_ambulances") as IList;
            if (ambulances == null)
            {
                return;
            }

            var primary = FindPrimaryExternalAmbulance(ambulances);
            var secondary = primary != null && !ReferenceEquals(primary, ambulance);
            HideAmbulanceObject(ambulance, secondary);
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

        private static bool HasBusyExternalAmbulance(IList ambulances)
        {
            foreach (var ambulance in ambulances)
            {
                if (!IsExternalTransferAmbulance(ambulance))
                {
                    continue;
                }

                var state = ReflectionHelpers.GetField(ambulance, "m_state");
                if (ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_patient")) != null
                    || !string.Equals(Convert.ToString(ReflectionHelpers.GetField(state, "m_state")), "PARKED", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static int CountExternalAmbulances(IList ambulances)
        {
            var count = 0;
            foreach (var ambulance in ambulances)
            {
                if (IsExternalTransferAmbulance(ambulance))
                {
                    count++;
                }
            }

            return count;
        }

        private static Ambulance FindFreeExternalAmbulance(IList ambulances)
        {
            foreach (var ambulance in ambulances)
            {
                if (!IsExternalTransferAmbulance(ambulance))
                {
                    continue;
                }

                var state = ReflectionHelpers.GetField(ambulance, "m_state");
                if (ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_patient")) == null
                    && string.Equals(Convert.ToString(ReflectionHelpers.GetField(state, "m_state")), "PARKED", StringComparison.OrdinalIgnoreCase))
                {
                    return ambulance as Ambulance;
                }
            }

            return null;
        }

        private static object FindPrimaryExternalAmbulance(IList ambulances)
        {
            foreach (var ambulance in ambulances)
            {
                if (IsExternalTransferAmbulance(ambulance))
                {
                    return ambulance;
                }
            }

            return null;
        }

        private static void HideAmbulanceObject(object ambulance, bool hidden)
        {
            var state = ReflectionHelpers.GetField(ambulance, "m_state");
            var ambulanceObject = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_ambulanceObject"));
            var objectState = ReflectionHelpers.GetField(ambulanceObject, "m_compositeObjectPersistentData");
            var field = objectState == null ? null : AccessTools.Field(objectState.GetType(), "m_hidden");
            if (field != null)
            {
                field.SetValue(objectState, hidden);
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
