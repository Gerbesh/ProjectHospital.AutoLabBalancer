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
        public const string PluginVersion = "0.12.1";

        private AutoLabBalancerConfig _config;
        private Harmony _harmony;
        private float _nextTickAt;
        private float _nextOverlaySnapshotAt;
        private float _nextSurgeryAnalyticsAt;
        private BottleneckSnapshot _overlaySnapshot;
        private bool _showSettings;
        private Rect _settingsWindow = new Rect(30f, 80f, 720f, 520f);
        private int _settingsPage;

        private void Awake()
        {
            Logger.LogInfo(ModText.T("PluginName") + ModText.T("AwakeStarted"));
            _config = AutoLabBalancerConfig.Bind(Config);
            _nextTickAt = 0f;
            RuntimeSettings.Config = _config;
            RuntimeSettings.Logger = Logger;

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

            PerformanceProfiler.Tick(Time.realtimeSinceStartup);

            if (Time.realtimeSinceStartup < _nextTickAt)
            {
                return;
            }

            _nextTickAt = Time.realtimeSinceStartup + Mathf.Max(5f, _config.TickIntervalSeconds.Value);

            try
            {
                ProductivityTweaksService.Tick(Time.realtimeSinceStartup);
                IntakeControlService.ApplyDailyCap();
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
            if (!_showSettings)
            {
                return;
            }

            _settingsWindow = GUILayout.Window(871234, _settingsWindow, DrawSettingsWindow, ModText.T("WindowTitle"));
        }

        private void DrawSettingsWindow(int windowId)
        {
            GUILayout.Label(ModText.T("SettingsSaved"));
            GUILayout.BeginHorizontal();
            DrawPageButton(0, ModText.T("PageSettings"));
            DrawPageButton(1, ModText.T("PageCounters"));
            DrawPageButton(2, ModText.T("PageBottlenecks"));
            DrawPageButton(3, ModText.T("PageSurgery"));
            DrawPageButton(4, ModText.T("PagePerformance"));
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);
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

            GUILayout.Space(8f);
            if (GUILayout.Button(ModText.T("Close")))
            {
                _showSettings = false;
            }

            GUI.DragWindow();
        }

        private void DrawPageButton(int page, string label)
        {
            var text = _settingsPage == page ? "[" + label + "]" : label;
            if (GUILayout.Button(text))
            {
                _settingsPage = page;
            }
        }

        private void DrawSettingsPage()
        {
            var blockNegativePerks = GUILayout.Toggle(_config.PreventNegativeEmployeePerks.Value, ModText.T("BlockNegativePerks"));
            if (blockNegativePerks != _config.PreventNegativeEmployeePerks.Value)
            {
                _config.PreventNegativeEmployeePerks.Value = blockNegativePerks;
                Config.Save();
            }

            var debugLog = GUILayout.Toggle(_config.EnableDebugLog.Value, ModText.T("DebugLog"));
            if (debugLog != _config.EnableDebugLog.Value)
            {
                _config.EnableDebugLog.Value = debugLog;
                Config.Save();
            }

            GUILayout.Space(8f);
            GUILayout.Label(ModText.T("ProductivityTweaks"));
            DrawToggle(_config.EnableAggressiveMedicationPlanning, ModText.T("PlanMedication"));
            DrawToggle(_config.EnableFreeTimeSuppression, ModText.T("SuppressFreeTime"));
            DrawToggle(_config.EnablePostSurgeryCleanupPriority, ModText.T("PrioritizeOrCleanup"));
            DrawToggle(_config.EnableStuckReservationCleanup, ModText.T("CleanStuckReservations"));
            DrawToggle(_config.EnableFlexibleStretcherPickup, ModText.T("FlexibleStretcherPickup"));
            DrawToggle(_config.EnableChainedHospitalizedExaminations, ModText.T("ChainDiagnostics"));
            DrawToggle(_config.EnableTransportReservationTimeout, ModText.T("RetryTransportReservations"));
            DrawToggle(_config.EnableNurseCheckDischarge, ModText.T("NurseCheckDischarge"));
            DrawToggle(_config.EnableEmergencyRunSpeedBoost, ModText.T("EmergencyRunSpeedBoost"));
            DrawToggle(_config.EnableNurseAssistedORCleanup, ModText.T("NurseAssistedOrCleanup"));
            DrawToggle(_config.EnableEquipmentReferral, ModText.T("EquipmentReferral"));
            DrawToggle(_config.EnableUnsupportedDiagnosisReferral, ModText.T("UnsupportedDiagnosisReferral"));
            DrawToggle(_config.EnableManualReferralPayment, ModText.T("ManualReferralPayment"));
            DrawToggle(_config.EnableDebugProductivityLog, ModText.T("ProductivityDebugLog"));
            DrawToggle(_config.EnableBottleneckOverlay, ModText.T("ShowBottleneckOverlay"));
            DrawToggle(_config.EnableSurgeryAnalyticsLog, ModText.T("SurgeryAnalyticsLog"));
            DrawToggle(_config.EnableSurgeryTooltipFix, ModText.T("FixSurgeryTooltip"));
            GUILayout.Space(8f);
            GUILayout.Label(ModText.T("PerformanceProfiler"));
            DrawToggle(_config.EnablePerformanceProfiler, ModText.T("EnablePerformanceProfiler"));
            GUILayout.Space(8f);
            GUILayout.Label(ModText.T("IntakeControl"));
            DrawToggle(_config.EnableIntakeControl, ModText.T("EnableIntakeControl"));
            DrawToggle(_config.EnableDynamicIntakeByDoctors, ModText.T("EnableDynamicIntakeByDoctors"));
            GUILayout.Space(8f);
            GUILayout.Label(ModText.T("DeveloperTools"));
            DrawToggle(_config.DevCheapUpgrades, ModText.T("DevCheapUpgrades"));
            DrawToggle(_config.EnableAbsurdUpgrades, ModText.T("EnableAbsurdUpgrades"));
        }

        private void DrawCountersPage()
        {
            GUILayout.Label(ModText.T("PageCounters"));
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();
            GUILayout.Label(ModText.T("MedicationAutoAdded") + RuntimeCounters.MedicationsAutoPlanned);
            GUILayout.Label(ModText.T("FreeTimeSuppressed") + RuntimeCounters.FreeTimeSuppressed);
            GUILayout.Label(ModText.T("OrCleanupPriorities") + RuntimeCounters.ORCleanupPrioritiesCreated);
            GUILayout.Label(ModText.T("NurseOrTilesCleaned") + RuntimeCounters.NurseORTilesCleaned);
            GUILayout.EndVertical();
            GUILayout.BeginVertical();
            GUILayout.Label(ModText.T("StuckReservationsCleared") + RuntimeCounters.StuckReservationsCleared);
            GUILayout.Label(ModText.T("FlexibleTransportFallbacks") + RuntimeCounters.FlexibleTransportFallbacks);
            GUILayout.Label(ModText.T("TransportReservationsRetried") + RuntimeCounters.TransportReservationsRetried);
            GUILayout.Label(ModText.T("EmergencySpeedBoosts") + RuntimeCounters.EmergencySpeedBoosts);
            GUILayout.Label(ModText.T("EquipmentReferrals") + RuntimeCounters.EquipmentReferrals);
            GUILayout.Label(ModText.T("ReferralIncome") + RuntimeCounters.EquipmentReferralIncome);
            GUILayout.Label(ModText.T("UnsupportedDiagnosisReferrals") + RuntimeCounters.UnsupportedDiagnosisReferrals);
            GUILayout.Label(ModText.T("UnsupportedDiagnosisIncome") + RuntimeCounters.UnsupportedDiagnosisReferralIncome);
            GUILayout.Label(ModText.T("ManualReferralPayments") + RuntimeCounters.ManualReferralPayments);
            GUILayout.Label(ModText.T("ManualReferralIncome") + RuntimeCounters.ManualReferralIncome);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        private void DrawBottlenecksPage(bool surgeryOnly)
        {
            if (_config.EnableBottleneckOverlay.Value)
            {
                DrawBottleneckOverlay(surgeryOnly);
            }
            else
            {
                GUILayout.Label(ModText.T("OverlayDisabled"));
            }
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
            GUILayout.Label(ModText.T("PagePerformance"));
            if (!_config.EnablePerformanceProfiler.Value)
            {
                GUILayout.Label(ModText.T("PerformanceProfilerDisabled"));
                return;
            }

            var samples = PerformanceProfiler.GetTopSamples(_config.ProfilerTopN.Value);
            if (samples.Count == 0)
            {
                GUILayout.Label(ModText.T("PerformanceProfilerNoSamples"));
                return;
            }

            foreach (var sample in samples)
            {
                GUILayout.Label(PerformanceProfiler.FormatSample(sample));
            }

            if (GUILayout.Button(ModText.T("PerformanceProfilerReset")))
            {
                PerformanceProfiler.Reset();
            }
        }

        private void DrawToggle(ConfigEntry<bool> entry, string label)
        {
            var value = GUILayout.Toggle(entry.Value, label);
            if (value != entry.Value)
            {
                entry.Value = value;
                Config.Save();
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
        public ConfigEntry<int> MaxAutoMedicationsPerPlan { get; private set; }
        public ConfigEntry<int> MaxPlannedMedicationsPerPatient { get; private set; }
        public ConfigEntry<float> EmergencyRunSpeedMultiplier { get; private set; }
        public ConfigEntry<float> StuckReservationTimeoutSeconds { get; private set; }
        public ConfigEntry<float> TransportReservationTimeoutSeconds { get; private set; }
        public ConfigEntry<float> ORCleanupPriorityDurationSeconds { get; private set; }
        public ConfigEntry<float> NurseORCleanupMaxDurationSeconds { get; private set; }
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
        public ConfigEntry<bool> EnableIntakeControl { get; private set; }
        public ConfigEntry<bool> EnableDynamicIntakeByDoctors { get; private set; }
        public ConfigEntry<int> MaxClinicPatientsPerDay { get; private set; }
        public ConfigEntry<int> MaxAmbulancePatientsPerDay { get; private set; }
        public ConfigEntry<int> ClinicPatientsPerDoctorPerShift { get; private set; }
        public ConfigEntry<int> AmbulancePatientsPerDoctorPerShift { get; private set; }
        public ConfigEntry<int> ReserveEmergencyCapacityPercent { get; private set; }
        public ConfigEntry<bool> IntakeDebugLog { get; private set; }
        public ConfigEntry<bool> DevCheapUpgrades { get; private set; }
        public ConfigEntry<bool> EnableAbsurdUpgrades { get; private set; }
        public ConfigEntry<bool> EnablePerformanceProfiler { get; private set; }
        public ConfigEntry<float> ProfilerSampleIntervalSeconds { get; private set; }
        public ConfigEntry<int> ProfilerTopN { get; private set; }
        public ConfigEntry<float> ProfilerSlowCallMs { get; private set; }
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
                EnablePostSurgeryCleanupPriority = config.Bind("ProductivityTweaks", "EnablePostSurgeryCleanupPriority", true, "After surgery, prioritize the operating room for janitor cleanup."),
                EnableNurseAssistedORCleanup = config.Bind("ProductivityTweaks", "EnableNurseAssistedORCleanup", false, "Allow free surgical nurses to help with limited operating room cleanup when no higher-priority nurse work is detected."),
                EnableFreeTimeSuppression = config.Bind("ProductivityTweaks", "EnableFreeTimeSuppression", true, "Prevent doctor/nurse free-time procedures while the department is visibly busy."),
                EnableStuckReservationCleanup = config.Bind("ProductivityTweaks", "EnableStuckReservationCleanup", true, "Watchdog for stale employee and room reservations."),
                EnableFlexibleStretcherPickup = config.Bind("ProductivityTweaks", "EnableFlexibleStretcherPickup", true, "When vanilla cannot find a free department stretcher/wheelchair, search other departments for a free valid matching transport object."),
                EnableChainedHospitalizedExaminations = config.Bind("ProductivityTweaks", "EnableChainedHospitalizedExaminations", true, "Keep hospitalized patients near diagnostics when another examination is already planned instead of returning to bed immediately."),
                EnableTransportReservationTimeout = config.Bind("ProductivityTweaks", "EnableTransportReservationTimeout", true, "Retry stale procedure/transport reservations for chained hospitalized patients before sending them back to bed."),
                EnableNurseCheckDischarge = config.Bind("ProductivityTweaks", "EnableNurseCheckDischarge", true, "After a nurse checkup, discharge AI hospitalized patients that satisfy vanilla discharge checks except the daily release time window. ICU patients are left to vanilla placement logic."),
                EnableEmergencyRunSpeedBoost = config.Bind("ProductivityTweaks", "EnableEmergencyRunSpeedBoost", true, "Boost staff speed only in detected critical/collapse contexts."),
                EnableAggressiveMedicationPlanning = config.Bind("ProductivityTweaks", "EnableAggressiveMedicationPlanning", true, "After diagnosis, plan all available prescription/receipt treatments for known active symptoms."),
                MaxAutoMedicationsPerPlan = config.Bind("ProductivityTweaks", "MaxAutoMedicationsPerPlan", 4, "Maximum medication treatments the mod may add in one treatment-planning pass. 0 disables this per-pass limit."),
                MaxPlannedMedicationsPerPatient = config.Bind("ProductivityTweaks", "MaxPlannedMedicationsPerPatient", 8, "Maximum planned/active medication treatments allowed per patient before the mod stops adding more. 0 disables this patient-level limit."),
                EmergencyRunSpeedMultiplier = config.Bind("ProductivityTweaks", "EmergencyRunSpeedMultiplier", 2.0f, "Minimum multiplier applied to vanilla speed in emergency contexts."),
                StuckReservationTimeoutSeconds = config.Bind("ProductivityTweaks", "StuckReservationTimeoutSeconds", 120f, "How long a reservation must remain unchanged before the watchdog may clear it."),
                TransportReservationTimeoutSeconds = config.Bind("ProductivityTweaks", "TransportReservationTimeoutSeconds", 90f, "How long chained hospitalized patients may wait outside room before retrying procedure/transport reservation."),
                ORCleanupPriorityDurationSeconds = config.Bind("ProductivityTweaks", "ORCleanupPriorityDurationSeconds", 300f, "How long an operating room remains a high-priority cleanup target after surgery."),
                NurseORCleanupMaxDurationSeconds = config.Bind("ProductivityTweaks", "NurseORCleanupMaxDurationSeconds", 45f, "Maximum time a nurse-assisted cleanup attempt may own an operating room reservation."),
                SuppressFreeTimeWhenDepartmentBusy = config.Bind("ProductivityTweaks", "SuppressFreeTimeWhenDepartmentBusy", true, "When true, only suppress free-time if the department has obvious queued or critical work."),
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
                EnableIntakeControl = config.Bind("IntakeControl", "EnableIntakeControl", false, "When true, cap today's insurance patient intake after vanilla insurance calculation. Disabled by default."),
                EnableDynamicIntakeByDoctors = config.Bind("IntakeControl", "EnableDynamicIntakeByDoctors", true, "Calculate intake capacity from available outpatient doctors."),
                MaxClinicPatientsPerDay = config.Bind("IntakeControl", "MaxClinicPatientsPerDay", 0, "Hard cap for clinic/mobile patients per day. 0 disables this hard cap."),
                MaxAmbulancePatientsPerDay = config.Bind("IntakeControl", "MaxAmbulancePatientsPerDay", 0, "Hard cap for ambulance/immobile patients per day. 0 disables this hard cap."),
                ClinicPatientsPerDoctorPerShift = config.Bind("IntakeControl", "ClinicPatientsPerDoctorPerShift", 10, "Dynamic clinic/mobile patient capacity per outpatient doctor."),
                AmbulancePatientsPerDoctorPerShift = config.Bind("IntakeControl", "AmbulancePatientsPerDoctorPerShift", 3, "Dynamic ambulance/immobile patient capacity per outpatient doctor."),
                ReserveEmergencyCapacityPercent = config.Bind("IntakeControl", "ReserveEmergencyCapacityPercent", 15, "Percent of dynamic capacity reserved for emergency headroom."),
                IntakeDebugLog = config.Bind("IntakeControl", "IntakeDebugLog", false, "Write detailed intake-control decisions."),
                DevCheapUpgrades = config.Bind("Developer", "DevCheapUpgrades", false, "Developer helper: reduce hospital upgrade prices so the most expensive next level costs 100000."),
                EnableAbsurdUpgrades = config.Bind("Developer", "EnableAbsurdUpgrades", false, "Enable absurd hospital upgrade tier: expensive, intentionally overpowered final effects."),
                EnablePerformanceProfiler = config.Bind("Performance", "EnablePerformanceProfiler", false, "Enable internal performance profiler. Default off because Harmony timing has overhead."),
                ProfilerSampleIntervalSeconds = config.Bind("Performance", "ProfilerSampleIntervalSeconds", 10f, "How often to log and reset profiler rolling samples."),
                ProfilerTopN = config.Bind("Performance", "ProfilerTopN", 20, "How many profiler rows to show/log."),
                ProfilerSlowCallMs = config.Bind("Performance", "ProfilerSlowCallMs", 5f, "Calls at or above this duration are counted as slow calls.")
            };
        }
    }

    internal static class RuntimeSettings
    {
        public static AutoLabBalancerConfig Config;
        public static ManualLogSource Logger;

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
        public static int FreeTimeSuppressed;
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

        private static void Postfix(object __instance)
        {
            var entityField = AccessTools.Field(__instance.GetType(), "m_entity");
            var entity = entityField == null ? null : entityField.GetValue(__instance);
            if (entity == null || ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.EmployeeComponent") == null)
            {
                return;
            }

            var perkSet = AccessTools.Field(__instance.GetType(), "m_perkSet").GetValue(__instance);
            NegativePerkFilter.RemoveNegativePerks(perkSet, "perk component");
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
        public static object GetField(object instance, string fieldName)
        {
            if (instance == null)
            {
                return null;
            }

            var field = AccessTools.Field(instance.GetType(), fieldName);
            return field == null ? null : field.GetValue(instance);
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
                var method = pointer.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
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

                var property = pointer.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
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

            var method = instance.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            return method != null && Equals(method.Invoke(instance, null), true);
        }

        public static string GetStringProperty(object instance, string propertyName)
        {
            if (instance == null)
            {
                return null;
            }

            var property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return property == null ? null : property.GetValue(instance, null) as string;
        }

        public static object GetComponentByTypeName(object entity, string typeName)
        {
            var field = AccessTools.Field(entity.GetType(), "m_components");
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

