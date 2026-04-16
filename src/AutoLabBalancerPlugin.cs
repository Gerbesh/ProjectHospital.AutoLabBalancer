using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ProjectHospital.AutoLabBalancer
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class AutoLabBalancerPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "local.projecthospital.autolabbalancer";
        public const string PluginName = "Project Hospital Productivity Tweaks";
        public const string PluginVersion = "0.18.0";

        private AutoLabBalancerConfig _config;
        private Harmony _harmony;
        private float _nextTickAt;
        private float _nextOverlaySnapshotAt;
        private float _nextSurgeryAnalyticsAt;
        private BottleneckSnapshot _overlaySnapshot;
        private bool _showSettings;
        private Rect _settingsWindow = new Rect(30f, 80f, 860f, 620f);
        private int _settingsPage;
        private int _settingsCategory;
        private Vector2 _settingsScroll;
        private SaveScopedSettings _saveScopedSettings;
        private GUIStyle _settingsWindowStyle;
        private GUIStyle _settingsHeaderPanelStyle;
        private GUIStyle _settingsHeaderStyle;
        private GUIStyle _settingsMutedLabelStyle;
        private GUIStyle _settingsPageButtonStyle;
        private GUIStyle _settingsPageButtonActiveStyle;
        private GUIStyle _settingsCategoryButtonStyle;
        private GUIStyle _settingsCategoryButtonActiveStyle;
        private GUIStyle _settingsSectionStyle;
        private GUIStyle _settingsContentPanelStyle;
        private GUIStyle _settingsSectionHeaderStyle;
        private GUIStyle _settingsToggleStyle;
        private GUIStyle _settingsPrimaryButtonStyle;
        private GUIStyle _settingsScrollViewStyle;
        private GUIStyle _settingsTextBlockStyle;
        private Texture2D _settingsWindowTexture;
        private Texture2D _settingsHeaderTexture;
        private Texture2D _settingsSectionTexture;
        private Texture2D _settingsButtonTexture;
        private Texture2D _settingsButtonActiveTexture;
        private float _nextSettingsDiagnosticsRefreshAt;
        private string _cachedCountersText;
        private string _cachedBottlenecksText;
        private string _cachedSurgeryText;
        private string _cachedPerformanceText;
        private string _developerActionStatus;

        private void Awake()
        {
            Logger.LogInfo(ModText.T("PluginName") + ModText.T("AwakeStarted"));
            _config = AutoLabBalancerConfig.Bind(Config);
            _saveScopedSettings = new SaveScopedSettings(Logger);
            _saveScopedSettings.CaptureGlobalDefaults(_config);
            _nextTickAt = 0f;
            RuntimeSettings.Config = _config;
            RuntimeSettings.Logger = Logger;
            RuntimeSettings.SaveSettings = _saveScopedSettings;

            try
            {
                _harmony = new Harmony(PluginGuid);
                _harmony.PatchAll(typeof(AutoLabBalancerPlugin).Assembly);
                Logger.LogInfo(ModText.T("HarmonyInstalled"));
            }
            catch (Exception ex)
            {
                Logger.LogError(ModText.T("HarmonyFailed") + ex);
            }

            Logger.LogInfo(ModText.T("PluginName") + ModText.T("Loaded"));
        }

        private void Update()
        {
            if (Input.GetKeyDown(_config.SettingsWindowKey.Value))
            {
                _showSettings = !_showSettings;
            }

            if (!_config.Enabled.Value)
            {
                return;
            }

            _saveScopedSettings.RefreshScope(_config);
            TraceLoggingService.Tick(Time.realtimeSinceStartup);
            PerformanceProfiler.Tick(Time.realtimeSinceStartup);
            FramePacingService.Tick();
            SchedulingEngineService.Tick(Time.realtimeSinceStartup);
            ExternalTransferQueueBrokerService.Tick(Time.realtimeSinceStartup);

            if (Time.realtimeSinceStartup < _nextTickAt)
            {
                return;
            }

            _nextTickAt = Time.realtimeSinceStartup + Mathf.Max(5f, _config.TickIntervalSeconds.Value);

            try
            {
                RefreshSettingsDiagnosticsCache(force: true);
                PerformanceOptimizationService.Tick(Time.realtimeSinceStartup);
                ProductivityTweaksService.Tick(Time.realtimeSinceStartup);
                UnknownEmployeeService.Tick(Time.realtimeSinceStartup);
                IntakeControlService.ApplyDailyCap();
                MedicalCaseRewriteService.Tick();
                TickSurgeryAnalytics();
            }
            catch (Exception ex)
            {
                Logger.LogError(ModText.T("TickFailed") + ex);
            }
        }

        private void TickSurgeryAnalytics()
        {
            if (!_config.EnableSurgeryAnalyticsLog.Value || Time.realtimeSinceStartup < _nextSurgeryAnalyticsAt)
            {
                return;
            }

            _nextSurgeryAnalyticsAt = Time.realtimeSinceStartup + Mathf.Max(10f, _config.SurgeryAnalyticsLogIntervalSeconds.Value);
            var snapshot = BottleneckOverlayService.CreateSnapshot();
            if (snapshot == null || !snapshot.Ready)
            {
                return;
            }

            Logger.LogInfo(ModText.T("AnalyticsTag") + " "
                + ModText.T("AnalyticsPlanned") + snapshot.PlannedSurgeries
                + ModText.T("AnalyticsCritical") + snapshot.CriticalSurgeryPatients
                + ModText.T("AnalyticsWaitingDepartments") + snapshot.WaitingSurgeryDepartments
                + ModText.T("AnalyticsBlockers") + snapshot.SurgeryWaitingForRoom
                + "/" + snapshot.SurgeryWaitingForStaff
                + "/" + snapshot.SurgeryWaitingForTransport
                + "/" + snapshot.SurgeryWaitingForCriticalPatients
                + ModText.T("AnalyticsTransportWaits") + snapshot.WaitingForExamTransport
                + "/" + snapshot.WaitingForTreatmentTransport
                + "/" + snapshot.OutsideRoomChainedPatients
                + ModText.T("AnalyticsRadiology") + snapshot.RadiologyPlannedExaminations
                + "/" + snapshot.RadiologyCtExaminations
                + "/" + snapshot.RadiologyMriExaminations
                + "/" + snapshot.RadiologyXrayExaminations
                + "/" + snapshot.RadiologyUsgExaminations
                + "/" + snapshot.RadiologyAngioExaminations
                + "/" + snapshot.CardiologyExaminations
                + "/" + snapshot.NeurologyExaminations
                + "/" + snapshot.HematologyExaminations
                + "/" + snapshot.MicrobiologyExaminations
                + "/" + snapshot.HistologyExaminations
                + "/" + snapshot.OfficeExaminations
                + "/" + snapshot.OtherExaminations
                + ModText.T("AnalyticsIntake") + snapshot.IntakeClinicPatients
                + "/" + snapshot.IntakeClinicCapacity
                + "/" + snapshot.IntakeAmbulancePatients
                + "/" + snapshot.IntakeAmbulanceCapacity
                + ModText.T("AnalyticsFreeStaff") + snapshot.FreeDoctors
                + "/" + snapshot.FreeNurses
                + "/" + snapshot.FreeJanitors
                + (string.IsNullOrEmpty(snapshot.SurgeryReadinessDetails) ? string.Empty : ModText.T("AnalyticsReadiness") + snapshot.SurgeryReadinessDetails.Replace("\n", " | ")));
        }

        private void OnDestroy()
        {
            try
            {
                if (_harmony != null)
                {
                    _harmony.UnpatchSelf();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ModText.T("CleanupFailed") + ex);
            }
        }

        private void OnGUI()
        {
            MedicalCaseRewriteService.DrawCaseWindow();

            if (!_showSettings)
            {
                return;
            }

            EnsureSettingsStyles();
            var oldColor = GUI.color;
            var oldBackground = GUI.backgroundColor;
            var oldContent = GUI.contentColor;
            try
            {
                GUI.color = Color.white;
                GUI.backgroundColor = Color.white;
                GUI.contentColor = Color.white;
                _settingsWindow = GUILayout.Window(871234, _settingsWindow, DrawSettingsWindow, string.Empty, _settingsWindowStyle);
            }
            finally
            {
                GUI.color = oldColor;
                GUI.backgroundColor = oldBackground;
                GUI.contentColor = oldContent;
            }
        }

        private void DrawSettingsWindow(int windowId)
        {
            _saveScopedSettings.RefreshScope(_config);
            GUILayout.BeginVertical();
            DrawSettingsHeader();
            GUILayout.Space(8f);

            GUILayout.BeginVertical(_settingsSectionStyle);
            GUILayout.BeginHorizontal();
            DrawPageButton(0, ModText.T("PageSettings"));
            DrawPageButton(1, ModText.T("PageCounters"));
            DrawPageButton(2, ModText.T("PageBottlenecks"));
            DrawPageButton(3, ModText.T("PageSurgery"));
            DrawPageButton(4, ModText.T("PagePerformance"));
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.Space(8f);
            GUILayout.BeginVertical(_settingsContentPanelStyle, GUILayout.ExpandHeight(true));
            _settingsScroll = GUILayout.BeginScrollView(_settingsScroll, false, true, GUIStyle.none, GUI.skin.verticalScrollbar, _settingsScrollViewStyle, GUILayout.ExpandHeight(true));
            if (_settingsPage == 0)
            {
                DrawSettingsPage();
            }
            else if (_settingsPage == 1)
            {
                DrawCountersPage();
            }
            else if (_settingsPage == 2)
            {
                DrawBottlenecksPage(false);
            }
            else if (_settingsPage == 3)
            {
                DrawBottlenecksPage(true);
            }
            else
            {
                DrawPerformancePage();
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(ModText.T("Close"), _settingsPrimaryButtonStyle, GUILayout.Width(200f), GUILayout.Height(30f)))
            {
                _showSettings = false;
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 40f));
        }

        private void DrawPageButton(int page, string label)
        {
            var style = _settingsPage == page ? _settingsPageButtonActiveStyle : _settingsPageButtonStyle;
            if (GUILayout.Button(label, style, GUILayout.Height(28f)))
            {
                _settingsPage = page;
                _settingsScroll = Vector2.zero;
            }
        }

        private void DrawSettingsPage()
        {
            GUILayout.BeginVertical(_settingsSectionStyle);
            GUILayout.BeginHorizontal();
            DrawSettingsCategoryButton(0, ModText.T("SettingsCategoryGameplay"));
            DrawSettingsCategoryButton(1, ModText.T("SettingsCategoryPatients"));
            DrawSettingsCategoryButton(2, ModText.T("SettingsCategoryPerformance"));
            DrawSettingsCategoryButton(3, ModText.T("SettingsCategoryDeveloper"));
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.Space(8f);
            if (_settingsCategory == 0)
            {
                DrawSectionCard(ModText.T("SettingsCategoryGameplay"), DrawGameplaySettingsCategory);
            }
            else if (_settingsCategory == 1)
            {
                DrawSectionCard(ModText.T("SettingsCategoryPatients"), DrawPatientSettingsCategory);
            }
            else if (_settingsCategory == 2)
            {
                DrawSectionCard(ModText.T("SettingsCategoryPerformance"), DrawPerformanceSettingsCategory);
            }
            else
            {
                DrawSectionCard(ModText.T("SettingsCategoryDeveloper"), DrawDeveloperSettingsCategory);
            }
        }

        private void DrawCountersPage()
        {
            DrawSectionCard(ModText.T("PageCounters"), delegate
            {
                GUILayout.Label(_cachedCountersText ?? string.Empty, _settingsTextBlockStyle);
            });
        }

        private void DrawBottlenecksPage(bool surgeryOnly)
        {
            DrawSectionCard(ModText.T(surgeryOnly ? "PageSurgery" : "PageBottlenecks"), delegate
            {
                GUILayout.Label(surgeryOnly ? (_cachedSurgeryText ?? string.Empty) : (_cachedBottlenecksText ?? string.Empty), _settingsTextBlockStyle);
            });
        }

        private void DrawBottleneckOverlay(bool surgeryOnly)
        {
            if (_overlaySnapshot == null || Time.realtimeSinceStartup >= _nextOverlaySnapshotAt)
            {
                _overlaySnapshot = BottleneckOverlayService.CreateSnapshot();
                _nextOverlaySnapshotAt = Time.realtimeSinceStartup + 2f;
            }

            GUILayout.Space(8f);
            GUILayout.Label(ModText.T("PageBottlenecks"));
            if (_overlaySnapshot == null || !_overlaySnapshot.Ready)
            {
                GUILayout.Label(ModText.T("GameNotReady"));
                return;
            }

            if (!surgeryOnly)
            {
                GUILayout.BeginHorizontal();
                GUILayout.BeginVertical();
                GUILayout.Label(ModText.T("Patients") + _overlaySnapshot.Patients);
                GUILayout.Label(ModText.T("HighRisk") + _overlaySnapshot.HighRiskPatients);
                GUILayout.Label(ModText.T("PlannedMeds") + _overlaySnapshot.PatientsWithPlannedMedication);
                GUILayout.Label(ModText.T("IdleLabQueue") + _overlaySnapshot.IdleLabProcedures);
                GUILayout.Label(ModText.T("DepartmentsBusy") + _overlaySnapshot.BusyDepartments + "/" + _overlaySnapshot.Departments);
                GUILayout.EndVertical();
                GUILayout.BeginVertical();
                GUILayout.Label(ModText.T("FreeDoctors") + _overlaySnapshot.FreeDoctors + "/" + _overlaySnapshot.Doctors);
                GUILayout.Label(ModText.T("FreeNurses") + _overlaySnapshot.FreeNurses + "/" + _overlaySnapshot.Nurses);
                GUILayout.Label(ModText.T("FreeLabs") + _overlaySnapshot.FreeLabSpecialists + "/" + _overlaySnapshot.LabSpecialists);
                GUILayout.Label(ModText.T("FreeJanitors") + _overlaySnapshot.FreeJanitors + "/" + _overlaySnapshot.Janitors);
                GUILayout.Label(ModText.T("OrCleanupRooms") + ProductivityTweaksService.HighPriorityCleanupRoomCount);
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
                if (!string.IsNullOrEmpty(_overlaySnapshot.JanitorDiagnostics))
                {
                    GUILayout.Label(ModText.T("JanitorDiagnostics"));
                    GUILayout.Label(_overlaySnapshot.JanitorDiagnostics);
                }
                GUILayout.Label(ModText.T("NurseCleanupJobs") + ProductivityTweaksService.NurseCleanupJobCount);
                GUILayout.Label(ModText.F("RadiologyQueueLine",
                    _overlaySnapshot.RadiologyPlannedExaminations,
                    _overlaySnapshot.RadiologyCtExaminations,
                    _overlaySnapshot.RadiologyMriExaminations,
                    _overlaySnapshot.RadiologyXrayExaminations,
                    _overlaySnapshot.RadiologyUsgExaminations,
                    _overlaySnapshot.RadiologyAngioExaminations,
                    _overlaySnapshot.CardiologyExaminations,
                    _overlaySnapshot.NeurologyExaminations,
                    _overlaySnapshot.HematologyExaminations,
                    _overlaySnapshot.MicrobiologyExaminations,
                    _overlaySnapshot.HistologyExaminations,
                    _overlaySnapshot.OfficeExaminations,
                    _overlaySnapshot.OtherExaminations));
                GUILayout.Label(ModText.F("IntakeLine",
                    _overlaySnapshot.IntakeClinicPatients,
                    _overlaySnapshot.IntakeClinicCapacity,
                    _overlaySnapshot.IntakeAmbulancePatients,
                    _overlaySnapshot.IntakeAmbulanceCapacity,
                    _overlaySnapshot.IntakeOutpatientDoctorCapacity));
                GUILayout.Label(ModText.F("IntakeDynamicLine",
                    _overlaySnapshot.IntakeDynamicDepartmentChoices,
                    _overlaySnapshot.IntakeDirectDepartmentReferrals));
            }

            GUILayout.Label(ModText.F("SurgeryLine", _overlaySnapshot.PlannedSurgeries, _overlaySnapshot.CriticalSurgeryPatients, _overlaySnapshot.WaitingSurgeryDepartments));
            GUILayout.Label(ModText.F("SurgeryBlockersLine", _overlaySnapshot.SurgeryWaitingForRoom, _overlaySnapshot.SurgeryWaitingForStaff, _overlaySnapshot.SurgeryWaitingForTransport, _overlaySnapshot.SurgeryWaitingForCriticalPatients));
            GUILayout.Label(ModText.F("TransportWaitsLine", _overlaySnapshot.WaitingForExamTransport, _overlaySnapshot.WaitingForTreatmentTransport, _overlaySnapshot.OutsideRoomChainedPatients));
            if (surgeryOnly)
            {
                GUILayout.Label(ModText.T("SurgeryTooltipNote"));
            }

            if (surgeryOnly && !string.IsNullOrEmpty(_overlaySnapshot.SurgeryReadinessDetails))
            {
                GUILayout.Label(ModText.T("SurgeryReadiness"));
                GUILayout.Label(_overlaySnapshot.SurgeryReadinessDetails);
            }
            if (!string.IsNullOrEmpty(_overlaySnapshot.Warning))
            {
                GUILayout.Label(ModText.T("OverlayWarning") + _overlaySnapshot.Warning);
            }
        }

        private void DrawPerformancePage()
        {
            DrawSectionCard(ModText.T("PagePerformance"), delegate
            {
            GUILayout.Label(_cachedPerformanceText ?? string.Empty, _settingsTextBlockStyle);
            GUILayout.Space(8f);

            if (GUILayout.Button(ModText.T("PerformanceProfilerReset")))
            {
                PerformanceProfiler.Reset();
                SchedulingEngineService.ResetCounters();
                PerformanceOptimizationService.ResetCounters();
            }
            });
        }

        private void DrawToggle(ConfigEntry<bool> entry, string label)
        {
            var value = GUILayout.Toggle(entry.Value, label, _settingsToggleStyle);
            if (value != entry.Value)
            {
                entry.Value = value;
                if (_saveScopedSettings != null && _saveScopedSettings.HasActiveScope)
                {
                    _saveScopedSettings.SetOverride(entry, value);
                }
                else
                {
                    Config.Save();
                }
            }
        }

        private void DrawSettingsHeader()
        {
            GUILayout.BeginVertical(_settingsHeaderPanelStyle);
            GUILayout.Label(ModText.T("WindowTitle"), _settingsHeaderStyle);
            GUILayout.Label(ModText.T("SettingsSaved"), _settingsMutedLabelStyle);
            GUILayout.Label(_saveScopedSettings.HasActiveScope
                ? ModText.T("SaveSettingsScope") + _saveScopedSettings.ScopeDisplayName
                : ModText.T("SaveSettingsGlobalScope"), _settingsMutedLabelStyle);
            GUILayout.EndVertical();
        }

        private void DrawSettingsCategoryButton(int category, string label)
        {
            var style = _settingsCategory == category ? _settingsCategoryButtonActiveStyle : _settingsCategoryButtonStyle;
            if (GUILayout.Button(label, style, GUILayout.Height(28f)))
            {
                _settingsCategory = category;
                _settingsScroll = Vector2.zero;
            }
        }

        private void DrawSectionCard(string title, Action content)
        {
            GUILayout.BeginVertical(_settingsSectionStyle);
            GUILayout.Label(title, _settingsSectionHeaderStyle);
            GUILayout.Space(4f);
            if (content != null)
            {
                content();
            }
            GUILayout.EndVertical();
        }

        private void DrawGameplaySettingsCategory()
        {
            var blockNegativePerks = GUILayout.Toggle(_config.PreventNegativeEmployeePerks.Value, ModText.T("BlockNegativePerks"), _settingsToggleStyle);
            if (blockNegativePerks != _config.PreventNegativeEmployeePerks.Value)
            {
                _config.PreventNegativeEmployeePerks.Value = blockNegativePerks;
                Config.Save();
            }

            GUILayout.Space(4f);
            GUILayout.Label(ModText.T("ProductivityTweaks"), _settingsSectionHeaderStyle);
            DrawToggle(_config.EnableCustomPerks, ModText.T("EnableCustomPerks"));
            DrawToggle(_config.EnableUnknownEmployees, ModText.T("EnableUnknownEmployees"));
            DrawToggle(_config.EnableAggressiveMedicationPlanning, ModText.T("PlanMedication"));
            DrawToggle(_config.EnablePostSurgeryCleanupPriority, ModText.T("PrioritizeOrCleanup"));
            DrawToggle(_config.EnableJanitorStandbyAfterCleaning, ModText.T("JanitorStandbyAfterCleaning"));
            DrawToggle(_config.EnableStuckReservationCleanup, ModText.T("CleanStuckReservations"));
            DrawToggle(_config.EnableFlexibleStretcherPickup, ModText.T("FlexibleStretcherPickup"));
            DrawToggle(_config.EnableChainedHospitalizedExaminations, ModText.T("ChainDiagnostics"));
            DrawToggle(_config.EnableTransportReservationTimeout, ModText.T("RetryTransportReservations"));
            DrawToggle(_config.EnableNurseCheckDischarge, ModText.T("NurseCheckDischarge"));
            DrawToggle(_config.EnableEmergencyRunSpeedBoost, ModText.T("EmergencyRunSpeedBoost"));
            DrawToggle(_config.EnableNurseAssistedORCleanup, ModText.T("NurseAssistedOrCleanup"));
            DrawToggle(_config.EnableSurgeryTooltipFix, ModText.T("FixSurgeryTooltip"));
        }

        private void DrawPatientSettingsCategory()
        {
            GUILayout.Label(ModText.T("IntakeControl"), _settingsSectionHeaderStyle);
            DrawToggle(_config.EnableIntakeControl, ModText.T("EnableIntakeControl"));
            DrawToggle(_config.EnableDynamicIntakeByDoctors, ModText.T("EnableDynamicIntakeByDoctors"));

            GUILayout.Space(6f);
            GUILayout.Label(ModText.T("MedicalCaseRewrite"), _settingsSectionHeaderStyle);
            DrawToggle(_config.EnableMedicalCaseRewrite, ModText.T("EnableMedicalCaseRewrite"));
            DrawToggle(_config.EnableHopelessCases, ModText.T("EnableHopelessCases"));
            DrawToggle(_config.HopelessRequiresHospitalUpgrades, ModText.T("HopelessRequiresHospitalUpgrades"));

            GUILayout.Space(6f);
            GUILayout.Label(ModText.T("ExternalTransferQueueBroker"), _settingsSectionHeaderStyle);
            DrawToggle(_config.EnableEquipmentReferral, ModText.T("EquipmentReferral"));
            DrawToggle(_config.EnableUnsupportedDiagnosisReferral, ModText.T("UnsupportedDiagnosisReferral"));
            DrawToggle(_config.EnableManualReferralPayment, ModText.T("ManualReferralPayment"));
            DrawToggle(_config.EnableExternalTransferQueueBroker, ModText.T("ExternalTransferQueueBroker"));
            DrawToggle(_config.EnableExternalTransferParamedicSpeed, ModText.T("ExternalTransferParamedicSpeed"));
        }

        private void DrawPerformanceSettingsCategory()
        {
            GUILayout.Label(ModText.T("PerformanceProfiler"), _settingsSectionHeaderStyle);
            DrawToggle(_config.EnablePerformanceProfiler, ModText.T("EnablePerformanceProfiler"));
            DrawToggle(_config.ProfilerAutoResetAfterLog, ModText.T("ProfilerAutoResetAfterLog"));
            DrawToggle(_config.EnableFramePacing, ModText.T("EnableFramePacing"));
            DrawToggle(_config.FramePacingUseMonitorRefreshRate, ModText.T("FramePacingUseMonitorRefreshRate"));
            DrawToggle(_config.EnablePerformanceOptimizations, ModText.T("EnablePerformanceOptimizations"));

            GUILayout.Space(6f);
            GUILayout.Label(ModText.T("PageBottlenecks"), _settingsSectionHeaderStyle);
            DrawToggle(_config.EnableBottleneckOverlay, ModText.T("ShowBottleneckOverlay"));
        }

        private void DrawDeveloperSettingsCategory()
        {
            var debugLog = GUILayout.Toggle(_config.EnableDebugLog.Value, ModText.T("DebugLog"), _settingsToggleStyle);
            if (debugLog != _config.EnableDebugLog.Value)
            {
                _config.EnableDebugLog.Value = debugLog;
                Config.Save();
            }

            DrawToggle(_config.EnableDebugProductivityLog, ModText.T("ProductivityDebugLog"));
            DrawToggle(_config.CaseRewriteDebugLog, ModText.T("CaseRewriteDebugLog"));
            DrawToggle(_config.EnableDeepTraceLog, ModText.T("DeepTraceLog"));
            DrawToggle(_config.EnableSurgeryAnalyticsLog, ModText.T("SurgeryAnalyticsLog"));

            if (_config.EnableDeepTraceLog.Value)
            {
                var tracePath = TraceLoggingService.CurrentLogPath;
                if (!string.IsNullOrEmpty(tracePath))
                {
                    GUILayout.Label(ModText.F("DeepTraceLogPath", tracePath), _settingsMutedLabelStyle);
                }
            }

            GUILayout.Space(6f);
            GUILayout.Label(ModText.T("DeveloperTools"), _settingsSectionHeaderStyle);
            DrawToggle(_config.EnableHospitalUpgrades, ModText.T("EnableHospitalUpgrades"));
            DrawToggle(_config.DevCheapUpgrades, ModText.T("DevCheapUpgrades"));
            DrawToggle(_config.EnableAbsurdUpgrades, ModText.T("EnableAbsurdUpgrades"));

            GUILayout.Space(10f);
            if (GUILayout.Button(ModText.T("DevRemoveAllPatients"), _settingsPrimaryButtonStyle, GUILayout.Height(28f)))
            {
                var removed = DeveloperToolsService.RemoveAllPatients();
                _developerActionStatus = ModText.F("DevRemoveAllPatientsResult", removed);
                RefreshSettingsDiagnosticsCache(force: true);
            }

            if (!string.IsNullOrEmpty(_developerActionStatus))
            {
                GUILayout.Label(_developerActionStatus, _settingsMutedLabelStyle);
            }
        }

        private void EnsureSettingsStyles()
        {
            if (_settingsWindowStyle != null)
            {
                return;
            }

            _settingsWindowTexture = MakeSolidTexture(new Color(0.94f, 0.94f, 0.94f, 1f));
            _settingsHeaderTexture = MakeSolidTexture(new Color(0.28f, 0.28f, 0.28f, 1f));
            _settingsSectionTexture = MakeSolidTexture(new Color(0.90f, 0.90f, 0.90f, 1f));
            _settingsButtonTexture = MakeSolidTexture(new Color(0.40f, 0.40f, 0.40f, 1f));
            _settingsButtonActiveTexture = MakeSolidTexture(new Color(0.22f, 0.22f, 0.22f, 1f));

            _settingsWindowStyle = new GUIStyle(GUI.skin.window)
            {
                padding = new RectOffset(12, 12, 12, 12),
                margin = new RectOffset(0, 0, 0, 0)
            };
            _settingsWindowStyle.normal.background = _settingsWindowTexture;
            _settingsWindowStyle.active.background = _settingsWindowTexture;
            _settingsWindowStyle.focused.background = _settingsWindowTexture;
            _settingsWindowStyle.hover.background = _settingsWindowTexture;
            _settingsWindowStyle.normal.textColor = Color.black;

            _settingsHeaderStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };

            _settingsMutedLabelStyle = new GUIStyle(GUI.skin.label)
            {
                wordWrap = true,
                normal = { textColor = new Color(0.90f, 0.90f, 0.90f, 1f) }
            };

            _settingsHeaderPanelStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(10, 10, 10, 10),
                margin = new RectOffset(0, 0, 0, 8)
            };
            _settingsHeaderPanelStyle.normal.background = _settingsHeaderTexture;
            _settingsHeaderPanelStyle.normal.textColor = Color.white;

            _settingsSectionStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(10, 10, 10, 10),
                margin = new RectOffset(0, 0, 0, 8)
            };
            _settingsSectionStyle.normal.background = _settingsSectionTexture;
            _settingsSectionStyle.normal.textColor = Color.black;

            _settingsScrollViewStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(8, 8, 8, 8),
                margin = new RectOffset(0, 0, 0, 0)
            };
            _settingsScrollViewStyle.normal.background = _settingsWindowTexture;
            _settingsScrollViewStyle.hover.background = _settingsWindowTexture;
            _settingsScrollViewStyle.active.background = _settingsWindowTexture;
            _settingsScrollViewStyle.focused.background = _settingsWindowTexture;

            _settingsContentPanelStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(8, 8, 8, 8),
                margin = new RectOffset(0, 0, 0, 0)
            };
            _settingsContentPanelStyle.normal.background = _settingsWindowTexture;
            _settingsContentPanelStyle.hover.background = _settingsWindowTexture;
            _settingsContentPanelStyle.active.background = _settingsWindowTexture;
            _settingsContentPanelStyle.focused.background = _settingsWindowTexture;

            _settingsSectionHeaderStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 13,
                wordWrap = true,
                normal = { textColor = new Color(0.15f, 0.15f, 0.15f, 1f) }
            };

            _settingsPageButtonStyle = BuildButtonStyle(_settingsButtonTexture, Color.white);
            _settingsPageButtonActiveStyle = BuildButtonStyle(_settingsButtonActiveTexture, Color.white);
            _settingsCategoryButtonStyle = BuildButtonStyle(_settingsButtonTexture, Color.white);
            _settingsCategoryButtonActiveStyle = BuildButtonStyle(_settingsButtonActiveTexture, Color.white);
            _settingsPrimaryButtonStyle = BuildButtonStyle(_settingsButtonTexture, Color.white);

            _settingsToggleStyle = new GUIStyle(GUI.skin.toggle)
            {
                wordWrap = true,
                padding = new RectOffset(20, 4, 4, 4),
                margin = new RectOffset(2, 2, 2, 2)
            };
            _settingsToggleStyle.normal.textColor = Color.black;
            _settingsToggleStyle.onNormal.textColor = Color.black;
            _settingsToggleStyle.hover.textColor = Color.black;
            _settingsToggleStyle.onHover.textColor = Color.black;
            _settingsToggleStyle.focused.textColor = Color.black;
            _settingsToggleStyle.onFocused.textColor = Color.black;

            _settingsTextBlockStyle = new GUIStyle(GUI.skin.label)
            {
                wordWrap = true,
                richText = false,
                normal = { textColor = Color.black }
            };
        }

        private GUIStyle BuildButtonStyle(Texture2D background, Color textColor)
        {
            var style = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                fixedHeight = 0f,
                wordWrap = true,
                padding = new RectOffset(8, 8, 6, 6)
            };
            style.normal.background = background;
            style.hover.background = background;
            style.active.background = background;
            style.focused.background = background;
            style.normal.textColor = textColor;
            style.hover.textColor = textColor;
            style.active.textColor = textColor;
            style.focused.textColor = textColor;
            return style;
        }

        private Texture2D MakeSolidTexture(Color color)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            texture.hideFlags = HideFlags.HideAndDontSave;
            return texture;
        }

        private void RefreshSettingsDiagnosticsCache(bool force = false)
        {
            if (!force && Time.realtimeSinceStartup < _nextSettingsDiagnosticsRefreshAt)
            {
                return;
            }

            _nextSettingsDiagnosticsRefreshAt = Time.realtimeSinceStartup + 0.5f;
            _cachedCountersText = BuildCountersText();
            _cachedBottlenecksText = BuildBottlenecksText(false);
            _cachedSurgeryText = BuildBottlenecksText(true);
            _cachedPerformanceText = BuildPerformanceText();
        }

        private string BuildCountersText()
        {
            var lines = new List<string>
            {
                ModText.T("MedicationAutoAdded") + RuntimeCounters.MedicationsAutoPlanned,
                ModText.T("OrCleanupPriorities") + RuntimeCounters.ORCleanupPrioritiesCreated,
                ModText.T("NurseOrTilesCleaned") + RuntimeCounters.NurseORTilesCleaned,
                ModText.T("StuckReservationsCleared") + RuntimeCounters.StuckReservationsCleared,
                ModText.T("FlexibleTransportFallbacks") + RuntimeCounters.FlexibleTransportFallbacks,
                ModText.T("TransportReservationsRetried") + RuntimeCounters.TransportReservationsRetried,
                ModText.T("EmergencySpeedBoosts") + RuntimeCounters.EmergencySpeedBoosts,
                ModText.T("EquipmentReferrals") + RuntimeCounters.EquipmentReferrals,
                ModText.T("ReferralIncome") + RuntimeCounters.EquipmentReferralIncome,
                ModText.T("UnsupportedDiagnosisReferrals") + RuntimeCounters.UnsupportedDiagnosisReferrals,
                ModText.T("UnsupportedDiagnosisIncome") + RuntimeCounters.UnsupportedDiagnosisReferralIncome,
                ModText.T("ManualReferralPayments") + RuntimeCounters.ManualReferralPayments,
                ModText.T("ManualReferralIncome") + RuntimeCounters.ManualReferralIncome
            };
            return string.Join("\n", lines.ToArray());
        }

        private string BuildBottlenecksText(bool surgeryOnly)
        {
            if (!_config.EnableBottleneckOverlay.Value)
            {
                return ModText.T("OverlayDisabled");
            }

            if (_overlaySnapshot == null || Time.realtimeSinceStartup >= _nextOverlaySnapshotAt)
            {
                _overlaySnapshot = BottleneckOverlayService.CreateSnapshot();
                _nextOverlaySnapshotAt = Time.realtimeSinceStartup + 2f;
            }

            if (_overlaySnapshot == null || !_overlaySnapshot.Ready)
            {
                return ModText.T("GameNotReady");
            }

            var lines = new List<string>();
            if (!surgeryOnly)
            {
                lines.Add(ModText.T("Patients") + _overlaySnapshot.Patients);
                lines.Add(ModText.T("HighRisk") + _overlaySnapshot.HighRiskPatients);
                lines.Add(ModText.T("PlannedMeds") + _overlaySnapshot.PatientsWithPlannedMedication);
                lines.Add(ModText.T("IdleLabQueue") + _overlaySnapshot.IdleLabProcedures);
                lines.Add(ModText.T("DepartmentsBusy") + _overlaySnapshot.BusyDepartments + "/" + _overlaySnapshot.Departments);
                lines.Add(ModText.T("FreeDoctors") + _overlaySnapshot.FreeDoctors + "/" + _overlaySnapshot.Doctors);
                lines.Add(ModText.T("FreeNurses") + _overlaySnapshot.FreeNurses + "/" + _overlaySnapshot.Nurses);
                lines.Add(ModText.T("FreeLabs") + _overlaySnapshot.FreeLabSpecialists + "/" + _overlaySnapshot.LabSpecialists);
                lines.Add(ModText.T("FreeJanitors") + _overlaySnapshot.FreeJanitors + "/" + _overlaySnapshot.Janitors);
                lines.Add(ModText.T("OrCleanupRooms") + ProductivityTweaksService.HighPriorityCleanupRoomCount);
                if (!string.IsNullOrEmpty(_overlaySnapshot.JanitorDiagnostics))
                {
                    lines.Add(ModText.T("JanitorDiagnostics"));
                    lines.Add(_overlaySnapshot.JanitorDiagnostics);
                }
                lines.Add(ModText.T("NurseCleanupJobs") + ProductivityTweaksService.NurseCleanupJobCount);
                lines.Add(ModText.F("RadiologyQueueLine",
                    _overlaySnapshot.RadiologyPlannedExaminations,
                    _overlaySnapshot.RadiologyCtExaminations,
                    _overlaySnapshot.RadiologyMriExaminations,
                    _overlaySnapshot.RadiologyXrayExaminations,
                    _overlaySnapshot.RadiologyUsgExaminations,
                    _overlaySnapshot.RadiologyAngioExaminations,
                    _overlaySnapshot.CardiologyExaminations,
                    _overlaySnapshot.NeurologyExaminations,
                    _overlaySnapshot.HematologyExaminations,
                    _overlaySnapshot.MicrobiologyExaminations,
                    _overlaySnapshot.HistologyExaminations,
                    _overlaySnapshot.OfficeExaminations,
                    _overlaySnapshot.OtherExaminations));
                lines.Add(ModText.F("IntakeLine",
                    _overlaySnapshot.IntakeClinicPatients,
                    _overlaySnapshot.IntakeClinicCapacity,
                    _overlaySnapshot.IntakeAmbulancePatients,
                    _overlaySnapshot.IntakeAmbulanceCapacity,
                    _overlaySnapshot.IntakeOutpatientDoctorCapacity));
                lines.Add(ModText.F("IntakeDynamicLine",
                    _overlaySnapshot.IntakeDynamicDepartmentChoices,
                    _overlaySnapshot.IntakeDirectDepartmentReferrals));
            }

            lines.Add(ModText.F("SurgeryLine", _overlaySnapshot.PlannedSurgeries, _overlaySnapshot.CriticalSurgeryPatients, _overlaySnapshot.WaitingSurgeryDepartments));
            lines.Add(ModText.F("SurgeryBlockersLine", _overlaySnapshot.SurgeryWaitingForRoom, _overlaySnapshot.SurgeryWaitingForStaff, _overlaySnapshot.SurgeryWaitingForTransport, _overlaySnapshot.SurgeryWaitingForCriticalPatients));
            lines.Add(ModText.F("TransportWaitsLine", _overlaySnapshot.WaitingForExamTransport, _overlaySnapshot.WaitingForTreatmentTransport, _overlaySnapshot.OutsideRoomChainedPatients));
            if (surgeryOnly)
            {
                lines.Add(ModText.T("SurgeryTooltipNote"));
            }

            if (surgeryOnly && !string.IsNullOrEmpty(_overlaySnapshot.SurgeryReadinessDetails))
            {
                lines.Add(ModText.T("SurgeryReadiness"));
                lines.Add(_overlaySnapshot.SurgeryReadinessDetails);
            }

            if (!string.IsNullOrEmpty(_overlaySnapshot.Warning))
            {
                lines.Add(ModText.T("OverlayWarning") + _overlaySnapshot.Warning);
            }

            return string.Join("\n", lines.ToArray());
        }

        private string BuildPerformanceText()
        {
            var lines = new List<string>();
            if (!_config.EnablePerformanceProfiler.Value)
            {
                lines.Add(ModText.T("PerformanceProfilerDisabled"));
            }
            else
            {
                var samples = PerformanceProfiler.GetTopSamples(_config.ProfilerTopN.Value);
                if (samples.Count == 0)
                {
                    lines.Add(ModText.T("PerformanceProfilerNoSamples"));
                }
                else
                {
                    foreach (var sample in samples)
                    {
                        lines.Add(PerformanceProfiler.FormatSample(sample));
                    }
                }
            }

            var scheduling = SchedulingEngineService.Snapshot;
            if (scheduling != null)
            {
                lines.Add(string.Empty);
                lines.Add(ModText.T("SchedulingEngine"));
                if (!scheduling.Ready)
                {
                    lines.Add(ModText.T("SchedulingEngineNotReady") + scheduling.Warning);
                }
                else
                {
                    lines.Add(ModText.F("SchedulingEngineLine",
                        scheduling.Departments,
                        scheduling.TotalTasks,
                        scheduling.CriticalTasks,
                        scheduling.SurgeryTasks,
                        scheduling.MedicineTasks,
                        scheduling.TransportTasks,
                        scheduling.NurseTasks,
                        scheduling.DoctorTasks,
                        scheduling.NurseDryRunDispatches,
                        scheduling.DoctorDryRunDispatches,
                        scheduling.FreeStaff,
                        scheduling.Staff,
                        scheduling.RebuildMs.ToString("0.00")));
                    lines.Add(ModText.F("SchedulingEngineTaskObjectsLine", scheduling.TaskObjects));
                    lines.Add(ModText.T("SchedulingEngineTopBoard") + scheduling.TopBoardSummary);
                    lines.Add(ModText.T("SchedulingEngineTopDispatch") + scheduling.TopDispatchSummary);
                }

                var counters = SchedulingEngineService.GetCounters();
                lines.Add(ModText.F("SchedulingEngineCountersLine",
                    counters.Rebuilds,
                    counters.AverageRebuildMs.ToString("0.00"),
                    counters.MaxRebuildMs.ToString("0.00"),
                    counters.BoardHits,
                    counters.BoardMisses,
                    counters.BoardStale,
                    counters.NurseGatingSkips,
                    counters.NurseGatingChecks,
                    counters.OutpatientGatingSkips,
                    counters.OutpatientGatingChecks,
                    counters.DoctorSearchGatingSkips,
                    counters.DoctorSearchGatingChecks));
                lines.Add(ModText.F("SchedulingDispatcherCountersLine", counters.DispatcherRecommendations));
                lines.Add(ModText.F("SchedulingDispatcherApplyCountersLine",
                    counters.DispatcherApplyAllows,
                    counters.DispatcherApplySkips,
                    counters.DispatcherApplyChecks));
                lines.Add(ModText.F("ReservationBrokerCountersLine",
                    counters.ReservationBrokerHits,
                    counters.ReservationBrokerMisses,
                    counters.ReservationBrokerStores));
                lines.Add(ModText.F("ReservationBrokerReasonCountersLine",
                    counters.ReservationBrokerAvailableDrops,
                    counters.ReservationBrokerStaffUnavailableStores,
                    counters.ReservationBrokerRoomUnavailableStores,
                    counters.ReservationBrokerEquipmentUnavailableStores,
                    counters.ReservationBrokerOtherFailureStores));
                lines.Add(ModText.F("TaskBoardCountersLine",
                    counters.TaskBoardValidationFails,
                    counters.TaskBoardClaimSkips));
            }

            var perfCounters = PerformanceOptimizationService.GetCounters();
            lines.Add(ModText.F("PerformanceOptimizationCountersLine",
                perfCounters.ObjectSearchHits,
                perfCounters.ObjectSearchMisses,
                perfCounters.ObjectSearchInvalidHits,
                perfCounters.StaffSearchHits,
                perfCounters.StaffSearchMisses,
                perfCounters.StaffSearchInvalidHits));
            lines.Add(ModText.F("RouteRequestCountersLine",
                perfCounters.RouteRepeatedRequests,
                perfCounters.RouteRequests,
                perfCounters.ReflectionFallbacks,
                perfCounters.MissingTargets));

            var externalTransfer = ExternalTransferQueueBrokerService.Snapshot;
            if (externalTransfer != null)
            {
                lines.Add(string.Empty);
                lines.Add(ModText.T("ExternalTransferQueueBroker"));
                if (!externalTransfer.Ready)
                {
                    lines.Add(ModText.T("ExternalTransferQueueBrokerNotReady") + externalTransfer.Warning);
                }
                else
                {
                    lines.Add(ModText.F("ExternalTransferQueueBrokerLine",
                        externalTransfer.SentAwayPatients,
                        externalTransfer.WaitingTransfers,
                        externalTransfer.ActiveTransfers,
                        externalTransfer.ActiveParamedics,
                        externalTransfer.ExternalAmbulances,
                        externalTransfer.StuckTransfers,
                        externalTransfer.MaxActiveStateAge.ToString("0.0"),
                        string.IsNullOrEmpty(externalTransfer.ActiveState) ? "-" : externalTransfer.ActiveState));
                }
            }

            lines.Add(string.Empty);
            lines.Add(FramePacingService.Summary);
            return string.Join("\n", lines.ToArray());
        }
    }

    internal sealed class SaveScopedSettings
    {
        private readonly ManualLogSource _logger;
        private readonly Dictionary<string, bool> _globalDefaults = new Dictionary<string, bool>();
        private readonly Dictionary<string, string> _overrides = new Dictionary<string, string>();
        private string _scopeKey;
        private string _scopeDisplayName;
        private string _scopeIdentifier;
        private string _scopeIdentifierSource;
        private string _scopePath;

        public SaveScopedSettings(ManualLogSource logger)
        {
            _logger = logger;
        }

        public bool HasActiveScope
        {
            get { return !string.IsNullOrEmpty(_scopeKey); }
        }

        public string ScopeDisplayName
        {
            get { return string.IsNullOrEmpty(_scopeDisplayName) ? "unknown" : _scopeDisplayName; }
        }

        public string ScopeIdentifier
        {
            get { return string.IsNullOrEmpty(_scopeIdentifier) ? "unknown" : _scopeIdentifier; }
        }

        public void CaptureGlobalDefaults(AutoLabBalancerConfig config)
        {
            foreach (var entry in GetBoolEntries(config))
            {
                _globalDefaults[GetEntryKey(entry)] = entry.Value;
            }
        }

        public void RefreshScope(AutoLabBalancerConfig config)
        {
            var identity = GetCurrentScopeIdentity();
            var nextKey = identity == null || string.IsNullOrEmpty(identity.Identifier) ? null : MakeScopeKey(identity.Identifier);
            if (nextKey == _scopeKey)
            {
                return;
            }

            _scopeKey = nextKey;
            _scopeDisplayName = identity == null ? null : identity.DisplayName;
            _scopeIdentifier = identity == null ? null : identity.Identifier;
            _scopeIdentifierSource = identity == null ? null : identity.Source;
            _overrides.Clear();
            _scopePath = null;

            if (!string.IsNullOrEmpty(_scopeKey))
            {
                var dir = System.IO.Path.Combine(Paths.ConfigPath, "AutoLabBalancer.SaveSettings");
                _scopePath = System.IO.Path.Combine(dir, _scopeKey + ".cfg");
                TryMigrateLegacyHospitalNameScope(dir, identity);
                LoadOverrides();
            }

            Apply(config);
        }

        public void SetOverride(ConfigEntry<bool> entry, bool value)
        {
            if (string.IsNullOrEmpty(_scopePath))
            {
                return;
            }

            _overrides[GetEntryKey(entry)] = value.ToString();
            SaveOverrides();
        }

        public int GetInt(ConfigEntry<int> entry, int saveDefault)
        {
            if (!HasActiveScope || entry == null)
            {
                return entry == null ? saveDefault : entry.Value;
            }

            var value = GetRawValue(GetEntryKey(entry));
            int parsed;
            return int.TryParse(value, out parsed) ? parsed : saveDefault;
        }

        public bool GetBool(ConfigEntry<bool> entry, bool saveDefault)
        {
            if (!HasActiveScope || entry == null)
            {
                return entry != null && entry.Value;
            }

            var value = GetRawValue(GetEntryKey(entry));
            bool parsed;
            return bool.TryParse(value, out parsed) ? parsed : saveDefault;
        }

        public void SetInt(ConfigEntry<int> entry, int value)
        {
            SetRawValue(entry, value.ToString());
        }

        public void SetBool(ConfigEntry<bool> entry, bool value)
        {
            SetRawValue(entry, value.ToString());
        }

        private string GetRawValue(string key)
        {
            string value;
            return key != null && _overrides.TryGetValue(key, out value) ? value : null;
        }

        private void SetRawValue(ConfigEntryBase entry, string value)
        {
            if (entry == null || string.IsNullOrEmpty(_scopePath))
            {
                return;
            }

            _overrides[GetEntryKey(entry)] = value;
            SaveOverrides();
        }

        private void Apply(AutoLabBalancerConfig config)
        {
            foreach (var entry in GetBoolEntries(config))
            {
                var key = GetEntryKey(entry);
                bool value;
                string raw;
                if (_overrides.TryGetValue(key, out raw) && bool.TryParse(raw, out value))
                {
                    entry.Value = value;
                    continue;
                }

                if (!_globalDefaults.TryGetValue(key, out value))
                {
                    continue;
                }

                entry.Value = value;
            }
        }

        private void LoadOverrides()
        {
            if (string.IsNullOrEmpty(_scopePath) || !System.IO.File.Exists(_scopePath))
            {
                return;
            }

            try
            {
                foreach (var line in System.IO.File.ReadAllLines(_scopePath))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#"))
                    {
                        continue;
                    }

                    var split = trimmed.IndexOf('=');
                    if (split <= 0)
                    {
                        continue;
                    }

                    _overrides[trimmed.Substring(0, split).Trim()] = trimmed.Substring(split + 1).Trim();
                }
            }
            catch (Exception ex)
            {
                if (_logger != null)
                {
                    _logger.LogWarning("Failed to load save-scoped settings: " + ex.Message);
                }
            }
        }

        private void SaveOverrides()
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(_scopePath);
                if (!System.IO.Directory.Exists(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }

                var lines = new List<string>();
                lines.Add("# AutoLabBalancer save-scoped settings");
                lines.Add("# Hospital=" + ScopeDisplayName);
                lines.Add("# Identifier=" + ScopeIdentifier);
                lines.Add("# IdentifierSource=" + (string.IsNullOrEmpty(_scopeIdentifierSource) ? "unknown" : _scopeIdentifierSource));
                foreach (var pair in _overrides)
                {
                    lines.Add(pair.Key + "=" + pair.Value);
                }

                System.IO.File.WriteAllLines(_scopePath, lines.ToArray());
            }
            catch (Exception ex)
            {
                if (_logger != null)
                {
                    _logger.LogWarning("Failed to save save-scoped settings: " + ex.Message);
                }
            }
        }

        private static IEnumerable<ConfigEntry<bool>> GetBoolEntries(AutoLabBalancerConfig config)
        {
            if (config == null)
            {
                yield break;
            }

            var properties = typeof(AutoLabBalancerConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            for (var i = 0; i < properties.Length; i++)
            {
                if (properties[i].PropertyType != typeof(ConfigEntry<bool>))
                {
                    continue;
                }

                var entry = properties[i].GetValue(config, null) as ConfigEntry<bool>;
                if (entry != null)
                {
                    yield return entry;
                }
            }
        }

        private static string GetEntryKey(ConfigEntryBase entry)
        {
            return entry.Definition.Section + "." + entry.Definition.Key;
        }

        private static SaveScopeIdentity GetCurrentScopeIdentity()
        {
            var hospitalName = GetCurrentHospitalName();
            var saveName = GetCurrentSaveName();
            if (!string.IsNullOrEmpty(saveName))
            {
                var display = string.IsNullOrEmpty(hospitalName) ? saveName : hospitalName + " [" + saveName + "]";
                return new SaveScopeIdentity("save:" + saveName, display, "PlayerProfile.m_currentSave");
            }

            if (!string.IsNullOrEmpty(hospitalName))
            {
                return new SaveScopeIdentity("hospital-name:" + hospitalName, hospitalName, "HospitalPersistentData.m_hospitalName");
            }

            return null;
        }

        private void TryMigrateLegacyHospitalNameScope(string dir, SaveScopeIdentity identity)
        {
            if (identity == null
                || string.IsNullOrEmpty(identity.HospitalName)
                || string.IsNullOrEmpty(_scopePath)
                || System.IO.File.Exists(_scopePath))
            {
                return;
            }

            var legacyPath = System.IO.Path.Combine(dir, MakeScopeKey(identity.HospitalName) + ".cfg");
            if (!System.IO.File.Exists(legacyPath))
            {
                return;
            }

            try
            {
                System.IO.File.Copy(legacyPath, _scopePath, false);
            }
            catch (Exception ex)
            {
                if (_logger != null)
                {
                    _logger.LogWarning("Failed to migrate legacy save-scoped settings: " + ex.Message);
                }
            }
        }

        private static string GetCurrentHospitalName()
        {
            var hospital = Lopital.Hospital.Instance;
            if (hospital == null)
            {
                return null;
            }

            var state = ReflectionHelpers.GetField(hospital, "m_state");
            var name = ReflectionHelpers.GetField(state, "m_hospitalName") as string;
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }

            return string.IsNullOrEmpty(hospital.Name) ? null : hospital.Name;
        }

        private static string GetCurrentSaveName()
        {
            var type = AccessTools.TypeByName("PlayerProfile");
            if (type == null)
            {
                return null;
            }

            object profile = null;
            var property = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (property != null)
            {
                profile = property.GetValue(null, null);
            }

            if (profile == null)
            {
                var field = type.GetField("sm_instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                profile = field == null ? null : field.GetValue(null);
            }

            var currentSave = ReflectionHelpers.GetField(profile, "m_currentSave") as string;
            return string.IsNullOrEmpty(currentSave) ? null : currentSave.Trim();
        }

        private static string MakeScopeKey(string displayName)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(displayName);
            using (var sha1 = System.Security.Cryptography.SHA1.Create())
            {
                var hash = sha1.ComputeHash(bytes);
                var builder = new System.Text.StringBuilder("hospital-");
                for (var i = 0; i < hash.Length; i++)
                {
                    builder.Append(hash[i].ToString("x2"));
                }

                return builder.ToString();
            }
        }

        private sealed class SaveScopeIdentity
        {
            public readonly string Identifier;
            public readonly string DisplayName;
            public readonly string Source;
            public readonly string HospitalName;

            public SaveScopeIdentity(string identifier, string displayName, string source)
            {
                Identifier = identifier;
                DisplayName = displayName;
                Source = source;
                var marker = "hospital-name:";
                HospitalName = identifier != null && identifier.StartsWith(marker, StringComparison.Ordinal)
                    ? identifier.Substring(marker.Length)
                    : GetCurrentHospitalName();
            }
        }
    }

    internal sealed class AutoLabBalancerConfig
    {
        public ConfigEntry<bool> Enabled { get; private set; }
        public ConfigEntry<bool> PreventNegativeEmployeePerks { get; private set; }
        public ConfigEntry<bool> EnableDebugLog { get; private set; }
        public ConfigEntry<KeyCode> SettingsWindowKey { get; private set; }
        public ConfigEntry<float> TickIntervalSeconds { get; private set; }
        public ConfigEntry<bool> EnablePostSurgeryCleanupPriority { get; private set; }
        public ConfigEntry<bool> EnableNurseAssistedORCleanup { get; private set; }
        public ConfigEntry<bool> EnableFreeTimeSuppression { get; private set; }
        public ConfigEntry<bool> EnableStuckReservationCleanup { get; private set; }
        public ConfigEntry<bool> EnableFlexibleStretcherPickup { get; private set; }
        public ConfigEntry<bool> EnableChainedHospitalizedExaminations { get; private set; }
        public ConfigEntry<bool> EnableTransportReservationTimeout { get; private set; }
        public ConfigEntry<bool> EnableNurseCheckDischarge { get; private set; }
        public ConfigEntry<bool> EnableEmergencyRunSpeedBoost { get; private set; }
        public ConfigEntry<bool> EnableAggressiveMedicationPlanning { get; private set; }
        public ConfigEntry<bool> EnableCustomPerks { get; private set; }
        public ConfigEntry<bool> EnableUnknownEmployees { get; private set; }
        public ConfigEntry<int> MaxPerksPerCharacter { get; private set; }
        public ConfigEntry<int> MaxAutoMedicationsPerPlan { get; private set; }
        public ConfigEntry<int> MaxPlannedMedicationsPerPatient { get; private set; }
        public ConfigEntry<float> EmergencyRunSpeedMultiplier { get; private set; }
        public ConfigEntry<float> StuckReservationTimeoutSeconds { get; private set; }
        public ConfigEntry<float> TransportReservationTimeoutSeconds { get; private set; }
        public ConfigEntry<float> ORCleanupPriorityDurationSeconds { get; private set; }
        public ConfigEntry<float> NurseORCleanupMaxDurationSeconds { get; private set; }
        public ConfigEntry<bool> EnableJanitorStandbyAfterCleaning { get; private set; }
        public ConfigEntry<bool> SuppressFreeTimeWhenDepartmentBusy { get; private set; }
        public ConfigEntry<bool> EnableDebugProductivityLog { get; private set; }
        public ConfigEntry<bool> EnableBottleneckOverlay { get; private set; }
        public ConfigEntry<bool> EnableSurgeryAnalyticsLog { get; private set; }
        public ConfigEntry<float> SurgeryAnalyticsLogIntervalSeconds { get; private set; }
        public ConfigEntry<bool> EnableSurgeryTooltipFix { get; private set; }
        public ConfigEntry<bool> EnableEquipmentReferral { get; private set; }
        public ConfigEntry<int> EquipmentReferralPaymentPercent { get; private set; }
        public ConfigEntry<bool> EnableManualReferralPayment { get; private set; }
        public ConfigEntry<int> ManualReferralPaymentPercent { get; private set; }
        public ConfigEntry<bool> EnableUnsupportedDiagnosisReferral { get; private set; }
        public ConfigEntry<int> UnsupportedDiagnosisReferralPaymentPercent { get; private set; }
        public ConfigEntry<bool> ReferUnsupportedIfDepartmentMissing { get; private set; }
        public ConfigEntry<bool> ReferUnsupportedIfNoProfileDoctor { get; private set; }
        public ConfigEntry<bool> EquipmentReferralDebugLog { get; private set; }
        public ConfigEntry<bool> EnableExternalTransferAmbulanceTweaks { get; private set; }
        public ConfigEntry<bool> EnableParallelExternalTransferAmbulances { get; private set; }
        public ConfigEntry<bool> EnableExternalTransferQueueBroker { get; private set; }
        public ConfigEntry<bool> EnableExternalTransferParamedicSpeed { get; private set; }
        public ConfigEntry<float> ExternalTransferAmbulanceSpeedMultiplier { get; private set; }
        public ConfigEntry<float> ExternalTransferStuckWarningSeconds { get; private set; }
        public ConfigEntry<int> MaxParallelExternalTransferAmbulances { get; private set; }
        public ConfigEntry<bool> EnableIntakeControl { get; private set; }
        public ConfigEntry<bool> EnableDynamicIntakeByDoctors { get; private set; }
        public ConfigEntry<int> MaxClinicPatientsPerDay { get; private set; }
        public ConfigEntry<int> MaxAmbulancePatientsPerDay { get; private set; }
        public ConfigEntry<int> ClinicPatientsPerDoctorPerShift { get; private set; }
        public ConfigEntry<int> OutpatientPatientsPerDoctorPerDay { get; private set; }
        public ConfigEntry<int> AmbulancePatientsPerDoctorPerShift { get; private set; }
        public ConfigEntry<int> ReserveEmergencyCapacityPercent { get; private set; }
        public ConfigEntry<bool> IntakeDebugLog { get; private set; }
        public ConfigEntry<bool> EnableHospitalUpgrades { get; private set; }
        public ConfigEntry<bool> EnableMedicalCaseRewrite { get; private set; }
        public ConfigEntry<int> MaxDiagnosesPerPatient { get; private set; }
        public ConfigEntry<int> MultiDiagnosisChance { get; private set; }
        public ConfigEntry<bool> EnableHopelessCases { get; private set; }
        public ConfigEntry<int> HopelessCaseChance { get; private set; }
        public ConfigEntry<int> HopelessMinDiagnoses { get; private set; }
        public ConfigEntry<int> HopelessMaxDiagnoses { get; private set; }
        public ConfigEntry<bool> HopelessRequiresHospitalUpgrades { get; private set; }
        public ConfigEntry<bool> CaseRewriteDebugLog { get; private set; }
        public ConfigEntry<bool> EnableDeepTraceLog { get; private set; }
        public ConfigEntry<bool> DevCheapUpgrades { get; private set; }
        public ConfigEntry<bool> EnableAbsurdUpgrades { get; private set; }
        public ConfigEntry<bool> EnablePerformanceProfiler { get; private set; }
        public ConfigEntry<float> ProfilerSampleIntervalSeconds { get; private set; }
        public ConfigEntry<int> ProfilerTopN { get; private set; }
        public ConfigEntry<float> ProfilerSlowCallMs { get; private set; }
        public ConfigEntry<bool> ProfilerAutoResetAfterLog { get; private set; }
        public ConfigEntry<bool> EnableFramePacing { get; private set; }
        public ConfigEntry<bool> FramePacingUseMonitorRefreshRate { get; private set; }
        public ConfigEntry<int> FramePacingTargetFrameRate { get; private set; }
        public ConfigEntry<bool> FramePacingDisableVSync { get; private set; }
        public ConfigEntry<float> FramePacingMaximumDeltaTime { get; private set; }
        public ConfigEntry<bool> EnablePerformanceOptimizations { get; private set; }
        public ConfigEntry<float> SchedulingEngineIntervalSeconds { get; private set; }
        public ConfigEntry<float> SchedulingEngineMaxSnapshotAgeSeconds { get; private set; }
        public ConfigEntry<bool> SchedulingEngineDebugLog { get; private set; }
        public ConfigEntry<bool> EnableObjectSearchCache { get; private set; }
        public ConfigEntry<float> ObjectSearchCacheTtlSeconds { get; private set; }
        public ConfigEntry<bool> EnableDoctorSearchCache { get; private set; }
        public ConfigEntry<float> DoctorSearchCacheTtlSeconds { get; private set; }
        public ConfigEntry<bool> EnableSelectNextStepBackoff { get; private set; }
        public ConfigEntry<float> SelectNextStepBackoffSeconds { get; private set; }
        public ConfigEntry<float> SelectNextStepBackoffMaxSeconds { get; private set; }
        public ConfigEntry<bool> EnableReservationNegativeCache { get; private set; }
        public ConfigEntry<float> ReservationNegativeCacheTtlSeconds { get; private set; }
        public ConfigEntry<float> ReservationBrokerTtlSeconds { get; private set; }
        public ConfigEntry<bool> EnableNurseIdleBackoff { get; private set; }
        public ConfigEntry<float> NurseIdleBackoffSeconds { get; private set; }
        public ConfigEntry<float> NurseIdleBackoffMaxSeconds { get; private set; }
        public ConfigEntry<bool> EnableOutpatientQueueBackoff { get; private set; }
        public ConfigEntry<float> OutpatientQueueBackoffSeconds { get; private set; }
        public ConfigEntry<float> OutpatientQueueBackoffMaxSeconds { get; private set; }
        public ConfigEntry<float> RouteRequestThrottleSeconds { get; private set; }
        public ConfigFile SourceConfig { get; private set; }

        public static AutoLabBalancerConfig Bind(ConfigFile config)
        {
            return new AutoLabBalancerConfig
            {
                SourceConfig = config,
                Enabled = config.Bind("General", "Enabled", true, "Master switch for the mod."),
                PreventNegativeEmployeePerks = config.Bind("General", "PreventNegativeEmployeePerks", false, "When true, employee generation removes negative perks from hired/editor-created staff."),
                EnableDebugLog = config.Bind("General", "EnableDebugLog", false, "Write detailed queue and candidate logs."),
                SettingsWindowKey = config.Bind("General", "SettingsWindowKey", KeyCode.F8, "Key used to show the in-game mod settings window."),
                TickIntervalSeconds = config.Bind("General", "TickIntervalSeconds", 30f, "How often to run background mod maintenance."),
                EnablePostSurgeryCleanupPriority = config.Bind("ProductivityTweaks", "EnablePostSurgeryCleanupPriority", false, "After surgery, prioritize the operating room for janitor cleanup. Disabled by default because forcing janitor room selection can interfere with vanilla janitor shift state."),
                EnableNurseAssistedORCleanup = config.Bind("ProductivityTweaks", "EnableNurseAssistedORCleanup", false, "Allow free surgical nurses to help with limited operating room cleanup when no higher-priority nurse work is detected."),
                EnableFreeTimeSuppression = config.Bind("ProductivityTweaks", "EnableFreeTimeSuppression", false, "Legacy no-op. Free-time/needs are now handled as low-priority scheduling tasks."),
                EnableStuckReservationCleanup = config.Bind("ProductivityTweaks", "EnableStuckReservationCleanup", true, "Watchdog for stale employee and room reservations."),
                EnableFlexibleStretcherPickup = config.Bind("ProductivityTweaks", "EnableFlexibleStretcherPickup", true, "When vanilla cannot find a free department stretcher/wheelchair, search other departments for a free valid matching transport object."),
                EnableChainedHospitalizedExaminations = config.Bind("ProductivityTweaks", "EnableChainedHospitalizedExaminations", true, "Keep hospitalized patients near diagnostics when another examination is already planned instead of returning to bed immediately."),
                EnableTransportReservationTimeout = config.Bind("ProductivityTweaks", "EnableTransportReservationTimeout", true, "Retry stale procedure/transport reservations for chained hospitalized patients before sending them back to bed."),
                EnableNurseCheckDischarge = config.Bind("ProductivityTweaks", "EnableNurseCheckDischarge", true, "After a nurse checkup, discharge AI hospitalized patients that satisfy vanilla discharge checks except the daily release time window, and downgrade stable ICU patients through vanilla placement logic."),
                EnableEmergencyRunSpeedBoost = config.Bind("ProductivityTweaks", "EnableEmergencyRunSpeedBoost", true, "Boost staff speed only in detected critical/collapse contexts."),
                EnableCustomPerks = config.Bind("ProductivityTweaks", "EnableCustomPerks", true, "Add AutoLabBalancer custom patient/staff perks and their gameplay effects."),
                EnableUnknownEmployees = config.Bind("ProductivityTweaks", "EnableUnknownEmployees", false, "Harder hiring: employee skills and perks are hidden before hire; skills reveal after 3-7 days and perks reveal one by one every 5 days."),
                MaxPerksPerCharacter = config.Bind("ProductivityTweaks", "MaxPerksPerCharacter", 6, "Maximum total perks a generated patient or employee may have after custom perk fill."),
                EnableAggressiveMedicationPlanning = config.Bind("ProductivityTweaks", "EnableAggressiveMedicationPlanning", true, "After diagnosis, plan all available prescription/receipt treatments for known active symptoms."),
                MaxAutoMedicationsPerPlan = config.Bind("ProductivityTweaks", "MaxAutoMedicationsPerPlan", 4, "Maximum medication treatments the mod may add in one treatment-planning pass. 0 disables this per-pass limit."),
                MaxPlannedMedicationsPerPatient = config.Bind("ProductivityTweaks", "MaxPlannedMedicationsPerPatient", 8, "Maximum planned/active medication treatments allowed per patient before the mod stops adding more. 0 disables this patient-level limit."),
                EmergencyRunSpeedMultiplier = config.Bind("ProductivityTweaks", "EmergencyRunSpeedMultiplier", 2.0f, "Minimum multiplier applied to vanilla speed in emergency contexts."),
                StuckReservationTimeoutSeconds = config.Bind("ProductivityTweaks", "StuckReservationTimeoutSeconds", 120f, "How long a reservation must remain unchanged before the watchdog may clear it."),
                TransportReservationTimeoutSeconds = config.Bind("ProductivityTweaks", "TransportReservationTimeoutSeconds", 90f, "How long chained hospitalized patients may wait outside room before retrying procedure/transport reservation."),
                ORCleanupPriorityDurationSeconds = config.Bind("ProductivityTweaks", "ORCleanupPriorityDurationSeconds", 300f, "How long an operating room remains a high-priority cleanup target after surgery."),
                NurseORCleanupMaxDurationSeconds = config.Bind("ProductivityTweaks", "NurseORCleanupMaxDurationSeconds", 45f, "Maximum time a nurse-assisted cleanup attempt may own an operating room reservation."),
                EnableJanitorStandbyAfterCleaning = config.Bind("ProductivityTweaks", "EnableJanitorStandbyAfterCleaning", true, "Keep janitors on duty after returning a cart when their shift is still active, instead of letting vanilla send them home."),
                SuppressFreeTimeWhenDepartmentBusy = config.Bind("ProductivityTweaks", "SuppressFreeTimeWhenDepartmentBusy", false, "Legacy no-op. Free-time/needs are now handled by the central scheduler."),
                EnableDebugProductivityLog = config.Bind("ProductivityTweaks", "EnableDebugProductivityLog", false, "Write detailed Productivity Tweaks decisions."),
                EnableBottleneckOverlay = config.Bind("Overlay", "EnableBottleneckOverlay", true, "Show runtime bottleneck diagnostics in the F8 mod window."),
                EnableSurgeryAnalyticsLog = config.Bind("Overlay", "EnableSurgeryAnalyticsLog", true, "Periodically write surgery and transport bottleneck counters to the BepInEx log."),
                SurgeryAnalyticsLogIntervalSeconds = config.Bind("Overlay", "SurgeryAnalyticsLogIntervalSeconds", 30f, "How often to write surgery bottleneck analytics when enabled."),
                EnableSurgeryTooltipFix = config.Bind("Overlay", "EnableSurgeryTooltipFix", true, "Fix vanilla surgery staff tooltip so it shows two surgery nurses, matching actual procedure requirements."),
                EnableEquipmentReferral = config.Bind("Referral", "EnableEquipmentReferral", false, "When diagnosis is blocked by missing equipment or room, refer the patient to another hospital instead of counting them as untreated. Outpatients only; hospitalized patients are left to vanilla hospitalization flow."),
                EquipmentReferralPaymentPercent = config.Bind("Referral", "EquipmentReferralPaymentPercent", 20, "Percent of the patient's expected insurance payment paid for equipment-blocked referrals."),
                EnableManualReferralPayment = config.Bind("Referral", "EnableManualReferralPayment", true, "Pay a small partial fee when the player manually sends an untreated patient to another hospital."),
                ManualReferralPaymentPercent = config.Bind("Referral", "ManualReferralPaymentPercent", 10, "Percent of the patient's expected insurance payment paid for player-triggered untreated referrals."),
                EnableUnsupportedDiagnosisReferral = config.Bind("Referral", "EnableUnsupportedDiagnosisReferral", false, "After diagnosis, refer unsupported outpatient diagnoses to another hospital if the profile department or doctor is unavailable."),
                UnsupportedDiagnosisReferralPaymentPercent = config.Bind("Referral", "UnsupportedDiagnosisReferralPaymentPercent", 10, "Percent of the patient's expected insurance payment paid for unsupported-diagnosis referrals."),
                ReferUnsupportedIfDepartmentMissing = config.Bind("Referral", "ReferUnsupportedIfDepartmentMissing", true, "Refer diagnosed outpatients when the diagnosis profile department is not active or has no working clinic."),
                ReferUnsupportedIfNoProfileDoctor = config.Bind("Referral", "ReferUnsupportedIfNoProfileDoctor", true, "Refer diagnosed outpatients when no currently available doctor is found in the diagnosis profile department."),
                EquipmentReferralDebugLog = config.Bind("Referral", "EquipmentReferralDebugLog", false, "Write detailed equipment referral decisions."),
                EnableExternalTransferAmbulanceTweaks = config.Bind("Referral", "EnableExternalTransferAmbulanceTweaks", false, "Speed up external ambulances/paramedics that transport sent-away patients to another hospital. Disabled by default because vanilla external ambulance flow is fragile."),
                EnableParallelExternalTransferAmbulances = config.Bind("Referral", "EnableParallelExternalTransferAmbulances", true, "Allow multiple sent-away transfer jobs to run in parallel by spawning normal visible external ambulance jobs. The ambulance state-machine timeStep is not accelerated."),
                EnableExternalTransferQueueBroker = config.Bind("Referral", "EnableExternalTransferQueueBroker", true, "Build a safe read-only broker snapshot for external transfer ambulance queue diagnostics without touching the ambulance state machine."),
                EnableExternalTransferParamedicSpeed = config.Bind("Referral", "EnableExternalTransferParamedicSpeed", true, "Speed up only the external-transfer paramedic movement/animation. The ambulance state machine timeStep is never accelerated."),
                ExternalTransferAmbulanceSpeedMultiplier = config.Bind("Referral", "ExternalTransferAmbulanceSpeedMultiplier", 10.0f, "Movement/animation multiplier for external-transfer paramedics only. External ambulance state-machine timeStep is never accelerated."),
                ExternalTransferStuckWarningSeconds = config.Bind("Referral", "ExternalTransferStuckWarningSeconds", 120f, "How long an active external transfer ambulance state may run before F8 marks it as potentially stuck."),
                MaxParallelExternalTransferAmbulances = config.Bind("Referral", "MaxParallelExternalTransferAmbulances", 6, "Maximum number of parallel external transfer ambulance jobs including the visible primary ambulance."),
                EnableIntakeControl = config.Bind("IntakeControl", "EnableIntakeControl", false, "When true, cap today's insurance patient intake after vanilla insurance calculation. Disabled by default."),
                EnableDynamicIntakeByDoctors = config.Bind("IntakeControl", "EnableDynamicIntakeByDoctors", true, "Calculate intake capacity from available outpatient doctors."),
                MaxClinicPatientsPerDay = config.Bind("IntakeControl", "MaxClinicPatientsPerDay", 0, "Hard cap for clinic/mobile patients per day. 0 disables this hard cap."),
                MaxAmbulancePatientsPerDay = config.Bind("IntakeControl", "MaxAmbulancePatientsPerDay", 0, "Hard cap for ambulance/immobile patients per day. 0 disables this hard cap."),
                ClinicPatientsPerDoctorPerShift = config.Bind("IntakeControl", "ClinicPatientsPerDoctorPerShift", 10, "Legacy no-op. Use OutpatientPatientsPerDoctorPerDay."),
                OutpatientPatientsPerDoctorPerDay = config.Bind("IntakeControl", "OutpatientPatientsPerDoctorPerDay", 20, "Dynamic clinic/mobile patient capacity per outpatient doctor per day."),
                AmbulancePatientsPerDoctorPerShift = config.Bind("IntakeControl", "AmbulancePatientsPerDoctorPerShift", 3, "Dynamic ambulance/immobile patient capacity per outpatient doctor."),
                ReserveEmergencyCapacityPercent = config.Bind("IntakeControl", "ReserveEmergencyCapacityPercent", 15, "Percent of dynamic capacity reserved for emergency headroom."),
                IntakeDebugLog = config.Bind("IntakeControl", "IntakeDebugLog", false, "Write detailed intake-control decisions."),
                EnableHospitalUpgrades = config.Bind("HospitalUpgrades", "EnableHospitalUpgrades", true, "Master switch for hospital upgrade effects and purchases. Existing purchased levels are kept but ignored while disabled."),
                EnableMedicalCaseRewrite = config.Bind("MedicalCaseRewrite", "EnableMedicalCaseRewrite", true, "Case-wide multi-problem rewrite. This layer owns routing, disposition, referral, and vanilla-visible planned examinations/treatments for rewrite-owned patients."),
                MaxDiagnosesPerPatient = config.Bind("MedicalCaseRewrite", "MaxDiagnosesPerPatient", 15, "Maximum simultaneously instantiated case-problem tracks kept for one patient case."),
                MultiDiagnosisChance = config.Bind("MedicalCaseRewrite", "MultiDiagnosisChance", 35, "Percent chance that a generated patient receives additional diagnoses."),
                EnableHopelessCases = config.Bind("MedicalCaseRewrite", "EnableHopelessCases", true, "Allow rare very complex patient cases with denser interaction chains."),
                HopelessCaseChance = config.Bind("MedicalCaseRewrite", "HopelessCaseChance", 2, "Percent chance for a hopeless case when medical case rewrite is enabled."),
                HopelessMinDiagnoses = config.Bind("MedicalCaseRewrite", "HopelessMinDiagnoses", 4, "Minimum diagnoses for hopeless cases."),
                HopelessMaxDiagnoses = config.Bind("MedicalCaseRewrite", "HopelessMaxDiagnoses", 15, "Maximum diagnoses for hopeless cases."),
                HopelessRequiresHospitalUpgrades = config.Bind("MedicalCaseRewrite", "HopelessRequiresHospitalUpgrades", true, "Only generate hopeless cases after enough hospital upgrades are purchased."),
                CaseRewriteDebugLog = config.Bind("MedicalCaseRewrite", "CaseRewriteDebugLog", false, "Write detailed medical case rewrite diagnostics."),
                EnableDeepTraceLog = config.Bind("Developer", "EnableDeepTraceLog", false, "Write deep per-entity trace logs to a separate file for debugging patient/doctor/lab flows."),
                DevCheapUpgrades = config.Bind("Developer", "DevCheapUpgrades", false, "Developer helper: reduce hospital upgrade prices so the most expensive next level costs 100000."),
                EnableAbsurdUpgrades = config.Bind("Developer", "EnableAbsurdUpgrades", false, "Enable absurd hospital upgrade tier: expensive, intentionally overpowered final effects."),
                EnablePerformanceProfiler = config.Bind("Performance", "EnablePerformanceProfiler", false, "Enable internal performance profiler. Default off because Harmony timing has overhead."),
                ProfilerSampleIntervalSeconds = config.Bind("Performance", "ProfilerSampleIntervalSeconds", 10f, "How often to log profiler samples."),
                ProfilerTopN = config.Bind("Performance", "ProfilerTopN", 20, "How many profiler rows to show/log."),
                ProfilerSlowCallMs = config.Bind("Performance", "ProfilerSlowCallMs", 5f, "Calls at or above this duration are counted as slow calls."),
                ProfilerAutoResetAfterLog = config.Bind("Performance", "ProfilerAutoResetAfterLog", false, "When true, reset profiler samples after each periodic log. Leave false for long manual profiling sessions."),
                EnableFramePacing = config.Bind("Performance", "EnableFramePacing", true, "Apply stable frame pacing settings: target FPS, vSync mode, and maximumDeltaTime clamp."),
                FramePacingUseMonitorRefreshRate = config.Bind("Performance", "FramePacingUseMonitorRefreshRate", true, "Use the current monitor refresh rate as target FPS when frame pacing is enabled. Falls back to FramePacingTargetFrameRate if Unity reports 0."),
                FramePacingTargetFrameRate = config.Bind("Performance", "FramePacingTargetFrameRate", 60, "Manual target render FPS when monitor-refresh mode is disabled or unavailable."),
                FramePacingDisableVSync = config.Bind("Performance", "FramePacingDisableVSync", true, "Disable Unity vSync when frame pacing is enabled so Application.targetFrameRate can take effect."),
                FramePacingMaximumDeltaTime = config.Bind("Performance", "FramePacingMaximumDeltaTime", 0.05f, "Clamp Unity Time.maximumDeltaTime to reduce post-stutter catch-up spikes. Vanilla is often larger."),
                EnablePerformanceOptimizations = config.Bind("PerformanceOptimizations", "EnablePerformanceOptimizations", true, "Enable experimental runtime performance optimizations based on short-lived caches and backoff."),
                SchedulingEngineIntervalSeconds = config.Bind("PerformanceOptimizations", "SchedulingEngineIntervalSeconds", 0.5f, "How often to rebuild the central scheduling index."),
                SchedulingEngineMaxSnapshotAgeSeconds = config.Bind("PerformanceOptimizations", "SchedulingEngineMaxSnapshotAgeSeconds", 1.5f, "Maximum scheduling snapshot age before optimizations ignore it."),
                SchedulingEngineDebugLog = config.Bind("PerformanceOptimizations", "SchedulingEngineDebugLog", false, "Write central scheduling index rebuild summaries to the BepInEx log."),
                EnableObjectSearchCache = config.Bind("PerformanceOptimizations", "EnableObjectSearchCache", true, "Cache successful free-object searches for a short time."),
                ObjectSearchCacheTtlSeconds = config.Bind("PerformanceOptimizations", "ObjectSearchCacheTtlSeconds", 0.35f, "TTL for successful object-search cache entries."),
                EnableDoctorSearchCache = config.Bind("PerformanceOptimizations", "EnableDoctorSearchCache", true, "Cache successful doctor/lab-specialist qualification searches for a short time."),
                DoctorSearchCacheTtlSeconds = config.Bind("PerformanceOptimizations", "DoctorSearchCacheTtlSeconds", 0.35f, "TTL for successful doctor/lab-specialist search cache entries."),
                EnableSelectNextStepBackoff = config.Bind("PerformanceOptimizations", "EnableSelectNextStepBackoff", true, "Back off repeated hospitalized SelectNextStep calls when the previous attempt did not start new work."),
                SelectNextStepBackoffSeconds = config.Bind("PerformanceOptimizations", "SelectNextStepBackoffSeconds", 0.35f, "Initial backoff duration for hospitalized SelectNextStep misses."),
                SelectNextStepBackoffMaxSeconds = config.Bind("PerformanceOptimizations", "SelectNextStepBackoffMaxSeconds", 2.0f, "Maximum adaptive backoff duration for repeated hospitalized SelectNextStep misses."),
                EnableReservationNegativeCache = config.Bind("PerformanceOptimizations", "EnableReservationNegativeCache", false, "Legacy no-op. Reservation failures now go through the reservation broker."),
                ReservationNegativeCacheTtlSeconds = config.Bind("PerformanceOptimizations", "ReservationNegativeCacheTtlSeconds", 0.35f, "Legacy no-op TTL kept only for old configs."),
                ReservationBrokerTtlSeconds = config.Bind("PerformanceOptimizations", "ReservationBrokerTtlSeconds", 0.35f, "TTL for reservation broker failed reservation entries."),
                EnableNurseIdleBackoff = config.Bind("PerformanceOptimizations", "EnableNurseIdleBackoff", true, "Throttle repeated nurse idle scans when a nurse remains free and unreserved."),
                NurseIdleBackoffSeconds = config.Bind("PerformanceOptimizations", "NurseIdleBackoffSeconds", 0.30f, "Initial backoff duration for repeated nurse idle scans."),
                NurseIdleBackoffMaxSeconds = config.Bind("PerformanceOptimizations", "NurseIdleBackoffMaxSeconds", 2.5f, "Maximum adaptive backoff duration for repeated nurse idle scans."),
                EnableOutpatientQueueBackoff = config.Bind("PerformanceOptimizations", "EnableOutpatientQueueBackoff", true, "Throttle repeated outpatient waiting-room scans."),
                OutpatientQueueBackoffSeconds = config.Bind("PerformanceOptimizations", "OutpatientQueueBackoffSeconds", 0.30f, "Initial backoff duration for repeated outpatient waiting-room scans."),
                OutpatientQueueBackoffMaxSeconds = config.Bind("PerformanceOptimizations", "OutpatientQueueBackoffMaxSeconds", 2.0f, "Maximum adaptive backoff duration for repeated outpatient waiting-room scans."),
                RouteRequestThrottleSeconds = config.Bind("PerformanceOptimizations", "RouteRequestThrottleSeconds", 0.25f, "Short cooldown for duplicate route requests to the same destination.")
            };
        }
    }

    internal static class RuntimeSettings
    {
        public static AutoLabBalancerConfig Config;
        public static ManualLogSource Logger;
        public static SaveScopedSettings SaveSettings;

        public static bool BlockNegativePerks
        {
            get { return Config != null && Config.Enabled.Value && Config.PreventNegativeEmployeePerks.Value; }
        }

        public static bool ProductivityDebug
        {
            get { return Config != null && Config.Enabled.Value && Config.EnableDebugProductivityLog.Value; }
        }
    }

    internal static class RuntimeCounters
    {
        public static int MedicationsAutoPlanned;
        public static int ORCleanupPrioritiesCreated;
        public static int NurseCleanupJobsStarted;
        public static int NurseORTilesCleaned;
        public static int StuckReservationsCleared;
        public static int FlexibleTransportFallbacks;
        public static int TransportReservationsRetried;
        public static int EmergencySpeedBoosts;
        public static int EquipmentReferrals;
        public static int EquipmentReferralIncome;
        public static int UnsupportedDiagnosisReferrals;
        public static int UnsupportedDiagnosisReferralIncome;
        public static int ManualReferralPayments;
        public static int ManualReferralIncome;
    }

    [HarmonyPatch]
    internal static class SurgeryStaffTooltipFixPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("StringTable");
            return type == null ? null : AccessTools.Method(type, "GetLocalizedText", new[] { typeof(string), typeof(string[]) });
        }

        private static void Postfix(string stringID, ref string __result)
        {
            if (RuntimeSettings.Config == null
                || !RuntimeSettings.Config.Enabled.Value
                || !RuntimeSettings.Config.EnableSurgeryTooltipFix.Value
                || !string.Equals(stringID, "TOOLTIP_SURGERY_STAFF_DETAILS", StringComparison.Ordinal))
            {
                return;
            }

            __result = ModText.T("SurgeryTooltipFixed");
        }
    }

    [HarmonyPatch]
    internal static class PerkSetCreateAllowedEmployeePerkSetPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.PerkSet");
            return type == null ? null : AccessTools.Method(type, "CreateAllowedEmployeePerkSet");
        }

        private static void Postfix(object __instance)
        {
            NegativePerkFilter.RemoveNegativePerks(__instance, "generated employee perk set");
        }
    }

    [HarmonyPatch]
    internal static class PerkComponentConstructorPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName("Lopital.PerkComponent");
            if (type == null)
            {
                yield break;
            }

            foreach (var constructor in AccessTools.GetDeclaredConstructors(type))
            {
                yield return constructor;
            }
        }

        private static void Postfix(object __instance, MethodBase __originalMethod)
        {
            var entityField = AccessTools.Field(__instance.GetType(), "m_entity");
            var entity = entityField == null ? null : entityField.GetValue(__instance);
            if (entity == null)
            {
                return;
            }

            var perkSet = AccessTools.Field(__instance.GetType(), "m_perkSet").GetValue(__instance);
            CustomPerkService.FillPerks(perkSet, entity);

            if (ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.EmployeeComponent") == null)
            {
                return;
            }

            NegativePerkFilter.RemoveNegativePerks(perkSet, "perk component");
            UnknownEmployeeService.HideGeneratedEmployeePerks(perkSet, entity, __originalMethod != null && __originalMethod.GetParameters().Length == 1);
        }
    }

    [HarmonyPatch]
    internal static class CharacterEditorFillCharacterDataPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("CharacterEditorController");
            return type == null ? null : AccessTools.Method(type, "FillCharacterData");
        }

        private static void Postfix(object __instance)
        {
            if (!RuntimeSettings.BlockNegativePerks)
            {
                return;
            }

            var character = AccessTools.Field(__instance.GetType(), "m_character").GetValue(__instance);
            if (character == null)
            {
                return;
            }

            var perkComponent = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.PerkComponent");
            if (perkComponent == null)
            {
                return;
            }

            var perkSet = AccessTools.Field(perkComponent.GetType(), "m_perkSet").GetValue(perkComponent);
            NegativePerkFilter.RemoveNegativePerks(perkSet, "character editor");
        }
    }

    internal static class NegativePerkFilter
    {
        public static void RemoveNegativePerks(object perkSet, string source)
        {
            if (!RuntimeSettings.BlockNegativePerks || perkSet == null)
            {
                return;
            }

            var perksField = AccessTools.Field(perkSet.GetType(), "m_perks");
            var perks = perksField == null ? null : perksField.GetValue(perkSet) as IList;
            if (perks == null || perks.Count == 0)
            {
                return;
            }

            var removed = 0;
            for (var i = perks.Count - 1; i >= 0; i--)
            {
                var perk = perks[i];
                if (IsNegativePerk(perk))
                {
                    perks.RemoveAt(i);
                    removed++;
                }
            }

            // Keep this silent in normal play. Hiring screens can generate many candidates, and logging
            // every filtered perk can flood Unity/BepInEx logs badly enough to look like a freeze.
        }

        private static bool IsNegativePerk(object perk)
        {
            if (perk == null)
            {
                return false;
            }

            var perkPointer = AccessTools.Field(perk.GetType(), "m_perk").GetValue(perk);
            var gameDbPerk = ReflectionHelpers.ResolvePointer(perkPointer);
            if (gameDbPerk == null)
            {
                return false;
            }

            var property = gameDbPerk.GetType().GetProperty("PerkType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var perkType = property == null ? null : property.GetValue(gameDbPerk, null);
            return perkType != null && string.Equals(perkType.ToString(), "NEGATIVE", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class ReflectionHelpers
    {
        private static readonly Dictionary<string, FieldInfo> FieldCache = new Dictionary<string, FieldInfo>();
        private static readonly HashSet<string> MissingFieldCache = new HashSet<string>();
        private static readonly Dictionary<string, MethodInfo> MethodCache = new Dictionary<string, MethodInfo>();
        private static readonly HashSet<string> MissingMethodCache = new HashSet<string>();
        private static readonly Dictionary<string, PropertyInfo> PropertyCache = new Dictionary<string, PropertyInfo>();
        private static readonly HashSet<string> MissingPropertyCache = new HashSet<string>();

        public static object GetField(object instance, string fieldName)
        {
            if (instance == null)
            {
                return null;
            }

            var field = FindField(instance.GetType(), fieldName);
            return field == null ? null : field.GetValue(instance);
        }

        private static FieldInfo FindField(Type type, string fieldName)
        {
            if (type == null || string.IsNullOrEmpty(fieldName))
            {
                return null;
            }

            var key = type.FullName + "::" + fieldName;
            if (MissingFieldCache.Contains(key))
            {
                return null;
            }

            FieldInfo cached;
            if (FieldCache.TryGetValue(key, out cached))
            {
                return cached;
            }

            var current = type;
            while (current != null)
            {
                var field = current.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    FieldCache[key] = field;
                    return field;
                }

                current = current.BaseType;
            }

            MissingFieldCache.Add(key);
            return null;
        }

        private static MethodInfo FindMethod(Type type, string methodName, Type[] parameterTypes)
        {
            if (type == null || string.IsNullOrEmpty(methodName))
            {
                return null;
            }

            var signature = parameterTypes == null || parameterTypes.Length == 0 ? "()" : "(" + parameterTypes.Length + ")";
            var key = type.FullName + "::" + methodName + signature;
            if (MissingMethodCache.Contains(key))
            {
                return null;
            }

            MethodInfo cached;
            if (MethodCache.TryGetValue(key, out cached))
            {
                return cached;
            }

            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, parameterTypes ?? Type.EmptyTypes, null);
            if (method != null)
            {
                MethodCache[key] = method;
                return method;
            }

            MissingMethodCache.Add(key);
            PerformanceOptimizationService.RecordMissingTarget();
            return null;
        }

        private static PropertyInfo FindProperty(Type type, string propertyName)
        {
            if (type == null || string.IsNullOrEmpty(propertyName))
            {
                return null;
            }

            var key = type.FullName + "::" + propertyName;
            if (MissingPropertyCache.Contains(key))
            {
                return null;
            }

            PropertyInfo cached;
            if (PropertyCache.TryGetValue(key, out cached))
            {
                return cached;
            }

            var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null)
            {
                PropertyCache[key] = property;
                return property;
            }

            MissingPropertyCache.Add(key);
            PerformanceOptimizationService.RecordMissingTarget();
            return null;
        }

        public static IEnumerable<object> GetEnumerableField(object instance, string fieldName)
        {
            var value = GetField(instance, fieldName) as IEnumerable;
            if (value == null)
            {
                yield break;
            }

            foreach (var item in value)
            {
                if (item != null)
                {
                    yield return item;
                }
            }
        }

        public static object ResolvePointer(object pointer)
        {
            if (pointer == null)
            {
                return null;
            }

            foreach (var name in new[] { "GetEntity", "Entry", "DEBUG_Entity", "Value" })
            {
                var method = FindMethod(pointer.GetType(), name, Type.EmptyTypes);
                if (method != null)
                {
                    try
                    {
                        return method.Invoke(pointer, null);
                    }
                    catch
                    {
                    }
                }

                var property = FindProperty(pointer.GetType(), name);
                if (property != null)
                {
                    try
                    {
                        return property.GetValue(pointer, null);
                    }
                    catch
                    {
                    }
                }
            }

            return null;
        }

        public static bool InvokeBool(object instance, string methodName)
        {
            if (instance == null)
            {
                return false;
            }

            var method = FindMethod(instance.GetType(), methodName, Type.EmptyTypes);
            return method != null && Equals(method.Invoke(instance, null), true);
        }

        public static string GetStringProperty(object instance, string propertyName)
        {
            if (instance == null)
            {
                return null;
            }

            var property = FindProperty(instance.GetType(), propertyName);
            return property == null ? null : property.GetValue(instance, null) as string;
        }

        public static object GetComponentByTypeName(object entity, string typeName)
        {
            if (entity == null || string.IsNullOrEmpty(typeName))
            {
                return null;
            }

            var field = FindField(entity.GetType(), "m_components");
            var components = field == null ? null : field.GetValue(entity) as IEnumerable;
            if (components == null)
            {
                return null;
            }

            foreach (var component in components)
            {
                if (component != null && component.GetType().FullName == typeName)
                {
                    return component;
                }
            }

            return null;
        }
    }

    internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

        public new bool Equals(object x, object y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(object obj)
        {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}

