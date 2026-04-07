# Project Hospital Productivity Tweaks

Probe BepInEx 5 plugin for Project Hospital Unity Mono.

## What this version does

- Runs as a local BepInEx plugin.
- If `PreventNegativeEmployeePerks=true`, removes generated negative employee perks after staff perk generation and after character editor fill.
- Productivity Tweaks:
  - after diagnosis, plans all available prescription/receipt treatments for known active symptoms when `EnableAggressiveMedicationPlanning=true`;
  - limits aggressive medication planning with `MaxAutoMedicationsPerPlan` and `MaxPlannedMedicationsPerPatient`;
  - suppresses free-time procedure availability when the target department has critical patients, waiting surgery, planned inpatient procedures, outpatient waiting patients, or post-surgery cleanup priority;
  - marks a surgery room as high-priority cleanup after `ProcedureScriptTreatmentSurgery.UpdateStateProcedureFinished()`;
  - lets `BehaviorJanitor.SelectNextAction()` try the high-priority operating room before vanilla low-priority dirt, without permanently changing assigned rooms;
  - lets an idle surgery nurse clean a high-priority operating room for up to `NurseORCleanupMaxDurationSeconds`, using only direct walk-to-tile plus `MapScriptInterface.CleanTile(...)` and restoring the room reservation on every exit path;
  - adds a flexible stretcher fallback: if vanilla cannot find a free stretcher/wheelchair in the selected department, it searches other departments for a free valid matching transport object and lets the vanilla stretcher state machine store/restore its original location;
  - applies an emergency speed multiplier in detected critical/collapse patient contexts;
  - runs a conservative stale reservation watchdog for employee patient reservations and room reservations.
- The lab auto-balancer and lab order availability override were removed in `0.9.0`; the overlay can still show read-only lab bottleneck counters.
- The `F8` window shows runtime counters for medication planning, free-time suppression, OR cleanup, stuck reservation cleanup, transport fallback, and emergency speed boosts.
- The `F8` window is split into pages: settings, counters, bottlenecks, and surgery.
- The surgery page highlights active bottlenecks for planned surgeries: waiting room/staff/transport/critical queue, current transport waits, and per-surgery staff readiness. Duplicate role requirements are counted, so surgeries that require two surgery nurses show `EMPL_ROLE_SURGERY_NURSE 1/2` instead of a misleading single-role summary.
- The mod can write periodic `[SurgeryAnalytics]` lines to the BepInEx log when `EnableSurgeryAnalyticsLog=true`.
- Chained hospitalized diagnostics keeps a hospitalized patient near diagnostics when another planned examination is queued, instead of always returning to bed between CT/MRI/etc. It also has a conservative stale reservation retry.
- Manual player-triggered referrals to another hospital can pay a configurable partial fee for untreated patients.
- Automatic equipment-blocked referrals are conservative and disabled by default; hospitalized patients are left to the vanilla hospitalization flow.
- Press `F8` in game to open the mod settings window and toggle negative perk blocking and Productivity Tweaks.

## Safety defaults

Safe defaults:

```ini
[General]
Enabled = true
EnableDebugLog = true
PreventNegativeEmployeePerks = false
SettingsWindowKey = F8
TickIntervalSeconds = 30
```

```ini
[ProductivityTweaks]
EnablePostSurgeryCleanupPriority = true
EnableNurseAssistedORCleanup = false
EnableFreeTimeSuppression = true
EnableStuckReservationCleanup = true
EnableFlexibleStretcherPickup = true
EnableEmergencyRunSpeedBoost = true
EnableAggressiveMedicationPlanning = true
MaxAutoMedicationsPerPlan = 4
MaxPlannedMedicationsPerPatient = 8
EmergencyRunSpeedMultiplier = 2
StuckReservationTimeoutSeconds = 120
ORCleanupPriorityDurationSeconds = 300
NurseORCleanupMaxDurationSeconds = 45
SuppressFreeTimeWhenDepartmentBusy = true
EnableDebugProductivityLog = false

[Overlay]
EnableBottleneckOverlay = true
EnableSurgeryAnalyticsLog = true
SurgeryAnalyticsLogIntervalSeconds = 30

[Referral]
EnableEquipmentReferral = false
EnableManualReferralPayment = true
ManualReferralPaymentPercent = 10
```

## Build

```powershell
dotnet build .\src\ProjectHospital.AutoLabBalancer.csproj -c Release
```

Output:

```text
src\\bin\\Release\\net35\\ProjectHospital.AutoLabBalancer.dll
```

The project file expects local references to:

- BepInEx 5 x64 under `tools\BepInEx_win_x64_5.4.23.3\BepInEx\core`.
- Project Hospital managed assemblies under the default Steam install path:
  `C:\Program Files (x86)\Steam\steamapps\common\Project Hospital\ProjectHospital_Data\Managed`.

The repository intentionally does not include BepInEx binaries, game DLLs, or build outputs.

## Contract tests

Run these before installing a new DLL:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\ReflectionContract.Tests.ps1
```

The tests load the real `Assembly-CSharp.dll` and check the reflection/Harmony contracts used by the mod: target methods, private fields, enum values, surgery role requirements, dirty-tile cleanup methods, movement update signatures, referral hooks, and the vanilla surgery tooltip mismatch.

## Manual install

1. Install BepInEx 5 x64 into the Project Hospital game directory.
2. Start the game once so BepInEx creates its folders.
3. Copy `ProjectHospital.AutoLabBalancer.dll` into:

```text
C:\Program Files (x86)\Steam\steamapps\common\Project Hospital\BepInEx\plugins
```

4. Start the game and check:

```text
C:\Program Files (x86)\Steam\steamapps\common\Project Hospital\BepInEx\LogOutput.log
```

5. Adjust the generated config:

```text
C:\Program Files (x86)\Steam\steamapps\common\Project Hospital\BepInEx\config\local.projecthospital.autolabbalancer.cfg
```

