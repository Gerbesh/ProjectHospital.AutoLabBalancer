using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ProjectHospital.AutoLabBalancer
{
    internal sealed class HospitalUpgradeDefinition
    {
        public readonly string Id;
        public readonly string TitleKey;
        public readonly string Effect;
        public readonly int[] Costs;

        public HospitalUpgradeDefinition(string id, string titleKey, string effect, bool expensive)
        {
            Id = id;
            TitleKey = titleKey;
            Effect = effect;
            Costs = expensive
                ? new[] { 75000, 150000, 300000, 500000, 750000, 1000000 }
                : new[] { 50000, 100000, 200000, 350000, 500000, 750000 };
        }
    }

    internal static class HospitalUpgradesService
    {
        public const int MaxLevel = 6;
        public const int AbsurdCost = 1000000;
        public const int DevAbsurdCost = 200000;
        public const float AbsurdActionMultiplier = 100f;
        public const float AbsurdInsuranceMultiplier = 5f;
        private static readonly float[] ForwardMultipliers = { 1f, 1.15f, 1.35f, 1.60f, 2.00f, 2.50f, 3.00f };
        private static readonly float[] ReductionMultipliers = { 1f, 0.90f, 0.80f, 0.70f, 0.60f, 0.45f, 0.33f };
        private static readonly Dictionary<object, float> MovementRemainders = new Dictionary<object, float>(ReferenceEqualityComparer.Instance);

        public static readonly HospitalUpgradeDefinition[] Upgrades =
        {
            new HospitalUpgradeDefinition("ClinicalOverdrive", "UpgradeClinical", "x1.15 / x1.35 / x1.60 / x2.00 / x2.50 / x3.00 doctor actions", false),
            new HospitalUpgradeDefinition("NursingOverdrive", "UpgradeNursing", "x1.15 / x1.35 / x1.60 / x2.00 / x2.50 / x3.00 nurse actions", false),
            new HospitalUpgradeDefinition("LabConveyor", "UpgradeLab", "x1.15 / x1.35 / x1.60 / x2.00 / x2.50 / x3.00 lab actions", false),
            new HospitalUpgradeDefinition("SanitaryBlitz", "UpgradeCleaning", "x1.15 / x1.35 / x1.60 / x2.00 / x2.50 / x3.00 cleaning", false),
            new HospitalUpgradeDefinition("TurboDiagnostics", "UpgradeDiagnostics", "x1.15 / x1.35 / x1.60 / x2.00 / x2.50 / x3.00 examinations", false),
            new HospitalUpgradeDefinition("TherapyProtocols", "UpgradeTherapy", "x1.15 / x1.35 / x1.60 / x2.00 / x2.50 / x3.00 non-surgery treatments", false),
            new HospitalUpgradeDefinition("SurgeryConveyor", "UpgradeSurgery", "x1.15 / x1.35 / x1.60 / x2.00 / x2.50 / x3.00 surgery", true),
            new HospitalUpgradeDefinition("LifeSupportMonitoring", "UpgradeMonitoring", "x1.15 / x1.35 / x1.60 / x2.00 / x2.50 / x3.00 collapse/death timers", true),
            new HospitalUpgradeDefinition("InsurancePressure", "UpgradeInsurance", "x1.15 / x1.35 / x1.60 / x2.00 / x2.50 / x3.00 insurance payouts", true),
            new HospitalUpgradeDefinition("FinancialGrinder", "UpgradeWages", "x0.90 / x0.80 / x0.70 / x0.60 / x0.45 / x0.33 wages", true),
            new HospitalUpgradeDefinition("ProcurementCartel", "UpgradeProcurement", "x0.90 / x0.80 / x0.70 / x0.60 / x0.45 / x0.33 build costs", false)
        };

        private static readonly Dictionary<string, ConfigEntry<int>> Levels = new Dictionary<string, ConfigEntry<int>>();
        private static readonly Dictionary<string, ConfigEntry<bool>> AbsurdLevels = new Dictionary<string, ConfigEntry<bool>>();
        private static readonly Dictionary<string, HospitalUpgradeDefinition> ById = new Dictionary<string, HospitalUpgradeDefinition>();

        static HospitalUpgradesService()
        {
            foreach (var upgrade in Upgrades)
            {
                ById[upgrade.Id] = upgrade;
            }
        }

        public static int GetLevel(HospitalUpgradeDefinition definition)
        {
            var entry = GetEntry(definition);
            return Mathf.Clamp(entry.Value, 0, MaxLevel);
        }

        public static int GetNextCost(HospitalUpgradeDefinition definition)
        {
            var level = GetLevel(definition);
            if (level < MaxLevel)
            {
                return GetCost(definition, level);
            }

            return CanUseAbsurdTier() && !HasAbsurdLevel(definition) ? GetAbsurdCost() : 0;
        }

        public static bool HasAbsurdLevel(HospitalUpgradeDefinition definition)
        {
            return CanUseAbsurdTier() && GetAbsurdEntry(definition).Value;
        }

        public static float GetForwardMultiplier(string id)
        {
            HospitalUpgradeDefinition definition;
            if (!ById.TryGetValue(id, out definition))
            {
                return 1f;
            }

            if (HasAbsurdLevel(definition))
            {
                if (id == "InsurancePressure")
                {
                    return AbsurdInsuranceMultiplier;
                }

                return AbsurdActionMultiplier;
            }

            return ForwardMultipliers[Mathf.Clamp(GetLevel(definition), 0, MaxLevel)];
        }

        public static float GetReductionMultiplier(string id)
        {
            HospitalUpgradeDefinition definition;
            if (!ById.TryGetValue(id, out definition))
            {
                return 1f;
            }

            if (HasAbsurdLevel(definition))
            {
                return 0f;
            }

            return ReductionMultipliers[Mathf.Clamp(GetLevel(definition), 0, MaxLevel)];
        }

        public static float GetProcedureScriptMultiplier(object script)
        {
            if (script == null)
            {
                return 1f;
            }

            var name = script.GetType().FullName ?? string.Empty;
            if (name.IndexOf("ProcedureScriptTreatmentSurgery", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return GetForwardMultiplier("SurgeryConveyor");
            }

            if (name.IndexOf("StatLab", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return GetForwardMultiplier("LabConveyor");
            }

            if (name.IndexOf("ProcedureScriptExamination", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return GetForwardMultiplier("TurboDiagnostics");
            }

            if (name.IndexOf("ProcedureScriptTreatment", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return GetForwardMultiplier("TherapyProtocols");
            }

            if (name.IndexOf("ProcedureScriptControlNurse", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return GetForwardMultiplier("NursingOverdrive");
            }

            if (name.IndexOf("ProcedureScriptControlDoctor", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("ProcedureScriptDoctor", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return GetForwardMultiplier("ClinicalOverdrive");
            }

            return 1f;
        }

        public static void ApplyRoleMovementExtraSteps(object walkComponent, int updateCount, float deltaTime)
        {
            if (walkComponent == null || updateCount <= 0 || deltaTime <= 0f || deltaTime > 0.05f)
            {
                return;
            }

            var multiplier = GetMovementMultiplier(walkComponent);
            if (multiplier <= 1.001f)
            {
                MovementRemainders.Remove(walkComponent);
                return;
            }

            var desiredExtraSteps = ((multiplier - 1f) * updateCount) + GetMovementRemainder(walkComponent);
            var extraSteps = Math.Max(0, (int)Math.Floor(desiredExtraSteps));
            extraSteps = Math.Min(extraSteps, IsAbsurdMovement(walkComponent) ? 240 : 24);
            MovementRemainders[walkComponent] = desiredExtraSteps - extraSteps;
            if (extraSteps <= 0)
            {
                return;
            }

            var routeField = AccessTools.Field(walkComponent.GetType(), "m_route");
            var floorField = AccessTools.Field(walkComponent.GetType(), "m_floor");
            var updateMovement = AccessTools.Method(walkComponent.GetType(), "UpdateMovement");
            if (routeField == null || floorField == null || updateMovement == null)
            {
                return;
            }

            var floor = floorField.GetValue(walkComponent);
            for (var i = 0; i < extraSteps && routeField.GetValue(walkComponent) != null; i++)
            {
                var result = updateMovement.Invoke(walkComponent, new[] { floor, (object)deltaTime });
                if (result != null && string.Equals(result.ToString(), "NEXT_SEGMENT", StringComparison.OrdinalIgnoreCase) && routeField.GetValue(walkComponent) != null)
                {
                    updateMovement.Invoke(walkComponent, new[] { floor, (object)deltaTime });
                }
            }
        }

        public static void ApplyInsurancePayoutMultiplier(ref int amount, object category)
        {
            if (amount <= 0 || category == null || !category.ToString().StartsWith("INSURANCE_", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var multiplier = GetForwardMultiplier("InsurancePressure");
            if (multiplier > 1.001f)
            {
                amount = Math.Max(1, (int)Math.Round(amount * multiplier));
            }
        }

        public static void ApplyAnimationDeltaTime(object animModelComponent, ref float deltaTime)
        {
            if (animModelComponent == null || deltaTime <= 0f || deltaTime > 0.05f)
            {
                return;
            }

            var entity = ReflectionHelpers.GetField(animModelComponent, "m_entity");
            var multiplier = GetRoleMultiplier(entity);
            var walk = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.WalkComponent");
            if (walk != null && ProductivityTweaksService.ShouldBoostRunningMovement(walk))
            {
                multiplier += Math.Max(1f, RuntimeSettings.Config.EmergencyRunSpeedMultiplier.Value) - 1f;
            }

            if (multiplier > 1.001f)
            {
                deltaTime *= multiplier;
            }
        }

        public static void ApplyBuildCostReduction(ref int cost)
        {
            if (cost <= 0)
            {
                return;
            }

            var multiplier = GetReductionMultiplier("ProcurementCartel");
            if (multiplier < 0.999f)
            {
                cost = multiplier <= 0.001f ? 0 : Math.Max(1, (int)Math.Round(cost * multiplier));
            }
        }

        public static void ApplySalaryReduction(object employee)
        {
            var multiplier = GetReductionMultiplier("FinancialGrinder");
            if (employee == null || multiplier >= 0.999f)
            {
                return;
            }

            var state = ReflectionHelpers.GetField(employee, "m_state");
            var field = state == null ? null : AccessTools.Field(state.GetType(), "m_salary");
            if (field == null)
            {
                return;
            }

            var salary = Convert.ToInt32(field.GetValue(state));
            if (salary > 0)
            {
                field.SetValue(state, multiplier <= 0.001f ? 0 : Math.Max(1, (int)Math.Round(salary * multiplier)));
            }
        }

        public static void ApplyLifeSupportTimers(object medicalCondition)
        {
            var multiplier = GetForwardMultiplier("LifeSupportMonitoring");
            if (medicalCondition == null || multiplier <= 1.001f)
            {
                return;
            }

            foreach (var symptom in ReflectionHelpers.GetEnumerableField(medicalCondition, "m_symptoms"))
            {
                ScaleFutureTime(symptom, "m_collapseTriggerTimeHours", multiplier);
                ScaleFutureTime(symptom, "m_deathTriggerTimeHours", multiplier);
            }
        }

        public static bool TryBuy(HospitalUpgradeDefinition definition, out string message)
        {
            message = null;
            var level = GetLevel(definition);
            var buyingAbsurd = level >= MaxLevel;
            if (buyingAbsurd && (!CanUseAbsurdTier() || HasAbsurdLevel(definition)))
            {
                return false;
            }

            var cost = buyingAbsurd ? GetAbsurdCost() : GetCost(definition, level);
            var balance = GetBalance();
            if (balance < cost)
            {
                message = ModText.T("UpgradeNotEnoughMoney");
                return false;
            }

            SetBalance(balance - cost);
            if (buyingAbsurd)
            {
                GetAbsurdEntry(definition).Value = true;
            }
            else
            {
                var entry = GetEntry(definition);
                entry.Value = level + 1;
            }

            RuntimeSettings.Config.SourceConfig.Save();
            return true;
        }

        private static ConfigEntry<int> GetEntry(HospitalUpgradeDefinition definition)
        {
            ConfigEntry<int> entry;
            if (Levels.TryGetValue(definition.Id, out entry))
            {
                return entry;
            }

            entry = RuntimeSettings.Config.SourceConfig.Bind("HospitalUpgrades", definition.Id, 0, "Purchased level for " + definition.Id + ".");
            Levels[definition.Id] = entry;
            return entry;
        }

        private static ConfigEntry<bool> GetAbsurdEntry(HospitalUpgradeDefinition definition)
        {
            ConfigEntry<bool> entry;
            if (AbsurdLevels.TryGetValue(definition.Id, out entry))
            {
                return entry;
            }

            entry = RuntimeSettings.Config.SourceConfig.Bind("HospitalUpgrades.Absurd", definition.Id, false, "Purchased absurd tier for " + definition.Id + ".");
            AbsurdLevels[definition.Id] = entry;
            return entry;
        }

        private static bool CanUseAbsurdTier()
        {
            return RuntimeSettings.Config != null
                && RuntimeSettings.Config.Enabled.Value
                && RuntimeSettings.Config.EnableAbsurdUpgrades.Value;
        }

        private static int GetAbsurdCost()
        {
            return RuntimeSettings.Config != null && RuntimeSettings.Config.DevCheapUpgrades.Value ? DevAbsurdCost : AbsurdCost;
        }

        private static int GetCost(HospitalUpgradeDefinition definition, int level)
        {
            if (RuntimeSettings.Config != null && RuntimeSettings.Config.DevCheapUpgrades.Value)
            {
                var cheapCosts = definition.Costs[definition.Costs.Length - 1] >= 1000000
                    ? new[] { 7500, 15000, 30000, 50000, 75000, 100000 }
                    : new[] { 7000, 14000, 27000, 47000, 67000, 100000 };
                return cheapCosts[Mathf.Clamp(level, 0, cheapCosts.Length - 1)];
            }

            return definition.Costs[level];
        }

        private static int GetBalance()
        {
            var budget = GetBudget();
            var value = ReflectionHelpers.GetField(budget, "m_currentBalance");
            return value == null ? 0 : Convert.ToInt32(value);
        }

        private static void SetBalance(int value)
        {
            var budget = GetBudget();
            var field = budget == null ? null : AccessTools.Field(budget.GetType(), "m_currentBalance");
            if (field != null)
            {
                field.SetValue(budget, value);
            }
        }

        private static object GetBudget()
        {
            var hospital = Lopital.Hospital.Instance;
            var state = ReflectionHelpers.GetField(hospital, "m_state");
            return ReflectionHelpers.GetField(state, "m_budget");
        }

        private static float GetMovementMultiplier(object walkComponent)
        {
            var entity = ReflectionHelpers.GetField(walkComponent, "m_entity");
            return GetRoleMultiplier(entity);
        }

        private static float GetRoleMultiplier(object entity)
        {
            if (entity == null)
            {
                return 1f;
            }

            var multiplier = 1f;
            if (ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.BehaviorDoctor") != null)
            {
                multiplier = Math.Max(multiplier, GetForwardMultiplier("ClinicalOverdrive"));
            }

            if (ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.BehaviorNurse") != null)
            {
                multiplier = Math.Max(multiplier, GetForwardMultiplier("NursingOverdrive"));
            }

            if (ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.BehaviorLabSpecialist") != null)
            {
                multiplier = Math.Max(multiplier, GetForwardMultiplier("LabConveyor"));
            }

            if (ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.BehaviorJanitor") != null)
            {
                multiplier = Math.Max(multiplier, GetForwardMultiplier("SanitaryBlitz"));
            }

            return multiplier;
        }

        private static bool IsAbsurdMovement(object walkComponent)
        {
            var entity = ReflectionHelpers.GetField(walkComponent, "m_entity");
            return GetRoleMultiplier(entity) >= AbsurdActionMultiplier - 0.1f;
        }

        public static void ApplyJanitorCleaningDeltaTime(ref float deltaTime)
        {
            var definition = ById["SanitaryBlitz"];
            if (HasAbsurdLevel(definition) && deltaTime > 0f && deltaTime <= 0.05f)
            {
                deltaTime *= AbsurdActionMultiplier;
            }
        }

        private static float GetMovementRemainder(object walkComponent)
        {
            float value;
            return MovementRemainders.TryGetValue(walkComponent, out value) ? value : 0f;
        }

        private static void ScaleFutureTime(object instance, string fieldName, float multiplier)
        {
            var field = instance == null ? null : AccessTools.Field(instance.GetType(), fieldName);
            if (field == null)
            {
                return;
            }

            var value = Convert.ToSingle(field.GetValue(instance));
            if (value > 0f)
            {
                field.SetValue(instance, value * multiplier);
            }
        }
    }

    internal sealed class HospitalUpgradesNativePanel : MonoBehaviour
    {
        private GameObject _panel;
        private Text _message;
        private readonly List<Text> _levelTexts = new List<Text>();
        private readonly List<Text> _costTexts = new List<Text>();
        private readonly List<Button> _buttons = new List<Button>();
        private Font _font;

        public void Create(GameObject windowPanel)
        {
            _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            _panel = new GameObject("ProjectHospitalAutoLabBalancer_UpgradesPanel");
            var parent = windowPanel == null ? null : windowPanel.transform;
            _panel.transform.SetParent(parent, false);
            _panel.transform.localScale = Vector3.one;
            _panel.transform.localRotation = Quaternion.identity;

            var rect = _panel.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(42f, 118f);
            rect.offsetMax = new Vector2(-42f, -178f);

            var image = _panel.AddComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0.94f);

            var rootLayout = _panel.AddComponent<VerticalLayoutGroup>();
            rootLayout.padding = new RectOffset(16, 16, 16, 16);
            rootLayout.spacing = 8f;
            rootLayout.childControlWidth = true;
            rootLayout.childControlHeight = true;
            rootLayout.childForceExpandWidth = true;
            rootLayout.childForceExpandHeight = false;

            var header = new GameObject("Header");
            header.transform.SetParent(_panel.transform, false);
            var headerLayout = header.AddComponent<VerticalLayoutGroup>();
            headerLayout.spacing = 2f;
            headerLayout.childControlWidth = true;
            headerLayout.childControlHeight = false;
            headerLayout.childForceExpandWidth = true;
            headerLayout.childForceExpandHeight = false;
            header.AddComponent<LayoutElement>().preferredHeight = 66f;
            CreateLayoutText(header.transform, ModText.T("UpgradesTitle"), 24, FontStyle.Bold, 30f);
            _message = CreateLayoutText(header.transform, string.Empty, 13, FontStyle.Bold, 30f);

            var viewport = new GameObject("Viewport");
            viewport.transform.SetParent(_panel.transform, false);
            var viewportRect = viewport.AddComponent<RectTransform>();
            var viewportElement = viewport.AddComponent<LayoutElement>();
            viewportElement.minHeight = 220f;
            viewportElement.preferredHeight = 420f;
            viewportElement.flexibleHeight = 1f;
            var viewportImage = viewport.AddComponent<Image>();
            viewportImage.color = new Color(1f, 1f, 1f, 0.18f);
            viewport.AddComponent<Mask>().showMaskGraphic = false;

            var content = new GameObject("Content");
            content.transform.SetParent(viewport.transform, false);
            var contentRect = content.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0f, 1f);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(0f, 0f);
            var contentLayout = content.AddComponent<VerticalLayoutGroup>();
            contentLayout.padding = new RectOffset(8, 8, 8, 8);
            contentLayout.spacing = 8f;
            contentLayout.childControlWidth = true;
            contentLayout.childControlHeight = true;
            contentLayout.childForceExpandWidth = true;
            contentLayout.childForceExpandHeight = false;
            var fitter = content.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scrollRect = _panel.AddComponent<ScrollRect>();
            scrollRect.viewport = viewportRect;
            scrollRect.content = contentRect;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 28f;

            for (var i = 0; i < HospitalUpgradesService.Upgrades.Length; i++)
            {
                CreateUpgradeRow(content.transform, HospitalUpgradesService.Upgrades[i]);
            }

            Refresh();
            SetVisible(false);
        }

        public void SetVisible(bool visible)
        {
            if (_panel != null)
            {
                _panel.SetActive(visible);
                if (visible)
                {
                    _panel.transform.SetAsLastSibling();
                    Refresh();
                }
            }
        }

        private void CreateUpgradeRow(Transform parent, HospitalUpgradeDefinition definition)
        {
            var row = new GameObject("UpgradeRow_" + definition.Id);
            row.transform.SetParent(parent, false);
            var rowImage = row.AddComponent<Image>();
            rowImage.color = new Color(1f, 1f, 1f, 0.18f);
            var rowLayout = row.AddComponent<HorizontalLayoutGroup>();
            rowLayout.padding = new RectOffset(10, 10, 8, 8);
            rowLayout.spacing = 10f;
            rowLayout.childControlWidth = true;
            rowLayout.childControlHeight = true;
            rowLayout.childForceExpandWidth = false;
            rowLayout.childForceExpandHeight = false;
            row.AddComponent<LayoutElement>().preferredHeight = 68f;

            var left = new GameObject("Left");
            left.transform.SetParent(row.transform, false);
            var leftLayout = left.AddComponent<VerticalLayoutGroup>();
            leftLayout.spacing = 2f;
            leftLayout.childControlWidth = true;
            leftLayout.childControlHeight = false;
            leftLayout.childForceExpandWidth = true;
            leftLayout.childForceExpandHeight = false;
            var leftElement = left.AddComponent<LayoutElement>();
            leftElement.flexibleWidth = 1f;
            leftElement.minWidth = 210f;
            CreateLayoutText(left.transform, ModText.T(definition.TitleKey), 16, FontStyle.Bold, 22f);
            CreateLayoutText(left.transform, ModText.F("UpgradeEffect", definition.Effect), 11, FontStyle.Normal, 34f);

            var right = new GameObject("Right");
            right.transform.SetParent(row.transform, false);
            var rightLayout = right.AddComponent<VerticalLayoutGroup>();
            rightLayout.spacing = 3f;
            rightLayout.childControlWidth = true;
            rightLayout.childControlHeight = false;
            rightLayout.childForceExpandWidth = true;
            rightLayout.childForceExpandHeight = false;
            var rightElement = right.AddComponent<LayoutElement>();
            rightElement.minWidth = 146f;
            rightElement.preferredWidth = 146f;
            rightElement.flexibleWidth = 0f;
            _levelTexts.Add(CreateLayoutText(right.transform, string.Empty, 13, FontStyle.Normal, 18f));
            _costTexts.Add(CreateLayoutText(right.transform, string.Empty, 11, FontStyle.Normal, 18f));

            var button = CreateButton(right.transform, ModText.T("UpgradeBuy"), new Vector2(96f, 32f));
            var tooltip = button.gameObject.AddComponent<HospitalUpgradeTooltip>();
            tooltip.Init(this, definition);
            _buttons.Add(button);
            button.onClick.AddListener(delegate
            {
                string message;
                HospitalUpgradesService.TryBuy(definition, out message);
                _message.text = message ?? string.Empty;
                Refresh();
            });
        }

        private void Refresh()
        {
            for (var i = 0; i < HospitalUpgradesService.Upgrades.Length; i++)
            {
                var definition = HospitalUpgradesService.Upgrades[i];
                var level = HospitalUpgradesService.GetLevel(definition);
                var hasAbsurd = HospitalUpgradesService.HasAbsurdLevel(definition);
                _levelTexts[i].text = hasAbsurd
                    ? ModText.F("UpgradeLevel", level) + " + " + ModText.T("UpgradeAbsurdBought")
                    : ModText.F("UpgradeLevel", level);
                if (level < HospitalUpgradesService.MaxLevel)
                {
                    _costTexts[i].text = ModText.F("UpgradeCost", HospitalUpgradesService.GetNextCost(definition));
                    _buttons[i].GetComponentInChildren<Text>().text = ModText.T("UpgradeBuy");
                    _buttons[i].interactable = true;
                }
                else if (HospitalUpgradesService.GetNextCost(definition) > 0)
                {
                    _costTexts[i].text = ModText.F("UpgradeAbsurdCost", HospitalUpgradesService.GetNextCost(definition));
                    _buttons[i].GetComponentInChildren<Text>().text = ModText.T("UpgradeBuyAbsurd");
                    _buttons[i].interactable = true;
                }
                else
                {
                    _costTexts[i].text = hasAbsurd ? ModText.T("UpgradeAbsurdBought") : ModText.T("UpgradeMax");
                    _buttons[i].GetComponentInChildren<Text>().text = hasAbsurd ? ModText.T("UpgradeAbsurdBought") : ModText.T("UpgradeBuy");
                    _buttons[i].interactable = false;
                }
            }
        }

        public void ShowUpgradeDetails(HospitalUpgradeDefinition definition)
        {
            _message.text = ModText.T(definition.TitleKey) + " | " + ModText.F("UpgradeEffect", definition.Effect);
        }

        private Text CreateLayoutText(Transform parent, string text, int size, FontStyle style, float preferredHeight)
        {
            var label = CreateText(parent, text, size, style, new Vector2(0f, 0f), new Vector2(0f, preferredHeight));
            label.gameObject.AddComponent<LayoutElement>().preferredHeight = preferredHeight;
            return label;
        }

        private Text CreateText(Transform parent, string text, int size, FontStyle style, Vector2 anchoredPosition, Vector2 sizeDelta)
        {
            var gameObject = new GameObject("Text");
            gameObject.transform.SetParent(parent, false);
            var rect = gameObject.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = sizeDelta;

            var label = gameObject.AddComponent<Text>();
            label.font = _font;
            label.text = text;
            label.fontSize = size;
            label.fontStyle = style;
            label.color = new Color(0.14f, 0.14f, 0.14f, 1f);
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Truncate;
            return label;
        }

        private Button CreateButton(Transform parent, string label, Vector2 sizeDelta)
        {
            var gameObject = new GameObject("Button");
            gameObject.transform.SetParent(parent, false);
            var rect = gameObject.AddComponent<RectTransform>();
            rect.sizeDelta = sizeDelta;
            var layoutElement = gameObject.AddComponent<LayoutElement>();
            layoutElement.preferredWidth = sizeDelta.x;
            layoutElement.preferredHeight = sizeDelta.y;

            var image = gameObject.AddComponent<Image>();
            image.color = new Color(0.34f, 0.34f, 0.34f, 1f);
            var button = gameObject.AddComponent<Button>();
            var colors = button.colors;
            colors.normalColor = image.color;
            colors.highlightedColor = new Color(0.45f, 0.45f, 0.45f, 1f);
            colors.pressedColor = new Color(0.22f, 0.22f, 0.22f, 1f);
            button.colors = colors;

            var text = CreateText(gameObject.transform, label, 12, FontStyle.Bold, new Vector2(5f, -6f), new Vector2(sizeDelta.x - 10f, sizeDelta.y - 8f));
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleCenter;
            return button;
        }
    }

    internal sealed class HospitalUpgradeTooltip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        private HospitalUpgradesNativePanel _panel;
        private HospitalUpgradeDefinition _definition;

        public void Init(HospitalUpgradesNativePanel panel, HospitalUpgradeDefinition definition)
        {
            _panel = panel;
            _definition = definition;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_panel != null && _definition != null)
            {
                _panel.ShowUpgradeDetails(_definition);
            }
        }

        public void OnPointerExit(PointerEventData eventData)
        {
        }
    }

    [HarmonyPatch]
    internal static class HospitalUpgradesProcedureScriptSpeedPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var baseType = AccessTools.TypeByName("Lopital.ProcedureScript");
            if (baseType == null)
            {
                yield break;
            }

            foreach (var type in baseType.Assembly.GetTypes())
            {
                if (type == null || type.FullName == null || !type.FullName.StartsWith("Lopital.ProcedureScript", StringComparison.Ordinal))
                {
                    continue;
                }

                var method = AccessTools.Method(type, "ScriptUpdate", new[] { typeof(float) });
                if (method != null && method.DeclaringType == type)
                {
                    yield return method;
                }
            }
        }

        private static void Prefix(object __instance, ref float deltaTime)
        {
            var multiplier = HospitalUpgradesService.GetProcedureScriptMultiplier(__instance);
            if (multiplier > 1.001f && deltaTime > 0f && deltaTime <= 0.05f)
            {
                deltaTime *= multiplier;
            }
        }
    }

    [HarmonyPatch]
    internal static class HospitalUpgradesMovementPatch
    {
        private static MethodBase TargetMethod()
        {
            var walkType = AccessTools.TypeByName("Lopital.WalkComponent");
            return walkType == null ? null : AccessTools.Method(walkType, "MultiUpdate", new[] { typeof(int), typeof(float) });
        }

        private static void Postfix(object __instance, int updateCount, float deltaTime)
        {
            HospitalUpgradesService.ApplyRoleMovementExtraSteps(__instance, updateCount, deltaTime);
        }
    }

    [HarmonyPatch]
    internal static class HospitalUpgradesAnimationPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.AnimModelComponent");
            return type == null ? null : AccessTools.Method(type, "Update", new[] { typeof(float) });
        }

        private static void Prefix(object __instance, ref float deltaTime)
        {
            HospitalUpgradesService.ApplyAnimationDeltaTime(__instance, ref deltaTime);
        }
    }

    [HarmonyPatch(typeof(Lopital.Hospital), "Pay", new[] { typeof(int), typeof(Lopital.PaymentCategory) })]
    internal static class HospitalUpgradesHospitalPayPatch
    {
        private static void Prefix(ref int amount, Lopital.PaymentCategory category)
        {
            HospitalUpgradesService.ApplyInsurancePayoutMultiplier(ref amount, category);
        }
    }

    [HarmonyPatch]
    internal static class HospitalUpgradesDepartmentPayPatch
    {
        private static MethodBase TargetMethod()
        {
            var entityType = AccessTools.TypeByName("GLib.Entity");
            return entityType == null ? null : AccessTools.Method(typeof(Lopital.Department), "Pay", new[] { typeof(int), typeof(Lopital.PaymentCategory), entityType });
        }

        private static void Prefix(ref int amount, Lopital.PaymentCategory category)
        {
            HospitalUpgradesService.ApplyInsurancePayoutMultiplier(ref amount, category);
        }
    }

    [HarmonyPatch(typeof(Lopital.EmployeeComponent), "ComputeSalary")]
    internal static class HospitalUpgradesSalaryPatch
    {
        private static void Postfix(object __instance)
        {
            HospitalUpgradesService.ApplySalaryReduction(__instance);
        }
    }

    [HarmonyPatch]
    internal static class HospitalUpgradesJanitorCleaningPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.BehaviorJanitor");
            return type == null ? null : AccessTools.Method(type, "UpdateStateCleaning", new[] { typeof(float) });
        }

        private static void Prefix(ref float deltaTime)
        {
            HospitalUpgradesService.ApplyJanitorCleaningDeltaTime(ref deltaTime);
        }
    }

    [HarmonyPatch(typeof(Lopital.MedicalCondition), "ResetCollapseTimes")]
    internal static class HospitalUpgradesLifeSupportPatch
    {
        private static void Postfix(object __instance)
        {
            HospitalUpgradesService.ApplyLifeSupportTimers(__instance);
        }
    }

    [HarmonyPatch]
    internal static class HospitalUpgradesBuildCostPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var typeName in new[] { "GameDBObject", "GameDBCompositeObject", "GameDBDoor", "GameDBFloorType", "GameDBPrefabObject", "GameDBWall" })
            {
                var type = AccessTools.TypeByName(typeName);
                var property = type == null ? null : AccessTools.Property(type, "Cost");
                var getter = property == null ? null : property.GetGetMethod(true);
                if (getter != null)
                {
                    yield return getter;
                }
            }
        }

        private static void Postfix(ref int __result)
        {
            HospitalUpgradesService.ApplyBuildCostReduction(ref __result);
        }
    }

    [HarmonyPatch(typeof(HospitalManagementPanelController), "Start")]
    internal static class HospitalManagementUpgradesStartPatch
    {
        private static void Postfix(HospitalManagementPanelController __instance)
        {
            HospitalManagementUpgradesIntegration.EnsureInstalled(__instance);
        }
    }

    [HarmonyPatch(typeof(HospitalManagementPanelController), "SetTab")]
    internal static class HospitalManagementUpgradesSetTabPatch
    {
        private static void Prefix(HospitalManagementPanelController __instance)
        {
            HospitalManagementUpgradesIntegration.Hide(__instance);
        }
    }

    internal static class HospitalManagementUpgradesIntegration
    {
        private static readonly Dictionary<HospitalManagementPanelController, HospitalUpgradesNativePanel> Panels =
            new Dictionary<HospitalManagementPanelController, HospitalUpgradesNativePanel>();

        public static void EnsureInstalled(HospitalManagementPanelController controller)
        {
            if (controller == null || Panels.ContainsKey(controller))
            {
                return;
            }

            var statsTab = ReflectionHelpers.GetField(controller, "m_tabStatistics") as GameObject;
            var windowPanel = ReflectionHelpers.GetField(controller, "m_panel") as GameObject;
            var lastButton = ReflectionHelpers.GetField(controller, "m_tabButtonAmbulances") as GameObject;
            var sourceButton = ReflectionHelpers.GetField(controller, "m_tabButtonBudget") as GameObject ?? lastButton;
            if (statsTab == null || windowPanel == null || lastButton == null || sourceButton == null)
            {
                return;
            }

            var button = UnityEngine.Object.Instantiate(sourceButton);
            button.name = "ProjectHospitalAutoLabBalancer_UpgradesButton";
            button.transform.SetParent(lastButton.transform.parent, false);
            button.transform.localScale = sourceButton.transform.localScale;
            button.transform.localRotation = sourceButton.transform.localRotation;
            var rect = button.GetComponent<RectTransform>();
            var lastRect = lastButton.GetComponent<RectTransform>();
            if (rect != null && lastRect != null)
            {
                rect.anchorMin = lastRect.anchorMin;
                rect.anchorMax = lastRect.anchorMax;
                rect.pivot = lastRect.pivot;
                rect.sizeDelta = lastRect.sizeDelta;
                rect.anchoredPosition = lastRect.anchoredPosition + new Vector2(172f, 0f);
            }
            else
            {
                button.transform.localPosition = lastButton.transform.localPosition + new Vector3(172f, 0f, 0f);
            }

            var icon = button.GetComponent<IconButtonController>();
            if (icon != null)
            {
                icon.RemoveOnClickDelegate();
                icon.SetTextLocID(ModText.T("UpgradesTab"), true);
                icon.SetToolTipTextParameters(new[] { ModText.T("UpgradesTab") });
                icon.SetOnClickedDelegate(delegate { Show(controller); });
            }

            var panel = statsTab.AddComponent<HospitalUpgradesNativePanel>();
            panel.Create(windowPanel);
            Panels[controller] = panel;
        }

        public static void Show(HospitalManagementPanelController controller)
        {
            var activeTab = ReflectionHelpers.GetField(controller, "m_activeTab") as GameObject;
            if (activeTab != null)
            {
                activeTab.SetActive(false);
            }

            var sheet = ReflectionHelpers.GetField(controller, "m_textCurrentSheet") as GameObject;
            var text = sheet == null ? null : sheet.GetComponent<Text>();
            if (text != null)
            {
                text.text = ModText.T("UpgradesTab");
            }

            HospitalUpgradesNativePanel panel;
            if (Panels.TryGetValue(controller, out panel))
            {
                panel.SetVisible(true);
            }
        }

        public static void Hide(HospitalManagementPanelController controller)
        {
            HospitalUpgradesNativePanel panel;
            if (controller != null && Panels.TryGetValue(controller, out panel))
            {
                panel.SetVisible(false);
            }
        }
    }
}
