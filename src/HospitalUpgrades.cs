using System;
using System.Collections.Generic;
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
            return level >= MaxLevel ? 0 : GetCost(definition, level);
        }

        public static bool TryBuy(HospitalUpgradeDefinition definition, out string message)
        {
            message = null;
            var level = GetLevel(definition);
            if (level >= MaxLevel)
            {
                return false;
            }

            var cost = GetCost(definition, level);
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
            rect.offsetMin = new Vector2(42f, 42f);
            rect.offsetMax = new Vector2(-42f, -178f);

            var image = _panel.AddComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0.94f);

            var rootLayout = _panel.AddComponent<VerticalLayoutGroup>();
            rootLayout.padding = new RectOffset(16, 16, 16, 16);
            rootLayout.spacing = 8f;
            rootLayout.childControlWidth = true;
            rootLayout.childControlHeight = false;
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
            viewport.AddComponent<LayoutElement>().flexibleHeight = 1f;
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
            row.AddComponent<LayoutElement>().preferredHeight = 78f;

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
            CreateLayoutText(left.transform, ModText.T(definition.TitleKey), 17, FontStyle.Bold, 24f);
            CreateLayoutText(left.transform, ModText.F("UpgradeEffect", definition.Effect), 12, FontStyle.Normal, 40f);

            var right = new GameObject("Right");
            right.transform.SetParent(row.transform, false);
            var rightLayout = right.AddComponent<VerticalLayoutGroup>();
            rightLayout.spacing = 3f;
            rightLayout.childControlWidth = true;
            rightLayout.childControlHeight = false;
            rightLayout.childForceExpandWidth = true;
            rightLayout.childForceExpandHeight = false;
            var rightElement = right.AddComponent<LayoutElement>();
            rightElement.minWidth = 122f;
            rightElement.preferredWidth = 122f;
            rightElement.flexibleWidth = 0f;
            _levelTexts.Add(CreateLayoutText(right.transform, string.Empty, 14, FontStyle.Normal, 20f));
            _costTexts.Add(CreateLayoutText(right.transform, string.Empty, 12, FontStyle.Normal, 20f));

            var button = CreateButton(right.transform, ModText.T("UpgradeBuy"), new Vector2(70f, 32f));
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
                _levelTexts[i].text = ModText.F("UpgradeLevel", level);
                _costTexts[i].text = level >= HospitalUpgradesService.MaxLevel
                    ? ModText.T("UpgradeMax")
                    : ModText.F("UpgradeCost", HospitalUpgradesService.GetNextCost(definition));
                _buttons[i].interactable = level < HospitalUpgradesService.MaxLevel;
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
