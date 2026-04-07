param(
    [string]$ManagedDir = "C:\Program Files (x86)\Steam\steamapps\common\Project Hospital\ProjectHospital_Data\Managed",
    [string]$DatabaseDir = "C:\Program Files (x86)\Steam\steamapps\common\Project Hospital\ProjectHospital_Data\StreamingAssets\Database"
)

$ErrorActionPreference = "Stop"
$script:Failures = New-Object System.Collections.Generic.List[string]

function Add-Failure([string]$Message) {
    $script:Failures.Add($Message) | Out-Null
    Write-Host "[FAIL] $Message" -ForegroundColor Red
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        Add-Failure $Message
    }
}

function Resolve-Assembly([object]$Sender, [ResolveEventArgs]$Args) {
    $name = (New-Object Reflection.AssemblyName($Args.Name)).Name + ".dll"
    $candidate = Join-Path $ManagedDir $name
    if (Test-Path -LiteralPath $candidate) {
        return [Reflection.Assembly]::LoadFrom($candidate)
    }

    return $null
}

[AppDomain]::CurrentDomain.add_AssemblyResolve([ResolveEventHandler]{ param($sender, $args) Resolve-Assembly $sender $args })

$assemblyPath = Join-Path $ManagedDir "Assembly-CSharp.dll"
Assert-True (Test-Path -LiteralPath $assemblyPath) "Assembly-CSharp.dll exists at $assemblyPath"
$asm = [Reflection.Assembly]::LoadFrom($assemblyPath)
$allTypes = @($asm.GetTypes())
$typesByFullName = @{}
$typesByName = @{}
foreach ($type in $allTypes) {
    $typesByFullName[$type.FullName] = $type
    if (-not $typesByName.ContainsKey($type.Name)) {
        $typesByName[$type.Name] = $type
    }
}

function Get-TypeByName([string]$Name) {
    if ($typesByFullName.ContainsKey($Name)) {
        return $typesByFullName[$Name]
    }

    $type = [Type]::GetType($Name)
    if ($null -ne $type) {
        return $type
    }

    if ($Name -notmatch "\." -and $typesByName.ContainsKey($Name)) {
        return $typesByName[$Name]
    }

    return $null
}

function Require-Type([string]$Name) {
    $type = Get-TypeByName $Name
    Assert-True ($null -ne $type) "Type exists: $Name"
    if ($null -eq $type) {
        throw "Required type not found: $Name"
    }

    return $type
}

function Resolve-TypeName([string]$Name) {
    switch ($Name) {
        "System.Boolean" { return [bool] }
        "System.Int32" { return [int] }
        "System.Single" { return [single] }
        "System.String" { return [string] }
        "System.String[]" { return [string[]] }
        default { return Require-Type $Name }
    }
}

function Require-Method([string]$TypeName, [string]$MethodName, [string[]]$ParameterTypes, [string]$ReturnType = $null) {
    $type = Require-Type $TypeName
    $flags = [Reflection.BindingFlags]"Public,NonPublic,Instance,Static"
    $paramTypes = @($ParameterTypes | ForEach-Object { Resolve-TypeName $_ })
    $method = $null
    foreach ($candidate in $type.GetMethods($flags)) {
        if ($candidate.Name -ne $MethodName) {
            continue
        }

        $params = @($candidate.GetParameters())
        if ($params.Count -ne $paramTypes.Count) {
            continue
        }

        $matches = $true
        for ($i = 0; $i -lt $params.Count; $i++) {
            if ($params[$i].ParameterType -ne $paramTypes[$i]) {
                $matches = $false
                break
            }
        }

        if ($matches) {
            $method = $candidate
            break
        }
    }
    Assert-True ($null -ne $method) "Method exists: $TypeName.$MethodName($($ParameterTypes -join ', '))"
    if ($method -and $ReturnType) {
        $expectedReturn = Resolve-TypeName $ReturnType
        Assert-True ($method.ReturnType -eq $expectedReturn) "Method return type: $TypeName.$MethodName -> $ReturnType"
    }
    return $method
}

