using System;
using System.Collections.Generic;
using HarmonyLib;
using Lopital;

namespace ProjectHospital.AutoLabBalancer
{
    internal static class DeveloperToolsService
    {
        public static int RemoveAllPatients()
        {
            var hospital = Hospital.Instance;
            if (hospital == null)
            {
                return 0;
            }

            var patients = new List<BehaviorPatient>();
            foreach (var character in ReflectionHelpers.GetEnumerableField(hospital, "m_characters"))
            {
                var patient = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.BehaviorPatient") as BehaviorPatient;
                if (patient != null)
                {
                    patients.Add(patient);
                }
            }

            var removed = 0;
            for (var i = 0; i < patients.Count; i++)
            {
                var patient = patients[i];
                if (patient == null)
                {
                    continue;
                }

                try
                {
                    var entity = ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
                    var hospitalized = IsHospitalized(entity);
                    TryClearBookmark(entity);
                    MedicalCaseRewriteService.ForgetCaseForDeveloper(patient);
                    InvokeLeave(patient, hospitalized);
                    removed++;
                }
                catch (Exception ex)
                {
                    Debug("Failed to remove patient: " + ex.GetType().Name + " " + ex.Message);
                }
            }

            Debug("Removed all patients via dev tool. Count=" + removed + ".");
            return removed;
        }

        private static bool IsHospitalized(GLib.Entity entity)
        {
            var hospitalization = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.HospitalizationComponent") as HospitalizationComponent;
            return hospitalization != null && hospitalization.IsHospitalized();
        }

        private static void InvokeLeave(BehaviorPatient patient, bool hospitalized)
        {
            var method = AccessTools.Method(typeof(BehaviorPatient), "Leave", new[] { typeof(bool), typeof(bool), typeof(bool) });
            if (method != null)
            {
                method.Invoke(patient, new object[] { false, false, hospitalized });
            }
        }

        private static void TryClearBookmark(GLib.Entity entity)
        {
            if (entity == null)
            {
                return;
            }

            var managerType = AccessTools.TypeByName("Lopital.BookmarkedCharacterManager") ?? AccessTools.TypeByName("BookmarkedCharacterManager");
            var instance = managerType == null ? null : AccessTools.Property(managerType, "Instance");
            var manager = instance == null ? null : instance.GetValue(null, null);
            var method = manager == null ? null : AccessTools.Method(manager.GetType(), "RemoveCharacter", new[] { typeof(GLib.Entity) });
            if (method != null)
            {
                method.Invoke(manager, new object[] { entity });
            }
        }

        private static void Debug(string message)
        {
            if (RuntimeSettings.Logger != null)
            {
                RuntimeSettings.Logger.LogInfo("[DeveloperTools] " + message);
            }
        }
    }
}
