using System;
using GLib;
using HarmonyLib;
using Lopital;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectHospital.AutoLabBalancer
{
    internal static class UnknownEmployeeService
    {
        private static readonly System.Random Rng = new System.Random();
        private static Entity sm_renderedSkillCharacter;

        public static bool Enabled
        {
            get { return RuntimeSettings.Config != null && RuntimeSettings.Config.Enabled.Value && RuntimeSettings.Config.EnableUnknownEmployees.Value; }
        }

        public static void RandomizeGeneratedEmployeeSkills(Entity character)
        {
            if (!Enabled || character == null)
            {
                return;
            }

            var employee = character.GetComponent<EmployeeComponent>();
            if (employee == null || employee.m_state == null || employee.m_state.m_skillSet == null)
            {
                return;
            }

            RandomizeSkill(employee.m_state.m_skillSet.m_qualifications);
            RandomizeSkill(employee.m_state.m_skillSet.m_specialization1);
            RandomizeSkill(employee.m_state.m_skillSet.m_specialization2);
            employee.CacheRoleCount();
        }

        public static void HideGeneratedEmployeePerks(object perkSetObject, object characterObject, bool generatedPerkSet)
        {
            if (!Enabled || !generatedPerkSet)
            {
                return;
            }

            var character = characterObject as Entity;
            var perkSet = perkSetObject as PerkSet;
            if (character == null || perkSet == null || character.GetComponent<EmployeeComponent>() == null)
            {
                return;
            }

            for (var i = 0; i < perkSet.m_perks.Count; i++)
            {
                perkSet.m_perks[i].m_hidden = true;
            }
        }

        public static void Tick(float now)
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                var hospital = Hospital.Instance;
                if (hospital == null || hospital.m_characters == null)
                {
                    return;
                }

                for (var i = 0; i < hospital.m_characters.Count; i++)
                {
                    RevealDuePerks(hospital.m_characters[i]);
                }
            }
            catch (Exception ex)
            {
                if (RuntimeSettings.Logger != null)
                {
                    RuntimeSettings.Logger.LogError("[UnknownEmployees] tick failed: " + ex);
                }
            }
        }

        public static void BeginSkillRender(Entity character)
        {
            sm_renderedSkillCharacter = character;
        }

        public static void EndSkillRender(Entity character)
        {
            if (ReferenceEquals(sm_renderedSkillCharacter, character))
            {
                sm_renderedSkillCharacter = null;
            }
        }

        public static bool ShouldMaskSkills(Entity character)
        {
            if (!Enabled || character == null || character.GetComponent<EmployeeComponent>() == null)
            {
                return false;
            }

            if (!IsHiredEmployee(character))
            {
                return true;
            }

            var employee = character.GetComponent<EmployeeComponent>();
            return DaysEmployed(employee) < GetSkillRevealDelayDays(character);
        }

        public static void MaskRenderedSkillSegment(SkillLevelSegmentController controller)
        {
            if (controller == null || !ShouldMaskSkills(sm_renderedSkillCharacter))
            {
                return;
            }

            var gauge = controller.m_levelProgressGauge == null ? null : controller.m_levelProgressGauge.GetComponentInChildren<GaugeController>();
            if (gauge != null)
            {
                gauge.SetValues(0, 100, "?");
            }
        }

        public static void MaskHiringCard(HiringCardCharacterPanelController controller, Entity character)
        {
            if (controller == null || !ShouldMaskSkills(character))
            {
                return;
            }

            SetUnknownSkillText(controller.m_skill01Text);
            SetUnknownSkillText(controller.m_skill02Text);
            SetUnknownSkillText(controller.m_specialization01Text);
            SetUnknownSkillText(controller.m_specialization02Text);
            SetGauge(controller.m_levelGaugeDoctors, 0, 5);
            SetGauge(controller.m_levelGaugeNurses, 0, 3);
            SetGauge(controller.m_levelGaugeTechnologosts, 0, 3);
            SetGauge(controller.m_levelGaugeJanitors, 0, 3);
        }

        public static string MaskSkillLevelText(string value)
        {
            return Enabled ? " (?)" : value;
        }

        private static void RevealDuePerks(Entity character)
        {
            if (character == null || !IsHiredEmployee(character))
            {
                return;
            }

            var employee = character.GetComponent<EmployeeComponent>();
            var perkComponent = character.GetComponent<PerkComponent>();
            var perkSet = perkComponent == null ? null : perkComponent.m_perkSet;
            if (employee == null || perkSet == null || perkSet.m_perks == null || perkSet.m_perks.Count == 0)
            {
                return;
            }

            var targetRevealed = Math.Min(perkSet.m_perks.Count, DaysEmployed(employee) / 5);
            while (CountVisiblePerks(perkSet) < targetRevealed && perkSet.GetHiddenPerkCount() > 0)
            {
                perkSet.RevealFirstHiddenPerk();
            }
        }

        private static int CountVisiblePerks(PerkSet perkSet)
        {
            var count = 0;
            for (var i = 0; i < perkSet.m_perks.Count; i++)
            {
                if (!perkSet.m_perks[i].m_hidden)
                {
                    count++;
                }
            }

            return count;
        }

        private static int DaysEmployed(EmployeeComponent employee)
        {
            return Math.Max(0, 1 - employee.m_state.m_startDay + DayTime.Instance.GetDay());
        }

        private static int GetSkillRevealDelayDays(Entity character)
        {
            return 3 + Math.Abs(GetStableHash(character)) % 5;
        }

        private static int GetStableHash(Entity character)
        {
            var text = character == null ? string.Empty : character.Name;
            unchecked
            {
                var hash = 23;
                for (var i = 0; i < text.Length; i++)
                {
                    hash = hash * 31 + text[i];
                }

                return hash == int.MinValue ? 0 : hash;
            }
        }

        private static bool IsHiredEmployee(Entity character)
        {
            try
            {
                var hospital = Hospital.Instance;
                if (hospital == null || hospital.m_characters == null)
                {
                    return false;
                }

                for (var i = 0; i < hospital.m_characters.Count; i++)
                {
                    if (ReferenceEquals(hospital.m_characters[i], character))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static void RandomizeSkill(System.Collections.IEnumerable skills)
        {
            if (skills == null)
            {
                return;
            }

            foreach (var skill in skills)
            {
                RandomizeSkill(skill as Skill);
            }
        }

        private static void RandomizeSkill(Skill skill)
        {
            if (skill == null)
            {
                return;
            }

            skill.m_level = 1f + (float)(Rng.NextDouble() * 4.0);
        }

        private static void SetUnknownSkillText(GameObject textObject)
        {
            var text = textObject == null ? null : textObject.GetComponent<Text>();
            if (text == null || string.IsNullOrEmpty(text.text))
            {
                return;
            }

            var open = text.text.LastIndexOf(" (", StringComparison.Ordinal);
            if (open >= 0)
            {
                text.text = text.text.Substring(0, open) + " (?)";
            }
        }

        private static void SetGauge(GameObject gaugeObject, int value, int max)
        {
            if (gaugeObject == null || !gaugeObject.activeSelf)
            {
                return;
            }

            var gauge = gaugeObject.GetComponentInChildren<GaugeIconsController>();
            if (gauge != null)
            {
                gauge.SetValues(value, max);
            }
        }
    }

    [HarmonyPatch(typeof(LopitalEntityFactory), "CreateCharacterDoctor")]
    internal static class UnknownEmployeeDoctorFactoryPatch
    {
        private static void Postfix(Entity __result)
        {
            UnknownEmployeeService.RandomizeGeneratedEmployeeSkills(__result);
        }
    }

    [HarmonyPatch(typeof(LopitalEntityFactory), "CreateCharacterNurse")]
    internal static class UnknownEmployeeNurseFactoryPatch
    {
        private static void Postfix(Entity __result)
        {
            UnknownEmployeeService.RandomizeGeneratedEmployeeSkills(__result);
        }
    }

    [HarmonyPatch(typeof(LopitalEntityFactory), "CreateCharacterLabSpecialist")]
    internal static class UnknownEmployeeLabFactoryPatch
    {
        private static void Postfix(Entity __result)
        {
            UnknownEmployeeService.RandomizeGeneratedEmployeeSkills(__result);
        }
    }

    [HarmonyPatch(typeof(LopitalEntityFactory), "CreateCharacterJanitor")]
    internal static class UnknownEmployeeJanitorFactoryPatch
    {
        private static void Postfix(Entity __result)
        {
            UnknownEmployeeService.RandomizeGeneratedEmployeeSkills(__result);
        }
    }

    [HarmonyPatch(typeof(HiringCardCharacterPanelController), "GetSkillLevelText")]
    internal static class UnknownEmployeeHiringSkillTextPatch
    {
        private static void Postfix(Skill skill, ref string __result)
        {
            __result = UnknownEmployeeService.MaskSkillLevelText(__result);
        }
    }

    [HarmonyPatch(typeof(HiringCardCharacterPanelController), "FillPersonalInfo")]
    internal static class UnknownEmployeeHiringCardPatch
    {
        private static void Postfix(HiringCardCharacterPanelController __instance, Entity character)
        {
            UnknownEmployeeService.MaskHiringCard(__instance, character);
        }
    }

    [HarmonyPatch(typeof(CharacterPanelSkillPanelController), "UpdateSkills")]
    internal static class UnknownEmployeeSkillPanelPatch
    {
        private static void Prefix(EmployeeComponent employeeComponent)
        {
            UnknownEmployeeService.BeginSkillRender(employeeComponent == null ? null : employeeComponent.m_entity);
        }

        private static void Postfix(EmployeeComponent employeeComponent)
        {
            UnknownEmployeeService.EndSkillRender(employeeComponent == null ? null : employeeComponent.m_entity);
        }
    }

    [HarmonyPatch(typeof(SkillLevelSegmentController), "UpdateData")]
    internal static class UnknownEmployeeSkillSegmentPatch
    {
        private static void Postfix(SkillLevelSegmentController __instance)
        {
            UnknownEmployeeService.MaskRenderedSkillSegment(__instance);
        }
    }
}
