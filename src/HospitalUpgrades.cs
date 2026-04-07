using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
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
            new HospitalUpgradeDefinition("ProcurementCartel", "UpgradeProcurement", "x0.90 / x0.80 / x0.70 / x0.60 / x0.45 / x0.33 build costs", false),
            new HospitalUpgradeDefinition("MarketingMachine", "UpgradeReputation", "x1.15 / x1.35 / x1.60 / x2.00 / x2.50 / x3.00 reputation gains", false)
        };

        private static readonly Dictionary<string, ConfigEntry<int>> Levels = new Dictionary<string, ConfigEntry<int>>();

        public static int GetLevel(HospitalUpgradeDefinition definition)
        {
            var entry = GetEntry(definition);
            return Mathf.Clamp(entry.Value, 0, MaxLevel);
        }

        public static int GetNextCost(HospitalUpgradeDefinition definition)
        {
            var level = GetLevel(definition);
            return level >= MaxLevel ? 0 : definition.Costs[level];
        }

        public static bool TryBuy(HospitalUpgradeDefinition definition, out string message)
        {
            message = null;
            var level = GetLevel(definition);
            if (level >= MaxLevel)
            {
                return false;
            }

            var cost = definition.Costs[level];
            var balance = GetBalance();
            if (balance < cost)
            {
                message = ModText.T("UpgradeNotEnoughMoney");
                return false;
            }

            SetBalance(balance - cost);
            var entry = GetEntry(definition);
            entry.Value = level + 1;
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
    }

    internal sealed class HospitalUpgradesNativePanel : MonoBehaviour
    {
        private GameObject _panel;
        private Text _message;
        private readonly List<Text> _levelTexts = new List<Text>();
        private readonly List<Text> _costTexts = new List<Text>();
        private Font _font;

        public void Create(Transform parent)
        {
            _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            _panel = new GameObject("ProjectHospitalAutoLabBalancer_UpgradesPanel");
            _panel.transform.SetParent(parent, false);

            var rect = _panel.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.offsetMin = new Vector2(20f, 20f);
            rect.offsetMax = new Vector2(-20f, -190f);

            var image = _panel.AddComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0.86f);

            CreateText(_panel.transform, ModText.T("UpgradesTitle"), 24, FontStyle.Bold, new Vector2(20f, -18f), new Vector2(620f, 32f));
            _message = CreateText(_panel.transform, string.Empty, 15, FontStyle.Bold, new Vector2(360f, -22f), new Vector2(260f, 28f));

            for (var i = 0; i < HospitalUpgradesService.Upgrades.Length; i++)
            {
                CreateUpgradeRow(i, HospitalUpgradesService.Upgrades[i]);
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
                    Refresh();
                }
            }
        }

        private void CreateUpgradeRow(int index, HospitalUpgradeDefinition definition)
        {
            var column = index / 6;
            var row = index % 6;
            var x = 24f + column * 325f;
            var y = -62f - row * 54f;

            CreateText(_panel.transform, ModText.T(definition.TitleKey), 16, FontStyle.Bold, new Vector2(x, y), new Vector2(210f, 22f));
            CreateText(_panel.transform, ModText.F("UpgradeEffect", definition.Effect), 11, FontStyle.Normal, new Vector2(x, y - 20f), new Vector2(260f, 20f));
            _levelTexts.Add(CreateText(_panel.transform, string.Empty, 13, FontStyle.Normal, new Vector2(x + 210f, y), new Vector2(70f, 20f)));
            _costTexts.Add(CreateText(_panel.transform, string.Empty, 12, FontStyle.Normal, new Vector2(x + 210f, y - 20f), new Vector2(95f, 20f)));

            var button = CreateButton(_panel.transform, ModText.T("UpgradeBuy"), new Vector2(x + 270f, y - 10f), new Vector2(48f, 28f));
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
                _levelTexts[i].text = ModText.F("UpgradeLevel", level);
                _costTexts[i].text = level >= HospitalUpgradesService.MaxLevel
                    ? ModText.T("UpgradeMax")
                    : ModText.F("UpgradeCost", HospitalUpgradesService.GetNextCost(definition));
            }
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

        private Button CreateButton(Transform parent, string label, Vector2 anchoredPosition, Vector2 sizeDelta)
        {
            var gameObject = new GameObject("Button");
            gameObject.transform.SetParent(parent, false);
            var rect = gameObject.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = sizeDelta;

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
            var lastButton = ReflectionHelpers.GetField(controller, "m_tabButtonAmbulances") as GameObject;
            var sourceButton = ReflectionHelpers.GetField(controller, "m_tabButtonBudget") as GameObject ?? lastButton;
            if (statsTab == null || lastButton == null || sourceButton == null)
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
                rect.anchoredPosition = lastRect.anchoredPosition + new Vector2(42f, 0f);
            }
            else
            {
                button.transform.localPosition = lastButton.transform.localPosition + new Vector3(42f, 0f, 0f);
            }

            var icon = button.GetComponent<IconButtonController>();
            if (icon != null)
            {
                icon.RemoveOnClickDelegate();
                icon.SetToolTipTextParameters(new[] { ModText.T("UpgradesTab") });
                icon.SetOnClickedDelegate(delegate { Show(controller); });
            }

            var panel = statsTab.AddComponent<HospitalUpgradesNativePanel>();
            panel.Create(statsTab.transform.parent);
            Panels[controller] = panel;
        }

        public static void Show(HospitalManagementPanelController controller)
        {
            var activeTab = ReflectionHelpers.GetField(controller, "m_activeTab") as GameObject;
            if (activeTab != null)
            {
                activeTab.SetActive(false);
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