function Require-AnyMethod([string]$TypeName, [string]$MethodName) {
    $type = Require-Type $TypeName
    $flags = [Reflection.BindingFlags]"Public,NonPublic,Instance,Static"
    $methods = @($type.GetMethods($flags) | Where-Object { $_.Name -eq $MethodName })
    Assert-True ($methods.Count -gt 0) "Method exists: $TypeName.$MethodName"
    return $methods
}

function Require-Field([string]$TypeName, [string]$FieldName, [string]$FieldType = $null) {
    $type = Require-Type $TypeName
    $field = $type.GetField($FieldName, [Reflection.BindingFlags]"Public,NonPublic,Instance,Static")
    Assert-True ($null -ne $field) "Field exists: $TypeName.$FieldName"
    if ($field -and $FieldType) {
        $expectedType = Resolve-TypeName $FieldType
        Assert-True ($field.FieldType -eq $expectedType) "Field type: $TypeName.$FieldName -> $FieldType"
    }
    return $field
}

function Require-Property([string]$TypeName, [string]$PropertyName, [string]$PropertyType = $null) {
    $type = Require-Type $TypeName
    $property = $type.GetProperty($PropertyName, [Reflection.BindingFlags]"Public,NonPublic,Instance,Static")
    Assert-True ($null -ne $property) "Property exists: $TypeName.$PropertyName"
    if ($property -and $PropertyType) {
        $expectedType = Resolve-TypeName $PropertyType
        Assert-True ($property.PropertyType -eq $expectedType) "Property type: $TypeName.$PropertyName -> $PropertyType"
    }
    return $property
}

function Require-EnumValues([string]$TypeName, [string[]]$Names) {
    $type = Require-Type $TypeName
    Assert-True $type.IsEnum "Type is enum: $TypeName"
    $actual = @([Enum]::GetNames($type))
    foreach ($name in $Names) {
        Assert-True ($actual -contains $name) "Enum $TypeName contains $name"
    }
}

Write-Host "Checking Project Hospital reflection contracts against $assemblyPath"

Write-Host "Section: Plugin UI/localization"
# Plugin UI/localization
Require-Method "StringTable" "GetLocalizedText" @("System.String", "System.String[]") "System.String" | Out-Null
Require-Method "StringTable" "GetCurrentLanguage" @() "System.String" | Out-Null
Require-Method "HospitalManagementPanelController" "Start" @() | Out-Null
Require-Method "HospitalManagementPanelController" "SetTab" @("HospitalManagementPanelController+HospitalManagementTabs") | Out-Null
Require-Field "HospitalManagementPanelController" "m_tabStatistics" | Out-Null
Require-Field "HospitalManagementPanelController" "m_tabInsurance" | Out-Null
Require-Field "HospitalManagementPanelController" "m_panel" | Out-Null
Require-Field "HospitalManagementPanelController" "m_tabButtonAmbulances" | Out-Null
Require-Field "HospitalManagementPanelController" "m_activeTab" | Out-Null
Require-Method "IconButtonController" "RemoveOnClickDelegate" @() | Out-Null
Require-Method "IconButtonController" "SetOnClickedDelegate" @("IconButtonController+IconButtonOnClickDelegate") | Out-Null
Require-Method "IconButtonController" "SetToolTipTextParameters" @("System.String[]") | Out-Null

Write-Host "Section: Read-only bottleneck overlay lab counters"
# Read-only lab counters used by the bottleneck overlay. The old lab auto-balance and
# lab order availability override are intentionally removed.
Require-Field "Lopital.Hospital" "m_departments" | Out-Null
Require-Field "Lopital.Hospital" "m_characters" | Out-Null
Require-Property "Lopital.LabProcedureManager" "Instance" | Out-Null
Require-Field "Lopital.LabProcedureManager" "m_labProcedures" | Out-Null
Require-Field "Lopital.LabProcedurePersistentData" "m_statLab" | Out-Null
Require-Method "Lopital.BehaviorLabSpecialist" "IsFree" @() "System.Boolean" | Out-Null
Require-Method "Lopital.BehaviorLabSpecialist" "GetReserved" @() "System.Boolean" | Out-Null
Require-Method "Lopital.EmployeeComponent" "IsPerformingAProcedure" @() "System.Boolean" | Out-Null
Require-Field "Lopital.EmployeeComponentPersistentData" "m_department" | Out-Null
Require-Field "Lopital.DepartmentPersistentData" "m_rooms" | Out-Null
Require-Field "Lopital.RoomPersistentData" "m_roomType" | Out-Null

