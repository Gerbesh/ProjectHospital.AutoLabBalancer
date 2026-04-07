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
        public const string PluginName = "Project Hospital Auto Lab Balancer";
        public const string PluginVersion = "0.8.13";

        private AutoLabBalancerConfig _config;
        private LabSnapshotService _snapshotService;
        private LabAssignmentService _assignmentService;
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
            Logger.LogInfo(PluginName + " awake started.");
            _config = AutoLabBalancerConfig.Bind(Config);
            _snapshotService = new LabSnapshotService(Logger, _config);
            _assignmentService = new LabAssignmentService(Logger, _config);
            _nextTickAt = 0f;
            RuntimeSettings.Config = _config;
            RuntimeSettings.Logger = Logger;

            try
            {
                _harmony = new Harmony(PluginGuid);
                _harmony.PatchAll(typeof(AutoLabBalancerPlugin).Assembly);
                Logger.LogInfo("Harmony patches installed.");
            }
            catch (Exception ex)
            {
                Logger.LogError("Harmony patching failed; continuing without perk filtering patches. " + ex);
            }

            Logger.LogInfo(PluginName + " loaded. EnableAssignments=" + _config.EnableAssignments.Value);
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

            if (Time.realtimeSinceStartup < _nextTickAt)
            {
                return;
            }

            _nextTickAt = Time.realtimeSinceStartup + Mathf.Max(5f, _config.TickIntervalSeconds.Value);

            try
            {
                var snapshot = _snapshotService.CreateSnapshot();
                if (snapshot == null)
                {
                    return;
                }

                _assignmentService.UpdateAssignments(snapshot, Time.realtimeSinceStartup);
                ProductivityTweaksService.Tick(Time.realtimeSinceStartup);
                TickSurgeryAnalytics();
            }
            catch (Exception ex)
            {
                Logger.LogError("Auto Lab Balancer tick failed: " + ex);
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

            Logger.LogInfo("[SurgeryAnalytics] planned=" + snapshot.PlannedSurgeries
                + " critical=" + snapshot.CriticalSurgeryPatients
                + " waitingDepartments=" + snapshot.WaitingSurgeryDepartments
                + " blockers(room/staff/transport/criticalQueue)=" + snapshot.SurgeryWaitingForRoom
                + "/" + snapshot.SurgeryWaitingForStaff
                + "/" + snapshot.SurgeryWaitingForTransport
                + "/" + snapshot.SurgeryWaitingForCriticalPatients
                + " transportWaits(exam/treatment/chainedOutside)=" + snapshot.WaitingForExamTransport
                + "/" + snapshot.WaitingForTreatmentTransport
                + "/" + snapshot.OutsideRoomChainedPatients
                + " freeStaff(doctors/nurses/janitors)=" + snapshot.FreeDoctors
                + "/" + snapshot.FreeNurses
                + "/" + snapshot.FreeJanitors
                + (string.IsNullOrEmpty(snapshot.SurgeryReadinessDetails) ? string.Empty : " readiness=" + snapshot.SurgeryReadinessDetails.Replace("\n", " | ")));
        }

        private void OnDestroy()
        {
            try
            {
                _assignmentService.RestoreAll("plugin destroyed");
                if (_harmony != null)
                {
                    _harmony.UnpatchSelf();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Auto Lab Balancer restore on destroy failed: " + ex);
            }
        }

        private void OnGUI()
        {
            if (!_showSettings)
            {
                return;
            }

            _settingsWindow = GUILayout.Window(871234, _settingsWindow, DrawSettingsWindow, "Auto Lab Balancer");
        }

        private void DrawSettingsWindow(int windowId)
        {
            GUILayout.Label("Settings are saved to the BepInEx config.");
            GUILayout.BeginHorizontal();
            DrawPageButton(0, "Settings");
            DrawPageButton(1, "Counters");
            DrawPageButton(2, "Bottlenecks");
            DrawPageButton(3, "Surgery");
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
            else
            {
                DrawBottlenecksPage(true);
            }

            GUILayout.Space(8f);
            if (GUILayout.Button("Close"))
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
            var labBalance = GUILayout.Toggle(_config.EnableAssignments.Value, "Lab auto-balance: move free lab specialists");
            if (labBalance != _config.EnableAssignments.Value)
            {
                _config.EnableAssignments.Value = labBalance;
                Config.Save();
            }

            var labAvailability = GUILayout.Toggle(_config.EnableLabAvailabilityOverride.Value, "Allow lab orders when free matching lab staff exists");
            if (labAvailability != _config.EnableLabAvailabilityOverride.Value)
            {
                _config.EnableLabAvailabilityOverride.Value = labAvailability;
                Config.Save();
            }

            var blockNegativePerks = GUILayout.Toggle(_config.PreventNegativeEmployeePerks.Value, "Block negative employee perks on generation");
            if (blockNegativePerks != _config.PreventNegativeEmployeePerks.Value)
            {
                _config.PreventNegativeEmployeePerks.Value = blockNegativePerks;
                Config.Save();
            }

            var debugLog = GUILayout.Toggle(_config.EnableDebugLog.Value, "Debug log");
            if (debugLog != _config.EnableDebugLog.Value)
            {
                _config.EnableDebugLog.Value = debugLog;
                Config.Save();
            }

            GUILayout.Space(8f);
            GUILayout.Label("Productivity Tweaks");
            DrawToggle(_config.EnableAggressiveMedicationPlanning, "Plan all available medication for known active symptoms");
            DrawToggle(_config.EnableFreeTimeSuppression, "Suppress free-time when department is busy");
            DrawToggle(_config.EnablePostSurgeryCleanupPriority, "Prioritize post-surgery OR cleanup");
            DrawToggle(_config.EnableStuckReservationCleanup, "Clean stuck reservations watchdog");
            DrawToggle(_config.EnableFlexibleStretcherPickup, "Flexible stretcher pickup");
            DrawToggle(_config.EnableChainedHospitalizedExaminations, "Chain hospitalized diagnostics before returning to bed");
            DrawToggle(_config.EnableTransportReservationTimeout, "Retry stale transport/procedure reservations");
            DrawToggle(_config.EnableEmergencyRunSpeedBoost, "Emergency run speed boost");
            DrawToggle(_config.EnableNurseAssistedORCleanup, "Nurse-assisted OR cleanup");
            DrawToggle(_config.EnableEquipmentReferral, "Refer equipment-blocked patients to another hospital");
            DrawToggle(_config.EnableManualReferralPayment, "Pay partial fee for manual untreated referrals");
            DrawToggle(_config.EnableDebugProductivityLog, "Productivity debug log");
            DrawToggle(_config.EnableBottleneckOverlay, "Show bottleneck overlay diagnostics");
            DrawToggle(_config.EnableSurgeryAnalyticsLog, "Write surgery bottleneck analytics to BepInEx log");
            DrawToggle(_config.EnableSurgeryTooltipFix, "Fix vanilla surgery staff tooltip");
        }

        private void DrawCountersPage()
        {
            GUILayout.Label("Counters");
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();
            GUILayout.Label("Lab transfers active: " + RuntimeCounters.LabTransfersActive);
            GUILayout.Label("Lab transfers total: " + RuntimeCounters.LabTransfersStarted);
            GUILayout.Label("Medication auto-added: " + RuntimeCounters.MedicationsAutoPlanned);
            GUILayout.Label("Free-time suppressed: " + RuntimeCounters.FreeTimeSuppressed);
            GUILayout.Label("OR cleanup priorities: " + RuntimeCounters.ORCleanupPrioritiesCreated);
            GUILayout.Label("Nurse OR tiles cleaned: " + RuntimeCounters.NurseORTilesCleaned);
            GUILayout.EndVertical();
            GUILayout.BeginVertical();
            GUILayout.Label("Stuck reservations cleared: " + RuntimeCounters.StuckReservationsCleared);
            GUILayout.Label("Flexible transport fallbacks: " + RuntimeCounters.FlexibleTransportFallbacks);
            GUILayout.Label("Transport reservations retried: " + RuntimeCounters.TransportReservationsRetried);
            GUILayout.Label("Emergency speed boosts: " + RuntimeCounters.EmergencySpeedBoosts);
            GUILayout.Label("Equipment referrals: " + RuntimeCounters.EquipmentReferrals);
            GUILayout.Label("Referral income: $" + RuntimeCounters.EquipmentReferralIncome);
            GUILayout.Label("Manual referral payments: " + RuntimeCounters.ManualReferralPayments);
            GUILayout.Label("Manual referral income: $" + RuntimeCounters.ManualReferralIncome);
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
                GUILayout.Label("Bottleneck overlay is disabled.");
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
            GUILayout.Label("Bottlenecks");
            if (_overlaySnapshot == null || !_overlaySnapshot.Ready)
            {
                GUILayout.Label("Game state is not ready yet.");
                return;
            }

            if (!surgeryOnly)
            {
                GUILayout.BeginHorizontal();
                GUILayout.BeginVertical();
                GUILayout.Label("Patients: " + _overlaySnapshot.Patients);
                GUILayout.Label("High-risk: " + _overlaySnapshot.HighRiskPatients);
                GUILayout.Label("Planned meds: " + _overlaySnapshot.PatientsWithPlannedMedication);
                GUILayout.Label("Idle lab queue: " + _overlaySnapshot.IdleLabProcedures);
                GUILayout.Label("Departments busy: " + _overlaySnapshot.BusyDepartments + "/" + _overlaySnapshot.Departments);
                GUILayout.EndVertical();
                GUILayout.BeginVertical();
                GUILayout.Label("Free doctors: " + _overlaySnapshot.FreeDoctors + "/" + _overlaySnapshot.Doctors);
                GUILayout.Label("Free nurses: " + _overlaySnapshot.FreeNurses + "/" + _overlaySnapshot.Nurses);
                GUILayout.Label("Free labs: " + _overlaySnapshot.FreeLabSpecialists + "/" + _overlaySnapshot.LabSpecialists);
                GUILayout.Label("Free janitors: " + _overlaySnapshot.FreeJanitors + "/" + _overlaySnapshot.Janitors);
                GUILayout.Label("OR cleanup rooms: " + ProductivityTweaksService.HighPriorityCleanupRoomCount);
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
                GUILayout.Label("Nurse cleanup jobs: " + ProductivityTweaksService.NurseCleanupJobCount);
            }

            GUILayout.Label("Surgery: planned " + _overlaySnapshot.PlannedSurgeries
                + " / critical " + _overlaySnapshot.CriticalSurgeryPatients
                + " / waiting departments " + _overlaySnapshot.WaitingSurgeryDepartments);
            GUILayout.Label("Surgery blockers: room " + _overlaySnapshot.SurgeryWaitingForRoom
                + ", staff " + _overlaySnapshot.SurgeryWaitingForStaff
                + ", transport " + _overlaySnapshot.SurgeryWaitingForTransport
                + ", critical queue " + _overlaySnapshot.SurgeryWaitingForCriticalPatients);
            GUILayout.Label("Transport waits: exam " + _overlaySnapshot.WaitingForExamTransport
                + ", treatment " + _overlaySnapshot.WaitingForTreatmentTransport
                + ", chained outside room " + _overlaySnapshot.OutsideRoomChainedPatients);
            if (surgeryOnly)
            {
                GUILayout.Label("Note: vanilla surgery tooltip can understate surgery nurses; readiness uses actual RequiredNurseRoles from procedure DB.");
            }

            if (surgeryOnly && !string.IsNullOrEmpty(_overlaySnapshot.SurgeryReadinessDetails))
            {
                GUILayout.Label("Surgery readiness:");
                GUILayout.Label(_overlaySnapshot.SurgeryReadinessDetails);
            }
            if (!string.IsNullOrEmpty(_overlaySnapshot.Warning))
            {
                GUILayout.Label("Overlay warning: " + _overlaySnapshot.Warning);
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
        public ConfigEntry<bool> EnableAssignments { get; private set; }
        public ConfigEntry<bool> EnableLabAvailabilityOverride { get; private set; }
        public ConfigEntry<bool> PreventNegativeEmployeePerks { get; private set; }
        public ConfigEntry<bool> EnableDebugLog { get; private set; }
        public ConfigEntry<KeyCode> SettingsWindowKey { get; private set; }
        public ConfigEntry<float> TickIntervalSeconds { get; private set; }
        public ConfigEntry<int> OverloadedQueueThreshold { get; private set; }
        public ConfigEntry<int> IdleQueueThreshold { get; private set; }
        public ConfigEntry<float> MinTransferDurationSeconds { get; private set; }
        public ConfigEntry<int> MaxTransferredStaffPerLab { get; private set; }
        public ConfigEntry<bool> SameDepartmentOnly { get; private set; }
        public ConfigEntry<bool> PreferSameFloorStaff { get; private set; }
        public ConfigEntry<bool> EnablePostSurgeryCleanupPriority { get; private set; }
        public ConfigEntry<bool> EnableNurseAssistedORCleanup { get; private set; }
        public ConfigEntry<bool> EnableFreeTimeSuppression { get; private set; }
        public ConfigEntry<bool> EnableStuckReservationCleanup { get; private set; }
        public ConfigEntry<bool> EnableFlexibleStretcherPickup { get; private set; }
        public ConfigEntry<bool> EnableChainedHospitalizedExaminations { get; private set; }
        public ConfigEntry<bool> EnableTransportReservationTimeout { get; private set; }
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
        public ConfigEntry<bool> EquipmentReferralDebugLog { get; private set; }

        public static AutoLabBalancerConfig Bind(ConfigFile config)
        {
            return new AutoLabBalancerConfig
            {
                Enabled = config.Bind("General", "Enabled", true, "Master switch for the mod."),
                EnableAssignments = config.Bind("General", "EnableAssignments", false, "When false, the mod only logs diagnostics and does not change workplaces."),
                EnableLabAvailabilityOverride = config.Bind("General", "EnableLabAvailabilityOverride", true, "When true, lab examinations can be ordered if a matching free lab specialist exists even when the target lab currently has no assigned staff."),
                PreventNegativeEmployeePerks = config.Bind("General", "PreventNegativeEmployeePerks", false, "When true, employee generation removes negative perks from hired/editor-created staff."),
                EnableDebugLog = config.Bind("General", "EnableDebugLog", false, "Write detailed queue and candidate logs."),
                SettingsWindowKey = config.Bind("General", "SettingsWindowKey", KeyCode.F8, "Key used to show the in-game mod settings window."),
                TickIntervalSeconds = config.Bind("Balancing", "TickIntervalSeconds", 30f, "How often to evaluate laboratory queues."),
                OverloadedQueueThreshold = config.Bind("Balancing", "OverloadedQueueThreshold", 5, "A lab is overloaded when its waiting queue is at least this size."),
                IdleQueueThreshold = config.Bind("Balancing", "IdleQueueThreshold", 1, "A lab can donate staff when its waiting queue is at most this size."),
                MinTransferDurationSeconds = config.Bind("Balancing", "MinTransferDurationSeconds", 120f, "Minimum time before returning a transferred employee unless they are no longer safe to keep."),
                MaxTransferredStaffPerLab = config.Bind("Balancing", "MaxTransferredStaffPerLab", 2, "Maximum temporary staff assigned to the same target lab."),
                SameDepartmentOnly = config.Bind("Balancing", "SameDepartmentOnly", true, "Only transfer staff between labs in the same department."),
                PreferSameFloorStaff = config.Bind("Balancing", "PreferSameFloorStaff", true, "Prefer temporary lab staff on the same floor as the target lab."),
                EnablePostSurgeryCleanupPriority = config.Bind("ProductivityTweaks", "EnablePostSurgeryCleanupPriority", true, "After surgery, prioritize the operating room for janitor cleanup."),
                EnableNurseAssistedORCleanup = config.Bind("ProductivityTweaks", "EnableNurseAssistedORCleanup", false, "Allow free surgical nurses to help with limited operating room cleanup when no higher-priority nurse work is detected."),
                EnableFreeTimeSuppression = config.Bind("ProductivityTweaks", "EnableFreeTimeSuppression", true, "Prevent doctor/nurse free-time procedures while the department is visibly busy."),
                EnableStuckReservationCleanup = config.Bind("ProductivityTweaks", "EnableStuckReservationCleanup", true, "Watchdog for stale employee and room reservations."),
                EnableFlexibleStretcherPickup = config.Bind("ProductivityTweaks", "EnableFlexibleStretcherPickup", true, "When vanilla cannot find a free department stretcher/wheelchair, search other departments for a free valid matching transport object."),
                EnableChainedHospitalizedExaminations = config.Bind("ProductivityTweaks", "EnableChainedHospitalizedExaminations", true, "Keep hospitalized patients near diagnostics when another examination is already planned instead of returning to bed immediately."),
                EnableTransportReservationTimeout = config.Bind("ProductivityTweaks", "EnableTransportReservationTimeout", true, "Retry stale procedure/transport reservations for chained hospitalized patients before sending them back to bed."),
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
                EquipmentReferralDebugLog = config.Bind("Referral", "EquipmentReferralDebugLog", false, "Write detailed equipment referral decisions.")
            };
        }
    }

    internal sealed class LabSnapshotService
    {
        private readonly ManualLogSource _log;
        private readonly AutoLabBalancerConfig _config;
        private readonly GameApi _api;

        public LabSnapshotService(ManualLogSource log, AutoLabBalancerConfig config)
        {
            _log = log;
            _config = config;
            _api = new GameApi(log);
        }

        public LabSnapshot CreateSnapshot()
        {
            if (!_api.IsReady)
            {
                _log.LogWarning("Auto Lab Balancer could not resolve required game types yet.");
                return null;
            }

            var labProcedureManager = _api.GetStaticProperty(_api.LabProcedureManagerType, "Instance");
            var hospital = _api.GetStaticProperty(_api.HospitalType, "Instance");
            if (labProcedureManager == null || hospital == null)
            {
                if (_config.EnableDebugLog.Value)
                {
                    _log.LogDebug("Hospital or LabProcedureManager instance is not ready.");
                }

                return null;
            }

            var labs = CollectLabs(hospital);
            var employees = CollectLabSpecialists(hospital, labs);
            var queueByLab = CountIdleProceduresByLab(labProcedureManager);

            foreach (var lab in labs)
            {
                int count;
                if (queueByLab.TryGetValue(lab.Entity, out count))
                {
                    lab.IdleQueue = count;
                }
            }

            foreach (var employee in employees)
            {
                var homeLab = employee.WorkplaceRoom;
                if (homeLab == null)
                {
                    continue;
                }

                var lab = labs.FirstOrDefault(x => ReferenceEquals(x.Entity, homeLab));
                if (lab != null)
                {
                    lab.FreeStaff.Add(employee);
                }
            }

            if (_config.EnableDebugLog.Value)
            {
                foreach (var lab in labs.OrderByDescending(x => x.IdleQueue))
                {
                    _log.LogInfo("Lab queue: " + lab.DisplayName + " queue=" + lab.IdleQueue + " freeStaff=" + lab.FreeStaff.Count);
                }
            }

            return new LabSnapshot(labs, employees);
        }

        private List<LabRoomInfo> CollectLabs(object hospital)
        {
            var result = new List<LabRoomInfo>();
            foreach (var department in _api.GetEnumerableField(hospital, "m_departments"))
            {
                foreach (var room in _api.EnumerateDepartmentRooms(department))
                {
                    if (_api.IsLabRoom(room))
                    {
                        result.Add(new LabRoomInfo(room, department, _api.GetEntityName(room)));
                    }
                }
            }

            return result;
        }

        private List<LabEmployeeInfo> CollectLabSpecialists(object hospital, List<LabRoomInfo> labs)
        {
            var result = new List<LabEmployeeInfo>();
            foreach (var character in _api.GetEnumerableField(hospital, "m_characters"))
            {
                var behavior = _api.GetComponentByTypeName(character, "Lopital.BehaviorLabSpecialist");
                var employee = _api.GetComponentByTypeName(character, "Lopital.EmployeeComponent");
                if (behavior == null || employee == null)
                {
                    continue;
                }

                if (!_api.IsFreeLabSpecialist(behavior) || _api.IsEmployeePerformingProcedure(employee))
                {
                    continue;
                }

                var workDesk = _api.GetEmployeeWorkDesk(employee);
                result.Add(new LabEmployeeInfo(
                    character,
                    employee,
                    behavior,
                    _api.FindRoomContainingTileObject(workDesk, labs.Select(x => x.Entity)),
                    _api.GetEmployeeDepartment(employee),
                    _api.GetEntityName(character)));
            }

            return result;
        }

        private Dictionary<object, int> CountIdleProceduresByLab(object labProcedureManager)
        {
            var result = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
            foreach (var procedure in _api.GetEnumerableField(labProcedureManager, "m_labProcedures"))
            {
                if (!_api.IsIdleLabProcedure(procedure))
                {
                    continue;
                }

                var state = _api.GetField(procedure, "m_state");
                var lab = _api.ResolveEntityPointer(_api.GetField(state, "m_statLab"));
                if (lab == null)
                {
                    continue;
                }

                int count;
                result.TryGetValue(lab, out count);
                result[lab] = count + 1;
            }

            return result;
        }
    }

    internal sealed class LabAssignmentService
    {
        private readonly ManualLogSource _log;
        private readonly AutoLabBalancerConfig _config;
        private readonly GameApi _api;
        private readonly Dictionary<object, TemporaryAssignment> _assignments = new Dictionary<object, TemporaryAssignment>(ReferenceEqualityComparer.Instance);

        public LabAssignmentService(ManualLogSource log, AutoLabBalancerConfig config)
        {
            _log = log;
            _config = config;
            _api = new GameApi(log);
        }

        public void UpdateAssignments(LabSnapshot snapshot, float now)
        {
            RestoreCompletedAssignments(snapshot, now);
            RuntimeCounters.LabTransfersActive = _assignments.Count;

            var overloadedLabs = snapshot.Labs
                .Where(x => x.IdleQueue >= _config.OverloadedQueueThreshold.Value)
                .OrderByDescending(x => x.IdleQueue)
                .ToList();

            var idleLabs = snapshot.Labs
                .Where(x => x.IdleQueue <= _config.IdleQueueThreshold.Value)
                .OrderBy(x => x.IdleQueue)
                .ToList();

            foreach (var targetLab in overloadedLabs)
            {
                var currentTransfers = _assignments.Values.Count(x => ReferenceEquals(x.TargetLab, targetLab.Entity));
                if (currentTransfers >= _config.MaxTransferredStaffPerLab.Value)
                {
                    continue;
                }

                var candidate = FindCandidate(idleLabs, targetLab);
                if (candidate != null)
                {
                    Assign(candidate, targetLab, now);
                }
            }
        }

        public void RestoreAll(string reason)
        {
            foreach (var assignment in _assignments.Values.ToList())
            {
                Restore(assignment, reason);
            }
        }

        private LabEmployeeInfo FindCandidate(IEnumerable<LabRoomInfo> idleLabs, LabRoomInfo targetLab)
        {
            var targetFloor = _api.GetRoomFloor(targetLab.Entity);
            var orderedLabs = idleLabs;
            if (_config.PreferSameFloorStaff.Value && targetFloor.HasValue)
            {
                orderedLabs = idleLabs
                    .OrderBy(x =>
                    {
                        var floor = _api.GetRoomFloor(x.Entity);
                        return floor.HasValue ? Math.Abs(floor.Value - targetFloor.Value) : int.MaxValue;
                    })
                    .ThenBy(x => x.IdleQueue)
                    .ToList();
            }

            foreach (var sourceLab in orderedLabs)
            {
                if (ReferenceEquals(sourceLab.Entity, targetLab.Entity))
                {
                    continue;
                }

                if (_config.SameDepartmentOnly.Value && !ReferenceEquals(sourceLab.Department, targetLab.Department))
                {
                    continue;
                }

                var employees = sourceLab.FreeStaff.AsEnumerable();
                if (_config.PreferSameFloorStaff.Value && targetFloor.HasValue)
                {
                    employees = employees.OrderBy(x =>
                    {
                        var floor = _api.GetRoomFloor(x.WorkplaceRoom);
                        return floor.HasValue ? Math.Abs(floor.Value - targetFloor.Value) : int.MaxValue;
                    });
                }

                foreach (var employee in employees)
                {
                    if (_assignments.ContainsKey(employee.EmployeeComponent))
                    {
                        continue;
                    }

                    if (_api.IsFreeLabSpecialist(employee.Behavior) && _api.EmployeeCanUseLab(employee.EmployeeComponent, targetLab.Entity))
                    {
                        return employee;
                    }
                }
            }

            return null;
        }

        private void Assign(LabEmployeeInfo employee, LabRoomInfo targetLab, float now)
        {
            var original = _api.CaptureWorkplace(employee.EmployeeComponent);
            if (original == null || original.WorkDesk == null)
            {
                if (_config.EnableDebugLog.Value)
                {
                    _log.LogDebug("Skipping " + employee.DisplayName + ": no original work desk.");
                }

                return;
            }

            var targetWorkplace = _api.FindWorkplaceForLab(targetLab.Entity, targetLab.Department, original.Shift);
            if (targetWorkplace == null || targetWorkplace.WorkDesk == null)
            {
                if (_config.EnableDebugLog.Value)
                {
                    _log.LogDebug("Skipping " + employee.DisplayName + ": no target work desk in " + targetLab.DisplayName);
                }

                return;
            }

            var assignment = new TemporaryAssignment(employee.EmployeeComponent, employee.Behavior, original, targetLab.Entity, targetLab.DisplayName, now);
            _assignments[employee.EmployeeComponent] = assignment;

            if (!_config.EnableAssignments.Value)
            {
                _log.LogInfo("[dry-run] Would move " + employee.DisplayName + " to overloaded lab " + targetLab.DisplayName);
                return;
            }

            _api.ApplyWorkplace(employee.EmployeeComponent, targetWorkplace);
            _api.GoToWorkplace(employee.Behavior);
            RuntimeCounters.LabTransfersStarted++;
            RuntimeCounters.LabTransfersActive = _assignments.Count;
            _log.LogInfo("Moved " + employee.DisplayName + " to overloaded lab " + targetLab.DisplayName + " temporarily.");
        }

        private void RestoreCompletedAssignments(LabSnapshot snapshot, float now)
        {
            foreach (var assignment in _assignments.Values.ToList())
            {
                var targetLab = snapshot.Labs.FirstOrDefault(x => ReferenceEquals(x.Entity, assignment.TargetLab));
                var originalLab = snapshot.Labs.FirstOrDefault(x => ReferenceEquals(x.Entity, assignment.Original.WorkplaceRoom));
                var minDurationReached = now - assignment.AssignedAt >= _config.MinTransferDurationSeconds.Value;
                var targetRecovered = targetLab == null || targetLab.IdleQueue <= _config.IdleQueueThreshold.Value;
                var sourceNeedsStaff = originalLab != null && originalLab.IdleQueue >= _config.OverloadedQueueThreshold.Value;

                if (!_api.IsFreeLabSpecialist(assignment.Behavior))
                {
                    continue;
                }

                if (targetRecovered || sourceNeedsStaff || minDurationReached)
                {
                    Restore(assignment, targetRecovered ? "target recovered" : sourceNeedsStaff ? "source overloaded" : "minimum duration reached");
                }
            }
        }

        private void Restore(TemporaryAssignment assignment, string reason)
        {
            _assignments.Remove(assignment.EmployeeComponent);

            if (!_config.EnableAssignments.Value)
            {
                if (_config.EnableDebugLog.Value)
                {
                    _log.LogInfo("[dry-run] Restored tracked assignment for " + assignment.TargetLabName + ": " + reason);
                }

                return;
            }

            if (!_api.IsFreeLabSpecialist(assignment.Behavior))
            {
                _assignments[assignment.EmployeeComponent] = assignment;
                return;
            }

            _api.ApplyWorkplace(assignment.EmployeeComponent, assignment.Original);
            _api.GoToWorkplace(assignment.Behavior);
            RuntimeCounters.LabTransfersRestored++;
            RuntimeCounters.LabTransfersActive = _assignments.Count;
            _log.LogInfo("Restored lab specialist from " + assignment.TargetLabName + ": " + reason);
        }
    }

    internal sealed class LabSnapshot
    {
        public LabSnapshot(List<LabRoomInfo> labs, List<LabEmployeeInfo> employees)
        {
            Labs = labs;
            Employees = employees;
        }

        public List<LabRoomInfo> Labs { get; private set; }
        public List<LabEmployeeInfo> Employees { get; private set; }
    }

    internal sealed class LabRoomInfo
    {
        public LabRoomInfo(object entity, object department, string displayName)
        {
            Entity = entity;
            Department = department;
            DisplayName = displayName;
            FreeStaff = new List<LabEmployeeInfo>();
        }

        public object Entity { get; private set; }
        public object Department { get; private set; }
        public string DisplayName { get; private set; }
        public int IdleQueue { get; set; }
        public List<LabEmployeeInfo> FreeStaff { get; private set; }
    }

    internal sealed class LabEmployeeInfo
    {
        public LabEmployeeInfo(object character, object employeeComponent, object behavior, object workplaceRoom, object department, string displayName)
        {
            Character = character;
            EmployeeComponent = employeeComponent;
            Behavior = behavior;
            WorkplaceRoom = workplaceRoom;
            Department = department;
            DisplayName = displayName;
        }

        public object Character { get; private set; }
        public object EmployeeComponent { get; private set; }
        public object Behavior { get; private set; }
        public object WorkplaceRoom { get; private set; }
        public object Department { get; private set; }
        public string DisplayName { get; private set; }
    }

    internal sealed class TemporaryAssignment
    {
        public TemporaryAssignment(object employeeComponent, object behavior, WorkplaceSnapshot original, object targetLab, string targetLabName, float assignedAt)
        {
            EmployeeComponent = employeeComponent;
            Behavior = behavior;
            Original = original;
            TargetLab = targetLab;
            TargetLabName = targetLabName;
            AssignedAt = assignedAt;
        }

        public object EmployeeComponent { get; private set; }
        public object Behavior { get; private set; }
        public WorkplaceSnapshot Original { get; private set; }
        public object TargetLab { get; private set; }
        public string TargetLabName { get; private set; }
        public float AssignedAt { get; private set; }
    }

    internal sealed class WorkplaceSnapshot
    {
        public object WorkPlacePosition { get; set; }
        public int WorkPlaceFloorIndex { get; set; }
        public object Shift { get; set; }
        public object WorkDesk { get; set; }
        public object WorkplaceRoom { get; set; }
    }

    internal static class RuntimeSettings
    {
        public static AutoLabBalancerConfig Config;
        public static ManualLogSource Logger;

        public static bool BlockNegativePerks
        {
            get { return Config != null && Config.Enabled.Value && Config.PreventNegativeEmployeePerks.Value; }
        }

        public static bool LabAvailabilityOverride
        {
            get { return Config != null && Config.Enabled.Value && Config.EnableLabAvailabilityOverride.Value; }
        }

        public static bool ProductivityDebug
        {
            get { return Config != null && Config.Enabled.Value && Config.EnableDebugProductivityLog.Value; }
        }
    }

    internal static class RuntimeCounters
    {
        public static int LabTransfersActive;
        public static int LabTransfersStarted;
        public static int LabTransfersRestored;
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

            __result = "2x Surgeon\n1x Anesthesiologist\n2x Surgery nurse";
        }
    }

    [HarmonyPatch]
    internal static class ProcedureComponentLabAvailabilityPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(Lopital.ProcedureComponent)))
            {
                if ((method.Name == "GetProcedureAvailability" || method.Name == "GetProcedureAvailabilty")
                    && method.ReturnType == typeof(Lopital.ProcedureSceneAvailability))
                {
                    yield return method;
                }
            }
        }

        private static void Postfix(object[] __args, ref Lopital.ProcedureSceneAvailability __result)
        {
            if (!RuntimeSettings.LabAvailabilityOverride)
            {
                return;
            }

            if (__result != Lopital.ProcedureSceneAvailability.STAFF_UNAVAILABLE_LAB
                && __result != Lopital.ProcedureSceneAvailability.STAFF_UNAVAILABLE_LAB_ROLES)
            {
                return;
            }

            var procedure = __args == null ? null : __args.OfType<GameDBProcedure>().FirstOrDefault();
            var department = __args == null ? null : __args.OfType<Lopital.Department>().FirstOrDefault();
            var room = __args == null ? null : __args.OfType<Lopital.Room>().FirstOrDefault();

            if (LabAvailabilityOverrideService.CanCoverLabProcedure(procedure, department, room))
            {
                __result = Lopital.ProcedureSceneAvailability.AVAILABLE;
            }
        }
    }

    internal static class LabAvailabilityOverrideService
    {
        public static bool CanCoverLabProcedure(GameDBProcedure procedure, Lopital.Department department, Lopital.Room room)
        {
            if (procedure == null)
            {
                return false;
            }

            var requiredSkill = GetRequiredStatLabSkill(procedure);
            if (requiredSkill == null && !IsStatLabScript(procedure))
            {
                return false;
            }

            if (!HasUsableLab(procedure, department, room))
            {
                return false;
            }

            var hospital = Lopital.Hospital.Instance;
            if (hospital == null)
            {
                return false;
            }

            foreach (var character in ReflectionHelpers.GetEnumerableField(hospital, "m_characters"))
            {
                var behavior = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.BehaviorLabSpecialist");
                var employee = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.EmployeeComponent");
                if (behavior == null || employee == null)
                {
                    continue;
                }

                if (RuntimeSettings.Config.SameDepartmentOnly.Value && department != null)
                {
                    var employeeDepartment = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(ReflectionHelpers.GetField(employee, "m_state"), "m_department"));
                    if (!ReferenceEquals(employeeDepartment, department))
                    {
                        continue;
                    }
                }

                if (!ReflectionHelpers.InvokeBool(behavior, "IsFree") || ReflectionHelpers.InvokeBool(behavior, "GetReserved"))
                {
                    continue;
                }

                if (ReflectionHelpers.InvokeBool(employee, "IsPerformingAProcedure"))
                {
                    continue;
                }

                if (requiredSkill != null && !HasSkill(employee, requiredSkill))
                {
                    continue;
                }

                if (RuntimeSettings.Config.EnableDebugLog.Value && RuntimeSettings.Logger != null)
                {
                    RuntimeSettings.Logger.LogDebug("Lab availability override: matching free lab specialist found for " + procedure);
                }

                return true;
            }

            return false;
        }

        private static bool HasUsableLab(GameDBProcedure procedure, Lopital.Department department, Lopital.Room room)
        {
            if (room != null && room.AllowsProcedure(procedure))
            {
                return true;
            }

            if (department == null)
            {
                return false;
            }

            var state = ReflectionHelpers.GetField(department, "m_departmentPersistentData");
            foreach (var pointer in ReflectionHelpers.GetEnumerableField(state, "m_rooms"))
            {
                var lab = ReflectionHelpers.ResolvePointer(pointer) as Lopital.Room;
                if (lab != null && lab.AllowsProcedure(procedure))
                {
                    return true;
                }
            }

            return false;
        }

        private static object GetRequiredStatLabSkill(GameDBProcedure procedure)
        {
            var property = typeof(GameDBProcedure).GetProperty("RequiredStatLabQualificationRef", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return property == null ? null : ReflectionHelpers.ResolvePointer(property.GetValue(procedure, null));
        }

        private static bool IsStatLabScript(GameDBProcedure procedure)
        {
            var script = ReflectionHelpers.GetStringProperty(procedure, "ProcedureScript");
            return !string.IsNullOrEmpty(script) && script.IndexOf("StatLab", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HasSkill(object employee, object skill)
        {
            var method = employee.GetType().GetMethod("HasSkill", new[] { skill.GetType() });
            return method != null && Equals(method.Invoke(employee, new[] { skill }), true);
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

    internal sealed class GameApi
    {
        private readonly Type _employeeComponentType;
        private readonly Type _roomType;

        public GameApi(ManualLogSource log)
        {
            var gameAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == "Assembly-CSharp");
            if (gameAssembly == null)
            {
                return;
            }

            LabProcedureManagerType = gameAssembly.GetType("Lopital.LabProcedureManager");
            HospitalType = gameAssembly.GetType("Lopital.Hospital");
            _employeeComponentType = gameAssembly.GetType("Lopital.EmployeeComponent");
            _roomType = gameAssembly.GetType("Lopital.Room");
            IsReady = LabProcedureManagerType != null && HospitalType != null && _employeeComponentType != null;
        }

        public bool IsReady { get; private set; }
        public Type LabProcedureManagerType { get; private set; }
        public Type HospitalType { get; private set; }

        public object GetStaticProperty(Type type, string name)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return property == null ? null : property.GetValue(null, null);
        }

        public IEnumerable<object> GetEnumerableField(object instance, string fieldName)
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

        public object GetField(object instance, string fieldName)
        {
            if (instance == null)
            {
                return null;
            }

            var field = instance.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            return field == null ? null : field.GetValue(instance);
        }

        public object ResolveEntityPointer(object pointer)
        {
            if (pointer == null)
            {
                return null;
            }

            foreach (var name in new[] { "Get", "GetEntity", "Entry", "DEBUG_Entity", "Value" })
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

        public object GetComponentByTypeName(object entity, string typeName)
        {
            foreach (var component in GetEnumerableField(entity, "m_components"))
            {
                if (component.GetType().FullName == typeName)
                {
                    return component;
                }
            }

            return null;
        }

        public IEnumerable<object> EnumerateDepartmentRooms(object department)
        {
            var state = GetField(department, "m_departmentPersistentData");
            foreach (var pointer in GetEnumerableField(state, "m_rooms"))
            {
                var room = ResolveEntityPointer(pointer);
                if (room != null)
                {
                    yield return room;
                }
            }
        }

        public bool IsLabRoom(object room)
        {
            var name = GetEntityName(room);
            if (!string.IsNullOrEmpty(name) && name.IndexOf("lab", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            var state = GetField(room, "m_roomPersistentData");
            var roomTypePointer = GetField(state, "m_roomType");
            var roomType = ResolveEntityPointer(roomTypePointer) ?? roomTypePointer;
            var roomTypeText = roomType == null ? string.Empty : roomType.ToString();
            return roomTypeText.IndexOf("lab", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public bool IsIdleLabProcedure(object procedure)
        {
            var method = procedure.GetType().GetMethod("IsIdle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            return method != null && Equals(method.Invoke(procedure, null), true);
        }

        public bool IsFreeLabSpecialist(object behavior)
        {
            return behavior != null && InvokeBool(behavior, "IsFree") && !InvokeBool(behavior, "GetReserved");
        }

        public bool IsEmployeePerformingProcedure(object employee)
        {
            return InvokeBool(employee, "IsPerformingAProcedure");
        }

        public bool EmployeeCanUseLab(object employee, object lab)
        {
            return employee != null && lab != null;
        }

        public object GetEmployeeDepartment(object employee)
        {
            var state = GetField(employee, "m_state");
            return ResolveEntityPointer(GetField(state, "m_department"));
        }

        public object GetEmployeeWorkplaceRoom(object employee)
        {
            var snapshot = CaptureWorkplace(employee);
            return snapshot == null ? null : snapshot.WorkplaceRoom;
        }

        public int? GetRoomFloor(object room)
        {
            return InvokeInt(room, "GetFloorIndex");
        }

        public object GetEmployeeWorkDesk(object employee)
        {
            var state = GetField(employee, "m_state");
            return ResolveEntityPointer(GetField(state, "m_workDesk"));
        }

        public WorkplaceSnapshot CaptureWorkplace(object employee)
        {
            var state = GetField(employee, "m_state");
            if (state == null)
            {
                return null;
            }

            var workDesk = ResolveEntityPointer(GetField(state, "m_workDesk"));
            return new WorkplaceSnapshot
            {
                WorkPlacePosition = GetField(state, "m_workPlacePosition"),
                WorkPlaceFloorIndex = Convert.ToInt32(GetField(state, "m_workPlaceFloorIndex")),
                Shift = GetField(state, "m_shift"),
                WorkDesk = workDesk,
                WorkplaceRoom = null
            };
        }

        public WorkplaceSnapshot FindWorkplaceForLab(object lab, object department, object shift)
        {
            var departmentState = GetField(department, "m_departmentPersistentData");
            foreach (var pointer in GetEnumerableField(departmentState, "m_objects"))
            {
                var target = ResolveEntityPointer(pointer) ?? pointer;
                if (target == null || target.GetType().FullName != "Lopital.TileObject")
                {
                    continue;
                }

                if (!IsTileObjectInRoom(target, lab))
                {
                    continue;
                }

                if (!IsPotentialWorkspace(target) || GetWorkspaceOwner(target, shift) != null)
                {
                    continue;
                }

                var tileState = GetField(target, "m_state");
                var position = GetField(tileState, "m_position");
                var floor = GetField(tileState, "m_floorIndex");
                if (position == null || floor == null)
                {
                    continue;
                }

                return new WorkplaceSnapshot
                {
                    WorkPlacePosition = position,
                    WorkPlaceFloorIndex = Convert.ToInt32(floor),
                    Shift = shift,
                    WorkDesk = target,
                    WorkplaceRoom = lab
                };
            }

            return null;
        }

        public void ApplyWorkplace(object employee, WorkplaceSnapshot workplace)
        {
            var method = _employeeComponentType.GetMethod("SetWorkplace", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null)
            {
                throw new MissingMethodException("EmployeeComponent.SetWorkplace");
            }

            method.Invoke(employee, new[] { workplace.WorkPlacePosition, workplace.WorkPlaceFloorIndex, workplace.Shift, workplace.WorkDesk });
        }

        public void GoToWorkplace(object behavior)
        {
            var method = behavior.GetType().GetMethod("GoToWorkplace", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? behavior.GetType().GetMethod("GoToWorkPlace", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (method != null)
            {
                method.Invoke(behavior, null);
            }
        }

        public string GetEntityName(object entity)
        {
            if (entity == null)
            {
                return "<null>";
            }

            var property = entity.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var name = property == null ? null : property.GetValue(entity, null) as string;
            return string.IsNullOrEmpty(name) ? entity.ToString() : name;
        }

        public object FindRoomContainingTileObject(object tileObject, IEnumerable<object> rooms)
        {
            if (tileObject == null || rooms == null)
            {
                return null;
            }

            foreach (var room in rooms)
            {
                if (IsTileObjectInRoom(tileObject, room))
                {
                    return room;
                }
            }

            return null;
        }

        private bool IsTileObjectInRoom(object tileObject, object room)
        {
            if (tileObject == null || room == null)
            {
                return false;
            }

            var tileState = GetField(tileObject, "m_state");
            var position = GetField(tileState, "m_position");
            var objectFloor = GetField(tileState, "m_floorIndex");
            if (position == null || objectFloor == null)
            {
                return false;
            }

            var roomFloor = InvokeInt(room, "GetFloorIndex");
            if (roomFloor.HasValue && Convert.ToInt32(objectFloor) != roomFloor.Value)
            {
                return false;
            }

            var method = room.GetType().GetMethod("IsPositionInRoom", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return method != null && Equals(method.Invoke(room, new[] { position }), true);
        }

        private bool IsPotentialWorkspace(object tileObject)
        {
            var gameObject = ResolveEntityPointer(GetField(GetField(tileObject, "m_state"), "m_gameDBObject")) ?? GetField(GetField(tileObject, "m_state"), "m_gameDBObject");
            var tags = GetStringArrayProperty(gameObject, "Tags");
            if (tags.Any(tag => tag.IndexOf("workspace", StringComparison.OrdinalIgnoreCase) >= 0 || tag.IndexOf("workplace", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            var useDirection = GetStringProperty(gameObject, "UseDirection");
            return !string.IsNullOrEmpty(useDirection) && tags.Any(tag => tag.IndexOf("lab", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private object GetWorkspaceOwner(object tileObject, object shift)
        {
            var method = tileObject.GetType().GetMethod("GetWorkspaceOwner", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return method == null ? null : method.Invoke(tileObject, new[] { shift });
        }

        private bool InvokeBool(object instance, string methodName)
        {
            if (instance == null)
            {
                return false;
            }

            var method = instance.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            return method != null && Equals(method.Invoke(instance, null), true);
        }

        private int? InvokeInt(object instance, string methodName)
        {
            var method = instance.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (method == null)
            {
                return null;
            }

            return Convert.ToInt32(method.Invoke(instance, null));
        }

        private string GetStringProperty(object instance, string propertyName)
        {
            if (instance == null)
            {
                return null;
            }

            var property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return property == null ? null : property.GetValue(instance, null) as string;
        }

        private IEnumerable<string> GetStringArrayProperty(object instance, string propertyName)
        {
            if (instance == null)
            {
                return Enumerable.Empty<string>();
            }

            var property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var values = property == null ? null : property.GetValue(instance, null) as string[];
            return values ?? Enumerable.Empty<string>();
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