$availabilityMethods = @(Require-AnyMethod "Lopital.ProcedureComponent" "GetProcedureAvailability") + @(Require-AnyMethod "Lopital.ProcedureComponent" "GetProcedureAvailabilty")
Assert-True ((@($availabilityMethods | Where-Object { $_.ReturnType.FullName -eq "Lopital.ProcedureSceneAvailability" }).Count -gt 0)) "Procedure availability methods return Lopital.ProcedureSceneAvailability"

Write-Host "Section: Perk filtering"
# Perk filtering
Require-AnyMethod "Lopital.PerkSet" "CreateAllowedEmployeePerkSet" | Out-Null
Require-Field "Lopital.PerkComponent" "m_entity" | Out-Null
Require-Field "Lopital.PerkComponent" "m_perkSet" | Out-Null
Require-Field "PerkSet" "m_perks" | Out-Null
Require-Property "GameDBPerk" "PerkType" | Out-Null

Write-Host "Section: Medication planning"
# Medication planning
Require-Method "Lopital.ProcedureComponent" "PlanAllTreatments" @("Lopital.MedicalCondition", "System.Boolean", "System.Boolean") | Out-Null
Require-Field "Lopital.MedicalCondition" "m_diagnosedMedicalCondition" | Out-Null
Require-Field "Lopital.MedicalCondition" "m_symptoms" | Out-Null
Require-Field "Symptom" "m_active" | Out-Null
Require-Field "Symptom" "m_hidden" | Out-Null
Require-Field "Symptom" "m_symptom" | Out-Null
Require-Property "GameDBSymptom" "Treatments" | Out-Null
Require-Property "GameDBTreatment" "TreatmentType" "TreatmentType" | Out-Null
Require-EnumValues "TreatmentType" @("RECEIPT", "PRESCRIPTION", "SURGERY") | Out-Null
Require-Field "Lopital.ProcedureComponentPersistentData" "m_procedureQueue" | Out-Null
Require-Field "Lopital.ProcedureQueue" "m_plannedTreatmentStates" | Out-Null
Require-Field "Lopital.ProcedureQueue" "m_activeTreatmentStates" | Out-Null
Require-Field "Lopital.ProcedureQueue" "m_finishedTreatmentStates" | Out-Null
Require-Field "PlannedTreatmentState" "m_treatment" | Out-Null
Require-Field "PlannedTreatmentState" "m_reservationStatus" | Out-Null

Write-Host "Section: Productivity tweaks"
# Productivity: OR cleanup, free-time suppression, reservations, transport, emergency movement
Require-Method "Lopital.ProcedureScriptTreatmentSurgery" "UpdateStateProcedureFinished" @() | Out-Null
Require-Field "Lopital.ProcedureScriptTreatmentSurgery" "m_stateData" | Out-Null
Require-Field "Lopital.ProcedureScriptPersistentData" "m_procedureScene" | Out-Null
Require-Field "Lopital.ProcedureScene" "m_room" | Out-Null
Require-Method "Lopital.BehaviorJanitor" "SelectNextAction" @() | Out-Null
Require-Method "Lopital.BehaviorNurse" "UpdateStateIdle" @("System.Single") | Out-Null
Require-Method "Lopital.Room" "GetFloorIndex" @() "System.Int32" | Out-Null
Require-Field "Lopital.RoomPersistentData" "m_reservedByCharacter" | Out-Null
Require-Field "Lopital.RoomPersistentData" "m_currentProcedureOwner" | Out-Null
Require-Method "Lopital.MapScriptInterface" "FindClosestDirtyTileInARoom" @("Lopital.Room", "GLib.Vector2i") "GLib.Vector2i" | Out-Null
Require-Method "Lopital.MapScriptInterface" "FindFirstDirtyTileZigZag" @("Lopital.Room") "GLib.Vector2i" | Out-Null
Require-Method "Lopital.MapScriptInterface" "CleanTile" @("GLib.Vector2i", "System.Int32") | Out-Null
Require-Method "Lopital.WalkComponent" "UpdateMovement" @("Lopital.Floor", "System.Single") "Lopital.MovementResult" | Out-Null
Require-Method "Lopital.WalkComponent" "MultiUpdate" @("System.Int32", "System.Single") | Out-Null
Require-Field "Lopital.WalkComponent" "m_state" | Out-Null
Require-Field "Lopital.WalkComponent" "m_floor" | Out-Null
Require-Field "Lopital.WalkComponent" "m_route" | Out-Null
Require-Field "Lopital.WalkComponentPersistentData" "m_movementType" | Out-Null
Require-Method "Lopital.BehaviorDoctor" "GetSpeedModifier" @() "System.Single" | Out-Null
Require-Method "Lopital.BehaviorNurse" "GetSpeedModifier" @() "System.Single" | Out-Null
Require-Method "Lopital.BehaviorPatient" "HasCriticalSurgeryPlanned" @() "System.Boolean" | Out-Null
Require-Method "Lopital.HospitalizationComponent" "WillCollapse" @() "System.Boolean" | Out-Null
Require-Method "Lopital.Department" "HasAnyCriticalPatients" @() "System.Boolean" | Out-Null
Require-Method "Lopital.Department" "HasWaitingSurgery" @() "System.Boolean" | Out-Null
Require-Method "Lopital.Department" "HasAnyCriticalSurgeryScheduled" @() "System.Boolean" | Out-Null
Require-Method "Lopital.Department" "HasAnyHospitalizedPatientsWithScheduledProcedures" @() "System.Boolean" | Out-Null
Require-Method "Lopital.Department" "HasAnyWaitingPatients" @() "System.Boolean" | Out-Null
Require-Field "Lopital.EmployeeComponentPersistentData" "m_reservedByPatient" | Out-Null
Require-Field "Lopital.EmployeeComponentPersistentData" "m_reservedForProcedureLocID" | Out-Null
Require-Method "Lopital.MapScriptInterface" "FindClosestObjectWithTag" @("GLib.Vector2i", "System.Int32", "Lopital.Department", "System.String", "Lopital.AccessRights", "System.String[]", "System.Boolean", "System.Boolean", "System.Boolean", "System.Boolean") "Lopital.TileObject" | Out-Null

Write-Host "Section: Chained hospitalized examinations"
# Chained hospitalized examinations and stale reservation retry
Require-Method "Lopital.HospitalizationComponent" "UpdateStateAfterExaminationCheck" @("System.Single") | Out-Null
Require-Method "Lopital.HospitalizationComponent" "SelectNextStep" @("System.Single") "System.Boolean" | Out-Null
Require-Method "Lopital.HospitalizationComponent" "SendBackToRoom" @() | Out-Null
Require-Method "Lopital.ProcedureComponent" "CancelReservation" @() | Out-Null
Require-Field "Lopital.HospitalizationComponentPersistentData" "m_oustideRoom" | Out-Null
Require-Field "Lopital.HospitalizationComponentPersistentData" "m_procedureReservationStatus" | Out-Null
Require-Field "Lopital.HospitalizationComponentPersistentData" "m_timeInState" | Out-Null
Require-Field "Lopital.BehaviorPatientStateData" "m_department" | Out-Null
Require-Field "Lopital.BehaviorPatientStateData" "m_patientState" | Out-Null
Require-Field "Lopital.BehaviorPatientStateData" "m_sentAway" | Out-Null
Require-Field "Lopital.BehaviorPatientStateData" "m_sentHome" | Out-Null
Require-Field "Lopital.BehaviorPatientStateData" "m_deathTriggered" | Out-Null
Require-Field "Lopital.ProcedureQueue" "m_plannedExaminationStates" | Out-Null
Require-Field "PlannedExaminationState" "m_examination" | Out-Null
Require-Field "PlannedExaminationState" "m_reservationStatus" | Out-Null
Require-EnumValues "Lopital.ProcedureReservationStatus" @("WAITING_FOR_ROOM_EXM", "WAITING_FOR_ROOM_TRT", "WAITING_FOR_ROOM_SURG", "WAITING_FOR_STAFF_EXM", "WAITING_FOR_STAFF_TRT", "WAITING_FOR_STAFF_SURG", "WAITING_FOR_TRANSPORT_EXM", "WAITING_FOR_TRANSPORT_TRT", "WAITING_FOR_TRANSPORT_SURG", "WAITING_FOR_CRITICAL_PATIENTS") | Out-Null
Require-EnumValues "Lopital.PatientState" @("Left", "OverriddenByHospitalization") | Out-Null

Write-Host "Section: Referrals"
# Referrals
Require-Method "Lopital.BehaviorPatient" "TryToStartScheduledExamination" @() | Out-Null
Require-Method "Lopital.BehaviorPatient" "TryToScheduleExamination" @("System.Boolean") "System.Boolean" | Out-Null
Require-Method "Lopital.BehaviorPatient" "Leave" @("System.Boolean", "System.Boolean", "System.Boolean") | Out-Null
Require-Method "Lopital.BehaviorPatient" "Diagnose" @("System.Int32", "System.Boolean") "Lopital.DiagnosisResult" | Out-Null
Require-Method "Lopital.BehaviorPatient" "DiagnoseNow" @() | Out-Null
Require-Method "Lopital.HospitalizationComponent" "IsHospitalized" @() "System.Boolean" | Out-Null
Require-Property "GameDBExamination" "Procedure" "GameDBProcedure" | Out-Null
Require-Field "Lopital.BehaviorPatientStateData" "m_medicalCondition" | Out-Null
Require-Field "Lopital.BehaviorPatientStateData" "m_moneySpent" | Out-Null
Require-Field "Lopital.BehaviorPatientStateData" "m_untreated" | Out-Null
Require-Field "Lopital.BehaviorPatientStateData" "m_waitingForPlayer" | Out-Null
Require-Field "Lopital.BehaviorPatientStateData" "m_bookmarked" | Out-Null
Require-Field "Lopital.MedicalCondition" "m_gameDBMedicalCondition" | Out-Null
Require-Field "Lopital.MedicalCondition" "m_diagnosedMedicalCondition" | Out-Null
Require-Property "GameDBMedicalCondition" "InsurancePayment" | Out-Null
Require-Property "GameDBMedicalCondition" "DepartmentRef" | Out-Null
Require-Method "Lopital.Department" "Pay" @("System.Int32", "Lopital.PaymentCategory", "GLib.Entity") | Out-Null
Require-Method "Lopital.Department" "HasWorkingClinic" @() "System.Boolean" | Out-Null
Require-Method "Lopital.MapScriptInterface" "GetDepartmentOfType" @("GameDBDepartment") "Lopital.Department" | Out-Null
Require-Method "CharacterPanelPatientPanelController" "SendPatientToAnotherHospital" @() | Out-Null
Require-AnyMethod "CharacterPanelPatientPanelController" "IsPatientTreated" | Out-Null

Write-Host "Section: Surgery readiness and tooltip"
# Surgery readiness analytics and tooltip fix
Require-Property "GameDBTreatment" "Procedure" "GameDBProcedure" | Out-Null
Require-Property "GameDBProcedure" "RequiredDoctorRoles" | Out-Null
Require-Property "GameDBProcedure" "RequiredNurseRoles" | Out-Null
Require-Property "GameDBProcedure" "RequiredDoctorQualifications" | Out-Null
Require-Property "GameDBProcedure" "RequiredNurseQualifications" | Out-Null
Require-Method "Lopital.EmployeeComponent" "HasRole" @("GameDBEmployeeRole") "System.Boolean" | Out-Null
Require-Method "Lopital.EmployeeComponent" "IsAvailable" @() "System.Boolean" | Out-Null

$surgeryXml = Join-Path $DatabaseDir "Procedures\Surgery.xml"
Write-Host "Section: Surgery XML"
Assert-True (Test-Path -LiteralPath $surgeryXml) "Surgery.xml exists at $surgeryXml"
if (Test-Path -LiteralPath $surgeryXml) {
    [xml]$xml = Get-Content -LiteralPath $surgeryXml
    $surgeries = @($xml.Database.GameDBTreatment)
    Assert-True ($surgeries.Count -gt 0) "Surgery.xml has GameDBTreatment entries"
    foreach ($treatment in $surgeries) {
        $roles = @($treatment.Procedure.RequiredNurseRoles.RoleRef)
        $surgeryNurses = @($roles | Where-Object { $_ -eq "EMPL_ROLE_SURGERY_NURSE" })
        Assert-True ($surgeryNurses.Count -eq 2) "$($treatment.ID) requires exactly 2 EMPL_ROLE_SURGERY_NURSE roles"
    }
}

$enUiXml = Join-Path $DatabaseDir "Localization\StringTableEnUI.xml"
Write-Host "Section: Surgery tooltip XML"
Assert-True (Test-Path -LiteralPath $enUiXml) "StringTableEnUI.xml exists at $enUiXml"
if (Test-Path -LiteralPath $enUiXml) {
    $tooltipLine = $null
    foreach ($line in [IO.File]::ReadLines($enUiXml)) {
        if ($line.IndexOf("TOOLTIP_SURGERY_STAFF_DETAILS", [StringComparison]::Ordinal) -ge 0) {
            $tooltipLine = $line
            break
        }
    }

    Assert-True ($null -ne $tooltipLine) "Vanilla tooltip TOOLTIP_SURGERY_STAFF_DETAILS exists"
    if ($tooltipLine) {
        Assert-True ($tooltipLine -match "1x Surgery nurse") "Vanilla tooltip still understates surgery nurse count, so runtime tooltip fix is still needed"
    }
}

$examinationsXml = Join-Path $DatabaseDir "Procedures\Examinations.xml"
Write-Host "Section: Radiology queue XML"
Assert-True (Test-Path -LiteralPath $examinationsXml) "Examinations.xml exists at $examinationsXml"
if (Test-Path -LiteralPath $examinationsXml) {
    [xml]$xml = Get-Content -LiteralPath $examinationsXml
    $examinationIds = @($xml.Database.GameDBExamination | ForEach-Object { $_.ID })
    foreach ($requiredId in @("EXM_CT", "EXM_CT_ENTEROGRAPHY", "EXM_MRI", "EXM_USG", "EXM_FAST", "EXM_ANGIOGRAPHY", "EXM_X_RAY_BACK", "EXM_X_RAY_HEAD", "EXM_X_RAY_LOWER_LIMB", "EXM_X_RAY_TORSO", "EXM_X_RAY_UPPER_LIMB")) {
        Assert-True ($examinationIds -contains $requiredId) "Radiology examination exists: $requiredId"
    }

    foreach ($requiredId in @("EXM_ECG", "EXM_ECHO", "EXM_CC_ECHO", "EXM_HEART_MONITORING", "EXM_URGENT_BLOOD_ANALYSIS", "EXM_EEG", "EXM_EMG", "EXM_PERIMETRY", "EXM_TONOMETRY", "EXM_BLOOD_TEST_TESTING", "EXM_CBC_TESTING", "EXM_BACTERIA_CULTIVATION_TESTING", "EXM_FUNGAL_CULTIVATION_TESTING", "EXM_BIOPSY_TESTING", "EXM_STOOL_ANALYSIS_TESTING", "EXM_URINE_SAMPLE_ANALYSIS_TESTING", "EXM_INTERVIEW", "EXM_PHYSICAL_AND_VISUAL_EXAMINATION")) {
        Assert-True ($examinationIds -contains $requiredId) "Examination queue category input exists: $requiredId"
    }
}

Write-Host "Section: Intake control"
Require-Method "Lopital.InsuranceManager" "CalculatePatientCounts" @() | Out-Null
Require-Property "Lopital.InsuranceManager" "Instance" "Lopital.InsuranceManager" | Out-Null
Require-Field "Lopital.InsuranceManager" "m_state" | Out-Null
Require-Field "Lopital.InsuranceManagerPersistentData" "m_insuranceCompanies" | Out-Null
Require-Field "Lopital.InsuranceCompany" "m_currentPatients" | Out-Null
Require-Field "Lopital.InsuranceCompany" "m_currentImmobilePatients" | Out-Null
Require-Method "Lopital.InsuranceCompany" "IsContracted" @() "System.Boolean" | Out-Null
Require-Method "Lopital.Department" "AcceptsOutpatients" @() "System.Boolean" | Out-Null

Write-Host "Section: Summary"
if ($script:Failures.Count -gt 0) {
    Write-Host ""
    Write-Host "Reflection contract tests failed: $($script:Failures.Count)" -ForegroundColor Red
    exit 1
}

Write-Host "Reflection contract tests passed." -ForegroundColor Green
