using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;
using Lopital;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ProjectHospital.AutoLabBalancer
{
    internal sealed class MedicalCaseSegmentTooltip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        private enum TooltipKind
        {
            None,
            Symptom,
            Diagnosis,
            Text
        }

        private TooltipKind _kind;
        private GameDBSymptom _symptom;
        private int _hazardIcon;
        private string _hazardLocalizationId;
        private string _mobilityLocalizationId;
        private bool _suppressed;
        private Color _symptomColor;
        private GameDBMedicalCondition _condition;
        private int _insuranceIcon;
        private int _insuranceCover;
        private string _textLocId;

        public void BindSymptom(GameDBSymptom symptom, int hazardIcon, string hazardLocalizationId, string mobilityLocalizationId, bool suppressed, Color color)
        {
            _kind = TooltipKind.Symptom;
            _symptom = symptom;
            _hazardIcon = hazardIcon;
            _hazardLocalizationId = hazardLocalizationId;
            _mobilityLocalizationId = mobilityLocalizationId;
            _suppressed = suppressed;
            _symptomColor = color;
            _condition = null;
            _textLocId = null;
        }

        public void BindDiagnosis(GameDBMedicalCondition condition, int insuranceIcon, int insuranceCover)
        {
            _kind = TooltipKind.Diagnosis;
            _condition = condition;
            _insuranceIcon = insuranceIcon;
            _insuranceCover = insuranceCover;
            _symptom = null;
            _textLocId = null;
        }

        public void BindText(string textLocId)
        {
            _kind = TooltipKind.Text;
            _textLocId = textLocId;
            _symptom = null;
            _condition = null;
        }

        public void Clear()
        {
            _kind = TooltipKind.None;
            _symptom = null;
            _condition = null;
            _textLocId = null;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (TooltipManager.Instance == null)
            {
                return;
            }

            if (_kind == TooltipKind.Symptom && _symptom != null)
            {
                TooltipManager.Instance.GetTooltipComponent<TooltipSymptom>()
                    .UpdateData(_symptom, _hazardIcon, _hazardLocalizationId, _mobilityLocalizationId, _suppressed, false, _symptomColor);
                return;
            }

            if (_kind == TooltipKind.Diagnosis && _condition != null)
            {
                TooltipManager.Instance.GetTooltip<TooltipDiagnosis>()
                    .GetComponent<TooltipDiagnosis>()
                    .UpdateData(_condition, string.Empty, _insuranceIcon, _insuranceCover);
                return;
            }

            if (_kind == TooltipKind.Text && !string.IsNullOrEmpty(_textLocId))
            {
                TooltipManager.Instance.GetTooltipComponent<TooltipText>().SetText(_textLocId);
            }
        }

        public void OnPointerExit(PointerEventData eventData)
        {
        }
    }

    internal enum CaseDiagnosisStatus
    {
        Active,
        Hidden,
        Suspected,
        Diagnosed,
        Treated
    }

    [Flags]
    internal enum CaseDirtyFlags
    {
        None = 0,
        Evidence = 1 << 0,
        Routing = 1 << 1,
        Materialization = 1 << 2,
        Disposition = 1 << 3,
        Timeline = 1 << 4,
        Ui = 1 << 5
    }

    internal enum SupportLabel
    {
        WeaklySupported,
        ClinicallyPlausible,
        StronglySupported,
        StrongLabBacked,
        ContradictoryEvidence
    }

    internal enum ComorbidityInteractionKind
    {
        MasksEvidence,
        AmplifiesRisk,
        TreatmentConflict,
        UnlocksInterpretation,
        BlocksDisposition
    }

    internal enum ReferralOutcomeClass
    {
        None,
        NecessaryExternalReferral,
        CapacityOverflowReferral,
        RiskAvoidanceReferral,
        PlayerChoiceReferral
    }

    internal enum CareClusterExecutionState
    {
        Dormant,
        Candidate,
        Active,
        Blocked,
        WaitingTransfer,
        Completed,
        ReferredOut
    }

    internal enum CaseIntentKind
    {
        Examination,
        Treatment,
        Hospitalization,
        Transfer,
        Observation
    }

    internal enum CaseIntentStatus
    {
        Latent,
        ReadyToMaterialize,
        Materialized,
        Running,
        Completed,
        Cancelled,
        Blocked
    }

    internal enum DiagnosticEventKind
    {
        Interview,
        ReceptionFast,
        ExaminationFinished,
        LabResultsReady,
        MonitoringReveal
    }

    internal enum MaterializedBindingKind
    {
        PlannedExamination,
        PlannedTreatment,
        ReservedProcedure,
        ActiveExamination,
        ActiveTreatment,
        LabProcedure,
        HospitalizationTransition,
        BridgedDepartmentOwnership
    }

    internal enum CaseCheckpoint
    {
        Bootstrap,
        DiagnosticEvent,
        TryScheduleExamination,
        TryStartScheduledExamination,
        SelectNextProcedure,
        RoutingGate,
        HospitalizationGate,
        ChangeDepartmentCommit,
        NurseCheck,
        LeaveGate,
        Monitoring
    }

    internal enum CaseDispositionMode
    {
        StayInCurrentCluster,
        TransferToClinic,
        ReferOut,
        LeaveHospital
    }

    internal sealed class CaseSymptomState
    {
        public string SymptomId;
        public bool Spawned;
        public bool Hidden;
        public bool PatientKnowsAndComplains;
        public bool Active;
        public bool Suppressed;
        public readonly List<string> RevealSources = new List<string>();
        public readonly List<string> RevealedByEventIds = new List<string>();
    }

    internal sealed class CaseComorbidityInteraction
    {
        public ComorbidityInteractionKind Kind;
        public string SourceProblemId;
        public string TargetProblemId;
        public string Summary;
        public int PriorityDelta;
        public bool BlocksDisposition;
    }

    internal class CaseProblem
    {
        public string ProblemId;
        public string DiagnosisId;
        public string OwningClusterId;
        public float Certainty;
        public SupportLabel SupportLabel;
        public readonly List<string> SymptomIds = new List<string>();
        public readonly List<CaseSymptomState> Symptoms = new List<CaseSymptomState>();
        public readonly List<string> KnownSymptomIds = new List<string>();
        public readonly List<string> RevealedByEventIds = new List<string>();
        public bool RequiresHospitalization;
        public bool BlocksDischarge;
        public readonly List<CaseComorbidityInteraction> ActiveInteractions = new List<CaseComorbidityInteraction>();
    }

    internal sealed class CareCluster
    {
        public string ClusterId;
        public string DepartmentId;
        public readonly List<string> ProblemIds = new List<string>();
        public CareClusterExecutionState ExecutionState;
        public bool NeedsHospitalization;
        public int Priority;
        public readonly List<string> Blockers = new List<string>();
    }

    internal sealed class CaseIntent
    {
        public string IntentId;
        public CaseIntentKind Kind;
        public string ProcedureId;
        public readonly List<string> ReasonProblemIds = new List<string>();
        public string OwningClusterId;
        public CaseIntentStatus Status;
        public readonly List<string> Blockers = new List<string>();
        public int Priority;
    }

    internal sealed class MaterializedBinding
    {
        public MaterializedBindingKind Kind;
        public string BoundId;
        public string IntentId;
        public string ProcedureId;
        public string DepartmentId;
        public string Description;
    }

    internal sealed class MaterializedSlice
    {
        public string ClusterId;
        public string DepartmentId;
        public int Version;
        public readonly List<string> VisibleProblemIds = new List<string>();
        public readonly List<string> IntentIds = new List<string>();
        public readonly List<MaterializedBinding> Bindings = new List<MaterializedBinding>();
        public bool WaitingTransferCommit;
        public string PendingTargetDepartmentId;
        public string LastFingerprint;
    }

    internal sealed class CompatibilityProjection
    {
        public string ProjectedDiagnosisId;
        public string ProjectedClusterId;
        public string ProjectedDepartmentId;
        public string Reason;
        public float UpdatedAtHours;
    }

    internal sealed class DispositionState
    {
        public CaseDispositionMode Mode;
        public string ClusterId;
        public string TargetDepartmentId;
        public ReferralOutcomeClass ReferralOutcome;
        public string Reason;
        public string ReferralTradeoff;
        public bool BlocksDischarge;
        public bool BlocksReleaseFromObservation;
    }

    internal sealed class DiagnosticEventJournalEntry
    {
        public string EventId;
        public DiagnosticEventKind Kind;
        public string SubjectId;
        public string Source;
        public float ProcessedAtHours;
    }

    internal sealed class TimelineEntry
    {
        public int Day;
        public float Hour;
        public string Category;
        public string ProblemId;
        public string ClusterId;
        public string Reason;
        public string Text;
    }

    internal sealed class PatientCase
    {
        public string CaseId;
        public uint PatientEntityId;
        public string PatientName;
        public bool Hopeless;
        public bool Complete;
        public string ActiveDepartmentId;
        public int RiskScore;
        public float CollapseTimerMultiplier = 1f;
        public readonly List<CaseProblem> Problems = new List<CaseProblem>();
        public readonly List<CaseDiagnosis> Diagnoses = new List<CaseDiagnosis>();
        public readonly List<CareCluster> Clusters = new List<CareCluster>();
        public readonly List<CaseIntent> Intents = new List<CaseIntent>();
        public readonly List<DiagnosticEventJournalEntry> ProcessedDiagnosticEventJournal = new List<DiagnosticEventJournalEntry>();
        public readonly List<TimelineEntry> TimelineEntries = new List<TimelineEntry>();
        public readonly List<CaseComorbidityInteraction> ActiveInteractions = new List<CaseComorbidityInteraction>();
        public readonly MaterializedSlice MaterializedSlice = new MaterializedSlice();
        public readonly CompatibilityProjection CompatibilityProjection = new CompatibilityProjection();
        public readonly DispositionState Disposition = new DispositionState();
        public CaseDirtyFlags DirtyFlags = CaseDirtyFlags.None;
        public int ProblemTrackCap = 15;
        public readonly List<CaseTimelineEvent> Timeline = new List<CaseTimelineEvent>();
    }

    internal sealed class CaseDiagnosis : CaseProblem
    {
        public string DepartmentId;
        public string Hazard;
        public bool CollapseCapable;
        public bool SurgeryLikely;
        public bool NeedsHospitalization;
        public bool CanNotTalk;
        public int BleedingLevel;
        public PatientMobility Mobility;
        public float WalkSpeedModifier;
        public string WalkAnimSuffix;
        public float CollapseDeadlineHours = -1f;
        public CaseDiagnosisStatus Status;
        public readonly List<string> TreatedSymptomIds = new List<string>();
    }

    internal sealed class CaseEffects
    {
        public SymptomHazard Hazard = SymptomHazard.Unknown;
        public bool NeedsHospitalization;
        public bool CanNotTalk;
        public bool Immobile;
        public int BleedingLevel;
        public float WalkSpeedModifier;
        public string WalkAnimSuffix;
    }

    internal enum CaseRouteStepType
    {
        None,
        Transfer,
        Hospitalization
    }

    internal sealed class CaseRouteDecision
    {
        public CaseRouteStepType StepType;
        public string DiagnosisId;
        public CaseDiagnosisStatus DiagnosisStatus;
        public string CurrentDepartmentId;
        public string TargetDepartmentId;
        public bool NeedsHospitalization;
        public bool RouteExists;
        public bool CanExecuteNow;
        public string BlockerReason;
    }

    internal static class CaseCarePlanner
    {
        public static CaseDiagnosis SelectNextDiagnosis(PatientCase patientCase, string currentDepartmentId)
        {
            if (patientCase == null)
            {
                return null;
            }

            var hasCollapseCandidate = false;
            var hasUndiagnosedSameDepartment = false;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis != null && diagnosis.Status != CaseDiagnosisStatus.Treated && diagnosis.CollapseCapable)
                {
                    hasCollapseCandidate = true;
                }

                if (!string.IsNullOrEmpty(currentDepartmentId)
                    && diagnosis != null
                    && diagnosis.Status != CaseDiagnosisStatus.Treated
                    && diagnosis.DepartmentId == currentDepartmentId
                    && diagnosis.Status != CaseDiagnosisStatus.Diagnosed
                    && diagnosis.Status != CaseDiagnosisStatus.Active)
                {
                    hasUndiagnosedSameDepartment = true;
                }
            }

            if (!hasCollapseCandidate)
            {
                if (!string.IsNullOrEmpty(currentDepartmentId))
                {
                    for (var i = 0; i < patientCase.Diagnoses.Count; i++)
                    {
                        var diagnosis = patientCase.Diagnoses[i];
                        if (diagnosis != null
                            && diagnosis.Status != CaseDiagnosisStatus.Treated
                            && diagnosis.DepartmentId == currentDepartmentId
                            && (!hasUndiagnosedSameDepartment
                                || (diagnosis.Status != CaseDiagnosisStatus.Diagnosed && diagnosis.Status != CaseDiagnosisStatus.Active)))
                        {
                            return diagnosis;
                        }
                    }
                }

                for (var i = 0; i < patientCase.Diagnoses.Count; i++)
                {
                    var diagnosis = patientCase.Diagnoses[i];
                    if (diagnosis != null && diagnosis.Status != CaseDiagnosisStatus.Treated)
                    {
                        return diagnosis;
                    }
                }

                return null;
            }

            CaseDiagnosis best = null;
            var bestScore = int.MinValue;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis == null || diagnosis.Status == CaseDiagnosisStatus.Treated)
                {
                    continue;
                }

                if (hasCollapseCandidate && !diagnosis.CollapseCapable)
                {
                    continue;
                }

                var score = Score(diagnosis, currentDepartmentId);
                if (score > bestScore)
                {
                    best = diagnosis;
                    bestScore = score;
                }
            }

            return best;
        }

        private static int Score(CaseDiagnosis diagnosis, string currentDepartmentId)
        {
            var score = 0;
            if (string.Equals(diagnosis.Hazard, "High", StringComparison.OrdinalIgnoreCase))
            {
                score += 400;
            }
            else if (string.Equals(diagnosis.Hazard, "Medium", StringComparison.OrdinalIgnoreCase))
            {
                score += 200;
            }
            else
            {
                score += 50;
            }

            if (diagnosis.CollapseCapable)
            {
                score += 300;
                if (diagnosis.CollapseDeadlineHours > 0f)
                {
                    var hoursRemaining = Math.Max(0f, diagnosis.CollapseDeadlineHours - MedicalCaseRewriteService.GetCaseClockHours());
                    score += Math.Max(0, 1000 - (int)(hoursRemaining * 25f));
                }
            }

            if (diagnosis.NeedsHospitalization)
            {
                score += 120;
            }

            if (diagnosis.BleedingLevel > 0)
            {
                score += diagnosis.BleedingLevel * 40;
            }

            if (diagnosis.Mobility == PatientMobility.IMOBILE || diagnosis.Mobility == PatientMobility.INTUBATED)
            {
                score += 90;
            }

            if (!string.IsNullOrEmpty(currentDepartmentId) && diagnosis.DepartmentId == currentDepartmentId)
            {
                score += 35;
            }

            if (diagnosis.Status == CaseDiagnosisStatus.Active || diagnosis.Status == CaseDiagnosisStatus.Diagnosed)
            {
                score += 25;
            }

            return score;
        }
    }

    internal sealed class CaseTimelineEvent
    {
        public int Day;
        public float Hour;
        public string Text;
    }

    internal static class MedicalCaseRewriteService
    {
        private sealed class DiagnosisPanelItemSnapshot
        {
            public GameDBMedicalCondition Condition;
            public string DisplayName;
            public string StatusLabel;
            public Color Color;
        }

        private sealed class PatientPanelSnapshot
        {
            public uint PatientEntityId;
            public float BuiltAt;
            public int Known;
            public int Hidden;
            public int Treated;
            public string AggregateLabel;
            public string HiddenSummaryLabel;
            public int StatusIcon;
            public string StatusLabel;
            public readonly List<DiagnosisPanelItemSnapshot> Diagnoses = new List<DiagnosisPanelItemSnapshot>();
            public readonly List<SymptomPanelItemSnapshot> SymptomItems = new List<SymptomPanelItemSnapshot>();
        }

        private sealed class SymptomPanelItemSnapshot
        {
            public GameDBSymptom Symptom;
            public bool Suppressed;
            public Color Color;
            public int HazardIcon;
            public string HazardLocalizationId;
            public string MobilityLocalizationId;
            public string Label;
        }

        private sealed class CaseWindowSnapshot
        {
            public uint PatientEntityId;
            public float BuiltAt;
            public string TitleLine;
            public readonly List<string> DiagnosisLines = new List<string>();
            public readonly List<string> SymptomLines = new List<string>();
            public readonly List<string> BlockerLines = new List<string>();
            public readonly List<string> TimelineLines = new List<string>();
        }

        private sealed class PendingDiagnosticFocus
        {
            public string DepartmentId;
            public float RequestedAt;
        }

        private sealed class ReferralPopupMessage
        {
            public string PatientName;
            public string Reason;
        }

        private static readonly Dictionary<uint, PatientCase> Cases = new Dictionary<uint, PatientCase>();
        private static readonly Dictionary<object, PatientCase> ConditionCases = new Dictionary<object, PatientCase>(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<string, bool> LoadAttempted = new Dictionary<string, bool>();
        private static readonly List<GameDBMedicalCondition> ScratchConditions = new List<GameDBMedicalCondition>();
        private static readonly Dictionary<string, GameDBMedicalCondition> DiagnosisCache = new Dictionary<string, GameDBMedicalCondition>();
        private static readonly Dictionary<string, GameDBDepartment> DepartmentTypeCache = new Dictionary<string, GameDBDepartment>();
        private static readonly Dictionary<uint, PatientPanelSnapshot> PatientPanelSnapshots = new Dictionary<uint, PatientPanelSnapshot>();
        private static readonly Dictionary<uint, CaseWindowSnapshot> CaseWindowSnapshots = new Dictionary<uint, CaseWindowSnapshot>();
        private static readonly Dictionary<string, float> NotificationMuteUntil = new Dictionary<string, float>();
        private static readonly Dictionary<uint, PendingDiagnosticFocus> PendingDiagnosticFocuses = new Dictionary<uint, PendingDiagnosticFocus>();
        private static readonly Dictionary<uint, float> BlockedCaseRetryUntil = new Dictionary<uint, float>();
        private static readonly Queue<ReferralPopupMessage> ReferralPopupQueue = new Queue<ReferralPopupMessage>();
        private static Rect CaseWindow = new Rect(760f, 80f, 430f, 560f);
        private static Rect ReferralPopupWindow = new Rect(640f, 160f, 500f, 180f);
        private static Vector2 CaseWindowScroll;
        private static PatientCase SelectedCase;
        private static bool ShowCaseWindow;
        private static ReferralPopupMessage ActiveReferralPopup;
        private static string LoadedPath;
        private static bool DiagnosisCacheBuilt;
        private static bool DepartmentCacheBuilt;
        private static GUIStyle WindowStyle;
        private static GUIStyle PanelStyle;
        private static GUIStyle HeaderStyle;
        private static GUIStyle TextStyle;
        private static GUIStyle MutedStyle;
        private static GUIStyle ButtonStyle;
        private static Texture2D WhiteTexture;
        private static Texture2D DarkHeaderTexture;
        private static int CaseWindowSnapshotFrame = -1;
        private static uint CaseWindowSnapshotPatientId;
        private static CaseWindowSnapshot ActiveCaseWindowSnapshot;
        private const float PatientPanelSnapshotTtlSeconds = 0.15f;
        private const float CaseWindowSnapshotTtlSeconds = 0.25f;
        private const int MaxDiagnosticJournalEntries = 128;
        private const int MaxTimelineEntries = 96;
        private const int DefaultProblemTrackCap = 15;
        private static BehaviorPatient SelectedPatient;

        public static bool Enabled
        {
            get
            {
                return RuntimeSettings.Config != null
                    && RuntimeSettings.Config.Enabled.Value
                    && RuntimeSettings.Config.EnableMedicalCaseRewrite.Value;
            }
        }

        public static void Tick()
        {
            if (!Enabled)
            {
                return;
            }

            EnsureLoaded();
            if (SelectedPatient != null)
            {
                ReconcileCaseAtCheckpoint(SelectedPatient, CaseCheckpoint.Bootstrap, "tick");
            }
        }

        public static bool IsRewriteOwned(BehaviorPatient patient)
        {
            return Enabled && GetCase(patient) != null;
        }

        private static void EnsureRuntimeModel(BehaviorPatient patient, PatientCase patientCase, CaseCheckpoint checkpoint, string reason)
        {
            if (patientCase == null)
            {
                return;
            }

            patientCase.ProblemTrackCap = Clamp(patientCase.ProblemTrackCap <= 0 ? DefaultProblemTrackCap : patientCase.ProblemTrackCap, 1, DefaultProblemTrackCap);
            SyncProblemsFromDiagnoses(patientCase);
            ClampInstantiatedProblems(patientCase, patient == null ? null : GetCaseRuntimeDepartmentId(patient, patientCase));
            RebuildClusters(patientCase);
            RebuildInteractions(patientCase);
            RebuildIntents(patientCase);
            ApplySupportLabels(patientCase);
            EvaluateCaseDisposition(patient, patientCase, reason);
            RefreshMaterializedSlice(patient, patientCase, checkpoint, reason);
        }

        private static void SyncProblemsFromDiagnoses(PatientCase patientCase)
        {
            if (patientCase == null)
            {
                return;
            }

            patientCase.Problems.Clear();
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis == null)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(diagnosis.ProblemId))
                {
                    diagnosis.ProblemId = diagnosis.DiagnosisId;
                }

                diagnosis.RequiresHospitalization = diagnosis.NeedsHospitalization;
                diagnosis.BlocksDischarge = diagnosis.Status != CaseDiagnosisStatus.Treated;
                EnsureSymptomStateTable(diagnosis);
                patientCase.Problems.Add(diagnosis);
            }
        }

        private static void EnsureSymptomStateTable(CaseDiagnosis diagnosis)
        {
            if (diagnosis == null)
            {
                return;
            }

            for (var i = diagnosis.Symptoms.Count - 1; i >= 0; i--)
            {
                var symptomState = diagnosis.Symptoms[i];
                if (symptomState == null || string.IsNullOrEmpty(symptomState.SymptomId) || !diagnosis.SymptomIds.Contains(symptomState.SymptomId))
                {
                    diagnosis.Symptoms.RemoveAt(i);
                }
            }

            for (var i = 0; i < diagnosis.SymptomIds.Count; i++)
            {
                var symptomId = diagnosis.SymptomIds[i];
                if (string.IsNullOrEmpty(symptomId) || FindCaseSymptomState(diagnosis, symptomId) != null)
                {
                    continue;
                }

                var symptom = ResolveSymptom(symptomId);
                var symptomState = new CaseSymptomState
                {
                    SymptomId = symptomId,
                    Spawned = true,
                    Hidden = !diagnosis.KnownSymptomIds.Contains(symptomId),
                    PatientKnowsAndComplains = symptom != null && symptom.PatientComplains,
                    Active = !diagnosis.TreatedSymptomIds.Contains(symptomId),
                    Suppressed = diagnosis.TreatedSymptomIds.Contains(symptomId)
                };
                BuildRevealSources(symptomState, symptom);
                diagnosis.Symptoms.Add(symptomState);
            }
        }

        private static void BuildRevealSources(CaseSymptomState symptomState, GameDBSymptom symptom)
        {
            if (symptomState == null || symptom == null)
            {
                return;
            }

            symptomState.RevealSources.Clear();
            if (symptom.PatientComplains)
            {
                symptomState.RevealSources.Add("interview");
            }

            if (symptom.DiscoveredByMonitoring)
            {
                symptomState.RevealSources.Add("monitoring");
            }

            if (symptom.Examinations != null)
            {
                for (var i = 0; i < symptom.Examinations.Length; i++)
                {
                    var exam = symptom.Examinations[i] == null ? null : symptom.Examinations[i].Entry;
                    if (exam == null)
                    {
                        continue;
                    }

                    symptomState.RevealSources.Add("exam:" + exam.DatabaseID);
                }
            }
        }

        private static void ClampInstantiatedProblems(PatientCase patientCase, string currentDepartmentId)
        {
            if (patientCase == null || patientCase.Diagnoses.Count <= patientCase.ProblemTrackCap)
            {
                return;
            }

            var ordered = new List<CaseDiagnosis>(patientCase.Diagnoses);
            ordered.Sort(delegate(CaseDiagnosis left, CaseDiagnosis right)
            {
                return CaseCarePlanner.SelectNextDiagnosis(new PatientCase { Diagnoses = { left, right } }, currentDepartmentId) == left ? -1 : 1;
            });

            var keep = new List<CaseDiagnosis>();
            for (var i = 0; i < ordered.Count && keep.Count < patientCase.ProblemTrackCap; i++)
            {
                keep.Add(ordered[i]);
            }

            patientCase.Diagnoses.Clear();
            patientCase.Diagnoses.AddRange(keep);
            patientCase.DirtyFlags |= CaseDirtyFlags.Evidence | CaseDirtyFlags.Routing | CaseDirtyFlags.Ui;
        }

        private static void RebuildClusters(PatientCase patientCase)
        {
            if (patientCase == null)
            {
                return;
            }

            patientCase.Clusters.Clear();
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis == null)
                {
                    continue;
                }

                var clusterId = string.IsNullOrEmpty(diagnosis.DepartmentId) ? "cluster:" + diagnosis.DiagnosisId : "cluster:" + diagnosis.DepartmentId;
                var cluster = FindCluster(patientCase, clusterId);
                if (cluster == null)
                {
                    cluster = new CareCluster
                    {
                        ClusterId = clusterId,
                        DepartmentId = diagnosis.DepartmentId,
                        ExecutionState = string.Equals(diagnosis.DepartmentId, patientCase.ActiveDepartmentId, StringComparison.Ordinal)
                            ? CareClusterExecutionState.Active
                            : CareClusterExecutionState.Candidate
                    };
                    patientCase.Clusters.Add(cluster);
                }

                diagnosis.OwningClusterId = clusterId;
                cluster.ProblemIds.Add(diagnosis.ProblemId);
                cluster.NeedsHospitalization = cluster.NeedsHospitalization || diagnosis.NeedsHospitalization;
                cluster.Priority += ScoreDiagnosisPriority(diagnosis);
                if (diagnosis.Status != CaseDiagnosisStatus.Treated)
                {
                    cluster.ExecutionState = string.Equals(cluster.DepartmentId, patientCase.ActiveDepartmentId, StringComparison.Ordinal)
                        ? CareClusterExecutionState.Active
                        : cluster.ExecutionState == CareClusterExecutionState.WaitingTransfer
                            ? cluster.ExecutionState
                            : CareClusterExecutionState.Candidate;
                }
            }

            for (var i = 0; i < patientCase.Clusters.Count; i++)
            {
                var cluster = patientCase.Clusters[i];
                if (cluster == null)
                {
                    continue;
                }

                var unresolved = 0;
                for (var j = 0; j < cluster.ProblemIds.Count; j++)
                {
                    var diagnosis = FindDiagnosisByProblemId(patientCase, cluster.ProblemIds[j]);
                    if (diagnosis != null && diagnosis.Status != CaseDiagnosisStatus.Treated)
                    {
                        unresolved++;
                    }
                }

                if (unresolved <= 0)
                {
                    cluster.ExecutionState = CareClusterExecutionState.Completed;
                }
            }
        }

        private static void RebuildInteractions(PatientCase patientCase)
        {
            if (patientCase == null)
            {
                return;
            }

            patientCase.ActiveInteractions.Clear();
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                if (patientCase.Diagnoses[i] != null)
                {
                    patientCase.Diagnoses[i].ActiveInteractions.Clear();
                }
            }

            for (var leftIndex = 0; leftIndex < patientCase.Diagnoses.Count; leftIndex++)
            {
                var left = patientCase.Diagnoses[leftIndex];
                if (left == null || left.Status == CaseDiagnosisStatus.Treated)
                {
                    continue;
                }

                for (var rightIndex = leftIndex + 1; rightIndex < patientCase.Diagnoses.Count; rightIndex++)
                {
                    var right = patientCase.Diagnoses[rightIndex];
                    if (right == null || right.Status == CaseDiagnosisStatus.Treated)
                    {
                        continue;
                    }

                    TryAddInteraction(patientCase, left, right);
                }
            }
        }

        private static void TryAddInteraction(PatientCase patientCase, CaseDiagnosis left, CaseDiagnosis right)
        {
            if (left == null || right == null)
            {
                return;
            }

            if (HasSymptomOverlap(left, right))
            {
                AddInteraction(patientCase, left, right, ComorbidityInteractionKind.MasksEvidence, "Symptoms overlap across problems, so one exam result can support more than one interpretation.", 30, false);
                if (left.KnownSymptomIds.Count != right.KnownSymptomIds.Count)
                {
                    AddInteraction(patientCase, left, right, ComorbidityInteractionKind.UnlocksInterpretation, "Evidence from one problem unlocks interpretation of another problem.", 20, false);
                }
            }

            if ((left.CollapseCapable || string.Equals(left.Hazard, "High", StringComparison.OrdinalIgnoreCase))
                && (right.CollapseCapable || string.Equals(right.Hazard, "High", StringComparison.OrdinalIgnoreCase)))
            {
                AddInteraction(patientCase, left, right, ComorbidityInteractionKind.AmplifiesRisk, "Combined acute problems amplify overall case risk.", 60, false);
            }

            if ((left.SurgeryLikely && right.NeedsHospitalization) || (right.SurgeryLikely && left.NeedsHospitalization))
            {
                AddInteraction(patientCase, left, right, ComorbidityInteractionKind.TreatmentConflict, "Treatment sequencing is constrained by overlapping invasive or inpatient needs.", 40, false);
            }

            if (left.NeedsHospitalization && right.NeedsHospitalization && !string.Equals(left.DepartmentId, right.DepartmentId, StringComparison.Ordinal))
            {
                AddInteraction(patientCase, left, right, ComorbidityInteractionKind.BlocksDisposition, "Problems compete for different inpatient ownership, blocking simple discharge/disposition.", 80, true);
            }
        }

        private static void AddInteraction(PatientCase patientCase, CaseDiagnosis left, CaseDiagnosis right, ComorbidityInteractionKind kind, string summary, int priorityDelta, bool blocksDisposition)
        {
            var interaction = new CaseComorbidityInteraction
            {
                Kind = kind,
                SourceProblemId = left.ProblemId,
                TargetProblemId = right.ProblemId,
                Summary = summary,
                PriorityDelta = priorityDelta,
                BlocksDisposition = blocksDisposition
            };
            patientCase.ActiveInteractions.Add(interaction);
            left.ActiveInteractions.Add(interaction);
            right.ActiveInteractions.Add(interaction);
        }

        private static void RebuildIntents(PatientCase patientCase)
        {
            if (patientCase == null)
            {
                return;
            }

            patientCase.Intents.Clear();
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis == null || diagnosis.Status == CaseDiagnosisStatus.Treated)
                {
                    continue;
                }

                if (diagnosis.KnownSymptomIds.Count < diagnosis.SymptomIds.Count)
                {
                    patientCase.Intents.Add(BuildIntent(diagnosis, CaseIntentKind.Examination, diagnosis.DiagnosisId, diagnosis.KnownSymptomIds.Count > 0 ? CaseIntentStatus.ReadyToMaterialize : CaseIntentStatus.Latent, ScoreDiagnosisPriority(diagnosis)));
                }

                if (diagnosis.TreatedSymptomIds.Count < diagnosis.KnownSymptomIds.Count || diagnosis.Status == CaseDiagnosisStatus.Diagnosed)
                {
                    patientCase.Intents.Add(BuildIntent(diagnosis, CaseIntentKind.Treatment, diagnosis.DiagnosisId, diagnosis.KnownSymptomIds.Count > 0 ? CaseIntentStatus.ReadyToMaterialize : CaseIntentStatus.Blocked, ScoreDiagnosisPriority(diagnosis) - 10));
                }

                if (diagnosis.NeedsHospitalization)
                {
                    patientCase.Intents.Add(BuildIntent(diagnosis, CaseIntentKind.Hospitalization, diagnosis.DiagnosisId, CaseIntentStatus.ReadyToMaterialize, ScoreDiagnosisPriority(diagnosis) + 40));
                }
            }
        }

        private static CaseIntent BuildIntent(CaseDiagnosis diagnosis, CaseIntentKind kind, string procedureId, CaseIntentStatus status, int priority)
        {
            var intent = new CaseIntent
            {
                IntentId = diagnosis.ProblemId + ":" + kind + ":" + procedureId,
                Kind = kind,
                ProcedureId = procedureId,
                OwningClusterId = diagnosis.OwningClusterId,
                Status = status,
                Priority = priority
            };
            intent.ReasonProblemIds.Add(diagnosis.ProblemId);
            return intent;
        }

        private static void ApplySupportLabels(PatientCase patientCase)
        {
            if (patientCase == null)
            {
                return;
            }

            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis == null)
                {
                    continue;
                }

                diagnosis.Certainty = ComputeCertainty(diagnosis);
                diagnosis.SupportLabel = ComputeSupportLabel(diagnosis);
            }
        }

        private static float ComputeCertainty(CaseDiagnosis diagnosis)
        {
            if (diagnosis == null || diagnosis.SymptomIds.Count <= 0)
            {
                return 0f;
            }

            var certainty = (float)diagnosis.KnownSymptomIds.Count / diagnosis.SymptomIds.Count;
            if (diagnosis.Status == CaseDiagnosisStatus.Diagnosed)
            {
                certainty += 0.25f;
            }

            if (diagnosis.Status == CaseDiagnosisStatus.Treated)
            {
                certainty += 0.15f;
            }

            if (HasLabBackedEvidence(diagnosis))
            {
                certainty += 0.20f;
            }

            if (HasContradictoryEvidence(diagnosis))
            {
                certainty -= 0.35f;
            }

            return Mathf.Clamp01(certainty);
        }

        private static SupportLabel ComputeSupportLabel(CaseDiagnosis diagnosis)
        {
            if (diagnosis == null)
            {
                return SupportLabel.WeaklySupported;
            }

            if (HasContradictoryEvidence(diagnosis))
            {
                return SupportLabel.ContradictoryEvidence;
            }

            if (HasLabBackedEvidence(diagnosis) && diagnosis.KnownSymptomIds.Count >= 1)
            {
                return SupportLabel.StrongLabBacked;
            }

            if (diagnosis.Certainty >= 0.75f || diagnosis.Status == CaseDiagnosisStatus.Diagnosed)
            {
                return SupportLabel.StronglySupported;
            }

            if (diagnosis.Certainty >= 0.35f)
            {
                return SupportLabel.ClinicallyPlausible;
            }

            return SupportLabel.WeaklySupported;
        }

        private static void EvaluateCaseDisposition(BehaviorPatient patient, PatientCase patientCase, string reason)
        {
            if (patientCase == null)
            {
                return;
            }

            var activeCluster = FindActiveCluster(patientCase, patient == null ? patientCase.ActiveDepartmentId : GetCaseRuntimeDepartmentId(patient, patientCase));
            var nextDiagnosis = CaseCarePlanner.SelectNextDiagnosis(patientCase, patient == null ? patientCase.ActiveDepartmentId : GetCaseRuntimeDepartmentId(patient, patientCase));
            patientCase.Disposition.ClusterId = activeCluster == null ? null : activeCluster.ClusterId;
            patientCase.Disposition.TargetDepartmentId = nextDiagnosis == null ? patientCase.ActiveDepartmentId : nextDiagnosis.DepartmentId;
            patientCase.Disposition.ReferralOutcome = ReferralOutcomeClass.None;
            patientCase.Disposition.Reason = reason;
            patientCase.Disposition.ReferralTradeoff = string.Empty;
            patientCase.Disposition.BlocksDischarge = nextDiagnosis != null;
            patientCase.Disposition.BlocksReleaseFromObservation = nextDiagnosis != null && nextDiagnosis.Status != CaseDiagnosisStatus.Treated;

            if (nextDiagnosis == null)
            {
                patientCase.Disposition.Mode = CaseDispositionMode.LeaveHospital;
                patientCase.Disposition.Reason = "All active case problems are terminal or resolved.";
                return;
            }

            if (HasDispositionBlockingInteraction(patientCase))
            {
                patientCase.Disposition.Mode = CaseDispositionMode.StayInCurrentCluster;
                patientCase.Disposition.Reason = "Active comorbidity interactions block discharge until one cluster remains authoritative.";
                patientCase.Disposition.ReferralTradeoff = "Staying local preserves evidence continuity but increases queue pressure.";
                return;
            }

            if (!string.IsNullOrEmpty(nextDiagnosis.DepartmentId)
                && !string.Equals(nextDiagnosis.DepartmentId, patientCase.ActiveDepartmentId, StringComparison.Ordinal))
            {
                patientCase.Disposition.Mode = CaseDispositionMode.TransferToClinic;
                patientCase.Disposition.Reason = "The next executable cluster belongs to " + nextDiagnosis.DepartmentId + ".";
                patientCase.Disposition.ReferralTradeoff = "Transfer keeps the case local but delays work until handoff commits.";
                return;
            }

            if (IsCaseBlockedForReferral(patient, patientCase))
            {
                patientCase.Disposition.Mode = CaseDispositionMode.ReferOut;
                patientCase.Disposition.ReferralOutcome = nextDiagnosis.CollapseCapable || string.Equals(nextDiagnosis.Hazard, "High", StringComparison.OrdinalIgnoreCase)
                    ? ReferralOutcomeClass.RiskAvoidanceReferral
                    : ReferralOutcomeClass.NecessaryExternalReferral;
                patientCase.Disposition.Reason = "No safe local route remains for the active cluster.";
                patientCase.Disposition.ReferralTradeoff = "Referral prevents a deadlock but forfeits some local income and continuity.";
                return;
            }

            patientCase.Disposition.Mode = CaseDispositionMode.StayInCurrentCluster;
            patientCase.Disposition.Reason = "Current cluster still has executable work.";
            patientCase.Disposition.ReferralTradeoff = "Staying local keeps backlog hidden from vanilla until the next safe checkpoint.";
        }

        public static void ReconcileCaseAtCheckpoint(BehaviorPatient patient, CaseCheckpoint checkpoint, string reason)
        {
            var patientCase = GetCase(patient);
            if (patientCase == null || patientCase.Complete)
            {
                return;
            }

            EnsureRuntimeModel(patient, patientCase, checkpoint, reason);
        }

        public static bool TryHandleNurseCheckDisposition(object hospitalization, HospitalizationState previousState, HospitalizationState newState)
        {
            if (hospitalization == null
                || previousState != HospitalizationState.OverridenByNurseCheckUp
                || newState != HospitalizationState.InBed)
            {
                return false;
            }

            var entity = ReflectionHelpers.GetField(hospitalization, "m_entity") as GLib.Entity;
            var patient = entity == null ? null : entity.GetComponent<BehaviorPatient>();
            var patientCase = GetCase(patient);
            if (patient == null || patientCase == null || patientCase.Complete)
            {
                return false;
            }

            EnsureRuntimeModel(patient, patientCase, CaseCheckpoint.NurseCheck, "nurse_check");
            switch (patientCase.Disposition.Mode)
            {
                case CaseDispositionMode.LeaveHospital:
                    InvokeSafeHospitalizationMethod(hospitalization, "SendHome");
                    InvokeSafeHospitalizationMethod(hospitalization, "StopMonitoring");
                    SwitchHospitalizationState(hospitalization, HospitalizationState.Leaving);
                    AddTimeline(patientCase, "Nurse-check disposition allowed discharge because the case is complete.");
                    patientCase.Complete = true;
                    Save();
                    return true;
                case CaseDispositionMode.TransferToClinic:
                    var targetDepartment = ResolveDepartment(patientCase.Disposition.TargetDepartmentId);
                    if (targetDepartment != null && patient.GetDepartment() != targetDepartment)
                    {
                        patient.ChangeDepartment(targetDepartment, checkHospitalizationPlace: false);
                    }

                    InvokeSafeHospitalizationMethod(hospitalization, "HospitalizationChange");
                    AddTimeline(patientCase, "Nurse-check disposition transferred execution to " + patientCase.Disposition.TargetDepartmentId + ".");
                    Save();
                    return true;
                default:
                    return true;
            }
        }

        public static GameDBDepartment GetProjectedDepartment(BehaviorPatient patient)
        {
            var patientCase = GetCase(patient);
            if (patientCase == null)
            {
                return null;
            }

            EnsureRuntimeModel(patient, patientCase, CaseCheckpoint.RoutingGate, "projected_department");
            var departmentId = !string.IsNullOrEmpty(patientCase.CompatibilityProjection.ProjectedDepartmentId)
                ? patientCase.CompatibilityProjection.ProjectedDepartmentId
                : (string.IsNullOrEmpty(patientCase.Disposition.TargetDepartmentId)
                    ? patientCase.ActiveDepartmentId
                    : patientCase.Disposition.TargetDepartmentId);
            if (string.IsNullOrEmpty(departmentId))
            {
                return null;
            }

            BuildDepartmentTypeCache();
            GameDBDepartment departmentType;
            return DepartmentTypeCache.TryGetValue(departmentId, out departmentType) ? departmentType : null;
        }

        public static bool ShouldDepartmentBeUnclear(BehaviorPatient patient, bool vanillaResult)
        {
            if (!IsRewriteOwned(patient))
            {
                return vanillaResult;
            }

            return GetProjectedDepartment(patient) == null;
        }

        public static bool ShouldHospitalizationBeOver(object hospitalization, bool vanillaResult)
        {
            var entity = ReflectionHelpers.GetField(hospitalization, "m_entity") as GLib.Entity;
            var patient = entity == null ? null : entity.GetComponent<BehaviorPatient>();
            var patientCase = GetCase(patient);
            if (patient == null || patientCase == null || patientCase.Complete)
            {
                return vanillaResult;
            }

            EnsureRuntimeModel(patient, patientCase, CaseCheckpoint.HospitalizationGate, "is_hospitalization_over");
            return patientCase.Disposition.Mode == CaseDispositionMode.LeaveHospital;
        }

        public static bool TryHandleReleaseFromObservation(object hospitalization, ref bool result)
        {
            var entity = ReflectionHelpers.GetField(hospitalization, "m_entity") as GLib.Entity;
            var patient = entity == null ? null : entity.GetComponent<BehaviorPatient>();
            var patientCase = GetCase(patient);
            if (patient == null || patientCase == null || patientCase.Complete)
            {
                return true;
            }

            EnsureRuntimeModel(patient, patientCase, CaseCheckpoint.HospitalizationGate, "release_from_observation");
            if (patientCase.Disposition.Mode == CaseDispositionMode.TransferToClinic)
            {
                var targetDepartment = ResolveDepartment(patientCase.Disposition.TargetDepartmentId);
                if (targetDepartment != null && patient.GetDepartment() != targetDepartment)
                {
                    patient.ChangeDepartment(targetDepartment, checkHospitalizationPlace: false);
                }

                InvokeSafeHospitalizationMethod(hospitalization, "HospitalizationChange");
                AddTimeline(patientCase, "ReleaseFromObservation redirected execution to " + patientCase.Disposition.TargetDepartmentId + ".");
                Save();
                result = true;
                return false;
            }

            if (patientCase.Disposition.Mode == CaseDispositionMode.LeaveHospital)
            {
                result = true;
                return true;
            }

            result = false;
            return false;
        }

        public static void OnDepartmentChangeCommitted(BehaviorPatient patient)
        {
            var patientCase = GetCase(patient);
            if (patientCase == null || patientCase.Complete)
            {
                return;
            }

            patientCase.ActiveDepartmentId = GetCaseRuntimeDepartmentId(patient, patientCase);
            ReconcileCaseAtCheckpoint(patient, CaseCheckpoint.ChangeDepartmentCommit, "change_department");
        }

        private static CaseSymptomState FindCaseSymptomState(CaseDiagnosis diagnosis, string symptomId)
        {
            if (diagnosis == null || string.IsNullOrEmpty(symptomId))
            {
                return null;
            }

            for (var i = 0; i < diagnosis.Symptoms.Count; i++)
            {
                var symptomState = diagnosis.Symptoms[i];
                if (symptomState != null && string.Equals(symptomState.SymptomId, symptomId, StringComparison.Ordinal))
                {
                    return symptomState;
                }
            }

            return null;
        }

        private static GameDBSymptom ResolveSymptom(string symptomId)
        {
            if (string.IsNullOrEmpty(symptomId) || Database.Instance == null)
            {
                return null;
            }

            try
            {
                return Database.Instance.GetEntry<GameDBSymptom>(symptomId);
            }
            catch
            {
                return null;
            }
        }

        private static CareCluster FindCluster(PatientCase patientCase, string clusterId)
        {
            if (patientCase == null || string.IsNullOrEmpty(clusterId))
            {
                return null;
            }

            for (var i = 0; i < patientCase.Clusters.Count; i++)
            {
                var cluster = patientCase.Clusters[i];
                if (cluster != null && string.Equals(cluster.ClusterId, clusterId, StringComparison.Ordinal))
                {
                    return cluster;
                }
            }

            return null;
        }

        private static CareCluster FindActiveCluster(PatientCase patientCase, string departmentId)
        {
            if (patientCase == null)
            {
                return null;
            }

            for (var i = 0; i < patientCase.Clusters.Count; i++)
            {
                var cluster = patientCase.Clusters[i];
                if (cluster != null
                    && string.Equals(cluster.DepartmentId, departmentId, StringComparison.Ordinal)
                    && cluster.ExecutionState != CareClusterExecutionState.Completed)
                {
                    return cluster;
                }
            }

            return patientCase.Clusters.Count > 0 ? patientCase.Clusters[0] : null;
        }

        private static CaseDiagnosis FindDiagnosisByProblemId(PatientCase patientCase, string problemId)
        {
            if (patientCase == null || string.IsNullOrEmpty(problemId))
            {
                return null;
            }

            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis != null && string.Equals(diagnosis.ProblemId, problemId, StringComparison.Ordinal))
                {
                    return diagnosis;
                }
            }

            return null;
        }

        private static int ScoreDiagnosisPriority(CaseDiagnosis diagnosis)
        {
            if (diagnosis == null)
            {
                return 0;
            }

            var score = 0;
            if (string.Equals(diagnosis.Hazard, "High", StringComparison.OrdinalIgnoreCase))
            {
                score += 200;
            }
            else if (string.Equals(diagnosis.Hazard, "Medium", StringComparison.OrdinalIgnoreCase))
            {
                score += 100;
            }
            else
            {
                score += 20;
            }

            if (diagnosis.CollapseCapable)
            {
                score += 140;
            }

            if (diagnosis.NeedsHospitalization)
            {
                score += 60;
            }

            score += diagnosis.KnownSymptomIds.Count * 8;
            score -= diagnosis.TreatedSymptomIds.Count * 4;
            return score;
        }

        private static bool HasSymptomOverlap(CaseDiagnosis left, CaseDiagnosis right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            for (var i = 0; i < left.SymptomIds.Count; i++)
            {
                if (right.SymptomIds.Contains(left.SymptomIds[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasLabBackedEvidence(CaseDiagnosis diagnosis)
        {
            if (diagnosis == null)
            {
                return false;
            }

            for (var i = 0; i < diagnosis.Symptoms.Count; i++)
            {
                var symptomState = diagnosis.Symptoms[i];
                if (symptomState == null || symptomState.Hidden)
                {
                    continue;
                }

                for (var j = 0; j < symptomState.RevealSources.Count; j++)
                {
                    var source = symptomState.RevealSources[j];
                    if (!string.IsNullOrEmpty(source) && source.IndexOf("LAB", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool HasContradictoryEvidence(CaseDiagnosis diagnosis)
        {
            return diagnosis != null
                && diagnosis.Status == CaseDiagnosisStatus.Diagnosed
                && diagnosis.KnownSymptomIds.Count <= 0;
        }

        private static bool HasDispositionBlockingInteraction(PatientCase patientCase)
        {
            if (patientCase == null)
            {
                return false;
            }

            for (var i = 0; i < patientCase.ActiveInteractions.Count; i++)
            {
                var interaction = patientCase.ActiveInteractions[i];
                if (interaction != null && interaction.BlocksDisposition)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsCaseBlockedForReferral(BehaviorPatient patient, PatientCase patientCase)
        {
            if (patient == null || patientCase == null)
            {
                return false;
            }

            var routeDecision = EvaluateCaseTransferOrHospitalization(patient);
            return routeDecision != null
                && routeDecision.RouteExists
                && !routeDecision.CanExecuteNow
                && !string.IsNullOrEmpty(routeDecision.BlockerReason);
        }

        private static void RefreshMaterializedSlice(BehaviorPatient patient, PatientCase patientCase, CaseCheckpoint checkpoint, string reason)
        {
            if (patientCase == null)
            {
                return;
            }

            var runtimeDepartmentId = patient == null ? patientCase.ActiveDepartmentId : GetCaseRuntimeDepartmentId(patient, patientCase);
            var activeCluster = FindActiveCluster(patientCase, runtimeDepartmentId);
            var projectedDepartmentId = activeCluster != null && !string.IsNullOrEmpty(activeCluster.DepartmentId)
                ? activeCluster.DepartmentId
                : (!string.IsNullOrEmpty(patientCase.Disposition.TargetDepartmentId)
                    ? patientCase.Disposition.TargetDepartmentId
                    : runtimeDepartmentId);
            var waitingTransferCommit = patientCase.Disposition.Mode == CaseDispositionMode.TransferToClinic
                && !string.IsNullOrEmpty(patientCase.Disposition.TargetDepartmentId)
                && !string.Equals(runtimeDepartmentId, patientCase.Disposition.TargetDepartmentId, StringComparison.Ordinal)
                && checkpoint != CaseCheckpoint.ChangeDepartmentCommit;
            var fingerprint = BuildMaterializedSliceFingerprint(patient, patientCase, activeCluster, checkpoint, reason);
            if (string.Equals(patientCase.MaterializedSlice.LastFingerprint, fingerprint, StringComparison.Ordinal))
            {
                UpdateCompatibilityProjection(patientCase, activeCluster, projectedDepartmentId, reason);
                return;
            }

            patientCase.MaterializedSlice.LastFingerprint = fingerprint;
            patientCase.MaterializedSlice.ClusterId = activeCluster == null ? null : activeCluster.ClusterId;
            patientCase.MaterializedSlice.DepartmentId = runtimeDepartmentId;
            patientCase.MaterializedSlice.WaitingTransferCommit = waitingTransferCommit;
            patientCase.MaterializedSlice.PendingTargetDepartmentId = waitingTransferCommit
                ? patientCase.Disposition.TargetDepartmentId
                : null;
            patientCase.MaterializedSlice.VisibleProblemIds.Clear();
            patientCase.MaterializedSlice.IntentIds.Clear();
            patientCase.MaterializedSlice.Bindings.Clear();
            if (activeCluster != null)
            {
                patientCase.MaterializedSlice.VisibleProblemIds.AddRange(activeCluster.ProblemIds);
            }

            for (var i = 0; i < patientCase.Intents.Count; i++)
            {
                var intent = patientCase.Intents[i];
                if (intent == null || intent.Status == CaseIntentStatus.Cancelled)
                {
                    continue;
                }

                if (activeCluster != null && string.Equals(intent.OwningClusterId, activeCluster.ClusterId, StringComparison.Ordinal))
                {
                    patientCase.MaterializedSlice.IntentIds.Add(intent.IntentId);
                }
            }

            CaptureExecutionBindings(patient, patientCase);
            if (patientCase.MaterializedSlice.WaitingTransferCommit)
            {
                patientCase.MaterializedSlice.Bindings.Add(new MaterializedBinding
                {
                    Kind = MaterializedBindingKind.BridgedDepartmentOwnership,
                    BoundId = patientCase.MaterializedSlice.PendingTargetDepartmentId,
                    IntentId = FindIntentIdForBinding(patientCase, MaterializedBindingKind.BridgedDepartmentOwnership, null),
                    DepartmentId = runtimeDepartmentId,
                    Description = "pending transfer to " + patientCase.MaterializedSlice.PendingTargetDepartmentId
                });
            }

            UpdateCompatibilityProjection(patientCase, activeCluster, projectedDepartmentId, reason);
            patientCase.MaterializedSlice.Version++;
            OnMaterializedSliceVersionChanged(patient, patientCase, checkpoint, reason);
        }

        private static void CaptureExecutionBindings(BehaviorPatient patient, PatientCase patientCase)
        {
            if (patient == null || patientCase == null)
            {
                return;
            }

            var procedure = patient.GetComponent<ProcedureComponent>();
            var queue = procedure == null || procedure.m_state == null ? null : procedure.m_state.m_procedureQueue;
            if (queue == null)
            {
                return;
            }

            var activeExam = ReflectionHelpers.ResolvePointer(queue.m_activeExamination) as GameDBExamination;
            if (activeExam != null)
            {
                patientCase.MaterializedSlice.Bindings.Add(new MaterializedBinding
                {
                    Kind = MaterializedBindingKind.ActiveExamination,
                    BoundId = activeExam.DatabaseID.ToString(),
                    IntentId = FindIntentIdForBinding(patientCase, MaterializedBindingKind.ActiveExamination, activeExam.DatabaseID.ToString()),
                    ProcedureId = activeExam.DatabaseID.ToString(),
                    DepartmentId = patientCase.ActiveDepartmentId,
                    Description = "active examination"
                });
            }

            CapturePlannedBindings(queue.m_plannedExaminationStates, "m_examination", MaterializedBindingKind.PlannedExamination, patientCase);
            CapturePlannedBindings(queue.m_plannedTreatmentStates, "m_treatment", MaterializedBindingKind.PlannedTreatment, patientCase);
            CapturePlannedBindings(queue.m_activeTreatmentStates, "m_treatment", MaterializedBindingKind.ActiveTreatment, patientCase);
            CaptureLabBindings(queue.m_labProcedures, patientCase);

            if (procedure.m_state != null && procedure.m_state.m_reservedProcedureScript != null && procedure.m_state.m_reservedProcedureScript.CheckEntity())
            {
                patientCase.MaterializedSlice.Bindings.Add(new MaterializedBinding
                {
                    Kind = MaterializedBindingKind.ReservedProcedure,
                    BoundId = procedure.m_state.m_reservedProcedureScript.GetEntity().GetEntityID().ToString(CultureInfo.InvariantCulture),
                    IntentId = FindIntentIdForBinding(patientCase, MaterializedBindingKind.ReservedProcedure, null),
                    DepartmentId = patientCase.ActiveDepartmentId,
                    Description = procedure.m_state.m_reservedProcedureScript.GetEntity().GetType().Name
                });
            }

            var hospitalization = patient.GetComponent<HospitalizationComponent>();
            if (hospitalization != null
                && (patientCase.Disposition.Mode == CaseDispositionMode.TransferToClinic || patientCase.Disposition.Mode == CaseDispositionMode.LeaveHospital))
            {
                patientCase.MaterializedSlice.Bindings.Add(new MaterializedBinding
                {
                    Kind = MaterializedBindingKind.HospitalizationTransition,
                    BoundId = patientCase.Disposition.Mode.ToString(),
                    IntentId = FindIntentIdForBinding(patientCase, MaterializedBindingKind.HospitalizationTransition, null),
                    DepartmentId = patientCase.ActiveDepartmentId,
                    Description = patientCase.Disposition.Reason
                });
            }
        }

        private static void CapturePlannedBindings(System.Collections.IEnumerable plannedStates, string fieldName, MaterializedBindingKind kind, PatientCase patientCase)
        {
            if (plannedStates == null || patientCase == null)
            {
                return;
            }

            foreach (var plannedState in plannedStates)
            {
                var bound = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(plannedState, fieldName));
                var id = SafeDatabaseId(bound as DatabaseEntry);
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                patientCase.MaterializedSlice.Bindings.Add(new MaterializedBinding
                {
                    Kind = kind,
                    BoundId = id,
                    IntentId = FindIntentIdForBinding(patientCase, kind, id),
                    ProcedureId = id,
                    DepartmentId = patientCase.ActiveDepartmentId,
                    Description = fieldName
                });
            }
        }

        private static void CaptureLabBindings(System.Collections.IEnumerable labProcedures, PatientCase patientCase)
        {
            if (labProcedures == null || patientCase == null)
            {
                return;
            }

            foreach (var labProcedure in labProcedures)
            {
                var examination = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(labProcedure, "m_examination")) as GameDBExamination;
                if (examination == null)
                {
                    continue;
                }

                patientCase.MaterializedSlice.Bindings.Add(new MaterializedBinding
                {
                    Kind = MaterializedBindingKind.LabProcedure,
                    BoundId = examination.DatabaseID.ToString(),
                    IntentId = FindIntentIdForBinding(patientCase, MaterializedBindingKind.LabProcedure, examination.DatabaseID.ToString()),
                    ProcedureId = examination.DatabaseID.ToString(),
                    DepartmentId = patientCase.ActiveDepartmentId,
                    Description = "lab"
                });
            }
        }

        private static string FindIntentIdForBinding(PatientCase patientCase, MaterializedBindingKind kind, string procedureId)
        {
            if (patientCase == null)
            {
                return null;
            }

            CaseIntentKind desiredKind;
            switch (kind)
            {
                case MaterializedBindingKind.PlannedExamination:
                case MaterializedBindingKind.ActiveExamination:
                case MaterializedBindingKind.LabProcedure:
                    desiredKind = CaseIntentKind.Examination;
                    break;
                case MaterializedBindingKind.PlannedTreatment:
                case MaterializedBindingKind.ActiveTreatment:
                case MaterializedBindingKind.ReservedProcedure:
                    desiredKind = CaseIntentKind.Treatment;
                    break;
                case MaterializedBindingKind.HospitalizationTransition:
                case MaterializedBindingKind.BridgedDepartmentOwnership:
                    desiredKind = CaseIntentKind.Hospitalization;
                    break;
                default:
                    desiredKind = CaseIntentKind.Examination;
                    break;
            }

            for (var i = 0; i < patientCase.Intents.Count; i++)
            {
                var intent = patientCase.Intents[i];
                if (intent == null || intent.Status == CaseIntentStatus.Cancelled || intent.Kind != desiredKind)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(patientCase.MaterializedSlice.ClusterId)
                    && !string.Equals(intent.OwningClusterId, patientCase.MaterializedSlice.ClusterId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(procedureId)
                    && !string.IsNullOrEmpty(intent.ProcedureId)
                    && string.Equals(intent.ProcedureId, procedureId, StringComparison.Ordinal))
                {
                    return intent.IntentId;
                }

                return intent.IntentId;
            }

            return null;
        }

        private static void UpdateCompatibilityProjection(PatientCase patientCase, CareCluster activeCluster, string projectedDepartmentId, string reason)
        {
            if (patientCase == null)
            {
                return;
            }

            patientCase.CompatibilityProjection.ProjectedClusterId = activeCluster == null ? null : activeCluster.ClusterId;
            patientCase.CompatibilityProjection.ProjectedDepartmentId = projectedDepartmentId;
            patientCase.CompatibilityProjection.Reason = reason;
            patientCase.CompatibilityProjection.UpdatedAtHours = GetCaseClockHours();
            patientCase.CompatibilityProjection.ProjectedDiagnosisId = patientCase.MaterializedSlice.VisibleProblemIds.Count > 0
                ? patientCase.MaterializedSlice.VisibleProblemIds[0]
                : null;
        }

        private static string BuildMaterializedSliceFingerprint(BehaviorPatient patient, PatientCase patientCase, CareCluster activeCluster, CaseCheckpoint checkpoint, string reason)
        {
            var queueState = TraceLoggingService.CaptureQueueState(patient);
            var builder = new StringBuilder(256);
            builder.Append(activeCluster == null ? "-" : activeCluster.ClusterId).Append("|")
                .Append(patientCase == null ? "-" : patientCase.ActiveDepartmentId).Append("|")
                .Append(queueState == null ? "-" : queueState.ActiveExaminationId).Append("|")
                .Append(queueState == null ? 0 : queueState.PlannedExaminationIds.Count).Append("|")
                .Append(queueState == null ? 0 : queueState.PlannedTreatmentIds.Count).Append("|")
                .Append(queueState == null ? 0 : queueState.ActiveTreatmentIds.Count).Append("|")
                .Append(queueState == null ? 0 : queueState.LabProcedureIds.Count).Append("|")
                .Append(checkpoint).Append("|")
                .Append(reason ?? string.Empty);
            return builder.ToString();
        }

        private static void OnMaterializedSliceVersionChanged(BehaviorPatient patient, PatientCase patientCase, CaseCheckpoint checkpoint, string reason)
        {
            patientCase.DirtyFlags &= ~(CaseDirtyFlags.Materialization | CaseDirtyFlags.Ui);
            SchedulingEngineService.InvalidateForMaterializedSliceChange(patient);
            PerformanceOptimizationService.InvalidateForMaterializedSliceChange(patient);
            TraceLoggingService.LogPatientAction(patient, "OnMaterializedSliceVersionChanged", "version=" + patientCase.MaterializedSlice.Version, "checkpoint=" + checkpoint + ";reason=" + reason);
        }

        private static void InvokeSafeHospitalizationMethod(object hospitalization, string methodName)
        {
            var method = hospitalization == null ? null : AccessTools.Method(hospitalization.GetType(), methodName);
            if (method != null)
            {
                method.Invoke(hospitalization, null);
            }
        }

        private static void SwitchHospitalizationState(object hospitalization, HospitalizationState state)
        {
            var switchState = hospitalization == null ? null : AccessTools.Method(hospitalization.GetType(), "SwitchState");
            if (switchState != null)
            {
                switchState.Invoke(hospitalization, new object[] { state });
            }
        }

        private static string BuildDiagnosticEventId(BehaviorPatient patient, DiagnosticEventKind kind, string subjectId, string source)
        {
            var entity = patient == null ? null : ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            var minuteStamp = Mathf.RoundToInt(GetCaseClockHours() * 60f);
            var queueState = TraceLoggingService.CaptureQueueState(patient);
            return (entity == null ? "0" : entity.GetEntityID().ToString(CultureInfo.InvariantCulture))
                + "|" + kind
                + "|" + (subjectId ?? "-")
                + "|" + minuteStamp.ToString(CultureInfo.InvariantCulture)
                + "|" + (queueState == null ? "-" : queueState.ActiveExaminationId)
                + "|" + (source ?? "-");
        }

        private static bool HasProcessedDiagnosticEvent(PatientCase patientCase, string eventId)
        {
            if (patientCase == null || string.IsNullOrEmpty(eventId))
            {
                return false;
            }

            for (var i = 0; i < patientCase.ProcessedDiagnosticEventJournal.Count; i++)
            {
                var entry = patientCase.ProcessedDiagnosticEventJournal[i];
                if (entry != null && string.Equals(entry.EventId, eventId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static void AppendDiagnosticEvent(PatientCase patientCase, string eventId, DiagnosticEventKind kind, string subjectId, string source)
        {
            if (patientCase == null || string.IsNullOrEmpty(eventId))
            {
                return;
            }

            if (patientCase.ProcessedDiagnosticEventJournal.Count >= MaxDiagnosticJournalEntries)
            {
                patientCase.ProcessedDiagnosticEventJournal.RemoveAt(0);
            }

            patientCase.ProcessedDiagnosticEventJournal.Add(new DiagnosticEventJournalEntry
            {
                EventId = eventId,
                Kind = kind,
                SubjectId = subjectId,
                Source = source,
                ProcessedAtHours = GetCaseClockHours()
            });
        }

        private static int ProcessDiagnosticEvent(BehaviorPatient patient, DiagnosticEventKind kind, string subjectId, string source)
        {
            var patientCase = GetCase(patient);
            if (patientCase == null || patientCase.Complete)
            {
                return 0;
            }

            EnsureRuntimeModel(patient, patientCase, CaseCheckpoint.DiagnosticEvent, source);
            var eventId = BuildDiagnosticEventId(patient, kind, subjectId, source);
            if (HasProcessedDiagnosticEvent(patientCase, eventId))
            {
                return 0;
            }

            var revealed = 0;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis == null || diagnosis.Status == CaseDiagnosisStatus.Treated)
                {
                    continue;
                }

                revealed += RevealFromDiagnosticEvent(diagnosis, kind, subjectId, eventId);
                if (revealed > 0 && diagnosis.Status == CaseDiagnosisStatus.Hidden)
                {
                    diagnosis.Status = CaseDiagnosisStatus.Suspected;
                }
            }

            AppendDiagnosticEvent(patientCase, eventId, kind, subjectId, source);
            if (revealed > 0)
            {
                patientCase.DirtyFlags |= CaseDirtyFlags.Evidence | CaseDirtyFlags.Routing | CaseDirtyFlags.Disposition | CaseDirtyFlags.Timeline | CaseDirtyFlags.Ui;
                AddTimeline(patientCase, BuildDiagnosticTimelineText(kind, subjectId, revealed));
                EnsureRuntimeModel(patient, patientCase, CaseCheckpoint.DiagnosticEvent, source);
                Save();
            }

            return revealed;
        }

        private static int RevealFromDiagnosticEvent(CaseDiagnosis diagnosis, DiagnosticEventKind kind, string subjectId, string eventId)
        {
            if (diagnosis == null)
            {
                return 0;
            }

            var revealed = 0;
            for (var i = 0; i < diagnosis.Symptoms.Count; i++)
            {
                var symptomState = diagnosis.Symptoms[i];
                var symptom = ResolveSymptom(symptomState == null ? null : symptomState.SymptomId);
                if (symptomState == null || symptom == null)
                {
                    continue;
                }

                if (!CanEventRevealSymptom(kind, subjectId, symptomState, symptom))
                {
                    continue;
                }

                if (TryRevealProblemSymptom(diagnosis, symptomState, eventId))
                {
                    revealed++;
                }
            }

            return revealed;
        }

        private static bool CanEventRevealSymptom(DiagnosticEventKind kind, string subjectId, CaseSymptomState symptomState, GameDBSymptom symptom)
        {
            if (symptomState == null || symptom == null || !symptomState.Hidden)
            {
                return false;
            }

            switch (kind)
            {
                case DiagnosticEventKind.Interview:
                    return symptom.PatientComplains || symptomState.RevealSources.Contains("interview");
                case DiagnosticEventKind.ReceptionFast:
                    return symptom.PatientComplains && symptom.Hazard >= SymptomHazard.Medium;
                case DiagnosticEventKind.MonitoringReveal:
                    return symptom.DiscoveredByMonitoring;
                case DiagnosticEventKind.ExaminationFinished:
                case DiagnosticEventKind.LabResultsReady:
                    return symptomState.RevealSources.Contains("exam:" + subjectId);
                default:
                    return false;
            }
        }

        private static bool TryRevealProblemSymptom(CaseDiagnosis diagnosis, CaseSymptomState symptomState, string eventId)
        {
            if (diagnosis == null || symptomState == null || !symptomState.Hidden)
            {
                return false;
            }

            symptomState.Hidden = false;
            symptomState.Active = true;
            if (!symptomState.RevealedByEventIds.Contains(eventId))
            {
                symptomState.RevealedByEventIds.Add(eventId);
            }

            if (!diagnosis.KnownSymptomIds.Contains(symptomState.SymptomId))
            {
                diagnosis.KnownSymptomIds.Add(symptomState.SymptomId);
            }

            if (!diagnosis.RevealedByEventIds.Contains(eventId))
            {
                diagnosis.RevealedByEventIds.Add(eventId);
            }

            return true;
        }

        private static string BuildDiagnosticTimelineText(DiagnosticEventKind kind, string subjectId, int revealed)
        {
            switch (kind)
            {
                case DiagnosticEventKind.Interview:
                    return "Interview evidence revealed " + revealed + " case symptom(s) across the patient case.";
                case DiagnosticEventKind.ReceptionFast:
                    return "Reception triage revealed " + revealed + " case symptom(s) across the patient case.";
                case DiagnosticEventKind.LabResultsReady:
                    return "Lab result " + subjectId + " revealed " + revealed + " case symptom(s) across the patient case.";
                case DiagnosticEventKind.MonitoringReveal:
                    return "Monitoring revealed " + revealed + " case symptom(s) across the patient case.";
                default:
                    return "Examination " + subjectId + " revealed " + revealed + " case symptom(s) across the patient case.";
            }
        }

        private static bool ProcessMonitoringRevealEvents(BehaviorPatient patient, PatientCase patientCase)
        {
            if (patient == null || patientCase == null || patient.m_state == null || patient.m_state.m_medicalCondition == null)
            {
                return false;
            }

            var symptoms = ReflectionHelpers.GetField(patient.m_state.m_medicalCondition, "m_symptoms") as System.Collections.IEnumerable;
            if (symptoms == null)
            {
                return false;
            }

            var changed = false;
            foreach (var symptomState in symptoms)
            {
                if (symptomState == null || Equals(ReflectionHelpers.GetField(symptomState, "m_hidden"), true))
                {
                    continue;
                }

                var symptom = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(symptomState, "m_symptom")) as GameDBSymptom;
                if (symptom == null || !symptom.DiscoveredByMonitoring)
                {
                    continue;
                }

                if (ProcessDiagnosticEvent(patient, DiagnosticEventKind.MonitoringReveal, symptom.DatabaseID.ToString(), "monitoring") > 0)
                {
                    changed = true;
                }
            }

            return changed;
        }

        public static float GetCaseClockHours()
        {
            if (DayTime.Instance == null)
            {
                return 0f;
            }

            return DayTime.Instance.GetDay() * 24f + DayTime.Instance.GetDayTimeHours();
        }

        public static void DrawCaseWindow()
        {
            if (!Enabled)
            {
                return;
            }

            EnsureStyles();
            DrawReferralPopup();
            if (!ShowCaseWindow || SelectedCase == null)
            {
                return;
            }

            CaseWindow = GUILayout.Window(871238, CaseWindow, DrawCaseWindowContents, ModText.T("MedicalCaseWindowTitle"), WindowStyle);
        }

        public static void SelectPatient(BehaviorPatient patient)
        {
            if (!Enabled || patient == null)
            {
                SelectedCase = null;
                SelectedPatient = null;
                ShowCaseWindow = false;
                ActiveCaseWindowSnapshot = null;
                CaseWindowSnapshotFrame = -1;
                CaseWindowSnapshotPatientId = 0;
                return;
            }

            var entity = ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            SelectedCase = GetOrCreateCompatibilityCase(patient, entity);
            SelectedPatient = patient;
            RegisterCurrentMedicalCondition(patient, SelectedCase);
        }

        public static void ShowPatientCase(BehaviorPatient patient)
        {
            SelectPatient(patient);
            ShowCaseWindow = SelectedCase != null;
        }

        public static void BindPatientPanelButton(object controller, BehaviorPatient patient)
        {
            if (!Enabled || controller == null || patient == null)
            {
                return;
            }

            var buttonObject = ReflectionHelpers.GetField(controller, "m_diagnosesListButton") as GameObject;
            var button = buttonObject == null ? null : buttonObject.GetComponent<IconButtonController>();
            if (button == null)
            {
                return;
            }

            button.RemoveOnClickDelegate();
            button.SetOnClickedDelegate(delegate { ShowPatientCase(patient); });
        }

        public static void ApplyAggregatedSymptomsPanel(object controller, BehaviorPatient patient)
        {
            if (!Enabled || controller == null || patient == null || patient.m_state == null || patient.m_state.m_medicalCondition == null)
            {
                return;
            }

            var patientCase = GetOrCreateCompatibilityCase(patient, ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity);
            if (patientCase == null)
            {
                return;
            }

            var snapshot = GetOrBuildPatientPanelSnapshot(patient, patientCase);
            if (snapshot == null)
            {
                return;
            }

            var symptomsPanel = ReflectionHelpers.GetField(controller, "m_symptomsPanel") as GameObject;
            var segment = symptomsPanel == null ? null : symptomsPanel.GetComponent<SegmentController>();
            if (segment == null)
            {
                return;
            }

            if (snapshot.SymptomItems.Count <= 0 && snapshot.Hidden <= 0)
            {
                TraceLoggingService.LogUiSnapshot(patient, "symptoms_panel_update");
                return;
            }

            var hasSummaryItem = snapshot.Hidden > 0;
            var requiredItems = snapshot.SymptomItems.Count + (hasSummaryItem ? 1 : 0);
            if (segment.m_displayedItemCount != requiredItems || segment.m_dirty)
            {
                segment.SetLayout(requiredItems, GetSegmentLayoutSpan(segment, 3));
                segment.m_dirty = false;
            }

            for (var i = 0; i < snapshot.SymptomItems.Count; i++)
            {
                var symptomItem = snapshot.SymptomItems[i];
                var symptom = symptomItem == null ? null : symptomItem.Symptom;
                if (symptomItem == null || symptom == null)
                {
                    continue;
                }

                if (symptom.CustomIconBigAssetRef != null && symptom.CustomIconSmallAssetRef != null)
                {
                    segment.SetItem(i,
                        IconManager.Instance.GetIcon(symptom.CustomIconSmallAssetRef.XmlID),
                        IconManager.Instance.GetIcon(symptom.CustomIconBigAssetRef.XmlID),
                        SegmentItemInteractivity.DISABLED,
                        symptomItem.Label,
                        symptomItem.Color);
                }
                else
                {
                    segment.SetItem(i, symptom.IconIndex, SegmentItemInteractivity.DISABLED, symptomItem.Label, symptomItem.Color);
                }

                SetSegmentButtonReadOnly(segment, i);
                SetSegmentTooltipEnabled(segment, i, true);
                BindSegmentSymptomTooltip(segment, i, symptomItem);
            }

            if (hasSummaryItem)
            {
                var hiddenIndex = snapshot.SymptomItems.Count;
                segment.SetItem(hiddenIndex, IconManager.ICON_HIDDEN_SYMPTOM, SegmentItemInteractivity.DISABLED, snapshot.HiddenSummaryLabel);
                SetSegmentButtonReadOnly(segment, hiddenIndex);
                SetSegmentTooltipEnabled(segment, hiddenIndex, true);
                try
                {
                    segment.GetItemImageComponent(hiddenIndex, 1).color = UISettings.Instance.SYMPTOM_COLOR_DARK_GRAY;
                }
                catch
                {
                }

                BindSegmentTextTooltip(segment, hiddenIndex, "TOOLTIP_HIDDEN_SYMPTOMS");
            }

            var symptomsController = symptomsPanel.GetComponent<SymptomsPanelController>();
            if (symptomsController != null)
            {
                SetFieldValue(symptomsController, "m_activeSymptomCount", snapshot.SymptomItems.Count);
                SetFieldValue(symptomsController, "m_hiddenSymptomCount", snapshot.Hidden);
            }

            segment.UpdateVisibility();
            TraceLoggingService.LogUiSnapshot(patient, "symptoms_panel_update");
        }

        public static void ApplyCaseDiagnosisPanel(object controller, BehaviorPatient patient)
        {
            if (!Enabled || controller == null || patient == null)
            {
                return;
            }

            var patientCase = GetOrCreateCompatibilityCase(patient, ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity);
            if (patientCase == null)
            {
                return;
            }

            var snapshot = GetOrBuildPatientPanelSnapshot(patient, patientCase);
            if (snapshot == null)
            {
                return;
            }

            ApplyCaseStatusSegment(controller, snapshot);
            ApplyKnownDiagnosesSegment(controller, patient, snapshot);
            TraceLoggingService.LogUiSnapshot(patient, "diagnosis_panel_update");
        }

        private static void ApplyCaseStatusSegment(object controller, PatientPanelSnapshot snapshot)
        {
            var segmentObject = ReflectionHelpers.GetField(controller, "m_diagnosisFinalStaticSegment") as GameObject;
            var segment = segmentObject == null ? null : segmentObject.GetComponent<SegmentController>();
            if (segment == null)
            {
                return;
            }

            SetSegmentHeading(segment, ModText.T("MedicalCaseUiStatusHeading"));
            if (segment.m_displayedItemCount != 1 || segment.m_dirty)
            {
                segment.SetLayout(1, GetSegmentLayoutSpan(segment, 1));
                segment.m_dirty = false;
            }

            segment.SetItem(0, snapshot.StatusIcon, SegmentItemInteractivity.DISABLED, snapshot.StatusLabel);
            SetSegmentButtonReadOnly(segment, 0);
            SetSegmentTooltipEnabled(segment, 0, false);
            segment.UpdateVisibility();
        }

        private static void ApplyKnownDiagnosesSegment(object controller, BehaviorPatient patient, PatientPanelSnapshot snapshot)
        {
            var segmentObject = ReflectionHelpers.GetField(controller, "m_diagnosisPossibleStaticSegment") as GameObject;
            var segment = segmentObject == null ? null : segmentObject.GetComponent<SegmentController>();
            if (segment == null)
            {
                return;
            }

            SetSegmentHeading(segment, ModText.T("MedicalCaseDiagnoses"));
            var capacity = GetSegmentCapacity(segment);
            if (capacity <= 0)
            {
                return;
            }

            var renderCount = Math.Min(snapshot.Diagnoses.Count, capacity);
            var span = GetSegmentLayoutSpan(segment, 2);
            if (segment.m_itemCount != renderCount || segment.m_dirty)
            {
                segment.SetLayout(renderCount, span);
                segment.m_dirty = false;
            }

            if (renderCount == 0)
            {
                segment.UpdateVisibility();
                return;
            }

            var insuranceIcon = GetInsuranceIconIndex(patient);
            var insuranceCover = GetInsuranceCoverPercent(patient);
            for (var index = 0; index < renderCount; index++)
            {
                if (!segment.IsInRange(index))
                {
                    continue;
                }

                var diagnosis = snapshot.Diagnoses[index];
                var condition = diagnosis == null ? null : diagnosis.Condition;
                if (condition == null)
                {
                    continue;
                }

                var item = segment.GetItemComponent<SegmentItemPercentageController>(index);
                if (item != null)
                {
                    item.UpdateData(diagnosis.DisplayName, diagnosis.StatusLabel, false);
                    if (condition.CustomIconBigAssetRef != null && condition.CustomIconSmallAssetRef != null)
                    {
                        item.UpdateIcon(
                            IconManager.Instance.GetIcon(condition.CustomIconSmallAssetRef.XmlID),
                            IconManager.Instance.GetIcon(condition.CustomIconBigAssetRef.XmlID),
                            segment.m_segmentSize == SegmentSize.MEDIUM,
                            diagnosis.Color);
                    }
                    else
                    {
                        item.UpdateIcon(condition.IconIndex, segment.m_segmentSize == SegmentSize.MEDIUM, diagnosis.Color);
                    }
                }
                else if (condition.CustomIconBigAssetRef != null && condition.CustomIconSmallAssetRef != null)
                {
                    segment.SetItem(index,
                        IconManager.Instance.GetIcon(condition.CustomIconSmallAssetRef.XmlID),
                        IconManager.Instance.GetIcon(condition.CustomIconBigAssetRef.XmlID),
                        SegmentItemInteractivity.DISABLED,
                        diagnosis.DisplayName,
                        diagnosis.Color);
                }
                else
                {
                    segment.SetItem(index, condition.IconIndex, SegmentItemInteractivity.DISABLED, diagnosis.DisplayName, diagnosis.Color);
                }

                SetSegmentButtonReadOnly(segment, index);
                SetSegmentTooltipEnabled(segment, index, true);
                BindSegmentDiagnosisTooltip(segment, index, condition, insuranceIcon, insuranceCover);
            }

            segment.UpdateVisibility();
        }

        private static int GetSegmentCapacity(SegmentController segment)
        {
            if (segment == null)
            {
                return 0;
            }

            try
            {
                var items = ReflectionHelpers.GetField(segment, "m_items") as System.Collections.IList;
                if (items != null && items.Count > 0)
                {
                    return items.Count;
                }
            }
            catch
            {
            }

            try
            {
                return Math.Max(segment.m_displayedItemCount, segment.m_itemCount);
            }
            catch
            {
                return 0;
            }
        }

        private static int GetSegmentLayoutSpan(SegmentController segment, int fallback)
        {
            if (segment == null)
            {
                return Math.Max(1, fallback);
            }

            var span = segment.m_staticSegmentSpan > 0
                ? segment.m_staticSegmentSpan
                : segment.m_segmentSpan;
            return Math.Max(1, span > 0 ? span : fallback);
        }

        private static PatientPanelSnapshot GetOrBuildPatientPanelSnapshot(BehaviorPatient patient, PatientCase patientCase)
        {
            if (patient == null || patientCase == null)
            {
                return null;
            }

            if (MirrorKnownVanillaSymptomsToCase(patient, patientCase) > 0)
            {
                PersistMirroredSymptoms(patientCase);
            }

            PatientPanelSnapshot snapshot;
            var now = Time.realtimeSinceStartup;
            if (PatientPanelSnapshots.TryGetValue(patientCase.PatientEntityId, out snapshot)
                && now - snapshot.BuiltAt <= PatientPanelSnapshotTtlSeconds)
            {
                return snapshot;
            }

            snapshot = new PatientPanelSnapshot
            {
                PatientEntityId = patientCase.PatientEntityId,
                BuiltAt = now
            };

            GetAggregateSymptomCounts(patientCase, out snapshot.Known, out snapshot.Hidden, out snapshot.Treated);
            snapshot.AggregateLabel = FormatAggregateSymptomLabel(snapshot.Known, snapshot.Hidden, snapshot.Treated);
            snapshot.HiddenSummaryLabel = FormatHiddenSymptomLabel(snapshot.Hidden);
            snapshot.StatusIcon = GetCaseStatusIcon(patient, patientCase);
            snapshot.StatusLabel = GetCaseStatusLabel(patient, patientCase);
            BuildDisplayedSymptomItems(patient, patientCase, snapshot.SymptomItems);

            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (!ShouldShowDiagnosisInVanillaPanel(diagnosis))
                {
                    continue;
                }

                var condition = ResolveDiagnosis(diagnosis.DiagnosisId);
                if (condition == null)
                {
                    continue;
                }

                snapshot.Diagnoses.Add(new DiagnosisPanelItemSnapshot
                {
                    Condition = condition,
                    DisplayName = SafeGetLocalizedText(condition) ?? SafeDatabaseId(condition) ?? diagnosis.DiagnosisId,
                    StatusLabel = GetStatusLabel(diagnosis.Status) + " / " + diagnosis.SupportLabel,
                    Color = Symptom.GetSymptomColor(condition.GetMainSymptom())
                });
            }

            PatientPanelSnapshots[patientCase.PatientEntityId] = snapshot;
            return snapshot;
        }

        private static CaseWindowSnapshot GetOrBuildCaseWindowSnapshot(BehaviorPatient patient, PatientCase patientCase)
        {
            if (patientCase == null)
            {
                return null;
            }

            EnsureRuntimeModel(patient, patientCase, CaseCheckpoint.Bootstrap, "case_window");

            if (MirrorKnownVanillaSymptomsToCase(patient, patientCase) > 0)
            {
                PersistMirroredSymptoms(patientCase);
            }

            CaseWindowSnapshot snapshot;
            var now = Time.realtimeSinceStartup;
            if (CaseWindowSnapshots.TryGetValue(patientCase.PatientEntityId, out snapshot)
                && now - snapshot.BuiltAt <= CaseWindowSnapshotTtlSeconds)
            {
                return snapshot;
            }

            snapshot = new CaseWindowSnapshot
            {
                PatientEntityId = patientCase.PatientEntityId,
                BuiltAt = now,
                TitleLine = (patientCase.Hopeless ? ModText.T("MedicalCaseHopeless") + "   " : string.Empty)
                    + ModText.T("MedicalCaseRisk") + patientCase.RiskScore
                    + "   " + ModText.T("MedicalCaseStatus") + (patientCase.Complete ? ModText.T("MedicalCaseComplete") : ModText.T("MedicalCaseOpen"))
            };

            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                snapshot.DiagnosisLines.Add((i + 1) + ". "
                    + ModText.T("MedicalCaseDiagnosis")
                    + " | " + GetStatusLabel(diagnosis.Status)
                    + " | support " + diagnosis.SupportLabel
                    + " | " + ModText.T("MedicalCaseHazard") + GetHazardLabel(diagnosis.Hazard)
                    + FormatCollapseWindow(diagnosis));
                snapshot.SymptomLines.Add((i + 1) + ". " + FormatSymptoms(diagnosis) + FormatInteractionSummary(diagnosis));
            }

            var blockers = BuildCaseBlockers(patient, patientCase);
            if (patientCase.Disposition != null)
            {
                snapshot.BlockerLines.Add("- disposition: " + patientCase.Disposition.Mode + " | " + NormalizeCaseUiText(patientCase.Disposition.Reason));
                if (!string.IsNullOrEmpty(patientCase.Disposition.ReferralTradeoff))
                {
                    snapshot.BlockerLines.Add("- referral tradeoff: " + NormalizeCaseUiText(patientCase.Disposition.ReferralTradeoff));
                }
            }

            for (var i = 0; i < blockers.Count; i++)
            {
                snapshot.BlockerLines.Add("- " + blockers[i]);
            }

            if (RuntimeSettings.Config.CaseRewriteDebugLog.Value)
            {
                for (var i = Math.Max(0, patientCase.Timeline.Count - 12); i < patientCase.Timeline.Count; i++)
                {
                    var item = patientCase.Timeline[i];
                    snapshot.TimelineLines.Add("D" + item.Day + " " + item.Hour.ToString("0.0") + ": " + item.Text);
                }
            }

            CaseWindowSnapshots[patientCase.PatientEntityId] = snapshot;
            return snapshot;
        }

        private static CaseWindowSnapshot GetStableCaseWindowSnapshot(BehaviorPatient patient, PatientCase patientCase)
        {
            if (patientCase == null)
            {
                return null;
            }

            if (ActiveCaseWindowSnapshot != null
                && CaseWindowSnapshotFrame == Time.frameCount
                && CaseWindowSnapshotPatientId == patientCase.PatientEntityId)
            {
                return ActiveCaseWindowSnapshot;
            }

            ActiveCaseWindowSnapshot = GetOrBuildCaseWindowSnapshot(patient, patientCase);
            CaseWindowSnapshotFrame = Time.frameCount;
            CaseWindowSnapshotPatientId = patientCase.PatientEntityId;
            return ActiveCaseWindowSnapshot;
        }

        private static void SetSegmentHeading(SegmentController segment, string text)
        {
            if (segment == null || segment.m_heading == null || string.IsNullOrEmpty(text))
            {
                return;
            }

            var label = segment.m_heading.GetComponentInChildren<Text>();
            if (label == null)
            {
                return;
            }

            label.text = text;
            UIManager.UpdateFont(segment.m_heading);
        }

        public static void OnPatientGenerated(BehaviorPatient patient, PatientMobility mobility)
        {
            if (!Enabled || patient == null)
            {
                return;
            }

            var entity = ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            if (entity == null || Cases.ContainsKey(entity.GetEntityID()))
            {
                return;
            }

            var primary = GetPrimaryDiagnosis(patient);
            if (primary == null)
            {
                return;
            }

            var patientCase = CreateCase(patient, entity, primary, mobility);
            EnsureRuntimeModel(patient, patientCase, CaseCheckpoint.Bootstrap, "generated");
            Cases[patientCase.PatientEntityId] = patientCase;
            RegisterCurrentMedicalCondition(patient, patientCase);
            Save();
        }

        public static bool HasOpenCase(object patient)
        {
            if (!Enabled)
            {
                return false;
            }

            var behavior = patient as BehaviorPatient;
            if (behavior == null)
            {
                behavior = ReflectionHelpers.GetComponentByTypeName(patient, "Lopital.BehaviorPatient") as BehaviorPatient;
            }

            var entity = behavior == null ? patient as GLib.Entity : ReflectionHelpers.GetField(behavior, "m_entity") as GLib.Entity;
            if (entity == null)
            {
                return false;
            }

            PatientCase patientCase;
            return Cases.TryGetValue(entity.GetEntityID(), out patientCase) && patientCase != null && !patientCase.Complete;
        }

        public static bool HasCaseRecord(BehaviorPatient patient)
        {
            return GetCase(patient) != null;
        }

        public static string GetCaseProfileDepartmentId(BehaviorPatient patient)
        {
            var patientCase = GetCase(patient);
            if (patientCase == null)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(patientCase.ActiveDepartmentId))
            {
                return patientCase.ActiveDepartmentId;
            }

            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis != null && !string.IsNullOrEmpty(diagnosis.DepartmentId))
                {
                    return diagnosis.DepartmentId;
                }
            }

            return null;
        }

        public static void ApplyCollapseDeadlineMultiplier(BehaviorPatient patient, float multiplier)
        {
            if (!Enabled || patient == null || multiplier <= 0f)
            {
                return;
            }

            var patientCase = GetCase(patient);
            if (patientCase == null || patientCase.Complete)
            {
                return;
            }

            var previousMultiplier = patientCase.CollapseTimerMultiplier <= 0f ? 1f : patientCase.CollapseTimerMultiplier;
            if (previousMultiplier <= 0f)
            {
                previousMultiplier = 1f;
            }

            if (Math.Abs(previousMultiplier - multiplier) < 0.01f)
            {
                return;
            }

            var now = GetCaseClockHours();
            var changed = false;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis == null
                    || diagnosis.Status == CaseDiagnosisStatus.Treated
                    || !diagnosis.CollapseCapable
                    || diagnosis.CollapseDeadlineHours <= 0f)
                {
                    continue;
                }

                var remaining = Math.Max(0f, diagnosis.CollapseDeadlineHours - now);
                var baseRemaining = previousMultiplier > 0.01f ? remaining / previousMultiplier : remaining;
                diagnosis.CollapseDeadlineHours = now + Math.Max(0f, baseRemaining * multiplier);
                changed = true;
            }

            patientCase.CollapseTimerMultiplier = multiplier;

            if (changed)
            {
                Save();
            }
        }

        public static void SyncKnownVanillaSymptoms(BehaviorPatient patient)
        {
            var patientCase = GetCase(patient);
            if (patientCase == null || patientCase.Complete)
            {
                return;
            }

            var changed = ProcessMonitoringRevealEvents(patient, patientCase);
            if (MirrorKnownVanillaSymptomsToCase(patient, patientCase) > 0)
            {
                changed = true;
            }

            if (changed)
            {
                EnsureRuntimeModel(patient, patientCase, CaseCheckpoint.Monitoring, "monitoring_sync");
                Save();
            }
        }

        public static bool HasCurrentDepartmentDoctorWork(BehaviorPatient patient)
        {
            var patientCase = GetCase(patient);
            if (patientCase == null || patientCase.Complete || patient == null)
            {
                return false;
            }

            var departmentId = patient.GetDepartment() == null || patient.GetDepartment().GetDepartmentType() == null
                ? patientCase.ActiveDepartmentId
                : patient.GetDepartment().GetDepartmentType().DatabaseID.ToString();
            if (string.IsNullOrEmpty(departmentId))
            {
                return false;
            }

            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis == null
                    || diagnosis.Status == CaseDiagnosisStatus.Treated
                    || !string.Equals(diagnosis.DepartmentId, departmentId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (diagnosis.Status == CaseDiagnosisStatus.Hidden
                    || diagnosis.Status == CaseDiagnosisStatus.Suspected
                    || diagnosis.Status == CaseDiagnosisStatus.Diagnosed)
                {
                    return true;
                }

                if (diagnosis.KnownSymptomIds.Count < diagnosis.SymptomIds.Count
                    || diagnosis.TreatedSymptomIds.Count < diagnosis.KnownSymptomIds.Count)
                {
                    return true;
                }
            }

            return false;
        }

        public static string GetTraceSummary(BehaviorPatient patient)
        {
            var patientCase = GetCase(patient);
            if (patientCase == null)
            {
                return "none";
            }

            var hidden = 0;
            var suspected = 0;
            var diagnosed = 0;
            var treated = 0;
            var active = 0;
            var knownSymptoms = 0;
            var totalSymptoms = 0;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis == null)
                {
                    continue;
                }

                totalSymptoms += diagnosis.SymptomIds.Count;
                knownSymptoms += diagnosis.KnownSymptomIds.Count;
                switch (diagnosis.Status)
                {
                    case CaseDiagnosisStatus.Active:
                        active++;
                        break;
                    case CaseDiagnosisStatus.Hidden:
                        hidden++;
                        break;
                    case CaseDiagnosisStatus.Suspected:
                        suspected++;
                        break;
                    case CaseDiagnosisStatus.Diagnosed:
                        diagnosed++;
                        break;
                    case CaseDiagnosisStatus.Treated:
                        treated++;
                        break;
                }
            }

            PendingDiagnosticFocus pending;
            var entity = ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            var pendingText = entity != null && PendingDiagnosticFocuses.TryGetValue(entity.GetEntityID(), out pending) && pending != null
                ? pending.DepartmentId
                : "-";
            var focus = GetCurrentDiagnosticFocusDiagnosis(patient, patientCase);
            var focusText = focus == null ? "-" : focus.DiagnosisId + ":" + focus.Status;

            return string.Format(CultureInfo.InvariantCulture,
                "open={0},complete={1},dept={2},risk={3},dx={4},active={5},hidden={6},suspected={7},diagnosed={8},treated={9},known={10}/{11},focus={12},pending={13},disposition={14},slice={15}",
                !patientCase.Complete,
                patientCase.Complete,
                string.IsNullOrEmpty(patientCase.ActiveDepartmentId) ? "-" : patientCase.ActiveDepartmentId,
                patientCase.RiskScore,
                patientCase.Diagnoses.Count,
                active,
                hidden,
                suspected,
                diagnosed,
                treated,
                knownSymptoms,
                totalSymptoms,
                focusText,
                pendingText,
                patientCase.Disposition == null ? "-" : patientCase.Disposition.Mode.ToString(),
                patientCase.MaterializedSlice == null ? 0 : patientCase.MaterializedSlice.Version);
        }

        internal static string GetActiveDepartmentIdForTrace(BehaviorPatient patient)
        {
            var patientCase = GetCase(patient);
            return patientCase == null || string.IsNullOrEmpty(patientCase.ActiveDepartmentId)
                ? "-"
                : patientCase.ActiveDepartmentId;
        }

        internal static string GetRequestedHospitalizationTreatmentIdForTrace(BehaviorPatient patient)
        {
            var id = GetRequestedHospitalizationTreatmentId(patient);
            return string.IsNullOrEmpty(id) ? "-" : id;
        }

        internal static string BuildDetailedCaseTrace(BehaviorPatient patient)
        {
            var patientCase = GetCase(patient);
            if (patient == null || patientCase == null)
            {
                return "case_exists=false";
            }

            EnsureRuntimeModel(patient, patientCase, CaseCheckpoint.Bootstrap, "trace");
            var runtimeDepartmentId = GetCaseRuntimeDepartmentId(patient, patientCase);
            var focusDiagnosis = GetCurrentDiagnosticFocusDiagnosis(patient, patientCase);
            var nextDiagnosis = CaseCarePlanner.SelectNextDiagnosis(patientCase, runtimeDepartmentId);
            PendingDiagnosticFocus pending;
            var entity = ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            var pendingDepartmentId = entity != null && PendingDiagnosticFocuses.TryGetValue(entity.GetEntityID(), out pending) && pending != null
                ? pending.DepartmentId
                : "-";
            var builder = new StringBuilder(640);
            builder.Append("case_exists=true");
            builder.Append(";case_id=").Append(string.IsNullOrEmpty(patientCase.CaseId) ? "-" : patientCase.CaseId);
            builder.Append(";case_complete=").Append(patientCase.Complete);
            builder.Append(";case_hopeless=").Append(patientCase.Hopeless);
            builder.Append(";risk_score=").Append(patientCase.RiskScore.ToString(CultureInfo.InvariantCulture));
            builder.Append(";runtime_department=").Append(string.IsNullOrEmpty(runtimeDepartmentId) ? "-" : runtimeDepartmentId);
            builder.Append(";active_department=").Append(string.IsNullOrEmpty(patientCase.ActiveDepartmentId) ? "-" : patientCase.ActiveDepartmentId);
            builder.Append(";current_focus=").Append(FormatDiagnosisFocusTrace(focusDiagnosis));
            builder.Append(";pending_focus=").Append(string.IsNullOrEmpty(pendingDepartmentId) ? "-" : pendingDepartmentId);
            builder.Append(";next_diagnosis=").Append(FormatDiagnosisFocusTrace(nextDiagnosis));
            builder.Append(";materialized_cluster=").Append(string.IsNullOrEmpty(patientCase.MaterializedSlice.ClusterId) ? "-" : patientCase.MaterializedSlice.ClusterId);
            builder.Append(";materialized_version=").Append(patientCase.MaterializedSlice.Version.ToString(CultureInfo.InvariantCulture));
            builder.Append(";visible_problem_ids=").Append(FormatTraceList(patientCase.MaterializedSlice.VisibleProblemIds));
            builder.Append(";active_bindings=").Append(FormatMaterializedBindingsTrace(patientCase));
            builder.Append(";support=").Append(FormatSupportTraceList(patientCase));
            builder.Append(";interactions=").Append(FormatInteractionsTrace(patientCase));
            builder.Append(";disposition=").Append(patientCase.Disposition == null ? "-" : patientCase.Disposition.Mode.ToString());
            builder.Append(";disposition_reason=").Append(NormalizeCaseTraceValue(patientCase.Disposition == null ? null : patientCase.Disposition.Reason));
            builder.Append(";referral_tradeoff=").Append(NormalizeCaseTraceValue(patientCase.Disposition == null ? null : patientCase.Disposition.ReferralTradeoff));
            builder.Append(";diagnoses=").Append(FormatDiagnosisTraceList(patientCase.Diagnoses));
            return builder.ToString();
        }

        internal static string BuildUiTraceSnapshot(BehaviorPatient patient, string source)
        {
            if (patient == null)
            {
                return "source=" + (string.IsNullOrEmpty(source) ? "-" : source) + ";case_exists=false";
            }

            var entity = ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            var patientCase = GetOrCreateCompatibilityCase(patient, entity);
            if (patientCase == null)
            {
                return "source=" + (string.IsNullOrEmpty(source) ? "-" : source) + ";case_exists=false";
            }

            EnsureRuntimeModel(patient, patientCase, CaseCheckpoint.Bootstrap, "ui_trace");
            var snapshot = GetOrBuildPatientPanelSnapshot(patient, patientCase);
            var blockers = BuildCaseBlockers(patient, patientCase);
            var skipVanilla = entity != null && ShouldSkipVanillaDiagnosisPanel(entity);
            var visibleDiagnosisCount = snapshot == null ? 0 : snapshot.Diagnoses.Count;
            var hiddenDiagnosisCount = CountHiddenDiagnosesForUi(patientCase);
            var builder = new StringBuilder(512);
            builder.Append("source=").Append(string.IsNullOrEmpty(source) ? "-" : source);
            builder.Append(";case_exists=true");
            builder.Append(";selected=").Append(ReferenceEquals(patient, SelectedPatient));
            builder.Append(";aggregate_symptom_counts=");
            if (snapshot == null)
            {
                builder.Append("known:0,hidden:0,treated:0");
            }
            else
            {
                builder.Append("known:").Append(snapshot.Known)
                    .Append(",hidden:").Append(snapshot.Hidden)
                    .Append(",treated:").Append(snapshot.Treated);
            }

            builder.Append(";visible_diagnosis_items=").Append(FormatUiDiagnosisItemsTrace(snapshot));
            builder.Append(";hidden_diagnosis_count=").Append(hiddenDiagnosisCount.ToString(CultureInfo.InvariantCulture));
            builder.Append(";case_status_label=").Append(snapshot == null || string.IsNullOrEmpty(snapshot.StatusLabel) ? "-" : snapshot.StatusLabel);
            builder.Append(";blockers=").Append(FormatTraceList(blockers));
            builder.Append(";hidden_summary_text=").Append(snapshot == null || string.IsNullOrEmpty(snapshot.HiddenSummaryLabel) ? "-" : snapshot.HiddenSummaryLabel);
            builder.Append(";skip_vanilla_diagnosis_panel=").Append(skipVanilla);
            builder.Append(";visible_diagnosis_count=").Append(visibleDiagnosisCount.ToString(CultureInfo.InvariantCulture));
            builder.Append(";case_diagnosis_count=").Append(patientCase.Diagnoses.Count.ToString(CultureInfo.InvariantCulture));
            builder.Append(";support=").Append(FormatSupportTraceList(patientCase));
            builder.Append(";interactions=").Append(FormatInteractionsTrace(patientCase));
            builder.Append(";disposition=").Append(patientCase.Disposition == null ? "-" : patientCase.Disposition.Mode.ToString());
            builder.Append(";disposition_reason=").Append(NormalizeCaseTraceValue(patientCase.Disposition == null ? null : patientCase.Disposition.Reason));
            builder.Append(";referral_tradeoff=").Append(NormalizeCaseTraceValue(patientCase.Disposition == null ? null : patientCase.Disposition.ReferralTradeoff));
            builder.Append(";materialized_bindings=").Append(FormatMaterializedBindingsTrace(patientCase));
            return builder.ToString();
        }

        internal static string GetUiRuntimeDesyncReason(BehaviorPatient patient)
        {
            if (patient == null)
            {
                return null;
            }

            var entity = ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            var patientCase = GetCase(patient);
            if (entity == null || patientCase == null || patientCase.Complete)
            {
                return null;
            }

            var snapshot = GetOrBuildPatientPanelSnapshot(patient, patientCase);
            var blockers = BuildCaseBlockers(patient, patientCase);
            var blockedState = patient.m_state != null
                && (patient.m_state.m_patientState == PatientState.BlockedByAmbiguousResults
                    || patient.m_state.m_patientState == PatientState.BlockedByComplicatedDiagnosis
                    || patient.m_state.m_patientState == PatientState.BlockedByNoTreatment);
            if (!ShouldSkipVanillaDiagnosisPanel(entity))
            {
                return "custom_diagnosis_panel_not_applied";
            }

            if (blockedState && blockers.Count == 0 && (snapshot == null || snapshot.Diagnoses.Count == 0))
            {
                return "blocked_runtime_state_without_ui_blockers";
            }

            return null;
        }

        private static int CountHiddenDiagnosesForUi(PatientCase patientCase)
        {
            if (patientCase == null)
            {
                return 0;
            }

            var hidden = 0;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                if (!ShouldShowDiagnosisInVanillaPanel(patientCase.Diagnoses[i]))
                {
                    hidden++;
                }
            }

            return hidden;
        }

        private static string FormatUiDiagnosisItemsTrace(PatientPanelSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Diagnoses.Count == 0)
            {
                return "[]";
            }

            var builder = new StringBuilder(snapshot.Diagnoses.Count * 32);
            builder.Append("[");
            for (var i = 0; i < snapshot.Diagnoses.Count; i++)
            {
                var diagnosis = snapshot.Diagnoses[i];
                if (i > 0)
                {
                    builder.Append("|");
                }

                builder.Append(diagnosis == null || diagnosis.Condition == null ? "-" : diagnosis.Condition.DatabaseID.ToString());
                builder.Append("{status=").Append(diagnosis == null || string.IsNullOrEmpty(diagnosis.StatusLabel) ? "-" : diagnosis.StatusLabel);
                builder.Append(",name=").Append(diagnosis == null || string.IsNullOrEmpty(diagnosis.DisplayName) ? "-" : diagnosis.DisplayName);
                builder.Append("}");
            }

            builder.Append("]");
            return builder.ToString();
        }

        private static string FormatDiagnosisTraceList(List<CaseDiagnosis> diagnoses)
        {
            if (diagnoses == null || diagnoses.Count == 0)
            {
                return "[]";
            }

            var builder = new StringBuilder(diagnoses.Count * 80);
            builder.Append("[");
            for (var i = 0; i < diagnoses.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append("|");
                }

                builder.Append(FormatDiagnosisTrace(diagnoses[i]));
            }

            builder.Append("]");
            return builder.ToString();
        }

        private static string FormatDiagnosisTrace(CaseDiagnosis diagnosis)
        {
            if (diagnosis == null)
            {
                return "-";
            }

            var builder = new StringBuilder(160);
            builder.Append(diagnosis.DiagnosisId);
            builder.Append("{dept=").Append(string.IsNullOrEmpty(diagnosis.DepartmentId) ? "-" : diagnosis.DepartmentId);
            builder.Append(",cluster=").Append(string.IsNullOrEmpty(diagnosis.OwningClusterId) ? "-" : diagnosis.OwningClusterId);
            builder.Append(",status=").Append(diagnosis.Status);
            builder.Append(",support=").Append(diagnosis.SupportLabel);
            builder.Append(",certainty=").Append(diagnosis.Certainty.ToString("0.00", CultureInfo.InvariantCulture));
            builder.Append(",hazard=").Append(string.IsNullOrEmpty(diagnosis.Hazard) ? "-" : diagnosis.Hazard);
            builder.Append(",collapse=").Append(diagnosis.CollapseCapable);
            builder.Append(",hospitalization=").Append(diagnosis.NeedsHospitalization);
            builder.Append(",can_not_talk=").Append(diagnosis.CanNotTalk);
            builder.Append(",mobility=").Append(diagnosis.Mobility);
            builder.Append(",known=").Append(FormatTraceList(diagnosis.KnownSymptomIds));
            builder.Append(",symptoms=").Append(FormatTraceList(diagnosis.SymptomIds));
            builder.Append(",treated=").Append(FormatTraceList(diagnosis.TreatedSymptomIds));
            builder.Append("}");
            return builder.ToString();
        }

        private static string FormatDiagnosisFocusTrace(CaseDiagnosis diagnosis)
        {
            return diagnosis == null
                ? "-"
                : diagnosis.DiagnosisId + ":" + diagnosis.Status + ":" + (string.IsNullOrEmpty(diagnosis.DepartmentId) ? "-" : diagnosis.DepartmentId);
        }

        private static string FormatTraceList(List<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return "[]";
            }

            return "[" + string.Join("|", values.ToArray()) + "]";
        }

        private static string FormatSupportTraceList(PatientCase patientCase)
        {
            if (patientCase == null || patientCase.Diagnoses.Count == 0)
            {
                return "[]";
            }

            var values = new List<string>();
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis == null)
                {
                    continue;
                }

                values.Add(diagnosis.DiagnosisId + ":" + diagnosis.SupportLabel + ":" + diagnosis.Certainty.ToString("0.00", CultureInfo.InvariantCulture));
            }

            return FormatTraceList(values);
        }

        private static string FormatInteractionsTrace(PatientCase patientCase)
        {
            if (patientCase == null || patientCase.ActiveInteractions.Count == 0)
            {
                return "[]";
            }

            var values = new List<string>();
            for (var i = 0; i < patientCase.ActiveInteractions.Count; i++)
            {
                var interaction = patientCase.ActiveInteractions[i];
                if (interaction == null)
                {
                    continue;
                }

                values.Add(interaction.Kind + ":" + interaction.SourceProblemId + ">" + interaction.TargetProblemId);
            }

            return FormatTraceList(values);
        }

        private static string FormatMaterializedBindingsTrace(PatientCase patientCase)
        {
            if (patientCase == null || patientCase.MaterializedSlice == null || patientCase.MaterializedSlice.Bindings.Count == 0)
            {
                return "[]";
            }

            var values = new List<string>();
            for (var i = 0; i < patientCase.MaterializedSlice.Bindings.Count; i++)
            {
                var binding = patientCase.MaterializedSlice.Bindings[i];
                if (binding == null)
                {
                    continue;
                }

                values.Add(binding.Kind + ":" + binding.BoundId);
            }

            return FormatTraceList(values);
        }

        private static string NormalizeCaseTraceValue(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "-";
            }

            return value.Replace(";", ",").Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
        }

        private static string BuildTraceDecisionContext(BehaviorPatient patient, PatientCase patientCase)
        {
            var runtimeDepartmentId = patientCase == null ? "-" : GetCaseRuntimeDepartmentId(patient, patientCase);
            var focusDiagnosis = patientCase == null ? null : GetCurrentDiagnosticFocusDiagnosis(patient, patientCase);
            PendingDiagnosticFocus pending;
            var entity = patient == null ? null : ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            var pendingDepartmentId = entity != null && PendingDiagnosticFocuses.TryGetValue(entity.GetEntityID(), out pending) && pending != null
                ? pending.DepartmentId
                : "-";
            return "patient_state=" + (patient == null || patient.m_state == null ? "-" : patient.m_state.m_patientState.ToString())
                + ";runtime_department=" + (string.IsNullOrEmpty(runtimeDepartmentId) ? "-" : runtimeDepartmentId)
                + ";active_department=" + (patientCase == null || string.IsNullOrEmpty(patientCase.ActiveDepartmentId) ? "-" : patientCase.ActiveDepartmentId)
                + ";focus_diagnosis=" + FormatDiagnosisFocusTrace(focusDiagnosis)
                + ";pending_focus=" + (string.IsNullOrEmpty(pendingDepartmentId) ? "-" : pendingDepartmentId)
                + ";case_summary=" + GetTraceSummary(patient);
        }

        public static bool IsCaseTreated(BehaviorPatient patient)
        {
            var patientCase = GetCase(patient);
            if (patientCase == null)
            {
                return true;
            }

            if (patientCase.Complete)
            {
                return true;
            }

            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                if (patientCase.Diagnoses[i].Status != CaseDiagnosisStatus.Treated)
                {
                    return false;
                }
            }

            patientCase.Complete = true;
            AddTimeline(patientCase, "Case completed by treated-state adapter.");
            Save();
            return true;
        }

        public static bool ShouldAllowLeave(BehaviorPatient patient, bool pay, bool leaveAfterHours)
        {
            var patientCase = GetCase(patient);
            if (patientCase == null || patientCase.Complete || patient == null || patient.m_state == null)
            {
                return true;
            }

            if (patient.m_state.m_deathTriggered)
            {
                MarkDead(patient);
                return true;
            }

            if (patient.m_state.m_sentAway)
            {
                MarkReferred(patient, "sent away before vanilla leave");
                return true;
            }

            if (leaveAfterHours)
            {
                MarkReferred(patient, "left after closing hours");
                return true;
            }

            if (!pay)
            {
                MarkReferred(patient, "vanilla no-payment leave");
                return true;
            }

            if (IsCaseFullyTreated(patientCase))
            {
                patientCase.Complete = true;
                AddTimeline(patientCase, "Case completed before vanilla leave.");
                Save();
                return true;
            }

            var allowDischarge = TryAdvanceBeforeDischarge(patient);
            if (allowDischarge)
            {
                return true;
            }

            if (patient.m_state.m_sentHome)
            {
                patient.m_state.m_sentHome = false;
            }

            return false;
        }

        public static void MarkReferred(BehaviorPatient patient, string reason)
        {
            var patientCase = GetCase(patient);
            if (patientCase == null || patientCase.Complete)
            {
                return;
            }

            patientCase.Complete = true;
            AddTimeline(patientCase, "Case referred to another hospital: " + reason + ".");
            Save();
        }

        public static void MarkDead(BehaviorPatient patient)
        {
            var patientCase = GetCase(patient);
            if (patientCase == null || patientCase.Complete)
            {
                return;
            }

            patientCase.Complete = true;
            AddTimeline(patientCase, "Case closed: patient died.");
            Save();
        }

        public static void MarkLeaving(BehaviorPatient patient)
        {
            if (!Enabled || patient == null || patient.m_state == null)
            {
                return;
            }

            if (patient.m_state.m_deathTriggered)
            {
                MarkDead(patient);
            }
            else if (patient.m_state.m_sentAway)
            {
                MarkReferred(patient, "vanilla sent-away state");
            }
            else if (patient.m_state.m_sentHome && IsCaseTreated(patient))
            {
                MarkCompletedByVanillaDischarge(patient);
            }
        }

        public static void MarkManualPanelTransfer(object controller)
        {
            if (!Enabled || controller == null)
            {
                return;
            }

            var character = ReflectionHelpers.GetField(controller, "m_character");
            var entityMethod = character == null ? null : character.GetType().GetMethod("GetEntity", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            var entity = entityMethod == null ? null : entityMethod.Invoke(character, null) as GLib.Entity;
            var patient = entity == null ? null : entity.GetComponent<BehaviorPatient>();
            var patientCase = GetCase(patient);
            if (patientCase == null || patientCase.Complete || IsCaseFullyTreated(patientCase))
            {
                return;
            }

            MarkReferred(patient, "manual patient panel transfer");
        }

        public static void ForgetCaseForDeveloper(BehaviorPatient patient)
        {
            if (patient == null)
            {
                return;
            }

            var entity = ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            if (entity == null)
            {
                return;
            }

            var patientId = entity.GetEntityID();
            PatientCase patientCase;
            if (Cases.TryGetValue(patientId, out patientCase))
            {
                Cases.Remove(patientId);
                if (ReferenceEquals(SelectedCase, patientCase))
                {
                    SelectedCase = null;
                    SelectedPatient = null;
                    ShowCaseWindow = false;
                    ActiveCaseWindowSnapshot = null;
                    CaseWindowSnapshotFrame = -1;
                    CaseWindowSnapshotPatientId = 0;
                }
            }

            PatientPanelSnapshots.Remove(patientId);
            CaseWindowSnapshots.Remove(patientId);
            PendingDiagnosticFocuses.Remove(patientId);
            BlockedCaseRetryUntil.Remove(patientId);

            var condition = patient.m_state == null ? null : patient.m_state.m_medicalCondition;
            if (condition != null)
            {
                ConditionCases.Remove(condition);
            }

            var conditionKeys = new List<object>();
            foreach (var pair in ConditionCases)
            {
                if (ReferenceEquals(pair.Value, patientCase))
                {
                    conditionKeys.Add(pair.Key);
                }
            }

            for (var i = 0; i < conditionKeys.Count; i++)
            {
                ConditionCases.Remove(conditionKeys[i]);
            }

            Save();
        }

        public static int GetCaseInsurancePayment(BehaviorPatient patient, int percent)
        {
            return GetCaseInsurancePaymentInternal(patient, percent, includeUndiagnosed: true);
        }

        public static int GetVisibleCaseInsurancePayment(BehaviorPatient patient, int percent)
        {
            return GetCaseInsurancePaymentInternal(patient, percent, includeUndiagnosed: false);
        }

        private static int GetCaseInsurancePaymentInternal(BehaviorPatient patient, int percent, bool includeUndiagnosed)
        {
            var patientCase = GetCase(patient);
            if (patientCase == null)
            {
                return 0;
            }

            var basePayment = 0;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis == null)
                {
                    continue;
                }

                if (!includeUndiagnosed
                    && diagnosis.Status != CaseDiagnosisStatus.Diagnosed
                    && diagnosis.Status != CaseDiagnosisStatus.Treated)
                {
                    continue;
                }

                var condition = ResolveDiagnosis(diagnosis.DiagnosisId);
                if (condition != null)
                {
                    basePayment += Math.Max(0, condition.InsurancePayment);
                }
            }

            if (basePayment <= 0)
            {
                return 0;
            }

            if (percent <= 0)
            {
                return 0;
            }

            if (percent > 100)
            {
                percent = 100;
            }

            var prestige = Hospital.Instance == null ? 0 : Hospital.Instance.GetPrestigeInsurancePaymentModifierLastDay();
            var cover = 0;
            var personalInfo = patient.GetComponent<CharacterPersonalInfoComponent>();
            if (personalInfo != null
                && personalInfo.m_personalInfo != null
                && personalInfo.m_personalInfo.m_insuranceCompany != null
                && personalInfo.m_personalInfo.m_insuranceCompany.Entry != null)
            {
                cover = personalInfo.m_personalInfo.m_insuranceCompany.Entry.CoverCostPercent;
            }

            var world = WorldEventManager.Instance == null ? 0f : WorldEventManager.Instance.GetInsurancePaymentModifier();
            var paymentPercent = Database.Instance == null ? 1f : (float)Database.Instance.GetEntry<GameDBTweakableInt>("TWEAKABLE_PATIENT_PAYMENTS_PERCENT").Value / 100f;
            var multiplier = 100 + prestige + cover + world;
            return Math.Max(0, (int)((float)basePayment * paymentPercent * multiplier / 100f) * percent / 100);
        }

        public static void MarkDiagnosed(BehaviorPatient patient)
        {
            var patientCase = GetCase(patient);
            if (patientCase == null || patientCase.Complete)
            {
                return;
            }

            var diagnosed = patient.m_state == null || patient.m_state.m_medicalCondition == null || patient.m_state.m_medicalCondition.m_diagnosedMedicalCondition == null
                ? null
                : patient.m_state.m_medicalCondition.m_diagnosedMedicalCondition.Entry;
            var diagnosedId = diagnosed == null ? null : diagnosed.DatabaseID.ToString();
            var changed = false;
            var activeDepartmentId = patientCase.ActiveDepartmentId;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                if (!string.IsNullOrEmpty(diagnosedId) && patientCase.Diagnoses[i].DiagnosisId == diagnosedId)
                {
                    if (patientCase.Diagnoses[i].Status != CaseDiagnosisStatus.Diagnosed)
                    {
                        patientCase.Diagnoses[i].Status = CaseDiagnosisStatus.Diagnosed;
                        changed = true;
                    }
                }
            }

            var sameVisitContinuationScheduled = TryContinueSameDepartmentOfficeDiagnostics(patient, patientCase, activeDepartmentId, diagnosedId);

            if (changed)
            {
                if (sameVisitContinuationScheduled)
                {
                    ClearPendingDiagnosticFocus(patient);
                }
                else if (HasUndiagnosedDiagnosisInDepartment(patientCase, activeDepartmentId, diagnosedId))
                {
                    QueueDiagnosticFocusAdvanceWithinDepartment(patient, patientCase);
                }
                else
                {
                    ClearPendingDiagnosticFocus(patient);
                }

                AddTimeline(patientCase, string.IsNullOrEmpty(diagnosedId) ? "Diagnosis attempt updated case suspicion." : "Diagnosis updated by doctor.");
                Save();
            }
            else if (sameVisitContinuationScheduled)
            {
                AddTimeline(patientCase, "Same-visit diagnostic continuation scheduled in current office.");
                Save();
            }
        }

        public static void HandleDiagnosisResult(BehaviorPatient patient, DiagnosisResult result)
        {
            if (!Enabled || patient == null || patient.m_state == null || patient.GetControlMode() != PatientControlMode.AI)
            {
                return;
            }

            var patientCase = GetCase(patient);
            if (patientCase == null || patientCase.Complete || IsCaseFullyTreated(patientCase))
            {
                return;
            }

            if (result != DiagnosisResult.COMPLICATED)
            {
                return;
            }

            try
            {
                var procedure = patient.GetComponent<ProcedureComponent>();
                if (procedure == null || procedure.m_state == null || procedure.m_state.m_procedureQueue == null)
                {
                    return;
                }

                if (procedure.m_state.m_procedureQueue.m_plannedExaminationStates.Count > 0
                    || procedure.m_state.m_procedureQueue.m_labProcedures.Count > 0)
                {
                    return;
                }

                var trySchedule = AccessTools.Method(typeof(BehaviorPatient), "TryToScheduleExamination", new[] { typeof(bool) });
                var scheduled = trySchedule != null && Equals(trySchedule.Invoke(patient, new object[] { true }), true);
                if (!scheduled)
                {
                    scheduled = TryScheduleCaseAwareExamination(patient);
                }

                if (scheduled)
                {
                    var currentCase = GetCase(patient);
                    if (currentCase != null)
                    {
                        AddTimeline(currentCase, "Complicated diagnosis recovered by scheduling another examination.");
                        Save();
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Failed to recover from complicated diagnosis: " + DescribeException(ex));
            }
        }

        public static void ProcessPendingDiagnosticFocus(BehaviorPatient patient)
        {
            if (!Enabled || patient == null || patient.m_state == null)
            {
                return;
            }

            var entity = ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            if (entity == null)
            {
                return;
            }

            PendingDiagnosticFocus pending;
            if (!PendingDiagnosticFocuses.TryGetValue(entity.GetEntityID(), out pending) || pending == null)
            {
                return;
            }

            var state = patient.m_state.m_patientState;
            if (state == PatientState.BeingExamined
                || state == PatientState.BeingTreated
                || state == PatientState.Leaving
                || state == PatientState.Left
                || state == PatientState.Collapsing
                || state == PatientState.Dead)
            {
                return;
            }

            var procedure = patient.GetComponent<ProcedureComponent>();
            var queue = procedure == null || procedure.m_state == null ? null : procedure.m_state.m_procedureQueue;
            if (queue != null && (queue.m_activeExamination != null || queue.m_activeTreatmentStates.Count > 0))
            {
                return;
            }

            if (patient.m_state.m_doctor != null
                && state != PatientState.GoingToDoctor
                && state != PatientState.WaitingBeingCalled
                && state != PatientState.Idle)
            {
                return;
            }

            if (state != PatientState.GoingToWaitingRoom
                && state != PatientState.WaitingGoingToChair
                && state != PatientState.WaitingSitting
                && state != PatientState.WaitingStandingIdle
                && state != PatientState.WaitingBeingCalled
                && state != PatientState.BlockedByAmbiguousResults
                && state != PatientState.BlockedByComplicatedDiagnosis
                && state != PatientState.Idle)
            {
                return;
            }

            var patientCase = GetCase(patient);
            if (patientCase == null || patientCase.Complete)
            {
                PendingDiagnosticFocuses.Remove(entity.GetEntityID());
                return;
            }

            if (!TryAdvanceDiagnosticFocusWithinDepartment(patient, patientCase, pending.DepartmentId))
            {
                TraceLoggingService.LogPatientAction(
                    patient,
                    "ProcessPendingDiagnosticFocus",
                    "failed",
                    BuildTraceDecisionContext(patient, patientCase)
                    + ";pending_department=" + pending.DepartmentId
                    + ";reason=advance_failed_trying_resume_or_referral");
                PendingDiagnosticFocuses.Remove(entity.GetEntityID());
                if (!TryResumeOrReferAfterFailedDiagnosticAdvance(patient))
                {
                    TryResumeCaseProcedureSelection(patient);
                }

                return;
            }

            TraceLoggingService.LogPatientAction(
                patient,
                "ProcessPendingDiagnosticFocus",
                "advanced",
                BuildTraceDecisionContext(patient, patientCase)
                + ";pending_department=" + pending.DepartmentId);
            PendingDiagnosticFocuses.Remove(entity.GetEntityID());
        }

        public static void RecoverStalledDoctorHandoff(BehaviorPatient patient)
        {
            if (!Enabled || patient == null || patient.m_state == null || !HasOpenCase(patient))
            {
                return;
            }

            var state = patient.m_state.m_patientState;
            if (state != PatientState.GoingToDoctor
                && state != PatientState.WaitingBeingCalled
                && state != PatientState.WaitingSitting
                && state != PatientState.WaitingStandingIdle
                && state != PatientState.Idle)
            {
                return;
            }

            var procedure = patient.GetComponent<ProcedureComponent>();
            var queue = procedure == null || procedure.m_state == null ? null : procedure.m_state.m_procedureQueue;
            if (procedure == null || queue == null)
            {
                return;
            }

            if (queue.m_activeExamination != null
                || queue.m_plannedExaminationStates.Count > 0
                || queue.m_labProcedures.Count > 0
                || queue.m_activeTreatmentStates.Count > 0
                || queue.m_plannedTreatmentStates.Count > 0)
            {
                return;
            }

            var patientCase = GetCase(patient);
            var focusDiagnosis = GetCurrentDiagnosticFocusDiagnosis(patient, patientCase);
            var focusCondition = focusDiagnosis == null ? null : ResolveDiagnosis(focusDiagnosis.DiagnosisId);
            if (focusDiagnosis != null && focusCondition != null && HasAvailableDiagnosticWorkForDiagnosis(patient, procedure, queue, focusDiagnosis, focusCondition))
            {
                if (patient.m_state.m_doctor == null)
                {
                    TraceLoggingService.LogRateLimitedPatientEvent(
                        patient,
                        "ACTION",
                        "event=action;method=RecoverStalledDoctorHandoff;outcome=waiting_for_doctor_assignment;details="
                        + BuildTraceDecisionContext(patient, patientCase)
                        + ";focus_diagnosis=" + focusDiagnosis.DiagnosisId,
                        1.0f);
                    var checkDoctor = AccessTools.Method(typeof(BehaviorPatient), "CheckDoctorForDiagnosis", Type.EmptyTypes);
                    if (checkDoctor != null)
                    {
                        checkDoctor.Invoke(patient, null);
                    }
                }
                else
                {
                    var doctorEntity = patient.m_state.m_doctor.CheckEntity() ? patient.m_state.m_doctor.GetEntity() : null;
                    var doctor = doctorEntity == null ? null : doctorEntity.GetComponent<BehaviorDoctor>();
                    if (doctor != null && doctor.IsFree())
                    {
                        TraceLoggingService.LogPatientAnomaly(
                            patient,
                            "patient_waiting_doctor_but_doctor_idle",
                            BuildTraceDecisionContext(patient, patientCase)
                            + ";doctor_id=" + doctorEntity.GetEntityID().ToString(CultureInfo.InvariantCulture)
                            + ";focus_diagnosis=" + focusDiagnosis.DiagnosisId);
                    }
                }

                return;
            }

            if (TryResumeOrReferAfterFailedDiagnosticAdvance(patient))
            {
                TraceLoggingService.LogPatientAction(patient, "RecoverStalledDoctorHandoff", "recovered", BuildTraceDecisionContext(patient, patientCase));
                return;
            }

            TraceLoggingService.LogPatientAction(patient, "RecoverStalledDoctorHandoff", "failed", BuildTraceDecisionContext(patient, patientCase) + ";reason=no_focus_work_and_no_recovery_route");
        }

        private static bool TryResumeOrReferAfterFailedDiagnosticAdvance(BehaviorPatient patient)
        {
            if (patient == null || patient.m_state == null)
            {
                TraceLoggingService.LogPatientAction(patient, "TryResumeOrReferAfterFailedDiagnosticAdvance", "failed", "reason=missing_patient_state");
                return false;
            }

            if (!CanRetryBlockedCaseRecovery(patient))
            {
                TraceLoggingService.LogPatientAction(patient, "TryResumeOrReferAfterFailedDiagnosticAdvance", "failed", "reason=recovery_cooldown_active");
                return false;
            }

            var patientCase = GetCase(patient);
            var hasTreatmentOrProgress = HasCaseAvailableTreatmentOrProgress(patient);
            if (hasTreatmentOrProgress)
            {
                TryResumeCaseProcedureSelection(patient);
                TraceLoggingService.LogPatientAction(patient, "TryResumeOrReferAfterFailedDiagnosticAdvance", "recovered", BuildTraceDecisionContext(patient, patientCase) + ";recovery_step=resume_vanilla_procedure_selection");
                return true;
            }

            if (TryAdvanceCaseTransferOrHospitalization(patient, "failed_diagnostic_advance"))
            {
                TraceLoggingService.LogPatientAction(patient, "TryResumeOrReferAfterFailedDiagnosticAdvance", "transferred", BuildTraceDecisionContext(patient, patientCase) + ";recovery_step=transfer_or_hospitalization");
                return true;
            }

            if (TryResumeTreatableDiagnosedCase(patient))
            {
                TraceLoggingService.LogPatientAction(patient, "TryResumeOrReferAfterFailedDiagnosticAdvance", "scheduled", BuildTraceDecisionContext(patient, patientCase) + ";recovery_step=resume_treatable_diagnosed_case");
                return true;
            }

            TraceLoggingService.LogPatientAction(patient, "TryResumeOrReferAfterFailedDiagnosticAdvance", "fallback_referral", BuildTraceDecisionContext(patient, patientCase) + ";recovery_step=referral");
            return TryReferBlockedCase(patient, "TryResumeOrReferAfterFailedDiagnosticAdvance", "no further case work after failed diagnostic advance");
        }

        private static void ClearPendingDiagnosticFocus(BehaviorPatient patient)
        {
            if (patient == null)
            {
                return;
            }

            var entity = ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            if (entity == null)
            {
                return;
            }

            PendingDiagnosticFocuses.Remove(entity.GetEntityID());
        }

        public static bool TryResolveCaseDiagnosisContinuation(BehaviorPatient patient)
        {
            if (!Enabled || patient == null || patient.m_state == null || !HasOpenCase(patient))
            {
                return false;
            }

            if (TryScheduleCaseAwareExamination(patient))
            {
                return true;
            }

            if (TryAdvanceCaseTransferOrHospitalization(patient, "diagnosis_continuation"))
            {
                MuteCaseProgressNotifications(patient, 6f);
                return true;
            }

            return false;
        }

        public static bool ShouldSkipVanillaDiagnosisPanel(GLib.Entity patientEntity)
        {
            if (!Enabled || patientEntity == null)
            {
                return false;
            }

            var patient = patientEntity.GetComponent<BehaviorPatient>();
            if (patient == null)
            {
                return false;
            }

            var patientCase = GetOrCreateCompatibilityCase(patient, patientEntity);
            return patientCase != null;
        }

        public static void RevealSymptomsFromLastExamination(BehaviorPatient patient, ProcedureScript procedureScript)
        {
            if (!Enabled || patient == null)
            {
                return;
            }

            try
            {
                var patientCase = GetOrCreateCompatibilityCase(patient, ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity);
                if (procedureScript != null)
                {
                    var scriptTypeName = procedureScript.GetType().Name;
                    if (scriptTypeName.IndexOf("DoctorsInterview", StringComparison.OrdinalIgnoreCase) >= 0
                        || scriptTypeName.IndexOf("ReceptionFast", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        ProcessDiagnosticEvent(
                            patient,
                            scriptTypeName.IndexOf("ReceptionFast", StringComparison.OrdinalIgnoreCase) >= 0 ? DiagnosticEventKind.ReceptionFast : DiagnosticEventKind.Interview,
                            scriptTypeName,
                            "procedure_script");
                        if (MirrorKnownVanillaSymptomsToCase(patient, patientCase) > 0)
                        {
                            PersistMirroredSymptoms(patientCase);
                        }

                        return;
                    }
                }

                var procedure = patient.GetComponent<ProcedureComponent>();
                var examination = procedure == null ? null : procedure.GetLastFinishedExamination();
                RevealSymptomsFromExamination(patient, examination);
                if (MirrorKnownVanillaSymptomsToCase(patient, patientCase) > 0)
                {
                    PersistMirroredSymptoms(patientCase);
                }
            }
            catch (Exception ex)
            {
                Log("Failed to mirror last examination reveal: " + ex.Message);
            }
        }

        public static void RevealSymptomsFromExamination(BehaviorPatient patient, GameDBExamination examination)
        {
            if (!Enabled || patient == null || examination == null)
            {
                return;
            }

            ProcessDiagnosticEvent(
                patient,
                examination.LabTestingExaminationRef != null ? DiagnosticEventKind.LabResultsReady : DiagnosticEventKind.ExaminationFinished,
                examination.DatabaseID.ToString(),
                "examination");

            var patientCase = GetCase(patient);
            if (patientCase == null || patientCase.Complete)
            {
                return;
            }

            var revealed = 0;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis == null || diagnosis.Status == CaseDiagnosisStatus.Treated)
                {
                    continue;
                }

                var condition = ResolveDiagnosis(diagnosis.DiagnosisId);
                var diagnosisRevealed = RevealSymptomsForDiagnosis(diagnosis, condition, examination);
                if (diagnosisRevealed <= 0)
                {
                    continue;
                }

                revealed += diagnosisRevealed;
                if (diagnosis.Status == CaseDiagnosisStatus.Hidden)
                {
                    diagnosis.Status = CaseDiagnosisStatus.Suspected;
                }

                PromoteDiagnosisAfterReveal(patientCase, diagnosis, condition);
            }

            if (revealed <= 0)
            {
                return;
            }

            AddTimeline(patientCase, revealed + " case symptom(s) revealed by examination.");
            Save();
        }

        public static bool RevealHighestPriorityHiddenSymptom(BehaviorPatient patient)
        {
            var patientCase = GetCase(patient);
            if (patientCase == null || patientCase.Complete)
            {
                return false;
            }

            CaseDiagnosis bestDiagnosis = null;
            GameDBMedicalCondition bestCondition = null;
            GameDBSymptom bestSymptom = null;
            var bestScore = int.MinValue;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis == null || diagnosis.Status == CaseDiagnosisStatus.Treated)
                {
                    continue;
                }

                var condition = ResolveDiagnosis(diagnosis.DiagnosisId);
                if (condition == null)
                {
                    continue;
                }

                if (condition.Symptoms == null)
                {
                    continue;
                }

                for (var symptomIndex = 0; symptomIndex < condition.Symptoms.Length; symptomIndex++)
                {
                    var symptom = GetSymptom(condition.Symptoms[symptomIndex]);
                    if (symptom == null)
                    {
                        continue;
                    }

                    var symptomId = symptom.DatabaseID.ToString();
                    if (diagnosis.KnownSymptomIds.Contains(symptomId))
                    {
                        continue;
                    }

                    var score = (int)symptom.Hazard * 100 + (symptom.IsMainSymptom ? 25 : 0);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestDiagnosis = diagnosis;
                        bestCondition = condition;
                        bestSymptom = symptom;
                    }
                }
            }

            if (bestDiagnosis == null || bestSymptom == null)
            {
                return false;
            }

            var bestSymptomId = bestSymptom.DatabaseID.ToString();
            if (!bestDiagnosis.KnownSymptomIds.Contains(bestSymptomId))
            {
                bestDiagnosis.KnownSymptomIds.Add(bestSymptomId);
            }

            if (bestDiagnosis.Status == CaseDiagnosisStatus.Hidden)
            {
                bestDiagnosis.Status = CaseDiagnosisStatus.Suspected;
            }

            PromoteDiagnosisAfterReveal(patientCase, bestDiagnosis, bestCondition);
            AddTimeline(patientCase, "Extra case symptom revealed by perk.");
            Save();
            return true;
        }

        public static void UpdateCaseCollapse(BehaviorPatient patient)
        {
            if (!Enabled || patient == null || patient.m_state == null)
            {
                return;
            }

            if (Equals(ReflectionHelpers.GetField(patient.m_state, "m_fromLevelNoCollapse"), true)
                || ReflectionHelpers.GetField(patient.m_state, "m_collapseSymptom") != null
                || ReflectionHelpers.GetField(patient.m_state, "m_collapseProcedure") != null
                || patient.m_state.m_sentAway
                || patient.m_state.m_sentHome
                || patient.m_state.m_deathTriggered)
            {
                return;
            }

            var patientCase = GetCase(patient);
            var diagnosis = GetDueCollapseDiagnosis(patientCase);
            if (diagnosis == null)
            {
                return;
            }

            var condition = ResolveDiagnosis(diagnosis.DiagnosisId);
            var collapseSymptom = FindCollapseSymptom(condition);
            if (collapseSymptom == null)
            {
                return;
            }

            try
            {
                patient.SetCollapseOnSymptom(collapseSymptom);
                AddTimeline(patientCase, "Case collapse timer triggered.");
                Save();
            }
            catch (Exception ex)
            {
                Log("Failed to trigger case collapse: " + ex.Message);
            }
        }

        public static void PostponeTriggeredCaseCollapse(BehaviorPatient patient)
        {
            var patientCase = GetCase(patient);
            var diagnosis = GetDueCollapseDiagnosis(patientCase);
            if (diagnosis == null)
            {
                return;
            }

            diagnosis.CollapseDeadlineHours = GetCaseClockHours() + (patientCase != null && patientCase.Hopeless ? 18f : 12f);
            AddTimeline(patientCase, "Case collapse timer postponed after collapse event.");
            Save();
        }

        public static bool HasDueCaseCollapse(MedicalCondition medicalCondition)
        {
            if (!Enabled || medicalCondition == null)
            {
                return false;
            }

            PatientCase patientCase;
            return ConditionCases.TryGetValue(medicalCondition, out patientCase) && GetDueCollapseDiagnosis(patientCase) != null;
        }

        public static void PlanSecondaryTreatments(ProcedureComponent procedureComponent, bool onlyCritical, ref TreatmentPlanningResult result)
        {
            if (!Enabled || procedureComponent == null || procedureComponent.m_state == null || procedureComponent.m_state.m_procedureQueue == null)
            {
                return;
            }

            var entity = ReflectionHelpers.GetField(procedureComponent, "m_entity") as GLib.Entity;
            var patient = entity == null ? null : entity.GetComponent<BehaviorPatient>();
            var patientCase = GetCase(patient);
            if (patient == null || patientCase == null || patientCase.Complete || patient.GetDepartment() == null || patient.GetDepartment().GetDepartmentType() == null)
            {
                return;
            }

            var currentDepartmentId = patient.GetDepartment().GetDepartmentType().DatabaseID.ToString();
            if (ShouldDelaySameDepartmentTreatments(patient, patientCase, currentDepartmentId, allowCriticalOverride: onlyCritical))
            {
                return;
            }

            var planned = 0;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (!CanCurrentDepartmentWorkOnDiagnosis(diagnosis, currentDepartmentId))
                {
                    continue;
                }

                var condition = ResolveDiagnosis(diagnosis.DiagnosisId);
                if (condition == null)
                {
                    continue;
                }

                if (CanExposeConditionLevelTreatments(diagnosis))
                {
                    planned += TryPlanTreatmentsForCondition(entity, patient, procedureComponent, procedureComponent.m_state.m_procedureQueue, condition, onlyCritical);
                }

                if (condition.Symptoms == null)
                {
                    continue;
                }

                for (var symptomIndex = 0; symptomIndex < condition.Symptoms.Length; symptomIndex++)
                {
                    var symptom = GetSymptom(condition.Symptoms[symptomIndex]);
                    if (symptom == null || symptom.Treatments == null)
                    {
                        continue;
                    }

                    var symptomId = symptom.DatabaseID.ToString();
                    if (!diagnosis.KnownSymptomIds.Contains(symptomId) || diagnosis.TreatedSymptomIds.Contains(symptomId))
                    {
                        continue;
                    }

                    planned += TryPlanTreatmentsForSymptom(entity, patient, procedureComponent, procedureComponent.m_state.m_procedureQueue, symptom, onlyCritical);
                }
            }

            if (planned <= 0)
            {
                if (HasRelevantSecondaryTreatmentProgress(procedureComponent, patient, currentDepartmentId, onlyCritical))
                {
                    result = TreatmentPlanningResult.PLANNED;
                }

                return;
            }

            AddTimeline(patientCase, planned + " secondary case treatment(s) planned.");
            result = TreatmentPlanningResult.PLANNED;
            Save();
        }

        public static bool HandleDepartmentDiagnosticSweepBeforeTreatment(BehaviorPatient patient)
        {
            if (!Enabled || patient == null || patient.m_state == null)
            {
                return false;
            }

            var patientCase = GetCase(patient);
            var currentDepartmentId = patient.GetDepartment() == null || patient.GetDepartment().GetDepartmentType() == null
                ? null
                : patient.GetDepartment().GetDepartmentType().DatabaseID.ToString();
            if (!ShouldDelaySameDepartmentTreatments(patient, patientCase, currentDepartmentId, allowCriticalOverride: false))
            {
                return false;
            }

            var procedure = patient.GetComponent<ProcedureComponent>();
            var queue = procedure == null || procedure.m_state == null ? null : procedure.m_state.m_procedureQueue;
            if (queue != null && (queue.m_plannedExaminationStates.Count > 0 || queue.m_labProcedures.Count > 0))
            {
                return true;
            }

            patient.TryToScheduleExamination();
            return true;
        }

        public static void AddSecondaryExaminationAvailability(
            ProcedureComponent procedureComponent,
            FakeMap<GameDBExamination, ProcedureSceneAvailability> availability)
        {
            AddSecondaryExaminationAvailability(procedureComponent, availability, null);
        }

        internal static void ApplyExaminationAvailabilityOverlay(
            ProcedureComponent procedureComponent,
            FakeMap<GameDBExamination, ProcedureSceneAvailability> availability)
        {
            if (availability == null)
            {
                return;
            }

            var trace = new ExaminationAvailabilityOverlayTrace();
            trace.BaselineCount = availability.Count;

            bool blockAll;
            if (ShouldReplaceVanillaExaminationAvailability(procedureComponent, out blockAll))
            {
                availability.Clear();
                if (blockAll)
                {
                    trace.FinalCount = 0;
                    trace.HasCaseAvailableExamination = false;
                    LogExaminationAvailabilityOverlayTrace(procedureComponent, trace);
                    return;
                }
            }

            AddSecondaryExaminationAvailability(procedureComponent, availability, trace);
            trace.FinalCount = availability.Count;
            trace.HasCaseAvailableExamination = HasAvailableExaminationInMap(availability);

            var entity = ReflectionHelpers.GetField(procedureComponent, "m_entity") as GLib.Entity;
            var patient = entity == null ? null : entity.GetComponent<BehaviorPatient>();
            var patientCase = GetCase(patient);
            var focusDiagnosis = GetCurrentDiagnosticFocusDiagnosis(patient, patientCase);
            var focusCondition = focusDiagnosis == null ? null : ResolveDiagnosis(focusDiagnosis.DiagnosisId);
            trace.HasFeasibleDiagnosticRoute = patient != null
                && patient.GetDepartment() != null
                && focusCondition != null
                && HasFeasibleDiagnosticRoute(patient, patient.GetDepartment(), focusCondition);

            LogExaminationAvailabilityOverlayTrace(procedureComponent, trace);
        }

        private sealed class ExaminationAvailabilityOverlayTrace
        {
            public string FocusDiagnosisId;
            public int BaselineCount;
            public int AddedCount;
            public int FinalCount;
            public bool? HasFeasibleDiagnosticRoute;
            public bool? HasCaseAvailableExamination;
            public readonly List<string> DroppedExamIds = new List<string>();
            public readonly List<string> DroppedReasons = new List<string>();
        }

        private static void AddSecondaryExaminationAvailability(
            ProcedureComponent procedureComponent,
            FakeMap<GameDBExamination, ProcedureSceneAvailability> availability,
            ExaminationAvailabilityOverlayTrace trace)
        {
            if (!Enabled || procedureComponent == null || procedureComponent.m_state == null || procedureComponent.m_state.m_procedureQueue == null || availability == null)
            {
                return;
            }

            var entity = ReflectionHelpers.GetField(procedureComponent, "m_entity") as GLib.Entity;
            var patient = entity == null ? null : entity.GetComponent<BehaviorPatient>();
            var patientCase = GetCase(patient);
            if (patient == null || patientCase == null || patientCase.Complete || patient.GetDepartment() == null || patient.GetDepartment().GetDepartmentType() == null)
            {
                return;
            }

            var focusDiagnosis = GetCurrentDiagnosticFocusDiagnosis(patient, patientCase);
            if (trace != null)
            {
                trace.FocusDiagnosisId = focusDiagnosis == null ? null : focusDiagnosis.DiagnosisId;
            }

            if (!CanDiagnosisUseExaminationFlow(focusDiagnosis))
            {
                return;
            }

            var condition = ResolveDiagnosis(focusDiagnosis.DiagnosisId);
            if (condition == null)
            {
                return;
            }

            var added = AddUnknownSymptomExaminationAvailability(
                procedureComponent.m_state.m_procedureQueue,
                availability,
                entity,
                patient,
                focusDiagnosis,
                condition,
                8,
                trace);
            if (added <= 0)
            {
                AddSecondaryConditionExaminationAvailability(
                    procedureComponent.m_state.m_procedureQueue,
                    availability,
                    entity,
                    patient,
                    condition,
                    4,
                    trace);
            }
        }

        public static void AddSecondaryExaminationsToList(
            ProcedureComponent procedureComponent,
            List<GameDBExamination> examinations)
        {
            AddSecondaryExaminationsToList(procedureComponent, examinations, null);
        }

        internal static void ApplyExaminationListOverlay(
            ProcedureComponent procedureComponent,
            List<GameDBExamination> examinations)
        {
            if (examinations == null)
            {
                return;
            }

            bool blockAll;
            if (ShouldReplaceVanillaExaminationAvailability(procedureComponent, out blockAll))
            {
                examinations.Clear();
                if (blockAll)
                {
                    return;
                }
            }

            AddSecondaryExaminationsToList(procedureComponent, examinations, null);
        }

        private static void AddSecondaryExaminationsToList(
            ProcedureComponent procedureComponent,
            List<GameDBExamination> examinations,
            ExaminationAvailabilityOverlayTrace trace)
        {
            if (!Enabled || procedureComponent == null || procedureComponent.m_state == null || procedureComponent.m_state.m_procedureQueue == null || examinations == null)
            {
                return;
            }

            var entity = ReflectionHelpers.GetField(procedureComponent, "m_entity") as GLib.Entity;
            var patient = entity == null ? null : entity.GetComponent<BehaviorPatient>();
            var patientCase = GetCase(patient);
            if (patient == null || patientCase == null || patientCase.Complete)
            {
                return;
            }

            if (patient.GetDepartment() == null || patient.GetDepartment().GetDepartmentType() == null)
            {
                return;
            }

            var focusDiagnosis = GetCurrentDiagnosticFocusDiagnosis(patient, patientCase);
            if (trace != null)
            {
                trace.FocusDiagnosisId = focusDiagnosis == null ? null : focusDiagnosis.DiagnosisId;
            }

            if (!CanDiagnosisUseExaminationFlow(focusDiagnosis))
            {
                return;
            }

            var condition = ResolveDiagnosis(focusDiagnosis.DiagnosisId);
            if (condition == null)
            {
                return;
            }

            var added = AddUnknownSymptomExaminationsToList(
                procedureComponent.m_state.m_procedureQueue,
                examinations,
                focusDiagnosis,
                condition,
                8,
                trace);
            if (added <= 0)
            {
                AddConditionExaminationsToList(
                    procedureComponent.m_state.m_procedureQueue,
                    examinations,
                    condition,
                    4,
                    trace);
            }
        }

        public static void AddSecondaryTreatmentAvailability(
            ProcedureComponent procedureComponent,
            FakeMap<GameDBTreatment, ProcedureSceneAvailability> availability,
            TreatmentPlanningMode treatmentPlanningMode)
        {
            if (!Enabled || procedureComponent == null || procedureComponent.m_state == null || procedureComponent.m_state.m_procedureQueue == null || availability == null)
            {
                return;
            }

            var entity = ReflectionHelpers.GetField(procedureComponent, "m_entity") as GLib.Entity;
            var patient = entity == null ? null : entity.GetComponent<BehaviorPatient>();
            var patientCase = GetCase(patient);
            if (patient == null || patientCase == null || patientCase.Complete || patient.GetDepartment() == null || patient.GetDepartment().GetDepartmentType() == null)
            {
                return;
            }

            var currentDepartmentId = patient.GetDepartment().GetDepartmentType().DatabaseID.ToString();
            if (ShouldDelaySameDepartmentTreatments(patient, patientCase, currentDepartmentId, allowCriticalOverride: true))
            {
                return;
            }

            var added = 0;
            for (var i = 0; i < patientCase.Diagnoses.Count && added < 6; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (!CanCurrentDepartmentWorkOnDiagnosis(diagnosis, currentDepartmentId))
                {
                    continue;
                }

                var condition = ResolveDiagnosis(diagnosis.DiagnosisId);
                if (condition == null)
                {
                    continue;
                }

                if (CanExposeConditionLevelTreatments(diagnosis))
                {
                    added += AddSecondaryConditionTreatmentAvailability(
                        procedureComponent.m_state.m_procedureQueue,
                        availability,
                        entity,
                        patient.GetDepartment(),
                        condition,
                        6 - added);
                }

                if (condition.Symptoms == null)
                {
                    continue;
                }

                for (var symptomIndex = 0; symptomIndex < condition.Symptoms.Length && added < 6; symptomIndex++)
                {
                    var symptom = GetSymptom(condition.Symptoms[symptomIndex]);
                    if (symptom == null || symptom.Treatments == null)
                    {
                        continue;
                    }

                    var symptomId = symptom.DatabaseID.ToString();
                    if (!diagnosis.KnownSymptomIds.Contains(symptomId) || diagnosis.TreatedSymptomIds.Contains(symptomId))
                    {
                        continue;
                    }

                    for (var treatmentIndex = 0; treatmentIndex < symptom.Treatments.Length && added < 6; treatmentIndex++)
                    {
                        var treatment = symptom.Treatments[treatmentIndex] == null ? null : symptom.Treatments[treatmentIndex].Entry;
                        if (!CanExposeSecondaryTreatment(procedureComponent, procedureComponent.m_state.m_procedureQueue, availability, treatment))
                        {
                            continue;
                        }

                        availability.Put(treatment, GetSecondaryTreatmentAvailability(entity, patient.GetDepartment(), treatment));
                        added++;
                    }
                }
            }
        }

        public static void ApplyDepartmentDiagnosticSweepTreatmentGate(ProcedureComponent procedureComponent, TreatmentPlanningMode treatmentPlanningMode, FakeMap<GameDBTreatment, ProcedureSceneAvailability> availability)
        {
            if (!Enabled || procedureComponent == null || availability == null || procedureComponent.m_state == null)
            {
                return;
            }

            var entity = ReflectionHelpers.GetField(procedureComponent, "m_entity") as GLib.Entity;
            var patient = entity == null ? null : entity.GetComponent<BehaviorPatient>();
            var patientCase = GetCase(patient);
            var currentDepartmentId = patient == null || patient.GetDepartment() == null || patient.GetDepartment().GetDepartmentType() == null
                ? null
                : patient.GetDepartment().GetDepartmentType().DatabaseID.ToString();
            if (!ShouldDelaySameDepartmentTreatments(patient, patientCase, currentDepartmentId, allowCriticalOverride: true))
            {
                return;
            }

            availability.Clear();
        }

        public static void MarkTreatmentApplied(ProcedureComponent procedureComponent, GameDBTreatment treatment)
        {
            if (!Enabled || procedureComponent == null || treatment == null)
            {
                return;
            }

            var entity = ReflectionHelpers.GetField(procedureComponent, "m_entity") as GLib.Entity;
            var patient = entity == null ? null : entity.GetComponent<BehaviorPatient>();
            var patientCase = GetCase(patient);
            if (patientCase == null || patientCase.Complete)
            {
                return;
            }

            var changed = false;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis == null || diagnosis.Status == CaseDiagnosisStatus.Hidden || diagnosis.Status == CaseDiagnosisStatus.Treated)
                {
                    continue;
                }

                var condition = ResolveDiagnosis(diagnosis.DiagnosisId);
                if (condition == null || condition.Symptoms == null)
                {
                    continue;
                }

                for (var symptomIndex = 0; symptomIndex < condition.Symptoms.Length; symptomIndex++)
                {
                    var symptom = GetSymptom(condition.Symptoms[symptomIndex]);
                    if (symptom == null || !SymptomHasTreatment(symptom, treatment))
                    {
                        continue;
                    }

                    var symptomId = symptom.DatabaseID.ToString();
                    if (!diagnosis.KnownSymptomIds.Contains(symptomId))
                    {
                        continue;
                    }

                    if (!diagnosis.TreatedSymptomIds.Contains(symptomId))
                    {
                        diagnosis.TreatedSymptomIds.Add(symptomId);
                        changed = true;
                    }
                }

                if ((diagnosis.Status == CaseDiagnosisStatus.Diagnosed || diagnosis.Status == CaseDiagnosisStatus.Active)
                    && !HasUnknownSymptoms(diagnosis)
                    && AreKnownSymptomsTreated(diagnosis))
                {
                    diagnosis.Status = CaseDiagnosisStatus.Treated;
                    changed = true;
                }
            }

            if (!changed)
            {
                return;
            }

            if (TryAdvanceCompatibilityConditionWithinDepartment(patient, patientCase))
            {
                changed = true;
            }

            AddTimeline(patientCase, "Case symptom treatment state updated.");
            Save();
        }

        private static void PromoteDiagnosisAfterReveal(PatientCase patientCase, CaseDiagnosis diagnosis, GameDBMedicalCondition condition)
        {
            if (patientCase == null || diagnosis == null || condition == null || diagnosis.Status == CaseDiagnosisStatus.Treated)
            {
                return;
            }

            var mainSymptom = condition.GetMainSymptom();
            var mainSymptomId = mainSymptom == null ? null : mainSymptom.DatabaseID.ToString();
            if (string.IsNullOrEmpty(mainSymptomId) || !diagnosis.KnownSymptomIds.Contains(mainSymptomId))
            {
                return;
            }

            if (diagnosis.Status == CaseDiagnosisStatus.Hidden)
            {
                diagnosis.Status = CaseDiagnosisStatus.Suspected;
            }
        }

        private static int TryPlanTreatmentsForSymptom(GLib.Entity entity, BehaviorPatient patient, ProcedureComponent procedureComponent, ProcedureQueue queue, GameDBSymptom symptom, bool onlyCritical)
        {
            if (entity == null || patient == null || queue == null || symptom == null || symptom.Treatments == null)
            {
                return 0;
            }

            if (onlyCritical && symptom.Hazard != SymptomHazard.High)
            {
                return 0;
            }

            var department = patient.GetDepartment();
            if (department == null)
            {
                return 0;
            }

            var planned = 0;
            for (var i = 0; i < symptom.Treatments.Length; i++)
            {
                var treatment = symptom.Treatments[i] == null ? null : symptom.Treatments[i].Entry;
                if (!CanPlanSecondaryTreatment(entity, department, procedureComponent, queue, treatment))
                {
                    continue;
                }

                queue.AddPlannedTreatment(treatment);
                TraceLoggingService.LogQueueEvent(
                    patient,
                    "queue_treatment_add",
                    patient.m_state == null ? null : patient.m_state.m_doctor,
                    treatment == null ? "-" : treatment.DatabaseID.ToString(),
                    "TryPlanTreatmentsForSymptom",
                    symptom.DatabaseID + (onlyCritical ? ":critical" : ":secondary"),
                    "added");
                planned++;
            }

            return planned;
        }

        private static int TryPlanTreatmentsForCondition(GLib.Entity entity, BehaviorPatient patient, ProcedureComponent procedureComponent, ProcedureQueue queue, GameDBMedicalCondition condition, bool onlyCritical)
        {
            if (entity == null || patient == null || queue == null || condition == null || condition.Treatments == null)
            {
                return 0;
            }

            var department = patient.GetDepartment();
            if (department == null)
            {
                return 0;
            }

            var planned = 0;
            for (var i = 0; i < condition.Treatments.Length; i++)
            {
                var treatment = condition.Treatments[i] == null ? null : condition.Treatments[i].Entry;
                if (onlyCritical && !IsCriticalTreatment(condition, treatment))
                {
                    continue;
                }

                if (!CanPlanSecondaryTreatment(entity, department, procedureComponent, queue, treatment))
                {
                    continue;
                }

                queue.AddPlannedTreatment(treatment);
                TraceLoggingService.LogQueueEvent(
                    patient,
                    "queue_treatment_add",
                    patient.m_state == null ? null : patient.m_state.m_doctor,
                    treatment == null ? "-" : treatment.DatabaseID.ToString(),
                    "TryPlanTreatmentsForCondition",
                    condition.DatabaseID + (onlyCritical ? ":critical" : ":condition"),
                    "added");
                planned++;
            }

            return planned;
        }

        private static bool IsCriticalTreatment(GameDBMedicalCondition condition, GameDBTreatment treatment)
        {
            if (condition == null || treatment == null)
            {
                return false;
            }

            var mainSymptom = condition.GetMainSymptom();
            if (mainSymptom != null && mainSymptom.Hazard == SymptomHazard.High && SymptomHasTreatment(mainSymptom, treatment))
            {
                return true;
            }

            return GetWorstHazard(condition) == "High";
        }

        private static bool CanPlanSecondaryTreatment(GLib.Entity entity, Department department, ProcedureComponent procedureComponent, ProcedureQueue queue, GameDBTreatment treatment)
        {
            if (entity == null || department == null || queue == null || treatment == null || treatment.Procedure == null)
            {
                return false;
            }

            if (treatment.TreatmentType == TreatmentType.SURGERY || treatment.TreatmentType == TreatmentType.HOSPITALIZATION)
            {
                return false;
            }

            if (treatment.HospitalizationTreatmentRef != null)
            {
                return false;
            }

            if (IsTreatmentAlreadyHandled(procedureComponent, queue, treatment))
            {
                return false;
            }

            return ProcedureScene.IsProcedureAvailable(GetSecondaryTreatmentAvailability(entity, department, treatment));
        }

        private static bool HasAvailableConditionLevelDiagnosticWork(BehaviorPatient patient, ProcedureQueue queue, GameDBMedicalCondition condition)
        {
            if (patient == null || queue == null || condition == null || condition.Examinations == null)
            {
                return false;
            }

            var entity = ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            if (entity == null)
            {
                return false;
            }

            var scratch = new List<GameDBExamination>();
            for (var i = 0; i < condition.Examinations.Length; i++)
            {
                var examination = condition.Examinations[i] == null ? null : condition.Examinations[i].Entry;
                if (!CanUseExaminationForDiagnosticRoute(queue, scratch, examination))
                {
                    continue;
                }

                if (ProcedureScene.IsProcedureAvailable(GetSecondaryExaminationAvailability(entity, patient, examination)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool CanCurrentDepartmentWorkOnDiagnosis(CaseDiagnosis diagnosis, string currentDepartmentId)
        {
            return diagnosis != null
                && !string.IsNullOrEmpty(currentDepartmentId)
                && diagnosis.DepartmentId == currentDepartmentId
                && diagnosis.Status != CaseDiagnosisStatus.Hidden
                && diagnosis.Status != CaseDiagnosisStatus.Suspected
                && diagnosis.Status != CaseDiagnosisStatus.Treated;
        }

        private static bool CanCurrentDepartmentExamineDiagnosis(CaseDiagnosis diagnosis, string currentDepartmentId)
        {
            return diagnosis != null
                && !string.IsNullOrEmpty(currentDepartmentId)
                && diagnosis.DepartmentId == currentDepartmentId
                && diagnosis.Status != CaseDiagnosisStatus.Diagnosed
                && diagnosis.Status != CaseDiagnosisStatus.Treated;
        }

        private static bool CanDiagnosisUseExaminationFlow(CaseDiagnosis diagnosis)
        {
            return diagnosis != null
                && diagnosis.Status != CaseDiagnosisStatus.Diagnosed
                && diagnosis.Status != CaseDiagnosisStatus.Treated;
        }

        private static bool HasUnknownSymptoms(CaseDiagnosis diagnosis)
        {
            if (diagnosis == null || diagnosis.SymptomIds.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < diagnosis.SymptomIds.Count; i++)
            {
                if (!diagnosis.KnownSymptomIds.Contains(diagnosis.SymptomIds[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool CanExposeConditionLevelTreatments(CaseDiagnosis diagnosis)
        {
            if (diagnosis == null || diagnosis.Status == CaseDiagnosisStatus.Hidden || diagnosis.Status == CaseDiagnosisStatus.Suspected || diagnosis.Status == CaseDiagnosisStatus.Treated)
            {
                return false;
            }

            return diagnosis.Status == CaseDiagnosisStatus.Diagnosed || diagnosis.Status == CaseDiagnosisStatus.Active;
        }

        private static bool ShouldDelaySameDepartmentTreatments(PatientCase patientCase, string currentDepartmentId, bool allowCriticalOverride)
        {
            if (patientCase == null || patientCase.Complete || string.IsNullOrEmpty(currentDepartmentId))
            {
                return false;
            }

            var sameDepartmentOpen = 0;
            var unresolvedSameDepartment = 0;
            var hasCriticalSameDepartment = false;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis == null || diagnosis.DepartmentId != currentDepartmentId || diagnosis.Status == CaseDiagnosisStatus.Treated)
                {
                    continue;
                }

                sameDepartmentOpen++;
                if (diagnosis.Status != CaseDiagnosisStatus.Diagnosed)
                {
                    unresolvedSameDepartment++;
                }

                if (diagnosis.CollapseCapable || string.Equals(diagnosis.Hazard, "High", StringComparison.OrdinalIgnoreCase))
                {
                    hasCriticalSameDepartment = true;
                }
            }

            if (sameDepartmentOpen < 2 || unresolvedSameDepartment <= 0)
            {
                return false;
            }

            return !allowCriticalOverride || !hasCriticalSameDepartment;
        }

        private static bool ShouldDelaySameDepartmentTreatments(BehaviorPatient patient, PatientCase patientCase, string currentDepartmentId, bool allowCriticalOverride)
        {
            if (!ShouldDelaySameDepartmentTreatments(patientCase, currentDepartmentId, allowCriticalOverride))
            {
                return false;
            }

            if (patient == null)
            {
                return true;
            }

            var procedure = patient.GetComponent<ProcedureComponent>();
            var queue = procedure == null || procedure.m_state == null ? null : procedure.m_state.m_procedureQueue;
            if (queue == null)
            {
                return true;
            }

            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis == null
                    || diagnosis.DepartmentId != currentDepartmentId
                    || diagnosis.Status == CaseDiagnosisStatus.Treated
                    || diagnosis.Status == CaseDiagnosisStatus.Diagnosed
                    || diagnosis.Status == CaseDiagnosisStatus.Active)
                {
                    continue;
                }

                var condition = ResolveDiagnosis(diagnosis.DiagnosisId);
                if (condition != null && HasAvailableDiagnosticWorkForDiagnosis(patient, procedure, queue, diagnosis, condition))
                {
                    return true;
                }
            }

            return false;
        }

        private static CaseDiagnosis GetCurrentDiagnosticFocusDiagnosis(BehaviorPatient patient, PatientCase patientCase)
        {
            if (patient == null || patientCase == null || patientCase.Complete)
            {
                return null;
            }

            var currentDepartmentId = patient.GetDepartment() == null || patient.GetDepartment().GetDepartmentType() == null
                ? patientCase.ActiveDepartmentId
                : patient.GetDepartment().GetDepartmentType().DatabaseID.ToString();
            if (string.IsNullOrEmpty(currentDepartmentId))
            {
                return null;
            }

            var currentCondition = GetPrimaryDiagnosis(patient);
            var currentDiagnosisId = currentCondition == null ? null : currentCondition.DatabaseID.ToString();
            if (!string.IsNullOrEmpty(currentDiagnosisId))
            {
                for (var i = 0; i < patientCase.Diagnoses.Count; i++)
                {
                    var diagnosis = patientCase.Diagnoses[i];
                    if (diagnosis == null
                        || diagnosis.DepartmentId != currentDepartmentId
                        || diagnosis.DiagnosisId != currentDiagnosisId
                        || !CanDiagnosisUseExaminationFlow(diagnosis))
                    {
                        continue;
                    }

                    return diagnosis;
                }
            }

            var sameDepartment = SelectNextUndiagnosedDiagnosisInDepartment(patientCase, currentDepartmentId, currentDiagnosisId);
            if (sameDepartment != null)
            {
                return sameDepartment;
            }

            return SelectNextUndiagnosedDiagnosisOverall(patientCase, currentDepartmentId, currentDiagnosisId);
        }

        private static CaseDiagnosis SelectNextUndiagnosedDiagnosisOverall(PatientCase patientCase, string currentDepartmentId, string excludeDiagnosisId)
        {
            if (patientCase == null)
            {
                return null;
            }

            CaseDiagnosis best = null;
            var bestScore = int.MinValue;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis == null
                    || diagnosis.DiagnosisId == excludeDiagnosisId
                    || !CanDiagnosisUseExaminationFlow(diagnosis))
                {
                    continue;
                }

                var score = 0;
                if (!string.IsNullOrEmpty(currentDepartmentId) && diagnosis.DepartmentId == currentDepartmentId)
                {
                    score += 200;
                }

                if (diagnosis.CollapseCapable)
                {
                    score += 300;
                }

                if (string.Equals(diagnosis.Hazard, "High", StringComparison.OrdinalIgnoreCase))
                {
                    score += 200;
                }
                else if (string.Equals(diagnosis.Hazard, "Medium", StringComparison.OrdinalIgnoreCase))
                {
                    score += 100;
                }

                score += diagnosis.KnownSymptomIds.Count * 10;
                score -= Math.Max(0, diagnosis.SymptomIds.Count - diagnosis.KnownSymptomIds.Count) * 5;

                if (score > bestScore)
                {
                    best = diagnosis;
                    bestScore = score;
                }
            }

            return best;
        }

        private static int AddUnknownSymptomExaminationAvailability(
            ProcedureQueue queue,
            FakeMap<GameDBExamination, ProcedureSceneAvailability> availability,
            GLib.Entity entity,
            BehaviorPatient patient,
            CaseDiagnosis diagnosis,
            GameDBMedicalCondition condition,
            int maxToAdd,
            ExaminationAvailabilityOverlayTrace trace)
        {
            if (queue == null || availability == null || entity == null || patient == null || diagnosis == null || condition == null || condition.Symptoms == null || maxToAdd <= 0)
            {
                return 0;
            }

            var added = 0;
            for (var symptomIndex = 0; symptomIndex < condition.Symptoms.Length && added < maxToAdd; symptomIndex++)
            {
                var symptom = GetSymptom(condition.Symptoms[symptomIndex]);
                if (symptom == null || symptom.Examinations == null)
                {
                    continue;
                }

                var symptomId = symptom.DatabaseID.ToString();
                if (diagnosis.KnownSymptomIds.Contains(symptomId))
                {
                    continue;
                }

                for (var examIndex = 0; examIndex < symptom.Examinations.Length && added < maxToAdd; examIndex++)
                {
                    var examination = symptom.Examinations[examIndex] == null ? null : symptom.Examinations[examIndex].Entry;
                    string dropReason;
                    if (!CanExposeSecondaryExamination(queue, availability, examination, out dropReason))
                    {
                        RecordExaminationOverlayDrop(trace, examination, dropReason);
                        continue;
                    }

                    availability.Put(examination, GetSecondaryExaminationAvailability(entity, patient, examination));
                    RecordExaminationOverlayAdd(trace);
                    added++;
                }
            }

            return added;
        }

        private static int AddUnknownSymptomExaminationsToList(
            ProcedureQueue queue,
            List<GameDBExamination> examinations,
            CaseDiagnosis diagnosis,
            GameDBMedicalCondition condition,
            int maxToAdd,
            ExaminationAvailabilityOverlayTrace trace)
        {
            if (queue == null || examinations == null || diagnosis == null || condition == null || condition.Symptoms == null || maxToAdd <= 0)
            {
                return 0;
            }

            var added = 0;
            for (var symptomIndex = 0; symptomIndex < condition.Symptoms.Length && added < maxToAdd; symptomIndex++)
            {
                var symptom = GetSymptom(condition.Symptoms[symptomIndex]);
                if (symptom == null || symptom.Examinations == null)
                {
                    continue;
                }

                var symptomId = symptom.DatabaseID.ToString();
                if (diagnosis.KnownSymptomIds.Contains(symptomId))
                {
                    continue;
                }

                for (var examIndex = 0; examIndex < symptom.Examinations.Length && added < maxToAdd; examIndex++)
                {
                    var examination = symptom.Examinations[examIndex] == null ? null : symptom.Examinations[examIndex].Entry;
                    string dropReason;
                    if (!CanExposeSecondaryExamination(queue, examinations, examination, out dropReason))
                    {
                        RecordExaminationOverlayDrop(trace, examination, dropReason);
                        continue;
                    }

                    examinations.Add(examination);
                    RecordExaminationOverlayAdd(trace);
                    added++;
                }
            }

            return added;
        }

        private static int AddConditionExaminationsToList(
            ProcedureQueue queue,
            List<GameDBExamination> examinations,
            GameDBMedicalCondition condition,
            int maxToAdd,
            ExaminationAvailabilityOverlayTrace trace)
        {
            if (queue == null || examinations == null || condition == null || condition.Examinations == null || maxToAdd <= 0)
            {
                return 0;
            }

            var added = 0;
            for (var examIndex = 0; examIndex < condition.Examinations.Length && added < maxToAdd; examIndex++)
            {
                var examination = condition.Examinations[examIndex] == null ? null : condition.Examinations[examIndex].Entry;
                string dropReason;
                if (!CanExposeSecondaryExamination(queue, examinations, examination, out dropReason))
                {
                    RecordExaminationOverlayDrop(trace, examination, dropReason);
                    continue;
                }

                examinations.Add(examination);
                RecordExaminationOverlayAdd(trace);
                added++;
            }

            return added;
        }

        private static int AddSecondaryConditionExaminationAvailability(
            ProcedureQueue queue,
            FakeMap<GameDBExamination, ProcedureSceneAvailability> availability,
            GLib.Entity entity,
            BehaviorPatient patient,
            GameDBMedicalCondition condition,
            int maxToAdd,
            ExaminationAvailabilityOverlayTrace trace)
        {
            if (queue == null || availability == null || entity == null || patient == null || condition == null || condition.Examinations == null || maxToAdd <= 0)
            {
                return 0;
            }

            var added = 0;
            for (var i = 0; i < condition.Examinations.Length && added < maxToAdd; i++)
            {
                var examination = condition.Examinations[i] == null ? null : condition.Examinations[i].Entry;
                string dropReason;
                if (!CanExposeSecondaryExamination(queue, availability, examination, out dropReason))
                {
                    RecordExaminationOverlayDrop(trace, examination, dropReason);
                    continue;
                }

                availability.Put(examination, GetSecondaryExaminationAvailability(entity, patient, examination));
                RecordExaminationOverlayAdd(trace);
                added++;
            }

            return added;
        }

        private static int AddSecondaryConditionTreatmentAvailability(
            ProcedureQueue queue,
            FakeMap<GameDBTreatment, ProcedureSceneAvailability> availability,
            GLib.Entity entity,
            Department department,
            GameDBMedicalCondition condition,
            int maxToAdd)
        {
            if (queue == null || availability == null || entity == null || department == null || condition == null || condition.Treatments == null || maxToAdd <= 0)
            {
                return 0;
            }

            var added = 0;
            var procedureComponent = entity.GetComponent<ProcedureComponent>();
            for (var i = 0; i < condition.Treatments.Length && added < maxToAdd; i++)
            {
                var treatment = condition.Treatments[i] == null ? null : condition.Treatments[i].Entry;
                if (!CanExposeSecondaryTreatment(procedureComponent, queue, availability, treatment))
                {
                    continue;
                }

                availability.Put(treatment, GetSecondaryTreatmentAvailability(entity, department, treatment));
                added++;
            }

            return added;
        }

        private static bool CanExposeSecondaryExamination(
            ProcedureQueue queue,
            FakeMap<GameDBExamination, ProcedureSceneAvailability> availability,
            GameDBExamination examination)
        {
            string ignored;
            return CanExposeSecondaryExamination(queue, availability, examination, out ignored);
        }

        private static bool CanExposeSecondaryExamination(
            ProcedureQueue queue,
            FakeMap<GameDBExamination, ProcedureSceneAvailability> availability,
            GameDBExamination examination,
            out string dropReason)
        {
            dropReason = null;
            if (queue == null || availability == null || examination == null || examination.Procedure == null)
            {
                dropReason = "invalid";
                return false;
            }

            if (examination.ExaminationType == ExaminationType.INTERVIEW
                || examination.ExaminationType == ExaminationType.OBSERVATION
                || examination.m_isTesting)
            {
                dropReason = examination.ExaminationType == ExaminationType.INTERVIEW
                    ? "interview-blocked"
                    : examination.ExaminationType == ExaminationType.OBSERVATION
                        ? "observation-blocked"
                        : "testing-blocked";
                return false;
            }

            if (queue.HasFinishedExamination(examination))
            {
                dropReason = "finished";
                return false;
            }

            if (queue.HasPlannedExamination(examination))
            {
                dropReason = "planned";
                return false;
            }

            if (queue.m_activeExamination != null && queue.m_activeExamination.Entry == examination)
            {
                dropReason = "active";
                return false;
            }

            if (availability.m_keys.Contains(examination))
            {
                dropReason = "duplicate";
                return false;
            }

            return true;
        }

        private static bool CanExposeSecondaryExamination(
            ProcedureQueue queue,
            List<GameDBExamination> examinations,
            GameDBExamination examination)
        {
            string ignored;
            return CanExposeSecondaryExamination(queue, examinations, examination, out ignored);
        }

        private static bool CanExposeSecondaryExamination(
            ProcedureQueue queue,
            List<GameDBExamination> examinations,
            GameDBExamination examination,
            out string dropReason)
        {
            dropReason = null;
            if (queue == null || examinations == null || examination == null || examination.Procedure == null)
            {
                dropReason = "invalid";
                return false;
            }

            if (examination.ExaminationType == ExaminationType.INTERVIEW
                || examination.ExaminationType == ExaminationType.OBSERVATION
                || examination.m_isTesting)
            {
                dropReason = examination.ExaminationType == ExaminationType.INTERVIEW
                    ? "interview-blocked"
                    : examination.ExaminationType == ExaminationType.OBSERVATION
                        ? "observation-blocked"
                        : "testing-blocked";
                return false;
            }

            if (queue.HasFinishedExamination(examination))
            {
                dropReason = "finished";
                return false;
            }

            if (queue.HasPlannedExamination(examination))
            {
                dropReason = "planned";
                return false;
            }

            if (queue.m_activeExamination != null && queue.m_activeExamination.Entry == examination)
            {
                dropReason = "active";
                return false;
            }

            if (examinations.Contains(examination))
            {
                dropReason = "duplicate";
                return false;
            }

            return true;
        }

        private static bool CanUseExaminationForDiagnosticRoute(
            ProcedureQueue queue,
            List<GameDBExamination> seen,
            GameDBExamination examination)
        {
            if (queue == null || seen == null || examination == null || examination.Procedure == null)
            {
                return false;
            }

            if (queue.HasFinishedExamination(examination) || queue.HasPlannedExamination(examination))
            {
                return false;
            }

            if (queue.m_activeExamination != null && queue.m_activeExamination.Entry == examination)
            {
                return false;
            }

            if (seen.Contains(examination))
            {
                return false;
            }

            seen.Add(examination);
            return true;
        }

        private static void RecordExaminationOverlayAdd(ExaminationAvailabilityOverlayTrace trace)
        {
            if (trace != null)
            {
                trace.AddedCount++;
            }
        }

        private static void RecordExaminationOverlayDrop(ExaminationAvailabilityOverlayTrace trace, GameDBExamination examination, string reason)
        {
            if (trace == null || examination == null || string.IsNullOrEmpty(reason))
            {
                return;
            }

            trace.DroppedExamIds.Add(examination.DatabaseID.ToString());
            trace.DroppedReasons.Add(reason);
        }

        private static void LogExaminationAvailabilityOverlayTrace(ProcedureComponent procedureComponent, ExaminationAvailabilityOverlayTrace trace)
        {
            if (procedureComponent == null || trace == null)
            {
                return;
            }

            var entity = ReflectionHelpers.GetField(procedureComponent, "m_entity") as GLib.Entity;
            var patient = entity == null ? null : entity.GetComponent<BehaviorPatient>();
            var patientCase = GetCase(patient);
            if (patient == null || patientCase == null || patientCase.Complete)
            {
                return;
            }

            var builder = new StringBuilder();
            builder.Append("focusDiagnosisId=")
                .Append(string.IsNullOrEmpty(trace.FocusDiagnosisId) ? "-" : trace.FocusDiagnosisId)
                .Append(";baselineVanillaExamCount=")
                .Append(trace.BaselineCount.ToString(CultureInfo.InvariantCulture))
                .Append(";caseAddedExamCount=")
                .Append(trace.AddedCount.ToString(CultureInfo.InvariantCulture))
                .Append(";finalMergedExamCount=")
                .Append(trace.FinalCount.ToString(CultureInfo.InvariantCulture))
                .Append(";HasFeasibleDiagnosticRoute=")
                .Append(trace.HasFeasibleDiagnosticRoute.HasValue ? trace.HasFeasibleDiagnosticRoute.Value.ToString() : "-")
                .Append(";HasCaseAvailableExamination=")
                .Append(trace.HasCaseAvailableExamination.HasValue ? trace.HasCaseAvailableExamination.Value.ToString() : "-")
                .Append(";droppedExamIds=");

            if (trace.DroppedExamIds.Count == 0)
            {
                builder.Append("-");
            }
            else
            {
                for (var i = 0; i < trace.DroppedExamIds.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(",");
                    }

                    builder.Append(trace.DroppedExamIds[i]);
                }
            }

            builder.Append(";dropReasons=");
            if (trace.DroppedReasons.Count == 0)
            {
                builder.Append("-");
            }
            else
            {
                for (var i = 0; i < trace.DroppedReasons.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(",");
                    }

                    builder.Append(trace.DroppedReasons[i]);
                }
            }

            TraceLoggingService.LogRateLimitedPatientEvent(patient, "EXAM_AVAIL", builder.ToString(), 0.5f);
        }

        private static bool CanExposeSecondaryTreatment(
            ProcedureComponent procedureComponent,
            ProcedureQueue queue,
            FakeMap<GameDBTreatment, ProcedureSceneAvailability> availability,
            GameDBTreatment treatment)
        {
            if (procedureComponent == null || queue == null || availability == null || treatment == null || treatment.Procedure == null)
            {
                return false;
            }

            if (treatment.TreatmentType == TreatmentType.SURGERY || treatment.TreatmentType == TreatmentType.HOSPITALIZATION)
            {
                return false;
            }

            if (treatment.HospitalizationTreatmentRef != null)
            {
                return false;
            }

            if (IsTreatmentAlreadyHandled(procedureComponent, queue, treatment))
            {
                return false;
            }

            return !availability.m_keys.Contains(treatment);
        }

        private static ProcedureSceneAvailability GetSecondaryExaminationAvailability(GLib.Entity entity, BehaviorPatient patient, GameDBExamination examination)
        {
            if (entity == null || patient == null || examination == null || examination.Procedure == null)
            {
                return ProcedureSceneAvailability.UNKNOWN;
            }

            try
            {
                if (!CanCurrentDoctorPrescribe(patient, examination.Procedure))
                {
                    return ProcedureSceneAvailability.DOCTOR_CAN_NOT_PRESCRIBE;
                }

                var department = examination.Procedure.DetachedDepartmentRef != null && MapScriptInterface.Instance != null
                    ? MapScriptInterface.Instance.GetDepartmentOfType(examination.Procedure.DetachedDepartmentRef.Entry)
                    : patient.GetDepartment();
                var fallbackDepartment = examination.Procedure.FallbackLabDepartmentRef != null && MapScriptInterface.Instance != null
                    ? MapScriptInterface.Instance.GetDepartmentOfType(examination.Procedure.FallbackLabDepartmentRef.Entry)
                    : null;

                if ((department == null || department.IsClosed()) && (fallbackDepartment == null || fallbackDepartment.IsClosed()))
                {
                    return ProcedureSceneAvailability.STAFF_UNAVAILABLE;
                }

                var availability = CreateProcedureAvailability(examination.Procedure, entity, department);
                if (!ProcedureScene.IsProcedureAvailable(availability) && fallbackDepartment != null && !fallbackDepartment.IsClosed())
                {
                    availability = CreateProcedureAvailability(examination.Procedure, entity, fallbackDepartment);
                }

                if (!ProcedureScene.IsProcedureAvailable(availability) || examination.LabTestingExaminationRef == null)
                {
                    return availability;
                }

                return CreateProcedureAvailability(examination.LabTestingExaminationRef.Entry.Procedure, entity, department ?? patient.GetDepartment());
            }
            catch (Exception ex)
            {
                Log("Secondary examination availability check failed: " + ex.Message);
                return ProcedureSceneAvailability.UNKNOWN;
            }
        }

        private static ProcedureSceneAvailability GetSecondaryTreatmentAvailability(GLib.Entity entity, Department department, GameDBTreatment treatment)
        {
            if (entity == null || department == null || treatment == null || treatment.Procedure == null)
            {
                return ProcedureSceneAvailability.UNKNOWN;
            }

            try
            {
                var procedureComponent = entity.GetComponent<ProcedureComponent>();
                if (procedureComponent == null)
                {
                    return ProcedureSceneAvailability.UNKNOWN;
                }

                var patient = entity.GetComponent<BehaviorPatient>();
                if (patient != null && !CanCurrentDoctorPrescribe(patient, treatment.Procedure))
                {
                    return ProcedureSceneAvailability.DOCTOR_CAN_NOT_PRESCRIBE;
                }

                var detachedDepartmentType = treatment.Procedure.DetachedDepartmentRef == null ? null : treatment.Procedure.DetachedDepartmentRef.Entry;
                var targetDepartment = detachedDepartmentType != null && MapScriptInterface.Instance != null
                    ? MapScriptInterface.Instance.GetDepartmentOfType(detachedDepartmentType)
                    : department;

                var database = Database.Instance;
                var defaultDepartmentType = database == null ? null : database.GetEntry<GameDBDepartment>("DPT_DEFAULT");
                var emergencyDepartmentType = database == null ? null : database.GetEntry<GameDBDepartment>("DPT_EMERGENCY");
                var targetDepartmentType = targetDepartment == null ? null : targetDepartment.GetDepartmentType();
                if (targetDepartment != null
                    && targetDepartmentType != null
                    && defaultDepartmentType != null
                    && targetDepartmentType == defaultDepartmentType
                    && emergencyDepartmentType != null
                    && MapScriptInterface.Instance != null)
                {
                    targetDepartment = MapScriptInterface.Instance.GetDepartmentOfType(emergencyDepartmentType);
                }

                if (targetDepartment == null || targetDepartment.IsClosed())
                {
                    return ProcedureSceneAvailability.STAFF_UNAVAILABLE;
                }

                return procedureComponent.GetProcedureAvailabilty(
                    treatment.Procedure,
                    entity,
                    targetDepartment,
                    AccessRights.PATIENT_PROCEDURE,
                    EquipmentListRules.ANY);
            }
            catch (Exception ex)
            {
                if (!(ex is NullReferenceException))
                {
                    Log("Secondary treatment availability check failed: " + DescribeException(ex));
                }
                return ProcedureSceneAvailability.UNKNOWN;
            }
        }

        private static bool IsTreatmentAlreadyHandled(ProcedureComponent procedureComponent, ProcedureQueue queue, GameDBTreatment treatment)
        {
            if (procedureComponent == null || queue == null || treatment == null)
            {
                return false;
            }

            if (queue.HasPlannedTreatment(treatment) || queue.HasActiveTreatment(treatment) || queue.HasFinishedTreatment(treatment))
            {
                return true;
            }

            var procedureEntity = ReflectionHelpers.GetField(procedureComponent, "m_entity") as GLib.Entity;
            var hospitalization = procedureEntity == null ? null : procedureEntity.GetComponent<HospitalizationComponent>();
            if (hospitalization != null && hospitalization.GetHospitalizationTreatment() == treatment)
            {
                return true;
            }

            var reservedScript = procedureComponent.m_state == null || procedureComponent.m_state.m_reservedProcedureScript == null || !procedureComponent.m_state.m_reservedProcedureScript.CheckEntity()
                ? null
                : procedureComponent.m_state.m_reservedProcedureScript.GetEntity();
            if (reservedScript != null && reservedScript.m_stateData != null && reservedScript.m_stateData.m_treatment == treatment)
            {
                return true;
            }

            var currentScript = procedureComponent.m_state == null || procedureComponent.m_state.m_currentProcedureScript == null || !procedureComponent.m_state.m_currentProcedureScript.CheckEntity()
                ? null
                : procedureComponent.m_state.m_currentProcedureScript.GetEntity();
            return currentScript != null && currentScript.m_stateData != null && currentScript.m_stateData.m_treatment == treatment;
        }

        private static bool HasRelevantSecondaryTreatmentProgress(ProcedureComponent procedureComponent, BehaviorPatient patient, string currentDepartmentId, bool onlyCritical)
        {
            if (procedureComponent == null || patient == null || procedureComponent.m_state == null || procedureComponent.m_state.m_procedureQueue == null)
            {
                return false;
            }

            var patientCase = GetCase(patient);
            if (patientCase == null || patientCase.Complete)
            {
                return false;
            }

            var queue = procedureComponent.m_state.m_procedureQueue;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (!CanCurrentDepartmentWorkOnDiagnosis(diagnosis, currentDepartmentId))
                {
                    continue;
                }

                var condition = ResolveDiagnosis(diagnosis.DiagnosisId);
                if (condition == null)
                {
                    continue;
                }

                if (HasRelevantSecondaryTreatmentProgressForCondition(procedureComponent, queue, diagnosis, condition, onlyCritical))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasAvailableTreatmentForDiagnosis(
            ProcedureComponent procedureComponent,
            BehaviorPatient patient,
            ProcedureQueue queue,
            CaseDiagnosis diagnosis,
            GameDBMedicalCondition condition,
            bool onlyCritical)
        {
            if (procedureComponent == null || patient == null || queue == null || diagnosis == null || condition == null)
            {
                return false;
            }

            var entity = ReflectionHelpers.GetField(procedureComponent, "m_entity") as GLib.Entity;
            var department = patient.GetDepartment();
            if (entity == null || department == null)
            {
                return false;
            }

            var scratchAvailability = new FakeMap<GameDBTreatment, ProcedureSceneAvailability>();
            if (CanExposeConditionLevelTreatments(diagnosis) && condition.Treatments != null)
            {
                for (var i = 0; i < condition.Treatments.Length; i++)
                {
                    var treatment = condition.Treatments[i] == null ? null : condition.Treatments[i].Entry;
                    if (treatment == null || (onlyCritical && !IsCriticalTreatment(condition, treatment)))
                    {
                        continue;
                    }

                    if (IsTreatmentAlreadyHandled(procedureComponent, queue, treatment))
                    {
                        return true;
                    }

                    if (!CanExposeSecondaryTreatment(procedureComponent, queue, scratchAvailability, treatment))
                    {
                        continue;
                    }

                    if (ProcedureScene.IsProcedureAvailable(GetSecondaryTreatmentAvailability(entity, department, treatment)))
                    {
                        return true;
                    }
                }
            }

            if (condition.Symptoms == null)
            {
                return false;
            }

            for (var symptomIndex = 0; symptomIndex < condition.Symptoms.Length; symptomIndex++)
            {
                var symptom = GetSymptom(condition.Symptoms[symptomIndex]);
                if (symptom == null || symptom.Treatments == null)
                {
                    continue;
                }

                if (onlyCritical && symptom.Hazard != SymptomHazard.High)
                {
                    continue;
                }

                var symptomId = SafeDatabaseId(symptom);
                if (string.IsNullOrEmpty(symptomId)
                    || !diagnosis.KnownSymptomIds.Contains(symptomId)
                    || diagnosis.TreatedSymptomIds.Contains(symptomId))
                {
                    continue;
                }

                for (var treatmentIndex = 0; treatmentIndex < symptom.Treatments.Length; treatmentIndex++)
                {
                    var treatment = symptom.Treatments[treatmentIndex] == null ? null : symptom.Treatments[treatmentIndex].Entry;
                    if (treatment == null)
                    {
                        continue;
                    }

                    if (IsTreatmentAlreadyHandled(procedureComponent, queue, treatment))
                    {
                        return true;
                    }

                    if (!CanExposeSecondaryTreatment(procedureComponent, queue, scratchAvailability, treatment))
                    {
                        continue;
                    }

                    if (ProcedureScene.IsProcedureAvailable(GetSecondaryTreatmentAvailability(entity, department, treatment)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool HasRelevantSecondaryTreatmentProgressForCondition(ProcedureComponent procedureComponent, ProcedureQueue queue, CaseDiagnosis diagnosis, GameDBMedicalCondition condition, bool onlyCritical)
        {
            if (procedureComponent == null || queue == null || diagnosis == null || condition == null)
            {
                return false;
            }

            if (CanExposeConditionLevelTreatments(diagnosis) && condition.Treatments != null)
            {
                for (var i = 0; i < condition.Treatments.Length; i++)
                {
                    var treatment = condition.Treatments[i] == null ? null : condition.Treatments[i].Entry;
                    if (treatment == null || (onlyCritical && !IsCriticalTreatment(condition, treatment)))
                    {
                        continue;
                    }

                    if (IsTreatmentAlreadyHandled(procedureComponent, queue, treatment))
                    {
                        return true;
                    }
                }
            }

            if (condition.Symptoms == null)
            {
                return false;
            }

            for (var symptomIndex = 0; symptomIndex < condition.Symptoms.Length; symptomIndex++)
            {
                var symptom = GetSymptom(condition.Symptoms[symptomIndex]);
                if (symptom == null || symptom.Treatments == null)
                {
                    continue;
                }

                if (onlyCritical && symptom.Hazard != SymptomHazard.High)
                {
                    continue;
                }

                var symptomId = symptom.DatabaseID.ToString();
                if (!diagnosis.KnownSymptomIds.Contains(symptomId))
                {
                    continue;
                }

                for (var treatmentIndex = 0; treatmentIndex < symptom.Treatments.Length; treatmentIndex++)
                {
                    var treatment = symptom.Treatments[treatmentIndex] == null ? null : symptom.Treatments[treatmentIndex].Entry;
                    if (treatment != null && IsTreatmentAlreadyHandled(procedureComponent, queue, treatment))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool HasCaseAvailableExamination(BehaviorPatient patient)
        {
            var patientCase = GetCase(patient);
            string reason;
            if (patient == null || patient.m_state == null || patient.m_state.m_medicalCondition == null)
            {
                reason = "missing_patient_or_medical_condition";
                TraceLoggingService.LogPatientDecision(patient, "HasCaseAvailableExamination", false, reason, "case_summary=none");
                return false;
            }

            if (ShouldBlockFurtherSameDepartmentExaminations(patient))
            {
                reason = "same_department_examinations_blocked";
                TraceLoggingService.LogPatientDecision(patient, "HasCaseAvailableExamination", false, reason, BuildTraceDecisionContext(patient, patientCase));
                return false;
            }

            var procedure = patient.GetComponent<ProcedureComponent>();
            var queue = procedure == null || procedure.m_state == null ? null : procedure.m_state.m_procedureQueue;
            if (procedure == null || queue == null)
            {
                reason = "missing_procedure_queue";
                TraceLoggingService.LogPatientDecision(patient, "HasCaseAvailableExamination", false, reason, BuildTraceDecisionContext(patient, patientCase));
                return false;
            }

            if (queue.m_activeExamination != null || queue.m_plannedExaminationStates.Count > 0 || queue.m_labProcedures.Count > 0)
            {
                reason = "existing_exam_or_lab_queue_work";
                TraceLoggingService.LogPatientDecision(
                    patient,
                    "HasCaseAvailableExamination",
                    true,
                    reason,
                    BuildTraceDecisionContext(patient, patientCase)
                    + ";queue_summary=existing_exam_or_lab_work");
                return true;
            }

            var forceAll = SymptomsPanelController.DLCHardCoreModeCheck(
                Hospital.Instance.m_state.m_diagnosePercentageAll,
                Hospital.Instance.m_state.m_diagnosePercentageControlled,
                patient.GetControlMode() == PatientControlMode.PlayerControl,
                patient.m_state.m_doctor);
            var examinations = procedure.UpdateAllExaminationsForMedicalCondition(patient.m_state.m_medicalCondition, -1, forceAll);
            var hasAvailable = HasAvailableExaminationInMap(examinations);

            if (patientCase != null && !patientCase.Complete)
            {
                LogDiagnosticAvailabilityAnomaly(patient, patientCase, hasAvailable);
            }

            reason = hasAvailable ? "runtime_available_examination_found" : "runtime_exam_availability_empty";
            TraceLoggingService.LogPatientDecision(
                patient,
                "HasCaseAvailableExamination",
                hasAvailable,
                reason,
                BuildTraceDecisionContext(patient, patientCase)
                + ";force_all=" + forceAll
                + ";availability_count=" + (examinations == null ? 0 : examinations.Count).ToString(CultureInfo.InvariantCulture));
            return hasAvailable;
        }

        private static bool HasAvailableExaminationInMap(FakeMap<GameDBExamination, ProcedureSceneAvailability> examinations)
        {
            if (examinations == null)
            {
                return false;
            }

            for (var i = 0; i < examinations.Count; i++)
            {
                if (ProcedureScene.IsProcedureAvailable(examinations.ValueAt(i)))
                {
                    return true;
                }
            }

            return false;
        }

        private static void LogDiagnosticAvailabilityAnomaly(BehaviorPatient patient, PatientCase patientCase, bool hasRuntimeAvailability)
        {
            if (patient == null || patientCase == null || patientCase.Complete || hasRuntimeAvailability)
            {
                return;
            }

            var department = patient.GetDepartment();
            if (department == null)
            {
                return;
            }

            var focusDiagnosis = GetCurrentDiagnosticFocusDiagnosis(patient, patientCase);
            if (!CanDiagnosisUseExaminationFlow(focusDiagnosis))
            {
                return;
            }

            var focusCondition = ResolveDiagnosis(focusDiagnosis.DiagnosisId);
            if (focusCondition == null || !HasFeasibleDiagnosticRoute(patient, department, focusCondition))
            {
                return;
            }

            TraceLoggingService.LogPatientAnomaly(
                patient,
                "feasible_but_unavailable",
                "focus_diagnosis=" + focusDiagnosis.DiagnosisId
                + ";runtime_department=" + GetCaseRuntimeDepartmentId(patient, patientCase)
                + ";reason=HasFeasibleDiagnosticRoute_true_but_runtime_exam_unavailable");
        }

        private static bool HasCaseAvailableTreatmentOrProgress(BehaviorPatient patient)
        {
            var patientCase = GetCase(patient);
            string reason;
            if (patient == null || patient.m_state == null || patient.m_state.m_medicalCondition == null)
            {
                reason = "missing_patient_or_medical_condition";
                TraceLoggingService.LogPatientDecision(patient, "HasCaseAvailableTreatmentOrProgress", false, reason, "case_summary=none");
                return false;
            }

            var procedure = patient.GetComponent<ProcedureComponent>();
            var queue = procedure == null || procedure.m_state == null ? null : procedure.m_state.m_procedureQueue;
            if (procedure == null || queue == null)
            {
                reason = "missing_procedure_queue";
                TraceLoggingService.LogPatientDecision(patient, "HasCaseAvailableTreatmentOrProgress", false, reason, BuildTraceDecisionContext(patient, patientCase));
                return false;
            }

            if (queue.m_activeTreatmentStates.Count > 0 || queue.m_plannedTreatmentStates.Count > 0)
            {
                reason = "existing_treatment_queue_work";
                TraceLoggingService.LogPatientDecision(patient, "HasCaseAvailableTreatmentOrProgress", true, reason, BuildTraceDecisionContext(patient, patientCase));
                return true;
            }

            var currentDepartmentId = patient.GetDepartment() == null || patient.GetDepartment().GetDepartmentType() == null
                ? (patientCase == null ? null : patientCase.ActiveDepartmentId)
                : patient.GetDepartment().GetDepartmentType().DatabaseID.ToString();
            if (HasRelevantSecondaryTreatmentProgress(procedure, patient, currentDepartmentId, onlyCritical: false))
            {
                reason = "secondary_treatment_progress_found";
                TraceLoggingService.LogPatientDecision(patient, "HasCaseAvailableTreatmentOrProgress", true, reason, BuildTraceDecisionContext(patient, patientCase));
                return true;
            }

            if (patientCase != null && !patientCase.Complete)
            {
                for (var i = 0; i < patientCase.Diagnoses.Count; i++)
                {
                    var diagnosis = patientCase.Diagnoses[i];
                    if (!CanCurrentDepartmentWorkOnDiagnosis(diagnosis, currentDepartmentId))
                    {
                        continue;
                    }

                    var condition = ResolveDiagnosis(diagnosis.DiagnosisId);
                    if (condition != null && HasAvailableTreatmentForDiagnosis(procedure, patient, queue, diagnosis, condition, onlyCritical: false))
                    {
                        reason = "case_diagnosis_treatment_available";
                        TraceLoggingService.LogPatientDecision(
                            patient,
                            "HasCaseAvailableTreatmentOrProgress",
                            true,
                            reason,
                            BuildTraceDecisionContext(patient, patientCase)
                            + ";candidate_diagnosis=" + diagnosis.DiagnosisId
                            + ";candidate_status=" + diagnosis.Status);
                        return true;
                    }
                }
            }

            var treatments = procedure.GetAllTreatmentsForMedicalCondition(patient.m_state.m_medicalCondition, TreatmentPlanningMode.ALL_SYMPTOMS);
            if (treatments == null)
            {
                reason = "vanilla_treatment_map_missing";
                TraceLoggingService.LogPatientDecision(patient, "HasCaseAvailableTreatmentOrProgress", false, reason, BuildTraceDecisionContext(patient, patientCase));
                return false;
            }

            for (var i = 0; i < treatments.Count; i++)
            {
                if (ProcedureScene.IsProcedureAvailable(treatments.ValueAt(i)))
                {
                    reason = "vanilla_treatment_available";
                    TraceLoggingService.LogPatientDecision(patient, "HasCaseAvailableTreatmentOrProgress", true, reason, BuildTraceDecisionContext(patient, patientCase));
                    return true;
                }
            }

            reason = "no_available_treatment_or_progress";
            TraceLoggingService.LogPatientDecision(patient, "HasCaseAvailableTreatmentOrProgress", false, reason, BuildTraceDecisionContext(patient, patientCase)
                + ";runtime_treatment_count=" + treatments.Count.ToString(CultureInfo.InvariantCulture));
            return false;
        }

        private static bool TryResumeTreatableDiagnosedCase(BehaviorPatient patient)
        {
            if (patient == null || patient.m_state == null)
            {
                TraceLoggingService.LogPatientAction(patient, "TryResumeTreatableDiagnosedCase", "failed", "reason=missing_patient_state");
                return false;
            }

            var patientCase = GetCase(patient);
            var procedure = patient.GetComponent<ProcedureComponent>();
            var queue = procedure == null || procedure.m_state == null ? null : procedure.m_state.m_procedureQueue;
            var currentDepartmentId = patient.GetDepartment() == null || patient.GetDepartment().GetDepartmentType() == null
                ? (patientCase == null ? null : patientCase.ActiveDepartmentId)
                : patient.GetDepartment().GetDepartmentType().DatabaseID.ToString();
            if (patientCase == null || patientCase.Complete || procedure == null || queue == null || string.IsNullOrEmpty(currentDepartmentId))
            {
                TraceLoggingService.LogPatientAction(patient, "TryResumeTreatableDiagnosedCase", "failed", "reason=missing_case_or_queue;" + BuildTraceDecisionContext(patient, patientCase));
                return false;
            }

            CaseDiagnosis targetDiagnosis = null;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (!CanCurrentDepartmentWorkOnDiagnosis(diagnosis, currentDepartmentId))
                {
                    continue;
                }

                var condition = ResolveDiagnosis(diagnosis.DiagnosisId);
                if (condition == null || !HasAvailableTreatmentForDiagnosis(procedure, patient, queue, diagnosis, condition, onlyCritical: false))
                {
                    continue;
                }

                targetDiagnosis = diagnosis;
                break;
            }

            if (targetDiagnosis == null)
            {
                TraceLoggingService.LogPatientAction(patient, "TryResumeTreatableDiagnosedCase", "failed", "reason=no_treatable_diagnosis;" + BuildTraceDecisionContext(patient, patientCase));
                return false;
            }

            var currentCondition = GetPrimaryDiagnosis(patient);
            var targetCondition = ResolveDiagnosis(targetDiagnosis.DiagnosisId);
            if (targetCondition == null)
            {
                TraceLoggingService.LogPatientAction(patient, "TryResumeTreatableDiagnosedCase", "failed", "reason=target_condition_missing;target_diagnosis=" + targetDiagnosis.DiagnosisId);
                return false;
            }

            var currentDiagnosisId = SafeDatabaseId(currentCondition);
            if (currentCondition == null || currentDiagnosisId != targetDiagnosis.DiagnosisId)
            {
                patient.SetMedicalCondition(targetCondition, false, 0);
                RegisterCurrentMedicalCondition(patient, patientCase);
            }

            TryResumeCaseProcedureSelection(patient);
            var resumed = HasCaseAvailableTreatmentOrProgress(patient)
                || queue.m_activeTreatmentStates.Count > 0
                || queue.m_plannedTreatmentStates.Count > 0;
            TraceLoggingService.LogPatientAction(
                patient,
                "TryResumeTreatableDiagnosedCase",
                resumed ? "resumed" : "failed",
                BuildTraceDecisionContext(patient, patientCase)
                + ";target_diagnosis=" + targetDiagnosis.DiagnosisId
                + ";target_department=" + targetDiagnosis.DepartmentId
                + ";compatibility_switched=" + (currentCondition == null || currentDiagnosisId != targetDiagnosis.DiagnosisId));
            return resumed;
        }

        private static bool HasCaseTransferOrHospitalizationRoute(BehaviorPatient patient)
        {
            var decision = EvaluateCaseTransferOrHospitalization(patient);
            LogCaseRouteDecision(patient, decision, "route_check", executionAttempted: false, executionSucceeded: false, actionInvoked: null, blockerOverride: null, rateLimitSeconds: 0.5f);
            return decision != null && decision.RouteExists && decision.CanExecuteNow;
        }

        private static CaseRouteDecision EvaluateCaseTransferOrHospitalization(BehaviorPatient patient)
        {
            var decision = new CaseRouteDecision
            {
                StepType = CaseRouteStepType.None,
                RouteExists = false,
                CanExecuteNow = false,
                BlockerReason = "unknown"
            };
            if (patient == null)
            {
                return decision;
            }

            var patientCase = GetCase(patient);
            if (patientCase == null || patientCase.Complete)
            {
                return decision;
            }

            var currentDepartmentId = GetCaseRuntimeDepartmentId(patient, patientCase);
            var nextDiagnosis = CaseCarePlanner.SelectNextDiagnosis(patientCase, currentDepartmentId);
            if (nextDiagnosis == null)
            {
                return decision;
            }

            var nextCondition = ResolveDiagnosis(nextDiagnosis.DiagnosisId);
            var targetDepartmentId = !string.IsNullOrEmpty(nextDiagnosis.DepartmentId)
                ? nextDiagnosis.DepartmentId
                : GetDepartmentId(nextCondition);
            var needsTransfer = !string.IsNullOrEmpty(targetDepartmentId)
                && !string.Equals(targetDepartmentId, currentDepartmentId, StringComparison.Ordinal);
            if (!needsTransfer && !nextDiagnosis.NeedsHospitalization)
            {
                return decision;
            }

            decision.StepType = nextDiagnosis.NeedsHospitalization ? CaseRouteStepType.Hospitalization : CaseRouteStepType.Transfer;
            decision.DiagnosisId = nextDiagnosis.DiagnosisId;
            decision.DiagnosisStatus = nextDiagnosis.Status;
            decision.CurrentDepartmentId = currentDepartmentId;
            decision.TargetDepartmentId = targetDepartmentId;
            decision.NeedsHospitalization = nextDiagnosis.NeedsHospitalization;
            decision.RouteExists = true;

            var targetDepartment = ResolveDepartment(targetDepartmentId);
            string blockerReason;
            decision.CanExecuteNow = decision.StepType == CaseRouteStepType.Hospitalization
                ? CanAdvanceCaseToHospitalization(patient, patientCase, nextDiagnosis, nextCondition, targetDepartment, out blockerReason)
                : CanAdvanceCaseToNextDepartment(patient, patientCase, nextDiagnosis, nextCondition, targetDepartment, out blockerReason);
            decision.BlockerReason = decision.CanExecuteNow
                ? string.Empty
                : (string.IsNullOrEmpty(blockerReason) ? "unknown" : blockerReason);
            return decision;
        }

        private static bool CanAdvanceCaseToNextDepartment(
            BehaviorPatient patient,
            PatientCase patientCase,
            CaseDiagnosis nextDiagnosis,
            GameDBMedicalCondition nextCondition,
            Department targetDepartment,
            out string blockerReason)
        {
            blockerReason = string.Empty;
            if (!IsCaseRouteCompatibleWithPatientState(patient))
            {
                blockerReason = "incompatible_patient_state";
                return false;
            }

            var targetDepartmentId = !string.IsNullOrEmpty(nextDiagnosis == null ? null : nextDiagnosis.DepartmentId)
                ? nextDiagnosis.DepartmentId
                : GetDepartmentId(nextCondition);
            if (string.IsNullOrEmpty(targetDepartmentId) || targetDepartment == null || targetDepartment.IsClosed())
            {
                blockerReason = "target_department_closed";
                return false;
            }

            var hospitalization = patient == null ? null : patient.GetComponent<HospitalizationComponent>();
            var isHospitalized = hospitalization != null && hospitalization.IsHospitalized();
            if (isHospitalized && !targetDepartment.AcceptsInpatients())
            {
                blockerReason = "incompatible_patient_state";
                return false;
            }

            if (!isHospitalized && !targetDepartment.AcceptsOutpatients())
            {
                blockerReason = "incompatible_patient_state";
                return false;
            }

            if (!HasAnyDoctorCapacity(targetDepartment))
            {
                blockerReason = "no_profile_capacity";
                return false;
            }

            return true;
        }

        private static bool CanAdvanceCaseToHospitalization(
            BehaviorPatient patient,
            PatientCase patientCase,
            CaseDiagnosis nextDiagnosis,
            GameDBMedicalCondition nextCondition,
            Department targetDepartment,
            out string blockerReason)
        {
            blockerReason = string.Empty;
            if (!IsCaseRouteCompatibleWithPatientState(patient))
            {
                blockerReason = "incompatible_patient_state";
                return false;
            }

            var targetDepartmentId = !string.IsNullOrEmpty(nextDiagnosis == null ? null : nextDiagnosis.DepartmentId)
                ? nextDiagnosis.DepartmentId
                : GetDepartmentId(nextCondition);
            if (string.IsNullOrEmpty(targetDepartmentId) || targetDepartment == null || targetDepartment.IsClosed())
            {
                blockerReason = "target_department_closed";
                return false;
            }

            if (!targetDepartment.AcceptsInpatients() || !targetDepartment.HasWorkingHospitalization())
            {
                blockerReason = "hospitalization_unavailable";
                return false;
            }

            GameDBTreatment hospitalizationTreatment;
            GameDBRoomType roomType;
            if (!TryResolveHospitalizationRequest(nextCondition, out hospitalizationTreatment, out roomType)
                || hospitalizationTreatment == null
                || hospitalizationTreatment.Procedure == null
                || roomType == null)
            {
                blockerReason = "hospitalization_unavailable";
                return false;
            }

            var hospitalization = patient == null ? null : patient.GetComponent<HospitalizationComponent>();
            var currentDepartmentId = GetCaseRuntimeDepartmentId(patient, patientCase);
            if (hospitalization != null
                && hospitalization.IsHospitalized()
                && string.Equals(currentDepartmentId, targetDepartmentId, StringComparison.Ordinal))
            {
                return true;
            }

            if (!ProcedureScene.IsProcedureAvailable(targetDepartment.CanHospitalize(hospitalizationTreatment.Procedure)))
            {
                blockerReason = "hospitalization_unavailable";
                return false;
            }

            return true;
        }

        private static bool TryResolveHospitalizationRequest(GameDBMedicalCondition condition, out GameDBTreatment hospitalizationTreatment, out GameDBRoomType roomType)
        {
            hospitalizationTreatment = null;
            roomType = null;
            if (condition == null || condition.Symptoms == null)
            {
                return false;
            }

            for (var symptomIndex = 0; symptomIndex < condition.Symptoms.Length; symptomIndex++)
            {
                var symptom = GetSymptom(condition.Symptoms[symptomIndex]);
                if (symptom == null || symptom.Treatments == null)
                {
                    continue;
                }

                for (var treatmentIndex = 0; treatmentIndex < symptom.Treatments.Length; treatmentIndex++)
                {
                    var treatment = symptom.Treatments[treatmentIndex] == null ? null : symptom.Treatments[treatmentIndex].Entry;
                    var candidate = treatment == null || treatment.HospitalizationTreatmentRef == null
                        ? null
                        : treatment.HospitalizationTreatmentRef.Entry;
                    if (candidate == null || candidate.Procedure == null)
                    {
                        continue;
                    }

                    var candidateRoomType = GetPrimaryRequiredRoomType(candidate.Procedure);
                    if (candidateRoomType == null)
                    {
                        continue;
                    }

                    hospitalizationTreatment = candidate;
                    roomType = candidateRoomType;
                    return true;
                }
            }

            return false;
        }

        private static GameDBRoomType GetPrimaryRequiredRoomType(GameDBProcedure procedure)
        {
            if (procedure == null || procedure.RequiredRoomTypes == null)
            {
                return null;
            }

            for (var i = 0; i < procedure.RequiredRoomTypes.Length; i++)
            {
                var roomType = procedure.RequiredRoomTypes[i] == null ? null : procedure.RequiredRoomTypes[i].Entry;
                if (roomType != null)
                {
                    return roomType;
                }
            }

            return null;
        }

        private static bool IsCaseRouteCompatibleWithPatientState(BehaviorPatient patient)
        {
            if (patient == null || patient.m_state == null)
            {
                return false;
            }

            if (patient.m_state.m_sentAway || patient.m_state.m_sentHome || patient.m_state.m_deathTriggered)
            {
                return false;
            }

            var state = patient.m_state.m_patientState;
            return state != PatientState.Left && state != PatientState.Leaving;
        }

        private static string GetCaseRuntimeDepartmentId(BehaviorPatient patient, PatientCase patientCase)
        {
            return patient == null || patient.GetDepartment() == null || patient.GetDepartment().GetDepartmentType() == null
                ? (patientCase == null ? null : patientCase.ActiveDepartmentId)
                : patient.GetDepartment().GetDepartmentType().DatabaseID.ToString();
        }

        private static CaseDiagnosis FindCaseDiagnosis(PatientCase patientCase, string diagnosisId)
        {
            if (patientCase == null || string.IsNullOrEmpty(diagnosisId))
            {
                return null;
            }

            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis != null && string.Equals(diagnosis.DiagnosisId, diagnosisId, StringComparison.Ordinal))
                {
                    return diagnosis;
                }
            }

            return null;
        }

        private static bool TryAdvanceCaseTransferOrHospitalization(BehaviorPatient patient, string initiator)
        {
            return TryAdvanceCaseTransferOrHospitalization(patient, null, initiator);
        }

        private static bool TryAdvanceCaseTransferOrHospitalization(BehaviorPatient patient, CaseRouteDecision decision, string initiator)
        {
            if (patient == null)
            {
                return false;
            }

            decision = decision ?? EvaluateCaseTransferOrHospitalization(patient);
            LogCaseRouteDecision(patient, decision, initiator, executionAttempted: false, executionSucceeded: false, actionInvoked: null, blockerOverride: null, rateLimitSeconds: 0.5f);
            if (decision == null || !decision.RouteExists || !decision.CanExecuteNow)
            {
                return false;
            }

            var patientCase = GetCase(patient);
            if (patientCase == null || patientCase.Complete)
            {
                return false;
            }

            var nextDiagnosis = FindCaseDiagnosis(patientCase, decision.DiagnosisId);
            var nextCondition = ResolveDiagnosis(decision.DiagnosisId);
            if (nextDiagnosis == null || nextCondition == null)
            {
                return false;
            }

            var targetDepartmentId = !string.IsNullOrEmpty(decision.TargetDepartmentId)
                ? decision.TargetDepartmentId
                : GetDepartmentId(nextCondition);
            string blockerReason;
            string actionInvoked;
            var success = decision.StepType == CaseRouteStepType.Hospitalization
                ? TryAdvanceCaseHospitalization(patient, patientCase, nextDiagnosis, nextCondition, targetDepartmentId, out blockerReason, out actionInvoked)
                : TryAdvanceCaseTransfer(patient, patientCase, nextDiagnosis, nextCondition, targetDepartmentId, out blockerReason, out actionInvoked);
            if (!success && string.IsNullOrEmpty(blockerReason))
            {
                blockerReason = string.IsNullOrEmpty(decision.BlockerReason) ? "unknown" : decision.BlockerReason;
            }

            if (!string.IsNullOrEmpty(blockerReason))
            {
                decision.BlockerReason = blockerReason;
            }

            LogCaseRouteDecision(patient, decision, initiator, executionAttempted: true, executionSucceeded: success, actionInvoked: actionInvoked, blockerOverride: blockerReason, rateLimitSeconds: null);
            if (decision.CanExecuteNow && !success)
            {
                LogCaseRouteAnomaly(patient, decision, initiator, blockerReason, actionInvoked);
            }

            if (success)
            {
                ClearBlockedCaseRetry(patient);
            }

            return success;
        }

        private static bool TryAdvanceCaseTransfer(
            BehaviorPatient patient,
            PatientCase patientCase,
            CaseDiagnosis nextDiagnosis,
            GameDBMedicalCondition nextCondition,
            string targetDepartmentId,
            out string blockerReason,
            out string actionInvoked)
        {
            blockerReason = string.Empty;
            actionInvoked = string.Empty;
            var targetDepartment = ResolveDepartment(targetDepartmentId);
            var currentDepartmentId = GetCaseRuntimeDepartmentId(patient, patientCase);
            if (!CanAdvanceCaseToNextDepartment(patient, patientCase, nextDiagnosis, nextCondition, targetDepartment, out blockerReason)
                && !string.Equals(currentDepartmentId, targetDepartmentId, StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                if (!string.Equals(currentDepartmentId, targetDepartmentId, StringComparison.Ordinal))
                {
                    patient.ChangeDepartment(targetDepartment, checkHospitalizationPlace: false);
                    actionInvoked = "ChangeDepartment";
                }

                var hospitalization = patient.GetComponent<HospitalizationComponent>();
                if (hospitalization != null && hospitalization.IsHospitalized())
                {
                    hospitalization.HospitalizationChange();
                    actionInvoked = string.IsNullOrEmpty(actionInvoked)
                        ? "HospitalizationChange"
                        : actionInvoked + "+HospitalizationChange";
                }
            }
            catch (Exception ex)
            {
                blockerReason = "cannot_change_department";
                Log("Failed to execute case transfer: " + DescribeException(ex));
                return false;
            }

            if (!string.Equals(GetCaseRuntimeDepartmentId(patient, patientCase), targetDepartmentId, StringComparison.Ordinal))
            {
                blockerReason = "cannot_change_department";
                return false;
            }

            CommitCaseRouteAdvance(patient, patientCase, nextDiagnosis, nextCondition, targetDepartmentId);
            ResumeCaseAfterDepartmentAdvance(patient, nextDiagnosis);
            AddTimeline(patientCase, "Case route transfer executed to department " + targetDepartmentId + ".");
            Save();
            return true;
        }

        private static bool TryAdvanceCaseHospitalization(
            BehaviorPatient patient,
            PatientCase patientCase,
            CaseDiagnosis nextDiagnosis,
            GameDBMedicalCondition nextCondition,
            string targetDepartmentId,
            out string blockerReason,
            out string actionInvoked)
        {
            blockerReason = string.Empty;
            actionInvoked = string.Empty;
            var targetDepartment = ResolveDepartment(targetDepartmentId);
            GameDBTreatment hospitalizationTreatment;
            GameDBRoomType roomType;
            var currentDepartmentId = GetCaseRuntimeDepartmentId(patient, patientCase);
            var hospitalization = patient.GetComponent<HospitalizationComponent>();
            var alreadyHospitalized = hospitalization != null && hospitalization.IsHospitalized();
            var requiresDepartmentTransfer = !string.Equals(currentDepartmentId, targetDepartmentId, StringComparison.Ordinal);
            if (!CanAdvanceCaseToHospitalization(patient, patientCase, nextDiagnosis, nextCondition, targetDepartment, out blockerReason)
                && !(alreadyHospitalized && string.Equals(currentDepartmentId, targetDepartmentId, StringComparison.Ordinal)))
            {
                return false;
            }

            if (!TryResolveHospitalizationRequest(nextCondition, out hospitalizationTreatment, out roomType)
                || hospitalizationTreatment == null
                || roomType == null)
            {
                blockerReason = "hospitalization_unavailable";
                return false;
            }

            var requestedBefore = GetRequestedHospitalizationTreatmentId(patient);
            var stateBefore = patient.m_state.m_patientState;
            try
            {
                if (requiresDepartmentTransfer)
                {
                    patient.ChangeDepartment(targetDepartment, checkHospitalizationPlace: false);
                    actionInvoked = "ChangeDepartment";
                }

                hospitalization = patient.GetComponent<HospitalizationComponent>();
                if (hospitalization != null && hospitalization.IsHospitalized())
                {
                    hospitalization.HospitalizationChange();
                    actionInvoked = string.IsNullOrEmpty(actionInvoked)
                        ? "HospitalizationChange"
                        : actionInvoked + "+HospitalizationChange";
                }
                else
                {
                    patient.RequestHospitalization(hospitalizationTreatment, roomType);
                    actionInvoked = string.IsNullOrEmpty(actionInvoked)
                        ? "RequestHospitalization"
                        : actionInvoked + "+RequestHospitalization";

                    RefreshHospitalizationFlow(patient);
                    hospitalization = patient.GetComponent<HospitalizationComponent>();
                    if (hospitalization != null)
                    {
                        hospitalization.HospitalizationChange();
                        actionInvoked += "+HospitalizationChange";
                    }
                }
            }
            catch (Exception ex)
            {
                blockerReason = "hospitalization_unavailable";
                Log("Failed to execute case hospitalization: " + DescribeException(ex));
                return false;
            }

            var requestedAfter = GetRequestedHospitalizationTreatmentId(patient);
            var stateChanged = patient.m_state.m_patientState != stateBefore;
            var requestedTarget = !string.IsNullOrEmpty(requestedAfter)
                && string.Equals(requestedAfter, SafeDatabaseId(hospitalizationTreatment), StringComparison.Ordinal);
            var hospitalizationStarted = hospitalization != null && hospitalization.IsHospitalized();
            var departmentReady = !requiresDepartmentTransfer
                || string.Equals(GetCaseRuntimeDepartmentId(patient, patientCase), targetDepartmentId, StringComparison.Ordinal);
            if (!departmentReady || (!hospitalizationStarted && !requestedTarget && !stateChanged && string.Equals(requestedBefore, requestedAfter, StringComparison.Ordinal)))
            {
                blockerReason = "hospitalization_unavailable";
                return false;
            }

            CommitCaseRouteAdvance(patient, patientCase, nextDiagnosis, nextCondition, targetDepartmentId);
            AddTimeline(patientCase, "Case route hospitalization executed for diagnosis " + nextDiagnosis.DiagnosisId + ".");
            Save();
            return true;
        }

        private static void CommitCaseRouteAdvance(
            BehaviorPatient patient,
            PatientCase patientCase,
            CaseDiagnosis nextDiagnosis,
            GameDBMedicalCondition nextCondition,
            string targetDepartmentId)
        {
            if (patient == null || patientCase == null || nextDiagnosis == null || nextCondition == null)
            {
                return;
            }

            PromoteDiagnosisForInvestigation(nextDiagnosis);
            var currentCondition = GetPrimaryDiagnosis(patient);
            if (!string.Equals(SafeDatabaseId(currentCondition), nextDiagnosis.DiagnosisId, StringComparison.Ordinal))
            {
                patient.SetMedicalCondition(nextCondition, false, 0);
            }

            RegisterCurrentMedicalCondition(patient, patientCase);
            patient.m_state.m_untreated = true;
            patient.m_state.m_sentAway = false;
            patient.m_state.m_sentHome = false;
            patient.m_state.m_waitingForPlayer = false;
            patientCase.ActiveDepartmentId = targetDepartmentId;
            ActivateDepartmentCare(patientCase, targetDepartmentId);
            patientCase.RiskScore = CalculateRisk(patientCase);
            ClearPendingDiagnosticFocus(patient);
        }

        private static void ResumeCaseAfterDepartmentAdvance(BehaviorPatient patient, CaseDiagnosis nextDiagnosis)
        {
            if (patient == null || patient.m_state == null || nextDiagnosis == null)
            {
                return;
            }

            try
            {
                if (nextDiagnosis.Status == CaseDiagnosisStatus.Diagnosed || nextDiagnosis.Status == CaseDiagnosisStatus.Active)
                {
                    TryResumeCaseProcedureSelection(patient);
                    return;
                }

                var checkDoctor = AccessTools.Method(typeof(BehaviorPatient), "CheckDoctorForDiagnosis", Type.EmptyTypes);
                if (checkDoctor != null)
                {
                    checkDoctor.Invoke(patient, null);
                }
            }
            catch (Exception ex)
            {
                Log("Failed to resume case after department advance: " + DescribeException(ex));
            }
        }

        private static void RefreshHospitalizationFlow(BehaviorPatient patient)
        {
            if (patient == null)
            {
                return;
            }

            try
            {
                var checkPlanned = AccessTools.Method(typeof(BehaviorPatient), "CheckPlannedHospitalization", Type.EmptyTypes);
                if (checkPlanned != null)
                {
                    checkPlanned.Invoke(patient, null);
                }

                var checkHospitalization = AccessTools.Method(typeof(BehaviorPatient), "CheckHospitalization", Type.EmptyTypes);
                if (checkHospitalization != null)
                {
                    checkHospitalization.Invoke(patient, null);
                }
            }
            catch (Exception ex)
            {
                Log("Failed to refresh hospitalization flow: " + DescribeException(ex));
            }
        }

        private static string GetRequestedHospitalizationTreatmentId(BehaviorPatient patient)
        {
            if (patient == null || patient.m_state == null)
            {
                return null;
            }

            var requestedTreatment = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(patient.m_state, "m_requestedHospitalizationTreatment")) as GameDBTreatment;
            return SafeDatabaseId(requestedTreatment);
        }

        private static void LogCaseRouteDecision(
            BehaviorPatient patient,
            CaseRouteDecision decision,
            string initiator,
            bool executionAttempted,
            bool executionSucceeded,
            string actionInvoked,
            string blockerOverride,
            float? rateLimitSeconds)
        {
            if (patient == null || decision == null)
            {
                return;
            }

            var patientCase = GetCase(patient);
            var blockerReason = !string.IsNullOrEmpty(blockerOverride)
                ? blockerOverride
                : (string.IsNullOrEmpty(decision.BlockerReason) ? "-" : decision.BlockerReason);
            var text = BuildTraceDecisionContext(patient, patientCase)
                + ";source=" + (string.IsNullOrEmpty(initiator) ? "-" : initiator)
                + ";target_diagnosis=" + (string.IsNullOrEmpty(decision.DiagnosisId) ? "-" : decision.DiagnosisId)
                + ";target_department=" + (string.IsNullOrEmpty(decision.TargetDepartmentId) ? "-" : decision.TargetDepartmentId)
                + ";diagnosis_status=" + decision.DiagnosisStatus
                + ";hospitalization_flag=" + decision.NeedsHospitalization
                + ";route_exists=" + decision.RouteExists
                + ";route_executable_now=" + decision.CanExecuteNow
                + ";execution_attempted=" + executionAttempted
                + ";execution_succeeded=" + executionSucceeded
                + ";action_invoked=" + (string.IsNullOrEmpty(actionInvoked) ? "-" : actionInvoked);
            if (rateLimitSeconds.HasValue)
            {
                TraceLoggingService.LogRateLimitedPatientEvent(
                    patient,
                    "DECISION",
                    "event=decision;method=HasCaseTransferOrHospitalizationRoute;result=" + (decision.RouteExists && decision.CanExecuteNow)
                    + ";blocker=" + blockerReason
                    + ";details=" + text,
                    rateLimitSeconds.Value);
            }
            else
            {
                TraceLoggingService.LogPatientDecision(patient, "HasCaseTransferOrHospitalizationRoute", decision.RouteExists && decision.CanExecuteNow, blockerReason, text);
            }
        }

        private static void LogCaseRouteAnomaly(BehaviorPatient patient, CaseRouteDecision decision, string initiator, string blockerReason, string actionInvoked)
        {
            if (patient == null || decision == null)
            {
                return;
            }

            TraceLoggingService.LogPatientAnomaly(
                patient,
                "route_exists_but_not_executed",
                "source=" + (string.IsNullOrEmpty(initiator) ? "-" : initiator)
                + ";target_diagnosis=" + (string.IsNullOrEmpty(decision.DiagnosisId) ? "-" : decision.DiagnosisId)
                + ";target_department=" + (string.IsNullOrEmpty(decision.TargetDepartmentId) ? "-" : decision.TargetDepartmentId)
                + ";action_invoked=" + (string.IsNullOrEmpty(actionInvoked) ? "-" : actionInvoked)
                + ";blocker_reason=" + (string.IsNullOrEmpty(blockerReason) ? "-" : blockerReason));
        }

        private static bool TryScheduleCaseAwareTreatment(BehaviorPatient patient)
        {
            if (!Enabled || patient == null || patient.m_state == null)
            {
                TraceLoggingService.LogPatientAction(patient, "TryScheduleCaseAwareTreatment", "failed", "reason=missing_patient_state");
                return false;
            }

            var procedure = patient.GetComponent<ProcedureComponent>();
            var queue = procedure == null || procedure.m_state == null ? null : procedure.m_state.m_procedureQueue;
            if (procedure == null || queue == null)
            {
                TraceLoggingService.LogPatientAction(patient, "TryScheduleCaseAwareTreatment", "failed", "reason=missing_procedure_queue");
                return false;
            }

            if (queue.m_activeTreatmentStates.Count > 0 || queue.m_plannedTreatmentStates.Count > 0)
            {
                TryResumeCaseProcedureSelection(patient);
                TraceLoggingService.LogPatientAction(patient, "TryScheduleCaseAwareTreatment", "already_queued", BuildTraceDecisionContext(patient, GetCase(patient)));
                return true;
            }

            var result = TreatmentPlanningResult.NOT_PLANNED;
            PlanSecondaryTreatments(procedure, onlyCritical: false, ref result);
            var scheduled = result == TreatmentPlanningResult.PLANNED || HasCaseAvailableTreatmentOrProgress(patient);
            if (scheduled)
            {
                TryResumeCaseProcedureSelection(patient);
                TraceLoggingService.LogPatientAction(
                    patient,
                    "TryScheduleCaseAwareTreatment",
                    "scheduled",
                    BuildTraceDecisionContext(patient, GetCase(patient))
                    + ";planning_result=" + result);
                return true;
            }

            TraceLoggingService.LogPatientAction(
                patient,
                "TryScheduleCaseAwareTreatment",
                "failed",
                BuildTraceDecisionContext(patient, GetCase(patient))
                + ";planning_result=" + result
                + ";reason=no_case_treatment_available");
            return false;
        }

        private static void TryResumeCaseProcedureSelection(BehaviorPatient patient)
        {
            if (patient == null || patient.m_state == null)
            {
                return;
            }

            try
            {
                patient.m_state.m_sentHome = false;
                patient.m_state.m_waitingForPlayer = false;
                patient.m_state.m_untreated = true;
                var selectNextProcedure = AccessTools.Method(typeof(BehaviorPatient), "SelectNextProcedure");
                if (selectNextProcedure != null)
                {
                    selectNextProcedure.Invoke(patient, null);
                }
            }
            catch (Exception ex)
            {
                Log("Failed to resume case procedure selection: " + DescribeException(ex));
            }
        }

        private static void TrySwitchBackToDoctor(BehaviorPatient patient)
        {
            if (patient == null || patient.m_state == null)
            {
                return;
            }

            try
            {
                patient.m_state.m_sentHome = false;
                patient.m_state.m_waitingForPlayer = false;
                patient.m_state.m_untreated = true;
                patient.SwitchState(PatientState.GoingToDoctor);
                var checkDoctor = AccessTools.Method(typeof(BehaviorPatient), "CheckDoctorForDiagnosis", Type.EmptyTypes);
                if (checkDoctor != null)
                {
                    checkDoctor.Invoke(patient, null);
                }
            }
            catch (Exception ex)
            {
                Log("Failed to switch case back to doctor: " + DescribeException(ex));
            }
        }

        private static ProcedureSceneAvailability CreateProcedureAvailability(GameDBProcedure procedure, GLib.Entity entity, Department department)
        {
            if (procedure == null || entity == null || department == null)
            {
                return ProcedureSceneAvailability.UNKNOWN;
            }

            var scene = ProcedureSceneFactory.CreateProcedureScene(
                procedure,
                entity,
                department,
                null,
                AccessRights.PATIENT_PROCEDURE,
                ProcedureSceneType.QUERY,
                EquipmentListRules.ANY);
            return scene == null ? ProcedureSceneAvailability.UNKNOWN : scene.m_availability;
        }

        private static bool CanCurrentDoctorPrescribe(BehaviorPatient patient, GameDBProcedure procedure)
        {
            if (patient == null || procedure == null || patient.m_state == null || patient.m_state.m_doctor == null)
            {
                return true;
            }

            if (!patient.m_state.m_doctor.CheckEntity())
            {
                return false;
            }

            var doctor = patient.m_state.m_doctor.GetEntity();
            var employee = doctor == null ? null : doctor.GetComponent<EmployeeComponent>();
            if (employee == null)
            {
                return false;
            }

            if (procedure.RequiredSkillsToPrescribe != null)
            {
                return employee.HasAnySkill(procedure.RequiredSkillsToPrescribe);
            }

            return procedure.RequiredDoctorQualifications == null || employee.HasAnySkill(procedure.RequiredDoctorQualifications);
        }

        private static bool SymptomHasTreatment(GameDBSymptom symptom, GameDBTreatment treatment)
        {
            if (symptom == null || treatment == null || symptom.Treatments == null)
            {
                return false;
            }

            for (var i = 0; i < symptom.Treatments.Length; i++)
            {
                if (symptom.Treatments[i] != null && symptom.Treatments[i].Entry == treatment)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool AreKnownSymptomsTreated(CaseDiagnosis diagnosis)
        {
            if (diagnosis == null || diagnosis.KnownSymptomIds.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < diagnosis.KnownSymptomIds.Count; i++)
            {
                if (!diagnosis.TreatedSymptomIds.Contains(diagnosis.KnownSymptomIds[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryAdvanceDiagnosticFocusWithinDepartment(BehaviorPatient patient, PatientCase patientCase)
        {
            return TryAdvanceDiagnosticFocusWithinDepartment(patient, patientCase, null);
        }

        private static bool TryAdvanceDiagnosticFocusWithinDepartment(BehaviorPatient patient, PatientCase patientCase, string requiredDepartmentId)
        {
            if (!Enabled || patient == null || patientCase == null || patientCase.Complete || patient.m_state == null)
            {
                TraceLoggingService.LogPatientAction(patient, "TryAdvanceDiagnosticFocusWithinDepartment", "failed", "reason=missing_case_state");
                return false;
            }

            var currentCondition = GetPrimaryDiagnosis(patient);
            if (currentCondition == null)
            {
                TraceLoggingService.LogPatientAction(patient, "TryAdvanceDiagnosticFocusWithinDepartment", "failed", "reason=current_condition_missing;" + BuildTraceDecisionContext(patient, patientCase));
                return false;
            }

            var currentDepartmentId = patient.GetDepartment() == null || patient.GetDepartment().GetDepartmentType() == null
                ? patientCase.ActiveDepartmentId
                : patient.GetDepartment().GetDepartmentType().DatabaseID.ToString();
            if (string.IsNullOrEmpty(currentDepartmentId))
            {
                TraceLoggingService.LogPatientAction(patient, "TryAdvanceDiagnosticFocusWithinDepartment", "failed", "reason=runtime_department_missing;" + BuildTraceDecisionContext(patient, patientCase));
                return false;
            }

            if (!string.IsNullOrEmpty(requiredDepartmentId) && !string.Equals(requiredDepartmentId, currentDepartmentId, StringComparison.Ordinal))
            {
                TraceLoggingService.LogPatientAction(patient, "TryAdvanceDiagnosticFocusWithinDepartment", "failed", "reason=required_department_mismatch;required_department=" + requiredDepartmentId + ";current_department=" + currentDepartmentId);
                return false;
            }

            CaseDiagnosis currentDiagnosis = null;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis != null && diagnosis.DiagnosisId == currentCondition.DatabaseID.ToString())
                {
                    currentDiagnosis = diagnosis;
                    break;
                }
            }

            if (currentDiagnosis == null
                || currentDiagnosis.DepartmentId != currentDepartmentId
                || currentDiagnosis.Status != CaseDiagnosisStatus.Diagnosed)
            {
                TraceLoggingService.LogPatientAction(patient, "TryAdvanceDiagnosticFocusWithinDepartment", "failed", "reason=current_focus_not_advanceable;" + BuildTraceDecisionContext(patient, patientCase));
                return false;
            }

            var nextDiagnosis = SelectNextUndiagnosedDiagnosisInDepartment(patientCase, currentDepartmentId, currentDiagnosis.DiagnosisId);
            if (nextDiagnosis == null)
            {
                TraceLoggingService.LogPatientAction(patient, "TryAdvanceDiagnosticFocusWithinDepartment", "failed", "reason=no_next_diagnosis_in_department;current_diagnosis=" + currentDiagnosis.DiagnosisId);
                return false;
            }

            var nextCondition = ResolveDiagnosis(nextDiagnosis.DiagnosisId);
            if (nextCondition == null)
            {
                TraceLoggingService.LogPatientAction(patient, "TryAdvanceDiagnosticFocusWithinDepartment", "failed", "reason=next_condition_missing;next_diagnosis=" + nextDiagnosis.DiagnosisId);
                return false;
            }

            var procedure = patient.GetComponent<ProcedureComponent>();
            var queue = procedure == null || procedure.m_state == null ? null : procedure.m_state.m_procedureQueue;
            if (procedure == null || queue == null || !HasAvailableDiagnosticWorkForDiagnosis(patient, procedure, queue, nextDiagnosis, nextCondition))
            {
                TraceLoggingService.LogPatientAction(
                    patient,
                    "TryAdvanceDiagnosticFocusWithinDepartment",
                    "failed",
                    BuildTraceDecisionContext(patient, patientCase)
                    + ";current_diagnosis=" + currentDiagnosis.DiagnosisId
                    + ";next_diagnosis=" + nextDiagnosis.DiagnosisId
                    + ";reason=no_available_diagnostic_work");
                return false;
            }

            PromoteDiagnosisForInvestigation(nextDiagnosis);
            patient.SetMedicalCondition(nextCondition, false, 0);
            RegisterCurrentMedicalCondition(patient, patientCase);
            patient.m_state.m_untreated = true;
            patient.m_state.m_sentHome = false;
            patient.m_state.m_waitingForPlayer = false;
            patientCase.ActiveDepartmentId = currentDepartmentId;
            ActivateDepartmentCare(patientCase, currentDepartmentId);

            try
            {
                var checkDoctor = AccessTools.Method(typeof(BehaviorPatient), "CheckDoctorForDiagnosis", Type.EmptyTypes);
                if (checkDoctor != null)
                {
                    checkDoctor.Invoke(patient, null);
                }

                patient.SwitchState(PatientState.GoingToDoctor);
            }
            catch (Exception ex)
            {
                Log("Failed to advance diagnostic focus within department: " + DescribeException(ex));
            }

            AddTimeline(patientCase, "Diagnostic focus advanced within department " + currentDepartmentId + ".");
            TraceLoggingService.LogPatientAction(
                patient,
                "TryAdvanceDiagnosticFocusWithinDepartment",
                "advanced",
                BuildTraceDecisionContext(patient, patientCase)
                + ";current_diagnosis=" + currentDiagnosis.DiagnosisId
                + ";next_diagnosis=" + nextDiagnosis.DiagnosisId
                + ";required_department=" + (string.IsNullOrEmpty(requiredDepartmentId) ? "-" : requiredDepartmentId));
            return true;
        }

        private static CaseDiagnosis SelectNextUndiagnosedDiagnosisInDepartment(PatientCase patientCase, string currentDepartmentId, string excludeDiagnosisId)
        {
            if (patientCase == null || string.IsNullOrEmpty(currentDepartmentId))
            {
                return null;
            }

            CaseDiagnosis best = null;
            var bestScore = int.MinValue;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis == null
                    || diagnosis.Status == CaseDiagnosisStatus.Treated
                    || diagnosis.Status == CaseDiagnosisStatus.Diagnosed
                    || diagnosis.DepartmentId != currentDepartmentId
                    || diagnosis.DiagnosisId == excludeDiagnosisId)
                {
                    continue;
                }

                var score = 0;
                if (diagnosis.CollapseCapable)
                {
                    score += 300;
                }

                if (string.Equals(diagnosis.Hazard, "High", StringComparison.OrdinalIgnoreCase))
                {
                    score += 200;
                }
                else if (string.Equals(diagnosis.Hazard, "Medium", StringComparison.OrdinalIgnoreCase))
                {
                    score += 100;
                }

                score += diagnosis.KnownSymptomIds.Count * 10;
                score -= Math.Max(0, diagnosis.SymptomIds.Count - diagnosis.KnownSymptomIds.Count) * 5;

                if (score > bestScore)
                {
                    best = diagnosis;
                    bestScore = score;
                }
            }

            return best;
        }

        private static bool HasUndiagnosedDiagnosisInDepartment(PatientCase patientCase, string currentDepartmentId, string excludeDiagnosisId)
        {
            return SelectNextUndiagnosedDiagnosisInDepartment(patientCase, currentDepartmentId, excludeDiagnosisId) != null;
        }

        private static bool HasAvailableDiagnosticWorkForDiagnosis(
            BehaviorPatient patient,
            ProcedureComponent procedureComponent,
            ProcedureQueue queue,
            CaseDiagnosis diagnosis,
            GameDBMedicalCondition condition)
        {
            if (patient == null || procedureComponent == null || queue == null || diagnosis == null || condition == null)
            {
                return false;
            }

            var entity = ReflectionHelpers.GetField(procedureComponent, "m_entity") as GLib.Entity;
            if (entity == null)
            {
                return false;
            }

            if (condition.Symptoms == null)
            {
                return HasAvailableConditionLevelDiagnosticWork(patient, queue, condition);
            }

            var scratchExaminations = new List<GameDBExamination>();
            for (var symptomIndex = 0; symptomIndex < condition.Symptoms.Length; symptomIndex++)
            {
                var symptom = GetSymptom(condition.Symptoms[symptomIndex]);
                if (symptom == null || symptom.Examinations == null)
                {
                    continue;
                }

                var symptomId = symptom.DatabaseID.ToString();
                if (diagnosis.KnownSymptomIds.Contains(symptomId))
                {
                    continue;
                }

                for (var examIndex = 0; examIndex < symptom.Examinations.Length; examIndex++)
                {
                    var examination = symptom.Examinations[examIndex] == null ? null : symptom.Examinations[examIndex].Entry;
                    if (!CanUseExaminationForDiagnosticRoute(queue, scratchExaminations, examination))
                    {
                        continue;
                    }

                    if (ProcedureScene.IsProcedureAvailable(GetSecondaryExaminationAvailability(entity, patient, examination)))
                    {
                        return true;
                    }
                }
            }

            return HasAvailableConditionLevelDiagnosticWork(patient, queue, condition);
        }

        private static bool TryContinueSameDepartmentOfficeDiagnostics(BehaviorPatient patient, PatientCase patientCase, string currentDepartmentId, string excludeDiagnosisId)
        {
            if (!Enabled || patient == null || patientCase == null || patientCase.Complete || string.IsNullOrEmpty(currentDepartmentId))
            {
                return false;
            }

            var procedure = patient.GetComponent<ProcedureComponent>();
            var queue = procedure == null || procedure.m_state == null ? null : procedure.m_state.m_procedureQueue;
            if (procedure == null || queue == null)
            {
                return false;
            }

            if (queue.m_activeExamination != null || queue.m_plannedExaminationStates.Count > 0 || queue.m_labProcedures.Count > 0)
            {
                return false;
            }

            var nextDiagnosis = SelectNextUndiagnosedDiagnosisInDepartment(patientCase, currentDepartmentId, excludeDiagnosisId);
            if (nextDiagnosis == null)
            {
                return false;
            }

            var nextCondition = ResolveDiagnosis(nextDiagnosis.DiagnosisId);
            if (nextCondition == null)
            {
                return false;
            }

            var examination = FindImmediateOfficeExaminationForDiagnosis(patient, procedure, queue, nextDiagnosis, nextCondition);
            if (examination == null)
            {
                return false;
            }

            PromoteDiagnosisForInvestigation(nextDiagnosis);
            patient.SetMedicalCondition(nextCondition, false, 0);
            RegisterCurrentMedicalCondition(patient, patientCase);
            patient.m_state.m_untreated = true;
            patient.m_state.m_sentHome = false;
            patient.m_state.m_waitingForPlayer = false;
            patientCase.ActiveDepartmentId = currentDepartmentId;
            ActivateDepartmentCare(patientCase, currentDepartmentId);
            patient.ScheduleExamination(examination);
            MuteCaseProgressNotifications(patient, 6f);
            return true;
        }

        private static GameDBExamination FindImmediateOfficeExaminationForDiagnosis(
            BehaviorPatient patient,
            ProcedureComponent procedureComponent,
            ProcedureQueue queue,
            CaseDiagnosis diagnosis,
            GameDBMedicalCondition condition)
        {
            if (patient == null || procedureComponent == null || queue == null || diagnosis == null || condition == null)
            {
                return null;
            }

            var entity = ReflectionHelpers.GetField(procedureComponent, "m_entity") as GLib.Entity;
            if (entity == null)
            {
                return null;
            }

            if (condition.Symptoms != null)
            {
                for (var symptomIndex = 0; symptomIndex < condition.Symptoms.Length; symptomIndex++)
                {
                    var symptom = GetSymptom(condition.Symptoms[symptomIndex]);
                    if (symptom == null || symptom.Examinations == null)
                    {
                        continue;
                    }

                    var symptomId = symptom.DatabaseID.ToString();
                    if (diagnosis.KnownSymptomIds.Contains(symptomId))
                    {
                        continue;
                    }

                    for (var examIndex = 0; examIndex < symptom.Examinations.Length; examIndex++)
                    {
                        var examination = symptom.Examinations[examIndex] == null ? null : symptom.Examinations[examIndex].Entry;
                        if (IsImmediateOfficeExaminationCandidate(patient, queue, entity, examination))
                        {
                            return examination;
                        }
                    }
                }
            }

            if (condition.Examinations != null)
            {
                for (var i = 0; i < condition.Examinations.Length; i++)
                {
                    var examination = condition.Examinations[i] == null ? null : condition.Examinations[i].Entry;
                    if (IsImmediateOfficeExaminationCandidate(patient, queue, entity, examination))
                    {
                        return examination;
                    }
                }
            }

            return null;
        }

        private static bool IsImmediateOfficeExaminationCandidate(BehaviorPatient patient, ProcedureQueue queue, GLib.Entity entity, GameDBExamination examination)
        {
            if (patient == null || queue == null || entity == null || examination == null || examination.Procedure == null)
            {
                return false;
            }

            if (!CanExposeSecondaryExamination(queue, new List<GameDBExamination>(), examination))
            {
                return false;
            }

            if (examination.LabTestingExaminationRef != null)
            {
                return false;
            }

            if (examination.Procedure.DetachedDepartmentRef != null && examination.Procedure.DetachedDepartmentRef.Entry != null)
            {
                return false;
            }

            var availability = GetSecondaryExaminationAvailability(entity, patient, examination);
            return ProcedureScene.IsProcedureAvailable(availability);
        }

        internal static bool ShouldBlockFurtherSameDepartmentExaminations(BehaviorPatient patient)
        {
            if (!Enabled || patient == null)
            {
                return false;
            }

            var patientCase = GetCase(patient);
            if (patientCase == null || patientCase.Complete || patient.GetDepartment() == null || patient.GetDepartment().GetDepartmentType() == null)
            {
                return false;
            }

            var currentDepartmentId = patient.GetDepartment().GetDepartmentType().DatabaseID.ToString();
            var hasCurrentDepartmentDiagnosis = false;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis == null || diagnosis.DepartmentId != currentDepartmentId || diagnosis.Status == CaseDiagnosisStatus.Treated)
                {
                    continue;
                }

                hasCurrentDepartmentDiagnosis = true;
                if (diagnosis.Status != CaseDiagnosisStatus.Diagnosed)
                {
                    return false;
                }
            }

            return hasCurrentDepartmentDiagnosis;
        }

        private static void PromoteDiagnosisForInvestigation(CaseDiagnosis diagnosis)
        {
            if (diagnosis == null)
            {
                return;
            }

            if (diagnosis.Status == CaseDiagnosisStatus.Hidden)
            {
                diagnosis.Status = CaseDiagnosisStatus.Suspected;
            }
        }

        internal static bool ShouldReplaceVanillaExaminationAvailability(ProcedureComponent procedureComponent, out bool blockAll)
        {
            blockAll = false;
            if (!Enabled || procedureComponent == null)
            {
                return false;
            }

            var entity = ReflectionHelpers.GetField(procedureComponent, "m_entity") as GLib.Entity;
            var patient = entity == null ? null : entity.GetComponent<BehaviorPatient>();
            var patientCase = GetCase(patient);
            if (patient == null || patientCase == null || patientCase.Complete)
            {
                return false;
            }

            if (ShouldBlockFurtherSameDepartmentExaminations(patient))
            {
                blockAll = true;
                return true;
            }

            var focusDiagnosis = GetCurrentDiagnosticFocusDiagnosis(patient, patientCase);
            if (focusDiagnosis == null)
            {
                return false;
            }

            return false;
        }

        private static bool TryAdvanceCompatibilityConditionWithinDepartment(BehaviorPatient patient, PatientCase patientCase)
        {
            if (!Enabled || patient == null || patientCase == null || patientCase.Complete || patient.m_state == null)
            {
                return false;
            }

            var currentCondition = GetPrimaryDiagnosis(patient);
            if (currentCondition == null)
            {
                return false;
            }

            var currentDepartmentId = patient.GetDepartment() == null || patient.GetDepartment().GetDepartmentType() == null
                ? patientCase.ActiveDepartmentId
                : patient.GetDepartment().GetDepartmentType().DatabaseID.ToString();
            if (string.IsNullOrEmpty(currentDepartmentId))
            {
                return false;
            }

            CaseDiagnosis currentDiagnosis = null;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis != null && diagnosis.DiagnosisId == currentCondition.DatabaseID.ToString())
                {
                    currentDiagnosis = diagnosis;
                    break;
                }
            }

            if (currentDiagnosis == null
                || currentDiagnosis.Status != CaseDiagnosisStatus.Treated
                || currentDiagnosis.DepartmentId != currentDepartmentId)
            {
                return false;
            }

            var nextDiagnosis = CaseCarePlanner.SelectNextDiagnosis(patientCase, currentDepartmentId);
            if (nextDiagnosis == null
                || nextDiagnosis.Status == CaseDiagnosisStatus.Treated
                || nextDiagnosis.DepartmentId != currentDepartmentId
                || nextDiagnosis.DiagnosisId == currentDiagnosis.DiagnosisId)
            {
                return false;
            }

            var nextCondition = ResolveDiagnosis(nextDiagnosis.DiagnosisId);
            if (nextCondition == null)
            {
                return false;
            }

            PromoteDiagnosisForInvestigation(nextDiagnosis);
            patient.SetMedicalCondition(nextCondition, false, 0);
            RegisterCurrentMedicalCondition(patient, patientCase);
            patient.m_state.m_untreated = true;
            patient.m_state.m_sentHome = false;
            ActivateDepartmentCare(patientCase, currentDepartmentId);

            try
            {
                if (nextDiagnosis.Status == CaseDiagnosisStatus.Diagnosed || nextDiagnosis.Status == CaseDiagnosisStatus.Active)
                {
                    TryResumeCaseProcedureSelection(patient);
                }
                else
                {
                    var method = AccessTools.Method(typeof(BehaviorPatient), "CheckDoctorForDiagnosis", Type.EmptyTypes);
                    if (method != null)
                    {
                        method.Invoke(patient, null);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Failed to refresh doctor after compatibility diagnosis advance: " + DescribeException(ex));
            }

            AddTimeline(patientCase, "Compatibility diagnosis advanced within department " + currentDepartmentId + ".");
            return true;
        }

        private static void QueueDiagnosticFocusAdvanceWithinDepartment(BehaviorPatient patient, PatientCase patientCase)
        {
            if (!Enabled || patient == null || patientCase == null || patientCase.Complete || patient.m_state == null)
            {
                return;
            }

            var currentCondition = GetPrimaryDiagnosis(patient);
            if (currentCondition == null)
            {
                return;
            }

            var currentDepartmentId = patient.GetDepartment() == null || patient.GetDepartment().GetDepartmentType() == null
                ? patientCase.ActiveDepartmentId
                : patient.GetDepartment().GetDepartmentType().DatabaseID.ToString();
            if (string.IsNullOrEmpty(currentDepartmentId))
            {
                return;
            }

            CaseDiagnosis currentDiagnosis = null;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis != null && diagnosis.DiagnosisId == currentCondition.DatabaseID.ToString())
                {
                    currentDiagnosis = diagnosis;
                    break;
                }
            }

            if (currentDiagnosis == null
                || currentDiagnosis.DepartmentId != currentDepartmentId
                || currentDiagnosis.Status != CaseDiagnosisStatus.Diagnosed)
            {
                return;
            }

            var nextDiagnosis = SelectNextUndiagnosedDiagnosisInDepartment(patientCase, currentDepartmentId, currentDiagnosis.DiagnosisId);
            if (nextDiagnosis == null)
            {
                return;
            }

            var procedure = patient.GetComponent<ProcedureComponent>();
            var queue = procedure == null || procedure.m_state == null ? null : procedure.m_state.m_procedureQueue;
            var nextCondition = ResolveDiagnosis(nextDiagnosis.DiagnosisId);
            if (procedure == null || queue == null || nextCondition == null || !HasAvailableDiagnosticWorkForDiagnosis(patient, procedure, queue, nextDiagnosis, nextCondition))
            {
                return;
            }

            var entity = ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            if (entity == null)
            {
                return;
            }

            PendingDiagnosticFocuses[entity.GetEntityID()] = new PendingDiagnosticFocus
            {
                DepartmentId = currentDepartmentId,
                RequestedAt = Time.realtimeSinceStartup
            };
        }

        private static bool IsCaseFullyTreated(PatientCase patientCase)
        {
            if (patientCase == null)
            {
                return true;
            }

            if (patientCase.Complete)
            {
                return true;
            }

            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                if (patientCase.Diagnoses[i] != null && patientCase.Diagnoses[i].Status != CaseDiagnosisStatus.Treated)
                {
                    return false;
                }
            }

            return true;
        }

        public static void MarkCompletedByVanillaDischarge(BehaviorPatient patient)
        {
            var patientCase = GetCase(patient);
            if (patientCase == null || patientCase.Complete)
            {
                return;
            }

            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                patientCase.Diagnoses[i].Status = CaseDiagnosisStatus.Treated;
            }

            patientCase.Complete = true;
            AddTimeline(patientCase, "Case closed by vanilla discharge bridge.");
            Save();
        }

        public static bool TryAdvanceBeforeDischarge(BehaviorPatient patient)
        {
            var patientCase = GetCase(patient);
            if (patientCase == null || patientCase.Complete)
            {
                return true;
            }

            var currentDepartmentId = patient.GetDepartment() == null || patient.GetDepartment().GetDepartmentType() == null
                ? patientCase.ActiveDepartmentId
                : patient.GetDepartment().GetDepartmentType().DatabaseID.ToString();
            var currentPrimary = GetPrimaryDiagnosis(patient);
            MarkDepartmentCheckpointResolved(patientCase, currentDepartmentId, currentPrimary == null ? null : currentPrimary.DatabaseID.ToString());

            var nextDiagnosis = CaseCarePlanner.SelectNextDiagnosis(patientCase, currentDepartmentId);

            if (nextDiagnosis == null)
            {
                patientCase.Complete = true;
                AddTimeline(patientCase, "Case completed; vanilla discharge allowed.");
                Save();
                return true;
            }

            var nextCondition = ResolveDiagnosis(nextDiagnosis.DiagnosisId);
            if (nextCondition == null)
            {
                AddTimeline(patientCase, "Next diagnosis missing from database; discharge blocked by case guard.");
                Save();
                return false;
            }

            var routeDecision = EvaluateCaseTransferOrHospitalization(patient);
            if (routeDecision != null && routeDecision.RouteExists)
            {
                if (TryAdvanceCaseTransferOrHospitalization(patient, routeDecision, "pre_discharge"))
                {
                    return false;
                }

                AddTimeline(
                    patientCase,
                    "Next case route unavailable before discharge: "
                    + (string.IsNullOrEmpty(routeDecision.BlockerReason) ? "unknown" : routeDecision.BlockerReason)
                    + ".");
                Save();
                return false;
            }

            var departmentType = nextCondition.DepartmentRef == null ? null : nextCondition.DepartmentRef.Entry;
            var targetDepartment = departmentType == null || MapScriptInterface.Instance == null ? null : MapScriptInterface.Instance.GetDepartmentOfType(departmentType);
            if (targetDepartment == null || targetDepartment.IsClosed())
            {
                AddTimeline(patientCase, "Next department unavailable; discharge blocked by case guard.");
                Save();
                return false;
            }

            try
            {
                PromoteDiagnosisForInvestigation(nextDiagnosis);
                patient.SetMedicalCondition(nextCondition, false, 0);
                RegisterCurrentMedicalCondition(patient, patientCase);
                if (patient.GetDepartment() != targetDepartment)
                {
                    patient.ChangeDepartment(targetDepartment, checkHospitalizationPlace: false);
                }

                patientCase.ActiveDepartmentId = GetDepartmentId(nextCondition);
                ActivateDepartmentCare(patientCase, patientCase.ActiveDepartmentId);
                patientCase.RiskScore = CalculateRisk(patientCase);
                AddTimeline(patientCase, "Advanced case care to department " + patientCase.ActiveDepartmentId + ".");
                var selectNextProcedure = AccessTools.Method(typeof(BehaviorPatient), "SelectNextProcedure");
                if (selectNextProcedure != null)
                {
                    try
                    {
                        selectNextProcedure.Invoke(patient, null);
                    }
                    catch (Exception selectEx)
                    {
                        AddTimeline(patientCase, "Case advanced; next procedure selection deferred to vanilla update.");
                        Log("Case advanced but SelectNextProcedure failed: " + DescribeException(selectEx));
                    }
                }

                Save();
                return false;
            }
            catch (Exception ex)
            {
                AddTimeline(patientCase, "Failed to advance case; discharge blocked by case guard.");
                Log("Failed to advance before discharge: " + DescribeException(ex));
                Save();
                return false;
            }
        }

        private static void MarkDepartmentCheckpointResolved(PatientCase patientCase, string departmentId, string primaryDiagnosisId)
        {
            if (patientCase == null || string.IsNullOrEmpty(departmentId))
            {
                return;
            }

            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis == null
                    || diagnosis.Status == CaseDiagnosisStatus.Treated
                    || diagnosis.Status == CaseDiagnosisStatus.Hidden
                    || diagnosis.DepartmentId != departmentId)
                {
                    continue;
                }

                if (diagnosis.DiagnosisId == primaryDiagnosisId)
                {
                    diagnosis.Status = CaseDiagnosisStatus.Treated;
                }
            }
        }

        private static PatientCase GetCase(BehaviorPatient patient)
        {
            if (!Enabled || patient == null)
            {
                return null;
            }

            var entity = ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            if (entity == null)
            {
                return null;
            }

            PatientCase patientCase;
            return Cases.TryGetValue(entity.GetEntityID(), out patientCase) ? patientCase : null;
        }

        private static PatientCase GetOrCreateCompatibilityCase(BehaviorPatient patient, GLib.Entity entity)
        {
            if (patient == null || entity == null)
            {
                return null;
            }

            EnsureLoaded();
            PatientCase patientCase;
            if (Cases.TryGetValue(entity.GetEntityID(), out patientCase))
            {
                if (!DoesCaseMatchPatientRuntime(patientCase, patient, entity))
                {
                    ForgetCaseForDeveloper(patient);
                }
                else
                {
                    EnsureRuntimeModel(patient, patientCase, CaseCheckpoint.Bootstrap, "attach");
                    return patientCase;
                }
            }

            if (Cases.TryGetValue(entity.GetEntityID(), out patientCase))
            {
                return patientCase;
            }

            var primary = GetPrimaryDiagnosis(patient);
            if (primary == null)
            {
                return null;
            }

            patientCase = CreateCase(patient, entity, primary, PatientMobility.ANY);
            EnsureRuntimeModel(patient, patientCase, CaseCheckpoint.Bootstrap, "attach_create");
            Cases[patientCase.PatientEntityId] = patientCase;
            RegisterCurrentMedicalCondition(patient, patientCase);
            Save();
            return patientCase;
        }

        private static bool DoesCaseMatchPatientRuntime(PatientCase patientCase, BehaviorPatient patient, GLib.Entity entity)
        {
            if (patientCase == null || patient == null || entity == null)
            {
                return false;
            }

            if (patientCase.PatientEntityId != entity.GetEntityID())
            {
                return false;
            }

            var entityName = entity.Name ?? string.Empty;
            if (!string.Equals(patientCase.PatientName ?? string.Empty, entityName, StringComparison.Ordinal))
            {
                return false;
            }

            var primary = GetPrimaryDiagnosis(patient);
            if (primary != null)
            {
                var primaryId = primary.DatabaseID.ToString();
                var hasPrimary = false;
                for (var i = 0; i < patientCase.Diagnoses.Count; i++)
                {
                    if (patientCase.Diagnoses[i] != null && string.Equals(patientCase.Diagnoses[i].DiagnosisId, primaryId, StringComparison.Ordinal))
                    {
                        hasPrimary = true;
                        break;
                    }
                }

                if (!hasPrimary)
                {
                    return false;
                }
            }

            if (patientCase.Complete && patient.m_state != null)
            {
                if (!patient.m_state.m_sentAway
                    && !patient.m_state.m_sentHome
                    && !patient.m_state.m_deathTriggered
                    && patient.m_state.m_patientState != PatientState.Left
                    && patient.m_state.m_patientState != PatientState.Leaving)
                {
                    return false;
                }
            }

            return true;
        }

        private static PatientCase CreateCase(BehaviorPatient patient, GLib.Entity entity, GameDBMedicalCondition primary, PatientMobility mobility)
        {
            var departmentId = GetDepartmentId(primary);
            var patientCase = new PatientCase
            {
                CaseId = entity.GetEntityID().ToString(CultureInfo.InvariantCulture),
                PatientEntityId = entity.GetEntityID(),
                PatientName = entity.Name ?? "Patient",
                ActiveDepartmentId = departmentId,
                Hopeless = ShouldGenerateHopelessCase()
            };

            AddDiagnosis(patientCase, primary, CaseDiagnosisStatus.Suspected);

            var targetCount = ChooseDiagnosisCount(patientCase.Hopeless);
            BuildCandidatePool(patient, primary, mobility);
            while (patientCase.Diagnoses.Count < targetCount && ScratchConditions.Count > 0)
            {
                var index = UnityEngine.Random.Range(0, ScratchConditions.Count);
                var condition = ScratchConditions[index];
                ScratchConditions.RemoveAt(index);
                if (!CanAddDiagnosis(patientCase, condition))
                {
                    continue;
                }

                AddDiagnosis(patientCase, condition, CaseDiagnosisStatus.Hidden);
            }

            patientCase.RiskScore = CalculateRisk(patientCase);
            ActivateDepartmentCare(patientCase, departmentId);
            AddTimeline(patientCase, "Case created with " + patientCase.Diagnoses.Count + " diagnosis item(s).");
            EnsureRuntimeModel(patient, patientCase, CaseCheckpoint.Bootstrap, "create_case");
            return patientCase;
        }

        private static int ChooseDiagnosisCount(bool hopeless)
        {
            if (hopeless)
            {
                var min = Clamp(RuntimeSettings.Config.HopelessMinDiagnoses.Value, 1, 9);
                var max = Clamp(RuntimeSettings.Config.HopelessMaxDiagnoses.Value, min, 9);
                return UnityEngine.Random.Range(min, max + 1);
            }

            if (UnityEngine.Random.Range(0, 100) >= Clamp(RuntimeSettings.Config.MultiDiagnosisChance.Value, 0, 100))
            {
                return 1;
            }

            var maxDiagnoses = Clamp(RuntimeSettings.Config.MaxDiagnosesPerPatient.Value, 1, 9);
            if (maxDiagnoses <= 2)
            {
                return maxDiagnoses;
            }

            var roll = UnityEngine.Random.Range(0, 100);
            if (roll < 70)
            {
                return Math.Min(2, maxDiagnoses);
            }

            if (roll < 93)
            {
                return Math.Min(3, maxDiagnoses);
            }

            return Math.Min(4, maxDiagnoses);
        }

        private static bool ShouldGenerateHopelessCase()
        {
            return RuntimeSettings.Config.EnableHopelessCases.Value
                && UnityEngine.Random.Range(0, 100) < Clamp(RuntimeSettings.Config.HopelessCaseChance.Value, 0, 100)
                && (!RuntimeSettings.Config.HopelessRequiresHospitalUpgrades.Value || HasEnoughHospitalUpgradesForHopelessCase());
        }

        private static bool HasEnoughHospitalUpgradesForHopelessCase()
        {
            var total = 0;
            for (var i = 0; i < HospitalUpgradesService.Upgrades.Length; i++)
            {
                total += HospitalUpgradesService.GetLevel(HospitalUpgradesService.Upgrades[i]);
            }

            return total >= 18;
        }

        private static void BuildCandidatePool(BehaviorPatient patient, GameDBMedicalCondition primary, PatientMobility mobility)
        {
            ScratchConditions.Clear();
            if (Hospital.Instance == null)
            {
                return;
            }

            var entries = Database.Instance.GetEntries<GameDBMedicalCondition>();
            for (var i = 0; i < entries.Length; i++)
            {
                var condition = entries[i];
                if (condition == null || condition.Disabled || condition == primary || !IsDiagnosisFeasible(patient, condition, mobility))
                {
                    continue;
                }

                ScratchConditions.Add(condition);
            }
        }

        private static bool IsStructurallySchedulable(ProcedureSceneAvailability availability)
        {
            return ProcedureScene.IsProcedureAvailable(availability)
                || availability == ProcedureSceneAvailability.STAFF_BUSY
                || availability == ProcedureSceneAvailability.EQUIPMENT_BUSY;
        }

        private static Department ResolveProcedureDepartment(GameDBProcedure procedure, Department defaultDepartment, bool useFallbackLabDepartment)
        {
            if (procedure == null)
            {
                return defaultDepartment;
            }

            var departmentRef = useFallbackLabDepartment ? procedure.FallbackLabDepartmentRef : procedure.DetachedDepartmentRef;
            if (departmentRef != null && departmentRef.Entry != null && MapScriptInterface.Instance != null)
            {
                return MapScriptInterface.Instance.GetDepartmentOfType(departmentRef.Entry);
            }

            return defaultDepartment;
        }

        private static ProcedureSceneAvailability GetStructuralExaminationAvailabilityForDepartment(GLib.Entity entity, Department defaultDepartment, GameDBExamination examination)
        {
            if (entity == null || examination == null || examination.Procedure == null)
            {
                return ProcedureSceneAvailability.UNKNOWN;
            }

            var primaryDepartment = ResolveProcedureDepartment(examination.Procedure, defaultDepartment, false);
            var fallbackDepartment = ResolveProcedureDepartment(examination.Procedure, null, true);
            if ((primaryDepartment == null || primaryDepartment.IsClosed()) && (fallbackDepartment == null || fallbackDepartment.IsClosed()))
            {
                return ProcedureSceneAvailability.STAFF_UNAVAILABLE;
            }

            var availability = CreateProcedureAvailability(examination.Procedure, entity, primaryDepartment);
            if (!IsStructurallySchedulable(availability)
                && fallbackDepartment != null
                && !ReferenceEquals(fallbackDepartment, primaryDepartment)
                && !fallbackDepartment.IsClosed())
            {
                availability = CreateProcedureAvailability(examination.Procedure, entity, fallbackDepartment);
            }

            if (!IsStructurallySchedulable(availability) || examination.LabTestingExaminationRef == null || examination.LabTestingExaminationRef.Entry == null)
            {
                return availability;
            }

            var labProcedure = examination.LabTestingExaminationRef.Entry.Procedure;
            var labDepartment = ResolveProcedureDepartment(labProcedure, fallbackDepartment ?? primaryDepartment ?? defaultDepartment, false);
            var labFallbackDepartment = ResolveProcedureDepartment(labProcedure, null, true);
            var labAvailability = CreateProcedureAvailability(labProcedure, entity, labDepartment);
            if (!IsStructurallySchedulable(labAvailability)
                && labFallbackDepartment != null
                && !ReferenceEquals(labFallbackDepartment, labDepartment)
                && !labFallbackDepartment.IsClosed())
            {
                labAvailability = CreateProcedureAvailability(labProcedure, entity, labFallbackDepartment);
            }

            return labAvailability;
        }

        private static bool HasFeasibleDiagnosticRoute(BehaviorPatient patient, Department department, GameDBMedicalCondition condition)
        {
            if (patient == null || department == null || condition == null || condition.Symptoms == null)
            {
                return false;
            }

            var entity = ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            if (entity == null)
            {
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var symptomIndex = 0; symptomIndex < condition.Symptoms.Length; symptomIndex++)
            {
                var symptom = GetSymptom(condition.Symptoms[symptomIndex]);
                if (symptom == null || symptom.Examinations == null)
                {
                    continue;
                }

                for (var examIndex = 0; examIndex < symptom.Examinations.Length; examIndex++)
                {
                    var examination = symptom.Examinations[examIndex] == null ? null : symptom.Examinations[examIndex].Entry;
                    if (examination == null || !seen.Add(examination.DatabaseID.ToString()))
                    {
                        continue;
                    }

                    if (IsStructurallySchedulable(GetStructuralExaminationAvailabilityForDepartment(entity, department, examination)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool HasFeasibleTreatmentRoute(BehaviorPatient patient, Department department, GameDBMedicalCondition condition)
        {
            if (patient == null || department == null || condition == null)
            {
                return false;
            }

            var entity = ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            if (entity == null)
            {
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (condition.Treatments != null)
            {
                for (var i = 0; i < condition.Treatments.Length; i++)
                {
                    var treatment = condition.Treatments[i] == null ? null : condition.Treatments[i].Entry;
                    if (treatment == null || treatment.Procedure == null || !seen.Add(treatment.DatabaseID.ToString()))
                    {
                        continue;
                    }

                    if (IsStructurallySchedulable(GetSecondaryTreatmentAvailability(entity, department, treatment)))
                    {
                        return true;
                    }
                }
            }

            if (condition.Symptoms != null)
            {
                for (var symptomIndex = 0; symptomIndex < condition.Symptoms.Length; symptomIndex++)
                {
                    var symptom = GetSymptom(condition.Symptoms[symptomIndex]);
                    if (symptom == null || symptom.Treatments == null)
                    {
                        continue;
                    }

                    for (var treatmentIndex = 0; treatmentIndex < symptom.Treatments.Length; treatmentIndex++)
                    {
                        var treatment = symptom.Treatments[treatmentIndex] == null ? null : symptom.Treatments[treatmentIndex].Entry;
                        if (treatment == null || treatment.Procedure == null || !seen.Add(treatment.DatabaseID.ToString()))
                        {
                            continue;
                        }

                        if (IsStructurallySchedulable(GetSecondaryTreatmentAvailability(entity, department, treatment)))
                        {
                            return true;
                        }
                    }
                }
            }

            return NeedsHospitalization(condition) && department.AcceptsInpatients();
        }

        private static bool IsDiagnosisFeasible(BehaviorPatient patient, GameDBMedicalCondition condition, PatientMobility mobility)
        {
            if (!MatchesPatientGender(patient, condition))
            {
                return false;
            }

            var departmentType = condition.DepartmentRef == null ? null : condition.DepartmentRef.Entry;
            var department = departmentType == null || MapScriptInterface.Instance == null ? null : MapScriptInterface.Instance.GetDepartmentOfType(departmentType);
            if (department == null || department.IsClosed())
            {
                return false;
            }

            if ((mobility == PatientMobility.IMOBILE || mobility == PatientMobility.HELICOPTER) && !department.AcceptsInpatients())
            {
                return false;
            }

            if (mobility != PatientMobility.IMOBILE && mobility != PatientMobility.HELICOPTER && !department.AcceptsOutpatients())
            {
                return false;
            }

            var validity = department.m_departmentPersistentData.m_departmentValidity;
            if (mobility != PatientMobility.IMOBILE && mobility != PatientMobility.HELICOPTER
                && validity.m_outpatientDoctors + validity.m_outpatientDoctorsNight <= 0)
            {
                return false;
            }

            return HasFeasibleDiagnosticRoute(patient, department, condition)
                && HasFeasibleTreatmentRoute(patient, department, condition);
        }

        private static bool MatchesPatientGender(BehaviorPatient patient, GameDBMedicalCondition condition)
        {
            if (condition == null || condition.MatchingGenderRef == null || condition.MatchingGenderRef.Entry == null)
            {
                return true;
            }

            var info = patient == null ? null : patient.GetComponent<CharacterPersonalInfoComponent>();
            var personalInfo = info == null ? null : info.m_personalInfo;
            var gender = personalInfo == null ? null : personalInfo.m_gender;
            return gender != null && gender.Entry == condition.MatchingGenderRef.Entry;
        }

        private static bool CanAddDiagnosis(PatientCase patientCase, GameDBMedicalCondition condition)
        {
            var id = condition.DatabaseID.ToString();
            var departmentId = GetDepartmentId(condition);
            var collapse = IsCollapseCapable(condition);
            var surgery = IsSurgeryLikely(condition);
            var highRiskCount = 0;
            var crossDepartmentCount = 0;

            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                if (patientCase.Diagnoses[i].DiagnosisId == id)
                {
                    return false;
                }

                if (patientCase.Diagnoses[i].CollapseCapable || patientCase.Diagnoses[i].SurgeryLikely)
                {
                    highRiskCount++;
                }

                if (patientCase.Diagnoses[i].DepartmentId != departmentId)
                {
                    crossDepartmentCount++;
                }
            }

            if (!patientCase.Hopeless && collapse && highRiskCount >= 1)
            {
                return false;
            }

            if (!patientCase.Hopeless && surgery && highRiskCount >= 2)
            {
                return false;
            }

            return patientCase.Hopeless || crossDepartmentCount <= 2;
        }

        private static void AddDiagnosis(PatientCase patientCase, GameDBMedicalCondition condition, CaseDiagnosisStatus status)
        {
            var diagnosis = new CaseDiagnosis
            {
                ProblemId = condition.DatabaseID.ToString(),
                DiagnosisId = condition.DatabaseID.ToString(),
                DepartmentId = GetDepartmentId(condition),
                Hazard = GetWorstHazard(condition),
                CollapseCapable = IsCollapseCapable(condition),
                SurgeryLikely = IsSurgeryLikely(condition),
                NeedsHospitalization = NeedsHospitalization(condition),
                RequiresHospitalization = NeedsHospitalization(condition),
                CanNotTalk = HasCanNotTalk(condition),
                BleedingLevel = GetMaxBleedingLevel(condition),
                Mobility = GetStrongestMobility(condition),
                WalkSpeedModifier = condition.WalkSpeedModifier,
                WalkAnimSuffix = condition.WalkAnimSuffix,
                Status = status
            };

            AddSymptoms(diagnosis, condition);
            EnsureSymptomStateTable(diagnosis);
            diagnosis.CollapseDeadlineHours = CreateCollapseDeadlineHours(patientCase, diagnosis);
            patientCase.Diagnoses.Add(diagnosis);
            patientCase.Problems.Add(diagnosis);
        }

        private static void ActivateDepartmentCare(PatientCase patientCase, string departmentId)
        {
            if (patientCase == null || string.IsNullOrEmpty(departmentId))
            {
                return;
            }
        }

        private static float CreateCollapseDeadlineHours(PatientCase patientCase, CaseDiagnosis diagnosis)
        {
            if (patientCase == null || diagnosis == null || !diagnosis.CollapseCapable)
            {
                return -1f;
            }

            var window = 30f;
            if (string.Equals(diagnosis.Hazard, "High", StringComparison.OrdinalIgnoreCase))
            {
                window = 10f;
            }
            else if (string.Equals(diagnosis.Hazard, "Medium", StringComparison.OrdinalIgnoreCase))
            {
                window = 18f;
            }

            if (diagnosis.NeedsHospitalization)
            {
                window *= 0.8f;
            }

            var collapseIndex = 0;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var existing = patientCase.Diagnoses[i];
                if (existing != null && existing.CollapseCapable && existing.Status != CaseDiagnosisStatus.Treated)
                {
                    collapseIndex++;
                }
            }

            if (collapseIndex > 0)
            {
                window *= 1f + collapseIndex * 0.75f;
            }

            if (patientCase.Hopeless)
            {
                window *= 1.4f;
            }

            return GetCaseClockHours() + window;
        }

        private static void AddSymptoms(CaseDiagnosis diagnosis, GameDBMedicalCondition condition)
        {
            if (diagnosis == null || condition == null || condition.Symptoms == null)
            {
                return;
            }

            for (var i = 0; i < condition.Symptoms.Length; i++)
            {
                var symptomRef = condition.Symptoms[i] == null ? null : condition.Symptoms[i].GameDBSymptomRef;
                var symptom = symptomRef == null ? null : symptomRef.Entry;
                if (symptom != null)
                {
                    diagnosis.SymptomIds.Add(symptom.DatabaseID.ToString());
                }
            }
        }

        private static int RevealSymptomsForDiagnosis(CaseDiagnosis diagnosis, GameDBMedicalCondition condition, GameDBExamination examination)
        {
            if (diagnosis == null || condition == null || examination == null || condition.Symptoms == null)
            {
                return 0;
            }

            var count = 0;
            for (var i = 0; i < condition.Symptoms.Length; i++)
            {
                var symptomRef = condition.Symptoms[i] == null ? null : condition.Symptoms[i].GameDBSymptomRef;
                var symptom = symptomRef == null ? null : symptomRef.Entry;
                if (symptom == null || symptom.Examinations == null || !IsSymptomUncoveredBy(symptom, examination))
                {
                    continue;
                }

                var symptomId = symptom.DatabaseID.ToString();
                if (!diagnosis.SymptomIds.Contains(symptomId))
                {
                    diagnosis.SymptomIds.Add(symptomId);
                }

                if (diagnosis.KnownSymptomIds.Contains(symptomId))
                {
                    continue;
                }

                diagnosis.KnownSymptomIds.Add(symptomId);
                count++;
            }

            return count;
        }

        private static int MirrorKnownVanillaSymptomsToCase(BehaviorPatient patient, PatientCase patientCase)
        {
            if (patient == null || patientCase == null || patient.m_state == null || patient.m_state.m_medicalCondition == null)
            {
                return 0;
            }

            var symptoms = ReflectionHelpers.GetField(patient.m_state.m_medicalCondition, "m_symptoms") as System.Collections.IEnumerable;
            if (symptoms == null)
            {
                return 0;
            }

            var mirrored = 0;
            foreach (var symptomState in symptoms)
            {
                if (symptomState == null || Equals(ReflectionHelpers.GetField(symptomState, "m_hidden"), true))
                {
                    continue;
                }

                var symptom = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(symptomState, "m_symptom")) as GameDBSymptom;
                if (symptom == null)
                {
                    continue;
                }

                var symptomId = symptom.DatabaseID.ToString();
                for (var i = 0; i < patientCase.Diagnoses.Count; i++)
                {
                    var diagnosis = patientCase.Diagnoses[i];
                    if (diagnosis == null
                        || diagnosis.Status == CaseDiagnosisStatus.Treated
                        || !diagnosis.SymptomIds.Contains(symptomId)
                        || diagnosis.KnownSymptomIds.Contains(symptomId))
                    {
                        continue;
                    }

                    diagnosis.KnownSymptomIds.Add(symptomId);
                    if (diagnosis.Status == CaseDiagnosisStatus.Hidden)
                    {
                        diagnosis.Status = CaseDiagnosisStatus.Suspected;
                    }

                    var trackedSymptomState = FindCaseSymptomState(diagnosis, symptomId);
                    if (trackedSymptomState != null)
                    {
                        trackedSymptomState.Hidden = false;
                        trackedSymptomState.Active = true;
                    }

                    PromoteDiagnosisAfterReveal(patientCase, diagnosis, ResolveDiagnosis(diagnosis.DiagnosisId));
                    mirrored++;
                }
            }

            return mirrored;
        }

        private static bool IsSymptomUncoveredBy(GameDBSymptom symptom, GameDBExamination examination)
        {
            if (symptom == null || examination == null || symptom.Examinations == null)
            {
                return false;
            }

            for (var i = 0; i < symptom.Examinations.Length; i++)
            {
                if (symptom.Examinations[i] != null && symptom.Examinations[i].Entry == examination)
                {
                    return true;
                }
            }

            return false;
        }

        private static CaseDiagnosis GetDueCollapseDiagnosis(PatientCase patientCase)
        {
            if (patientCase == null || patientCase.Complete)
            {
                return null;
            }

            var now = GetCaseClockHours();
            CaseDiagnosis best = null;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis == null
                    || diagnosis.Status == CaseDiagnosisStatus.Treated
                    || !diagnosis.CollapseCapable
                    || diagnosis.CollapseDeadlineHours <= 0f
                    || diagnosis.CollapseDeadlineHours > now)
                {
                    continue;
                }

                if (best == null || diagnosis.CollapseDeadlineHours < best.CollapseDeadlineHours)
                {
                    best = diagnosis;
                }
            }

            return best;
        }

        private static GameDBSymptom FindCollapseSymptom(GameDBMedicalCondition condition)
        {
            if (condition == null || condition.Symptoms == null)
            {
                return null;
            }

            for (var i = 0; i < condition.Symptoms.Length; i++)
            {
                var symptom = GetSymptom(condition.Symptoms[i]);
                if (symptom == null)
                {
                    continue;
                }

                if (symptom.CollapseProcedureRef != null && symptom.CollapseProcedureRef.Entry != null)
                {
                    return symptom;
                }

                if (symptom.CollapseSymptomRef != null
                    && symptom.CollapseSymptomRef.Entry != null
                    && symptom.CollapseSymptomRef.Entry.CollapseProcedureRef != null
                    && symptom.CollapseSymptomRef.Entry.CollapseProcedureRef.Entry != null)
                {
                    return symptom.CollapseSymptomRef.Entry;
                }
            }

            return null;
        }

        private static void GetAggregateSymptomCounts(PatientCase patientCase, out int known, out int hidden, out int treated)
        {
            known = 0;
            hidden = 0;
            treated = 0;
            if (patientCase == null)
            {
                return;
            }

            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis == null)
                {
                    continue;
                }

                int knownForDiagnosis;
                int hiddenForDiagnosis;
                int treatedForDiagnosis;
                GetDisplaySymptomCounts(diagnosis, out knownForDiagnosis, out hiddenForDiagnosis, out treatedForDiagnosis);
                known += knownForDiagnosis;
                hidden += hiddenForDiagnosis;
                treated += treatedForDiagnosis;
            }
        }

        private static string FormatAggregateSymptomLabel(int known, int hidden, int treated)
        {
            if (treated > 0)
            {
                return string.Format(ModText.T("MedicalCaseSymptomCountsWithTreated"), known, hidden, treated);
            }

            if (known > 0)
            {
                return string.Format(ModText.T("MedicalCaseSymptomCounts"), known, hidden);
            }

            if (hidden <= 0)
            {
                return "-";
            }

            return string.Format(ModText.T("MedicalCaseHiddenSymptoms"), hidden);
        }

        private static string FormatHiddenSymptomLabel(int hidden)
        {
            if (hidden > 0)
            {
                return string.Format(ModText.T("MedicalCaseHiddenSymptoms"), hidden);
            }

            return "-";
        }

        private static int CountVisibleDiagnosisItems(PatientCase patientCase)
        {
            if (patientCase == null)
            {
                return 0;
            }

            var count = 0;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                if (ShouldShowDiagnosisInVanillaPanel(patientCase.Diagnoses[i]))
                {
                    count++;
                }
            }

            return count;
        }

        private static void BuildDisplayedSymptomItems(BehaviorPatient patient, PatientCase patientCase, List<SymptomPanelItemSnapshot> result)
        {
            result.Clear();
            if (patientCase == null)
            {
                return;
            }

            var itemBySymptomId = new Dictionary<string, SymptomPanelItemSnapshot>(StringComparer.Ordinal);
            var orderedIds = new List<string>();

            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis == null)
                {
                    continue;
                }

                var condition = ResolveDiagnosis(diagnosis.DiagnosisId);
                for (var symptomIndex = 0; symptomIndex < diagnosis.SymptomIds.Count; symptomIndex++)
                {
                    var symptomId = diagnosis.SymptomIds[symptomIndex];
                    if (string.IsNullOrEmpty(symptomId))
                    {
                        continue;
                    }

                    var symptom = GetSymptomById(symptomId);
                    if (!ShouldShowCaseSymptomInUi(condition, symptom))
                    {
                        continue;
                    }

                    var isSuppressed = diagnosis.TreatedSymptomIds.Contains(symptomId);
                    var isKnown = diagnosis.KnownSymptomIds.Contains(symptomId) || isSuppressed;
                    if (!isKnown)
                    {
                        continue;
                    }

                    RegisterDisplayedSymptom(itemBySymptomId, orderedIds, symptom, isSuppressed);
                }
            }

            AddVanillaDisplayedSymptoms(patient, itemBySymptomId, orderedIds);

            for (var i = 0; i < orderedIds.Count; i++)
            {
                SymptomPanelItemSnapshot item;
                if (itemBySymptomId.TryGetValue(orderedIds[i], out item) && item != null)
                {
                    result.Add(item);
                }
            }
        }

        private static void AddVanillaDisplayedSymptoms(BehaviorPatient patient, Dictionary<string, SymptomPanelItemSnapshot> items, List<string> orderedIds)
        {
            if (patient == null || patient.m_state == null || patient.m_state.m_medicalCondition == null)
            {
                return;
            }

            var symptoms = ReflectionHelpers.GetField(patient.m_state.m_medicalCondition, "m_symptoms") as System.Collections.IEnumerable;
            if (symptoms == null)
            {
                return;
            }

            foreach (var symptomState in symptoms)
            {
                if (symptomState == null
                    || Equals(ReflectionHelpers.GetField(symptomState, "m_hidden"), true)
                    || !Equals(ReflectionHelpers.GetField(symptomState, "m_spawned"), true))
                {
                    continue;
                }

                var symptom = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(symptomState, "m_symptom")) as GameDBSymptom;
                if (symptom == null)
                {
                    continue;
                }

                var symptomId = symptom.DatabaseID.ToString();
                var suppressed = !Equals(ReflectionHelpers.GetField(symptomState, "m_active"), true);
                RegisterDisplayedSymptom(items, orderedIds, symptom, suppressed);
            }
        }

        private static void RegisterDisplayedSymptom(
            Dictionary<string, SymptomPanelItemSnapshot> items,
            List<string> orderedIds,
            GameDBSymptom symptom,
            bool suppressed)
        {
            if (items == null || orderedIds == null || symptom == null)
            {
                return;
            }

            var symptomId = symptom.DatabaseID.ToString();
            SymptomPanelItemSnapshot existing;
            if (items.TryGetValue(symptomId, out existing) && existing != null)
            {
                if (existing.Suppressed && !suppressed)
                {
                    existing.Suppressed = false;
                    existing.Color = GetDisplayedSymptomColor(symptom, false);
                    existing.HazardIcon = GetDisplayedSymptomHazardIcon(symptom, false);
                    existing.HazardLocalizationId = GetDisplayedSymptomHazardLocalizationId(symptom, false);
                }

                return;
            }

            items[symptomId] = new SymptomPanelItemSnapshot
            {
                Symptom = symptom,
                Suppressed = suppressed,
                Color = GetDisplayedSymptomColor(symptom, suppressed),
                HazardIcon = GetDisplayedSymptomHazardIcon(symptom, suppressed),
                HazardLocalizationId = GetDisplayedSymptomHazardLocalizationId(symptom, suppressed),
                MobilityLocalizationId = GetDisplayedSymptomMobilityLocalizationId(symptom),
                Label = SafeGetLocalizedText(SafeDatabaseId(symptom)) ?? SafeDatabaseId(symptom) ?? symptomId
            };
            orderedIds.Add(symptomId);
        }

        private static bool ShouldShowDiagnosisInVanillaPanel(CaseDiagnosis diagnosis)
        {
            return diagnosis != null
                && (diagnosis.Status == CaseDiagnosisStatus.Diagnosed
                    || diagnosis.Status == CaseDiagnosisStatus.Treated);
        }

        private static void SetSegmentButtonReadOnly(SegmentController segment, int index)
        {
            if (segment == null || !segment.IsInRange(index))
            {
                return;
            }

            var button = segment.GetItemComponent<Button>(index);
            if (button != null)
            {
                button.onClick.RemoveAllListeners();
            }
        }

        private static void SetSegmentTooltipEnabled(SegmentController segment, int index, bool enabled)
        {
            if (segment == null || !segment.IsInRange(index))
            {
                return;
            }

            var tooltip = segment.GetItemComponent<HoverTooltipDelay>(index);
            if (tooltip != null)
            {
                tooltip.enabled = enabled;
            }
        }

        private static void BindSegmentSymptomTooltip(SegmentController segment, int index, SymptomPanelItemSnapshot symptomItem)
        {
            if (segment == null || !segment.IsInRange(index) || symptomItem == null || symptomItem.Symptom == null)
            {
                return;
            }

            var tooltip = segment.GetItemComponent<HoverTooltipDelay>(index);
            if (tooltip == null)
            {
                return;
            }

            var binder = tooltip.gameObject.GetComponent<MedicalCaseSegmentTooltip>();
            if (binder == null)
            {
                binder = tooltip.gameObject.AddComponent<MedicalCaseSegmentTooltip>();
            }

            binder.BindSymptom(
                symptomItem.Symptom,
                symptomItem.HazardIcon,
                symptomItem.HazardLocalizationId,
                symptomItem.MobilityLocalizationId,
                symptomItem.Suppressed,
                symptomItem.Color);
        }

        private static void BindSegmentDiagnosisTooltip(SegmentController segment, int index, GameDBMedicalCondition condition, int insuranceIcon, int insuranceCover)
        {
            if (segment == null || !segment.IsInRange(index) || condition == null)
            {
                return;
            }

            var tooltip = segment.GetItemComponent<HoverTooltipDelay>(index);
            if (tooltip == null)
            {
                return;
            }

            var binder = tooltip.gameObject.GetComponent<MedicalCaseSegmentTooltip>();
            if (binder == null)
            {
                binder = tooltip.gameObject.AddComponent<MedicalCaseSegmentTooltip>();
            }

            binder.BindDiagnosis(condition, insuranceIcon, insuranceCover);
        }

        private static void BindSegmentTextTooltip(SegmentController segment, int index, string textLocId)
        {
            if (segment == null || !segment.IsInRange(index) || string.IsNullOrEmpty(textLocId))
            {
                return;
            }

            var tooltip = segment.GetItemComponent<HoverTooltipDelay>(index);
            if (tooltip == null)
            {
                return;
            }

            var binder = tooltip.gameObject.GetComponent<MedicalCaseSegmentTooltip>();
            if (binder == null)
            {
                binder = tooltip.gameObject.AddComponent<MedicalCaseSegmentTooltip>();
            }

            binder.BindText(textLocId);
        }

        private static void SetFieldValue(object instance, string fieldName, object value)
        {
            if (instance == null || string.IsNullOrEmpty(fieldName))
            {
                return;
            }

            var field = instance.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (field == null)
            {
                return;
            }

            field.SetValue(instance, value);
        }

        private static int GetCaseStatusIcon(BehaviorPatient patient, PatientCase patientCase)
        {
            var hospitalization = patient == null ? null : patient.GetComponent<HospitalizationComponent>();
            if (hospitalization != null && hospitalization.m_state != null && hospitalization.m_state.m_dead)
            {
                return IconManager.ICON_WARNING_RED;
            }

            if (patient != null && patient.m_state != null && patient.m_state.m_sentAway)
            {
                return 238;
            }

            if (patientCase != null && patientCase.Complete)
            {
                return IconManager.ICON_CHECKED;
            }

            if (patient != null && patient.m_state != null)
            {
                if (patient.m_state.m_patientState == PatientState.BeingTreated
                    || patient.m_state.m_patientState == PatientState.GoingToTreatment)
                {
                    return IconManager.ICON_CHECKED;
                }

                if (patient.m_state.m_patientState == PatientState.GoingToCollapse
                    || patient.m_state.m_patientState == PatientState.Collapsing)
                {
                    return IconManager.ICON_WARNING_RED;
                }
            }

            return IconManager.ICON_WAITING;
        }

        private static string GetCaseStatusLabel(BehaviorPatient patient, PatientCase patientCase)
        {
            var hospitalization = patient == null ? null : patient.GetComponent<HospitalizationComponent>();
            if (hospitalization != null && hospitalization.m_state != null && hospitalization.m_state.m_dead)
            {
                return ModText.T("MedicalCaseUiStatusDead");
            }

            if (patient != null && patient.m_state != null && patient.m_state.m_sentAway)
            {
                return ModText.T("MedicalCaseUiStatusReferred");
            }

            if (patientCase != null && patientCase.Complete)
            {
                return ModText.T("MedicalCaseUiStatusDischarged");
            }

            if (patient != null && patient.m_state != null)
            {
                if (patient.m_state.m_patientState == PatientState.BeingTreated
                    || patient.m_state.m_patientState == PatientState.GoingToTreatment)
                {
                    return ModText.T("MedicalCaseUiStatusTreating");
                }

                if (patient.m_state.m_patientState == PatientState.GoingToCollapse
                    || patient.m_state.m_patientState == PatientState.Collapsing)
                {
                    return ModText.T("MedicalCaseUiStatusCritical");
                }
            }

            return ModText.T("MedicalCaseUiStatusExamining");
        }

        private static int GetInsuranceIconIndex(BehaviorPatient patient)
        {
            var info = patient == null ? null : patient.GetComponent<CharacterPersonalInfoComponent>();
            return info == null
                || info.m_personalInfo == null
                || info.m_personalInfo.m_insuranceCompany == null
                || info.m_personalInfo.m_insuranceCompany.Entry == null
                ? IconManager.ICON_NONE
                : info.m_personalInfo.m_insuranceCompany.Entry.IconIndex;
        }

        private static int GetInsuranceCoverPercent(BehaviorPatient patient)
        {
            var info = patient == null ? null : patient.GetComponent<CharacterPersonalInfoComponent>();
            var cover = info == null
                || info.m_personalInfo == null
                || info.m_personalInfo.m_insuranceCompany == null
                || info.m_personalInfo.m_insuranceCompany.Entry == null
                ? 0
                : info.m_personalInfo.m_insuranceCompany.Entry.CoverCostPercent;
            return cover
                + (Hospital.Instance == null ? 0 : Hospital.Instance.GetPrestigeInsurancePaymentModifierLastDay())
                + (WorldEventManager.Instance == null ? 0 : (int)WorldEventManager.Instance.GetInsurancePaymentModifier())
                + 100;
        }

        private static void RegisterCurrentMedicalCondition(BehaviorPatient patient, PatientCase patientCase)
        {
            if (patient == null || patient.m_state == null || patient.m_state.m_medicalCondition == null || patientCase == null)
            {
                return;
            }

            ConditionCases[patient.m_state.m_medicalCondition] = patientCase;
        }

        public static CaseEffects GetEffects(BehaviorPatient patient)
        {
            var patientCase = GetCase(patient);
            return BuildEffects(patientCase);
        }

        public static CaseEffects GetEffects(MedicalCondition medicalCondition)
        {
            if (!Enabled || medicalCondition == null)
            {
                return null;
            }

            PatientCase patientCase;
            return ConditionCases.TryGetValue(medicalCondition, out patientCase) ? BuildEffects(patientCase) : null;
        }

        public static bool ShouldForceCompatibilityConditionUntreated(MedicalCondition medicalCondition)
        {
            if (!Enabled || medicalCondition == null)
            {
                return false;
            }

            PatientCase patientCase;
            if (!ConditionCases.TryGetValue(medicalCondition, out patientCase) || patientCase == null || patientCase.Complete)
            {
                return false;
            }

            var activeDepartmentId = patientCase.ActiveDepartmentId;
            if (string.IsNullOrEmpty(activeDepartmentId))
            {
                return false;
            }

            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis != null
                    && diagnosis.DepartmentId == activeDepartmentId
                    && diagnosis.Status != CaseDiagnosisStatus.Treated)
                {
                    TraceLoggingService.LogCaseAnomaly(
                        patientCase,
                        "case_open_but_vanilla_ready_to_leave",
                        "active_department=" + activeDepartmentId
                        + ";blocking_diagnosis=" + diagnosis.DiagnosisId
                        + ";blocking_status=" + diagnosis.Status);
                    return true;
                }
            }

            return false;
        }

        private static CaseEffects BuildEffects(PatientCase patientCase)
        {
            if (!Enabled || patientCase == null || patientCase.Complete)
            {
                return null;
            }

            var effects = new CaseEffects();
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                var diagnosis = patientCase.Diagnoses[i];
                if (diagnosis == null || diagnosis.Status == CaseDiagnosisStatus.Treated)
                {
                    continue;
                }

                effects.Hazard = MaxHazard(effects.Hazard, ParseHazard(diagnosis.Hazard));
                effects.NeedsHospitalization = effects.NeedsHospitalization || diagnosis.NeedsHospitalization;
                effects.CanNotTalk = effects.CanNotTalk || diagnosis.CanNotTalk;
                effects.BleedingLevel = Math.Max(effects.BleedingLevel, diagnosis.BleedingLevel);
                effects.Immobile = effects.Immobile || diagnosis.Mobility == PatientMobility.IMOBILE || diagnosis.Mobility == PatientMobility.INTUBATED;

                if (diagnosis.WalkSpeedModifier > 0f
                    && (effects.WalkSpeedModifier <= 0f || diagnosis.WalkSpeedModifier < effects.WalkSpeedModifier))
                {
                    effects.WalkSpeedModifier = diagnosis.WalkSpeedModifier;
                }

                if (string.IsNullOrEmpty(effects.WalkAnimSuffix) && !string.IsNullOrEmpty(diagnosis.WalkAnimSuffix))
                {
                    effects.WalkAnimSuffix = diagnosis.WalkAnimSuffix;
                }
            }

            if (effects.Hazard == SymptomHazard.Unknown && !effects.Immobile && !effects.NeedsHospitalization && effects.BleedingLevel == 0)
            {
                return null;
            }

            return effects;
        }

        private static SymptomHazard ParseHazard(string hazard)
        {
            if (string.Equals(hazard, "High", StringComparison.OrdinalIgnoreCase))
            {
                return SymptomHazard.High;
            }

            if (string.Equals(hazard, "Medium", StringComparison.OrdinalIgnoreCase))
            {
                return SymptomHazard.Medium;
            }

            if (string.Equals(hazard, "Low", StringComparison.OrdinalIgnoreCase))
            {
                return SymptomHazard.Low;
            }

            return SymptomHazard.Unknown;
        }

        private static SymptomHazard MaxHazard(SymptomHazard left, SymptomHazard right)
        {
            return (SymptomHazard)Math.Max((int)left, (int)right);
        }

        private static void BackfillDiagnosisData(CaseDiagnosis diagnosis)
        {
            if (diagnosis == null)
            {
                return;
            }

            var condition = ResolveDiagnosis(diagnosis.DiagnosisId);
            if (condition == null)
            {
                return;
            }

            diagnosis.NeedsHospitalization = NeedsHospitalization(condition);
            diagnosis.CanNotTalk = HasCanNotTalk(condition);
            diagnosis.BleedingLevel = GetMaxBleedingLevel(condition);
            diagnosis.Mobility = GetStrongestMobility(condition);
            diagnosis.WalkSpeedModifier = condition.WalkSpeedModifier;
            diagnosis.WalkAnimSuffix = condition.WalkAnimSuffix;
            if (diagnosis.SymptomIds.Count == 0)
            {
                AddSymptoms(diagnosis, condition);
            }

            if (diagnosis.CollapseCapable && diagnosis.CollapseDeadlineHours <= 0f)
            {
                diagnosis.CollapseDeadlineHours = GetCaseClockHours() + 24f;
            }
        }

        private static GameDBMedicalCondition GetPrimaryDiagnosis(BehaviorPatient patient)
        {
            var state = ReflectionHelpers.GetField(patient, "m_state");
            var medicalCondition = ReflectionHelpers.GetField(state, "m_medicalCondition") as MedicalCondition;
            return medicalCondition == null || medicalCondition.m_gameDBMedicalCondition == null
                ? null
                : medicalCondition.m_gameDBMedicalCondition.Entry;
        }

        private static GameDBMedicalCondition ResolveDiagnosis(string diagnosisId)
        {
            if (string.IsNullOrEmpty(diagnosisId) || Database.Instance == null)
            {
                return null;
            }

            EnsureDiagnosisCache();
            GameDBMedicalCondition condition;
            return DiagnosisCache.TryGetValue(diagnosisId, out condition) ? condition : null;
        }

        private static string GetDepartmentId(GameDBMedicalCondition condition)
        {
            var department = condition == null || condition.DepartmentRef == null ? null : condition.DepartmentRef.Entry;
            return department == null ? "unknown" : department.DatabaseID.ToString();
        }

        private static void EnsureDiagnosisCache()
        {
            if (DiagnosisCacheBuilt || Database.Instance == null)
            {
                return;
            }

            DiagnosisCache.Clear();
            var entries = Database.Instance.GetEntries<GameDBMedicalCondition>();
            for (var i = 0; i < entries.Length; i++)
            {
                var condition = entries[i];
                if (condition != null)
                {
                    DiagnosisCache[condition.DatabaseID.ToString()] = condition;
                }
            }

            DiagnosisCacheBuilt = true;
        }

        private static void EnsureDepartmentCache()
        {
            if (DepartmentCacheBuilt || Database.Instance == null)
            {
                return;
            }

            DepartmentTypeCache.Clear();
            var entries = Database.Instance.GetEntries<GameDBDepartment>();
            for (var i = 0; i < entries.Length; i++)
            {
                var department = entries[i];
                if (department != null)
                {
                    DepartmentTypeCache[department.DatabaseID.ToString()] = department;
                }
            }

            DepartmentCacheBuilt = true;
        }

        private static void BuildDepartmentTypeCache()
        {
            EnsureDepartmentCache();
        }

        private static Department ResolveDepartment(string departmentId)
        {
            if (string.IsNullOrEmpty(departmentId) || Database.Instance == null || MapScriptInterface.Instance == null)
            {
                return null;
            }

            EnsureDepartmentCache();
            GameDBDepartment departmentType;
            return DepartmentTypeCache.TryGetValue(departmentId, out departmentType)
                ? MapScriptInterface.Instance.GetDepartmentOfType(departmentType)
                : null;
        }

        private static bool HasAnyDoctorCapacity(Department department)
        {
            if (department == null || department.m_departmentPersistentData == null)
            {
                return false;
            }

            var validity = department.m_departmentPersistentData.m_departmentValidity;
            return validity.m_outpatientDoctors + validity.m_outpatientDoctorsNight > 0
                || validity.m_surgeryStaff
                || ReflectionHelpers.InvokeBool(department, "HasWorkingClinic");
        }

        private static GameDBProcedure GetRepresentativeProcedure(CaseDiagnosis diagnosis)
        {
            var condition = ResolveDiagnosis(diagnosis == null ? null : diagnosis.DiagnosisId);
            if (condition == null || diagnosis == null || condition.Symptoms == null)
            {
                return null;
            }

            for (var i = 0; i < condition.Symptoms.Length; i++)
            {
                var symptom = GetSymptom(condition.Symptoms[i]);
                if (symptom == null || symptom.Examinations == null)
                {
                    continue;
                }

                var symptomId = symptom.DatabaseID.ToString();
                if (diagnosis.KnownSymptomIds.Contains(symptomId))
                {
                    continue;
                }

                for (var examIndex = 0; examIndex < symptom.Examinations.Length; examIndex++)
                {
                    var examination = symptom.Examinations[examIndex] == null ? null : symptom.Examinations[examIndex].Entry;
                    if (examination != null
                        && examination.Procedure != null
                        && examination.ExaminationType != ExaminationType.INTERVIEW
                        && examination.ExaminationType != ExaminationType.OBSERVATION)
                    {
                        return examination.Procedure;
                    }
                }
            }

            if (condition.Examinations != null)
            {
                for (var i = 0; i < condition.Examinations.Length; i++)
                {
                    var examination = condition.Examinations[i] == null ? null : condition.Examinations[i].Entry;
                    if (examination != null
                        && examination.Procedure != null
                        && examination.ExaminationType != ExaminationType.INTERVIEW
                        && examination.ExaminationType != ExaminationType.OBSERVATION)
                    {
                        return examination.Procedure;
                    }
                }
            }

            for (var i = 0; i < condition.Symptoms.Length; i++)
            {
                var symptom = GetSymptom(condition.Symptoms[i]);
                if (symptom == null || symptom.Treatments == null)
                {
                    continue;
                }

                var symptomId = symptom.DatabaseID.ToString();
                if (!diagnosis.KnownSymptomIds.Contains(symptomId) || diagnosis.TreatedSymptomIds.Contains(symptomId))
                {
                    continue;
                }

                for (var treatmentIndex = 0; treatmentIndex < symptom.Treatments.Length; treatmentIndex++)
                {
                    var treatment = symptom.Treatments[treatmentIndex] == null ? null : symptom.Treatments[treatmentIndex].Entry;
                    if (treatment != null && treatment.Procedure != null)
                    {
                        return treatment.Procedure;
                    }
                }
            }

            return null;
        }

        private static bool IsRoomOrEquipmentBlock(ProcedureSceneAvailability availability)
        {
            var text = availability.ToString();
            return text.IndexOf("ROOM", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("EQUIPMENT", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetWorstHazard(GameDBMedicalCondition condition)
        {
            var worst = "Low";
            if (condition == null || condition.Symptoms == null)
            {
                return worst;
            }

            for (var i = 0; i < condition.Symptoms.Length; i++)
            {
                var symptomRef = condition.Symptoms[i] == null ? null : condition.Symptoms[i].GameDBSymptomRef;
                var symptom = symptomRef == null ? null : symptomRef.Entry;
                if (symptom == null)
                {
                    continue;
                }

                var hazard = symptom.Hazard.ToString();
                if (string.Equals(hazard, "High", StringComparison.OrdinalIgnoreCase))
                {
                    return "High";
                }

                if (string.Equals(hazard, "Medium", StringComparison.OrdinalIgnoreCase))
                {
                    worst = "Medium";
                }
            }

            return worst;
        }

        private static bool IsCollapseCapable(GameDBMedicalCondition condition)
        {
            if (condition == null || condition.Symptoms == null)
            {
                return false;
            }

            for (var i = 0; i < condition.Symptoms.Length; i++)
            {
                var symptomRef = condition.Symptoms[i] == null ? null : condition.Symptoms[i].GameDBSymptomRef;
                var symptom = symptomRef == null ? null : symptomRef.Entry;
                if (symptom != null && symptom.CollapseProcedureRef != null && symptom.CollapseProcedureRef.Entry != null)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSurgeryLikely(GameDBMedicalCondition condition)
        {
            if (condition == null || condition.Treatments == null)
            {
                return false;
            }

            for (var i = 0; i < condition.Treatments.Length; i++)
            {
                var treatment = condition.Treatments[i] == null ? null : condition.Treatments[i].Entry;
                if (treatment != null && treatment.TreatmentType.ToString().IndexOf("SURG", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool NeedsHospitalization(GameDBMedicalCondition condition)
        {
            if (condition == null || condition.Symptoms == null)
            {
                return false;
            }

            for (var i = 0; i < condition.Symptoms.Length; i++)
            {
                var symptom = GetSymptom(condition.Symptoms[i]);
                if (symptom != null
                    && symptom.Treatments != null
                    && symptom.Treatments.Length > 0
                    && symptom.Treatments[0] != null
                    && symptom.Treatments[0].Entry != null
                    && symptom.Treatments[0].Entry.HospitalizationTreatmentRef != null)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasCanNotTalk(GameDBMedicalCondition condition)
        {
            if (condition == null || condition.Symptoms == null)
            {
                return false;
            }

            for (var i = 0; i < condition.Symptoms.Length; i++)
            {
                var symptom = GetSymptom(condition.Symptoms[i]);
                if (symptom != null && symptom.CanNotTalk)
                {
                    return true;
                }
            }

            return false;
        }

        private static int GetMaxBleedingLevel(GameDBMedicalCondition condition)
        {
            var bleeding = 0;
            if (condition == null || condition.Symptoms == null)
            {
                return bleeding;
            }

            for (var i = 0; i < condition.Symptoms.Length; i++)
            {
                var symptom = GetSymptom(condition.Symptoms[i]);
                if (symptom != null)
                {
                    bleeding = Math.Max(bleeding, symptom.BleedingLevel);
                }
            }

            return bleeding;
        }

        private static PatientMobility GetStrongestMobility(GameDBMedicalCondition condition)
        {
            var mobility = PatientMobility.MOBILE;
            if (condition == null || condition.Symptoms == null)
            {
                return mobility;
            }

            for (var i = 0; i < condition.Symptoms.Length; i++)
            {
                var symptom = GetSymptom(condition.Symptoms[i]);
                if (symptom == null)
                {
                    continue;
                }

                if (symptom.PatientMobility == PatientMobility.INTUBATED)
                {
                    return PatientMobility.INTUBATED;
                }

                if (symptom.PatientMobility == PatientMobility.IMOBILE)
                {
                    mobility = PatientMobility.IMOBILE;
                }
            }

            return mobility;
        }

        private static GameDBSymptom GetSymptom(GameDBSymptomRules rules)
        {
            var symptomRef = rules == null ? null : rules.GameDBSymptomRef;
            return symptomRef == null ? null : symptomRef.Entry;
        }

        private static GameDBSymptom GetSymptomById(string symptomId)
        {
            return string.IsNullOrEmpty(symptomId) || Database.Instance == null
                ? null
                : Database.Instance.GetEntry<GameDBSymptom>(symptomId);
        }

        private static void GetDisplaySymptomCounts(CaseDiagnosis diagnosis, out int known, out int hidden, out int treated)
        {
            known = 0;
            hidden = 0;
            treated = 0;
            if (diagnosis == null || diagnosis.SymptomIds.Count == 0)
            {
                return;
            }

            var condition = ResolveDiagnosis(diagnosis.DiagnosisId);
            for (var i = 0; i < diagnosis.SymptomIds.Count; i++)
            {
                var symptomId = diagnosis.SymptomIds[i];
                var symptom = GetSymptomById(symptomId);
                if (!ShouldShowCaseSymptomInUi(condition, symptom))
                {
                    continue;
                }

                if (diagnosis.TreatedSymptomIds.Contains(symptomId))
                {
                    treated++;
                }
                else if (diagnosis.KnownSymptomIds.Contains(symptomId))
                {
                    known++;
                }
                else
                {
                    hidden++;
                }
            }
        }

        private static bool ShouldShowCaseSymptomInUi(GameDBMedicalCondition condition, GameDBSymptom symptom)
        {
            if (condition == null || symptom == null)
            {
                return false;
            }

            return !IsDiagnosisIdentitySymptom(condition, symptom);
        }

        private static Color GetDisplayedSymptomColor(GameDBSymptom symptom, bool suppressed)
        {
            if (symptom == null)
            {
                return UISettings.Instance == null ? Color.white : UISettings.Instance.SYMPTOM_COLOR_DARK_GRAY;
            }

            if (!suppressed)
            {
                return Symptom.GetSymptomColor(symptom);
            }

            return UISettings.Instance == null
                ? Color.gray
                : Color.Lerp(Symptom.GetSymptomColor(symptom), UISettings.Instance.SYMPTOM_COLOR_DARK_GRAY, 0.75f);
        }

        private static int GetDisplayedSymptomHazardIcon(GameDBSymptom symptom, bool suppressed)
        {
            if (suppressed || symptom == null)
            {
                return 165;
            }

            switch (symptom.Hazard)
            {
                case SymptomHazard.Low:
                    return 167;
                case SymptomHazard.Medium:
                    return 169;
                case SymptomHazard.High:
                    return 170;
                case SymptomHazard.Unknown:
                case SymptomHazard.None:
                    return 165;
                default:
                    return 165;
            }
        }

        private static string GetDisplayedSymptomHazardLocalizationId(GameDBSymptom symptom, bool suppressed)
        {
            if (suppressed || symptom == null)
            {
                return "HAZARD_UNKNOWN";
            }

            switch (symptom.Hazard)
            {
                case SymptomHazard.Low:
                    return "HAZARD_LOW";
                case SymptomHazard.Medium:
                    return "HAZARD_MEDIUM";
                case SymptomHazard.High:
                    return "HAZARD_HIGH";
                case SymptomHazard.Unknown:
                case SymptomHazard.None:
                    return "HAZARD_UNKNOWN";
                default:
                    return "HAZARD_UNKNOWN";
            }
        }

        private static string GetDisplayedSymptomMobilityLocalizationId(GameDBSymptom symptom)
        {
            if (symptom == null)
            {
                return "SYMPTOM_MOBILE";
            }

            switch (symptom.PatientMobility)
            {
                case PatientMobility.IMOBILE:
                    return "SYMPTOM_IMOBILE";
                case PatientMobility.INTUBATED:
                    return "SYMPTOM_INTUBATED";
                case PatientMobility.MOBILE:
                case PatientMobility.ANY:
                default:
                    return "SYMPTOM_MOBILE";
            }
        }

        private static bool IsDiagnosisIdentitySymptom(GameDBMedicalCondition condition, GameDBSymptom symptom)
        {
            if (condition == null || symptom == null || !symptom.IsMainSymptom)
            {
                return false;
            }

            var conditionName = NormalizeCaseUiText(SafeGetLocalizedText(condition));
            var symptomName = NormalizeCaseUiText(SafeGetLocalizedText(SafeDatabaseId(symptom)));
            if (string.IsNullOrEmpty(conditionName) || string.IsNullOrEmpty(symptomName))
            {
                return false;
            }

            if (conditionName == symptomName || conditionName.Contains(symptomName) || symptomName.Contains(conditionName))
            {
                return true;
            }

            var conditionTokens = GetMeaningfulTokens(conditionName);
            var symptomTokens = GetMeaningfulTokens(symptomName);
            if (conditionTokens.Count == 0 || symptomTokens.Count == 0)
            {
                return false;
            }

            var overlap = 0;
            foreach (var token in conditionTokens)
            {
                if (symptomTokens.Contains(token))
                {
                    overlap++;
                }
            }

            return overlap >= 2 && overlap >= Math.Min(conditionTokens.Count, symptomTokens.Count);
        }

        private static string NormalizeCaseUiText(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder(value.Length);
            var previousSpace = false;
            for (var i = 0; i < value.Length; i++)
            {
                var c = char.ToLowerInvariant(value[i]);
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(c);
                    previousSpace = false;
                }
                else if (!previousSpace)
                {
                    builder.Append(' ');
                    previousSpace = true;
                }
            }

            return builder.ToString().Trim();
        }

        private static HashSet<string> GetMeaningfulTokens(string value)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(value))
            {
                return result;
            }

            var parts = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length >= 4)
                {
                    result.Add(parts[i]);
                }
            }

            return result;
        }

        internal static bool TryScheduleCaseAwareExamination(BehaviorPatient patient)
        {
            if (!Enabled || patient == null || patient.m_state == null || Hospital.Instance == null || Hospital.Instance.m_state == null)
            {
                TraceLoggingService.LogPatientAction(patient, "TryScheduleCaseAwareExamination", "failed", "reason=missing_runtime_state");
                return false;
            }

            if (ShouldBlockFurtherSameDepartmentExaminations(patient))
            {
                TraceLoggingService.LogPatientAction(patient, "TryScheduleCaseAwareExamination", "failed", "reason=same_department_examinations_blocked;" + BuildTraceDecisionContext(patient, GetCase(patient)));
                return false;
            }

            var patientCase = GetCase(patient);
            if (patientCase != null && !patientCase.Complete)
            {
                TryAdvanceCompatibilityConditionWithinDepartment(patient, patientCase);
            }

            var procedure = patient.GetComponent<ProcedureComponent>();
            var queue = procedure == null || procedure.m_state == null ? null : procedure.m_state.m_procedureQueue;
            if (procedure == null || queue == null)
            {
                TraceLoggingService.LogPatientAction(patient, "TryScheduleCaseAwareExamination", "failed", "reason=missing_procedure_queue");
                return false;
            }

            if (queue.m_activeExamination != null || queue.m_plannedExaminationStates.Count > 0 || queue.m_labProcedures.Count > 0)
            {
                MuteCaseProgressNotifications(patient, 6f);
                TraceLoggingService.LogPatientAction(patient, "TryScheduleCaseAwareExamination", "already_queued", BuildTraceDecisionContext(patient, patientCase) + ";mute_applied=true");
                return true;
            }

            var forceAll = SymptomsPanelController.DLCHardCoreModeCheck(
                Hospital.Instance.m_state.m_diagnosePercentageAll,
                Hospital.Instance.m_state.m_diagnosePercentageControlled,
                patient.GetControlMode() == PatientControlMode.PlayerControl,
                patient.m_state.m_doctor);
            var examinations = procedure.UpdateAllExaminationsForMedicalCondition(patient.m_state.m_medicalCondition, -1, forceAll);
            if (examinations == null || examinations.Count == 0)
            {
                TraceLoggingService.LogPatientAction(patient, "TryScheduleCaseAwareExamination", "failed", BuildTraceDecisionContext(patient, patientCase) + ";force_all=" + forceAll + ";reason=empty_examination_map");
                return false;
            }

            for (var i = 0; i < examinations.Count; i++)
            {
                if (!ProcedureScene.IsProcedureAvailable(examinations.ValueAt(i)))
                {
                    continue;
                }

                var examination = examinations.KeyAt(i);
                if (examination == null)
                {
                    continue;
                }

                patient.ScheduleExamination(examination);
                MuteCaseProgressNotifications(patient, 6f);
                if (patient.m_state.m_patientState == PatientState.BlockedByAmbiguousResults
                    || patient.m_state.m_patientState == PatientState.BlockedByComplicatedDiagnosis
                    || patient.m_state.m_patientState == PatientState.BlockedByNoTreatment)
                {
                    patient.SwitchState(PatientState.GoingToDoctor);
                }

                TraceLoggingService.LogPatientAction(
                    patient,
                    "TryScheduleCaseAwareExamination",
                    "scheduled",
                    BuildTraceDecisionContext(patient, patientCase)
                    + ";selected_examination=" + examination.DatabaseID
                    + ";force_all=" + forceAll
                    + ";mute_applied=true");
                return true;
            }

            TraceLoggingService.LogPatientAction(patient, "TryScheduleCaseAwareExamination", "failed", BuildTraceDecisionContext(patient, patientCase) + ";force_all=" + forceAll + ";reason=no_available_runtime_examination");
            return false;
        }

        public static void RecoverBlockedCaseState(BehaviorPatient patient)
        {
            if (!Enabled || patient == null || patient.m_state == null || !HasOpenCase(patient))
            {
                return;
            }

            var state = patient.m_state.m_patientState;
            if (state != PatientState.BlockedByAmbiguousResults
                && state != PatientState.BlockedByComplicatedDiagnosis
                && state != PatientState.BlockedByNoTreatment)
            {
                return;
            }

            if (TryScheduleCaseAwareExamination(patient))
            {
                TraceLoggingService.LogPatientAction(patient, "RecoverBlockedCaseState", "recovered", BuildTraceDecisionContext(patient, GetCase(patient)) + ";recovery_step=examination");
                return;
            }

            if (TryScheduleCaseAwareTreatment(patient))
            {
                TraceLoggingService.LogPatientAction(patient, "RecoverBlockedCaseState", "recovered", BuildTraceDecisionContext(patient, GetCase(patient)) + ";recovery_step=treatment");
                return;
            }

            if (TryAdvanceCaseTransferOrHospitalization(patient, "blocked_recovery"))
            {
                TraceLoggingService.LogPatientAction(patient, "RecoverBlockedCaseState", "transferred", BuildTraceDecisionContext(patient, GetCase(patient)) + ";recovery_step=transfer_or_hospitalization");
                return;
            }

            if (TryReferBlockedCase(patient, "RecoverBlockedCaseState", "blocked state with no exam/treatment/route"))
            {
                TraceLoggingService.LogPatientAction(patient, "RecoverBlockedCaseState", "referred", BuildTraceDecisionContext(patient, GetCase(patient)) + ";recovery_step=referral");
                return;
            }

            TraceLoggingService.LogPatientAction(patient, "RecoverBlockedCaseState", "failed", BuildTraceDecisionContext(patient, GetCase(patient)) + ";recovery_step=referral_failed");
        }

        private static bool TryReferBlockedCase(BehaviorPatient patient, string initiator = null, string recoveryReason = null)
        {
            try
            {
                var patientCase = GetCase(patient);
                if (patient == null || patient.m_state == null || patientCase == null || patientCase.Complete)
                {
                    TraceLoggingService.LogPatientAction(patient, "TryReferBlockedCase", "failed", "reason=missing_case;" + (string.IsNullOrEmpty(initiator) ? string.Empty : "initiator=" + initiator));
                    return false;
                }

                EnsureRuntimeModel(patient, patientCase, CaseCheckpoint.RoutingGate, "blocked_case_referral");

                var currentDepartmentId = GetCaseRuntimeDepartmentId(patient, patientCase);
                var nextDiagnosis = CaseCarePlanner.SelectNextDiagnosis(patientCase, currentDepartmentId);
                var nextProcedure = GetRepresentativeProcedure(nextDiagnosis);
                var hasAvailableExamination = HasCaseAvailableExamination(patient);
                var hasAvailableTreatmentOrProgress = HasCaseAvailableTreatmentOrProgress(patient);
                var hasTransferOrHospitalizationRoute = HasCaseTransferOrHospitalizationRoute(patient);
                var referralContext = BuildTraceDecisionContext(patient, patientCase)
                    + ";initiator=" + (string.IsNullOrEmpty(initiator) ? "-" : initiator)
                    + ";recovery_reason=" + (string.IsNullOrEmpty(recoveryReason) ? "-" : recoveryReason)
                    + ";next_diagnosis=" + FormatDiagnosisFocusTrace(nextDiagnosis)
                    + ";target_department=" + (nextDiagnosis == null || string.IsNullOrEmpty(nextDiagnosis.DepartmentId) ? "-" : nextDiagnosis.DepartmentId)
                    + ";representative_procedure=" + (nextProcedure == null ? "-" : nextProcedure.DatabaseID.ToString())
                    + ";HasCaseAvailableExamination=" + hasAvailableExamination
                    + ";HasCaseAvailableTreatmentOrProgress=" + hasAvailableTreatmentOrProgress
                    + ";HasCaseTransferOrHospitalizationRoute=" + hasTransferOrHospitalizationRoute;

                string popupReason;
                bool equipmentLike;
                string referralReason;
                try
                {
                    referralReason = GetBlockedCaseReferralReason(patient, patientCase, out popupReason, out equipmentLike);
                }
                catch (Exception ex)
                {
                    popupReason = null;
                    equipmentLike = false;
                    referralReason = null;
                    Log("Failed to build blocked-case referral reason: " + DescribeException(ex));
                }

                if (string.IsNullOrEmpty(referralReason))
                {
                    referralReason = BuildGenericBlockedCaseReferralReason(patient, patientCase, out popupReason, out equipmentLike);
                    if (string.IsNullOrEmpty(referralReason))
                    {
                        RememberBlockedCaseRetry(patient, 1.5f);
                        TraceLoggingService.LogPatientAction(
                            patient,
                            "TryReferBlockedCase",
                            "failed",
                            referralContext
                            + ";reason=no_referral_reason_available");
                        return false;
                    }
                }

                var referred = EquipmentReferralService.TryReferCaseBlockedPatient(patient, referralReason, equipmentLike);
                if (!referred)
                {
                    referred = TryForceCaseBlockedReferral(patient, referralReason, equipmentLike);
                }

                if (!referred)
                {
                    MuteCaseProgressNotifications(patient, 2f);
                    RememberBlockedCaseRetry(patient, 1.5f);
                    TraceLoggingService.LogPatientAction(
                        patient,
                        "TryReferBlockedCase",
                        "failed",
                        referralContext
                        + ";referral_reason=" + (string.IsNullOrEmpty(referralReason) ? "-" : referralReason)
                        + ";popup_reason=" + (string.IsNullOrEmpty(popupReason) ? "-" : popupReason)
                        + ";equipment_like=" + equipmentLike
                        + ";mute_applied=true");
                    return false;
                }

                ClearBlockedCaseRetry(patient);
                QueueReferralPopup(
                    string.IsNullOrEmpty(patientCase.PatientName) ? "Patient" : patientCase.PatientName,
                    popupReason);
                MuteCaseProgressNotifications(patient, 8f);
                TraceLoggingService.LogPatientAction(
                    patient,
                    "TryReferBlockedCase",
                    "referred",
                    referralContext
                    + ";referral_reason=" + (string.IsNullOrEmpty(referralReason) ? "-" : referralReason)
                    + ";popup_reason=" + (string.IsNullOrEmpty(popupReason) ? "-" : popupReason)
                    + ";equipment_like=" + equipmentLike
                    + ";mute_applied=true");
                return true;
            }
            catch (Exception ex)
            {
                RememberBlockedCaseRetry(patient, 2f);
                Log("TryReferBlockedCase crashed: " + DescribeException(ex)
                    + "; initiator=" + (string.IsNullOrEmpty(initiator) ? "-" : initiator)
                    + "; recoveryReason=" + (string.IsNullOrEmpty(recoveryReason) ? "-" : recoveryReason));
                TraceLoggingService.LogPatientAction(
                    patient,
                    "TryReferBlockedCase",
                    "failed",
                    "reason=exception;initiator=" + (string.IsNullOrEmpty(initiator) ? "-" : initiator)
                    + ";recovery_reason=" + (string.IsNullOrEmpty(recoveryReason) ? "-" : recoveryReason)
                    + ";exception=" + NormalizeCaseTraceValue(ex.GetType().Name));
                return false;
            }
        }

        private static bool TryBuildBlockedCaseRouteReferralReason(
            PatientCase patientCase,
            CaseRouteDecision decision,
            out string popupReason,
            out bool equipmentLike,
            out string referralReason)
        {
            popupReason = null;
            equipmentLike = false;
            referralReason = null;
            if (patientCase == null || decision == null || !decision.RouteExists || decision.CanExecuteNow)
            {
                return false;
            }

            var diagnosisName = GetDiagnosisDisplayName(decision.DiagnosisId);
            var departmentName = GetDepartmentDisplayName(decision.TargetDepartmentId);
            switch (decision.BlockerReason)
            {
                case "target_department_closed":
                    popupReason = string.Format(ModText.T("MedicalCaseReferralReasonDepartment"), departmentName);
                    referralReason = decision.NeedsHospitalization
                        ? "hospitalization route unavailable"
                        : "profile department unavailable";
                    return true;
                case "no_profile_capacity":
                    popupReason = string.Format(ModText.T("MedicalCaseReferralReasonDoctor"), departmentName);
                    referralReason = "no available profile doctor";
                    return true;
                case "hospitalization_unavailable":
                    popupReason = BuildReferralStepReason(false, null, diagnosisName, departmentName);
                    referralReason = "hospitalization route unavailable";
                    return true;
                case "cannot_change_department":
                    popupReason = BuildReferralStepReason(
                        decision.DiagnosisStatus == CaseDiagnosisStatus.Hidden || decision.DiagnosisStatus == CaseDiagnosisStatus.Suspected,
                        null,
                        diagnosisName,
                        departmentName);
                    referralReason = "cannot change department";
                    return true;
                case "incompatible_patient_state":
                    popupReason = BuildReferralStepReason(
                        decision.DiagnosisStatus == CaseDiagnosisStatus.Hidden || decision.DiagnosisStatus == CaseDiagnosisStatus.Suspected,
                        null,
                        diagnosisName,
                        departmentName);
                    referralReason = "incompatible patient state";
                    return true;
                default:
                    popupReason = BuildReferralStepReason(
                        decision.DiagnosisStatus == CaseDiagnosisStatus.Hidden || decision.DiagnosisStatus == CaseDiagnosisStatus.Suspected,
                        null,
                        diagnosisName,
                        departmentName);
                    referralReason = "case route unavailable";
                    return true;
            }
        }

        private static string BuildGenericBlockedCaseReferralReason(BehaviorPatient patient, PatientCase patientCase, out string popupReason, out bool equipmentLike)
        {
            popupReason = ModText.T("MedicalCaseReferralReasonDiagnosticGeneric");
            equipmentLike = false;

            if (patientCase == null)
            {
                return "case route unavailable";
            }

            string referralReason;
            var routeDecision = EvaluateCaseTransferOrHospitalization(patient);
            if (TryBuildBlockedCaseRouteReferralReason(patientCase, routeDecision, out popupReason, out equipmentLike, out referralReason))
            {
                return referralReason;
            }

            var currentDepartmentId = GetCaseRuntimeDepartmentId(patient, patientCase);
            var nextDiagnosis = CaseCarePlanner.SelectNextDiagnosis(patientCase, currentDepartmentId);
            if (nextDiagnosis == null)
            {
                return "case route unavailable";
            }

            var diagnosisName = GetDiagnosisDisplayName(nextDiagnosis.DiagnosisId);
            var departmentName = GetDepartmentDisplayName(nextDiagnosis.DepartmentId);
            if (nextDiagnosis.NeedsHospitalization)
            {
                popupReason = BuildReferralStepReason(false, null, diagnosisName, departmentName);
                return "hospitalization route unavailable";
            }

            if (!string.IsNullOrEmpty(nextDiagnosis.DepartmentId)
                && !string.Equals(nextDiagnosis.DepartmentId, currentDepartmentId, StringComparison.Ordinal))
            {
                popupReason = string.Format(ModText.T("MedicalCaseReferralReasonDepartment"), departmentName);
                return "profile department unavailable";
            }

            popupReason = string.Format(
                "{0}",
                BuildReferralStepReason(
                    nextDiagnosis.Status == CaseDiagnosisStatus.Hidden || nextDiagnosis.Status == CaseDiagnosisStatus.Suspected,
                    null,
                    diagnosisName,
                    departmentName));
            return "case route unavailable";
        }

        private static bool TryForceCaseBlockedReferral(BehaviorPatient patient, string reason, bool equipmentLike)
        {
            if (patient == null || patient.m_state == null || patient.m_state.m_sentAway || patient.m_state.m_sentHome)
            {
                return false;
            }

            var entity = ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            if (entity == null)
            {
                return false;
            }

            var hospitalization = patient.GetComponent<HospitalizationComponent>();
            if (hospitalization != null && hospitalization.IsHospitalized())
            {
                return false;
            }

            var percent = equipmentLike
                ? (RuntimeSettings.Config == null ? 0 : RuntimeSettings.Config.EquipmentReferralPaymentPercent.Value)
                : (RuntimeSettings.Config == null ? 0 : RuntimeSettings.Config.UnsupportedDiagnosisReferralPaymentPercent.Value);
            var payment = GetCaseInsurancePayment(patient, percent);
            if (payment > 0)
            {
                PayBlockedCaseReferral(patient, entity, payment);
                if (equipmentLike)
                {
                    RuntimeCounters.EquipmentReferralIncome += payment;
                }
                else
                {
                    RuntimeCounters.UnsupportedDiagnosisReferralIncome += payment;
                }
            }

            patient.m_state.m_sentAway = true;
            patient.m_state.m_sentHome = false;
            patient.m_state.m_untreated = false;
            patient.m_state.m_waitingForPlayer = false;
            SetFieldValue(patient.m_state, "m_bookmarked", false);
            TryRemoveBookmark(entity);
            MarkReferred(patient, reason);
            if (equipmentLike)
            {
                RuntimeCounters.EquipmentReferrals++;
            }
            else
            {
                RuntimeCounters.UnsupportedDiagnosisReferrals++;
            }

            var leave = AccessTools.Method(typeof(BehaviorPatient), "Leave", new[] { typeof(bool), typeof(bool), typeof(bool) });
            if (leave != null)
            {
                leave.Invoke(patient, new object[] { false, false, false });
            }

            return true;
        }

        private static string GetBlockedCaseReferralReason(BehaviorPatient patient, PatientCase patientCase, out string popupReason, out bool equipmentLike)
        {
            popupReason = null;
            equipmentLike = false;
            if (patient == null || patientCase == null || patientCase.Complete)
            {
                TraceLoggingService.LogPatientDecision(patient, "GetBlockedCaseReferralReason", false, "missing_case", "case_summary=none");
                return null;
            }

            var routeDecision = EvaluateCaseTransferOrHospitalization(patient);
            var hasAvailableExamination = HasCaseAvailableExamination(patient);
            var hasAvailableTreatmentOrProgress = HasCaseAvailableTreatmentOrProgress(patient);
            var hasTransferRoute = routeDecision != null && routeDecision.RouteExists && routeDecision.CanExecuteNow;
            if (hasAvailableExamination
                || hasAvailableTreatmentOrProgress
                || hasTransferRoute)
            {
                TraceLoggingService.LogPatientDecision(
                    patient,
                    "GetBlockedCaseReferralReason",
                    false,
                    "case_progress_still_available",
                    BuildTraceDecisionContext(patient, patientCase)
                    + ";HasCaseAvailableExamination=" + hasAvailableExamination
                    + ";HasCaseAvailableTreatmentOrProgress=" + hasAvailableTreatmentOrProgress
                    + ";HasCaseTransferOrHospitalizationRoute=" + hasTransferRoute);
                return null;
            }

            string routeReferralReason;
            if (TryBuildBlockedCaseRouteReferralReason(patientCase, routeDecision, out popupReason, out equipmentLike, out routeReferralReason))
            {
                TraceLoggingService.LogPatientDecision(
                    patient,
                    "GetBlockedCaseReferralReason",
                    true,
                    routeReferralReason,
                    BuildTraceDecisionContext(patient, patientCase)
                    + ";HasCaseAvailableExamination=" + hasAvailableExamination
                    + ";HasCaseAvailableTreatmentOrProgress=" + hasAvailableTreatmentOrProgress
                    + ";HasCaseTransferOrHospitalizationRoute=" + hasTransferRoute
                    + ";popup_reason=" + (string.IsNullOrEmpty(popupReason) ? "-" : popupReason)
                    + ";equipment_like=" + equipmentLike);
                return routeReferralReason;
            }

            var currentDepartmentId = GetCaseRuntimeDepartmentId(patient, patientCase);
            var nextDiagnosis = CaseCarePlanner.SelectNextDiagnosis(patientCase, currentDepartmentId);
            if (nextDiagnosis == null)
            {
                popupReason = ModText.T("MedicalCaseReferralReasonDiagnosticGeneric");
                TraceLoggingService.LogPatientDecision(
                    patient,
                    "GetBlockedCaseReferralReason",
                    true,
                    "diagnostic route unavailable",
                    BuildTraceDecisionContext(patient, patientCase)
                    + ";HasCaseAvailableExamination=" + hasAvailableExamination
                    + ";HasCaseAvailableTreatmentOrProgress=" + hasAvailableTreatmentOrProgress
                    + ";HasCaseTransferOrHospitalizationRoute=" + hasTransferRoute
                    + ";popup_reason=" + popupReason
                    + ";reason=no_next_diagnosis");
                return "diagnostic route unavailable";
            }

            var department = ResolveDepartment(nextDiagnosis.DepartmentId);
            var diagnosisName = GetDiagnosisDisplayName(nextDiagnosis.DiagnosisId);
            var nextProcedure = GetRepresentativeProcedure(nextDiagnosis);
            var procedureName = GetProcedureDisplayName(nextProcedure);
            var departmentName = GetDepartmentDisplayName(nextDiagnosis.DepartmentId);
            if (department == null || department.IsClosed())
            {
                popupReason = string.Format(ModText.T("MedicalCaseReferralReasonDepartment"), departmentName);
                TraceLoggingService.LogPatientDecision(
                    patient,
                    "GetBlockedCaseReferralReason",
                    true,
                    "profile department unavailable",
                    BuildTraceDecisionContext(patient, patientCase)
                    + ";next_diagnosis=" + nextDiagnosis.DiagnosisId
                    + ";target_department=" + nextDiagnosis.DepartmentId
                    + ";representative_procedure=" + (nextProcedure == null ? "-" : nextProcedure.DatabaseID.ToString())
                    + ";popup_reason=" + popupReason);
                return "profile department unavailable";
            }

            if (!HasAnyDoctorCapacity(department))
            {
                popupReason = string.Format(ModText.T("MedicalCaseReferralReasonDoctor"), departmentName);
                TraceLoggingService.LogPatientDecision(
                    patient,
                    "GetBlockedCaseReferralReason",
                    true,
                    "no available profile doctor",
                    BuildTraceDecisionContext(patient, patientCase)
                    + ";next_diagnosis=" + nextDiagnosis.DiagnosisId
                    + ";target_department=" + nextDiagnosis.DepartmentId
                    + ";representative_procedure=" + (nextProcedure == null ? "-" : nextProcedure.DatabaseID.ToString())
                    + ";popup_reason=" + popupReason);
                return "no available profile doctor";
            }

            var entity = ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            var availability = nextProcedure == null || entity == null
                ? ProcedureSceneAvailability.UNKNOWN
                : CreateProcedureAvailability(nextProcedure, entity, department);
            if (IsRoomOrEquipmentBlock(availability))
            {
                popupReason = DescribeRoomEquipmentReferralReason(availability, procedureName, departmentName, diagnosisName);
                equipmentLike = true;
                TraceLoggingService.LogPatientDecision(
                    patient,
                    "GetBlockedCaseReferralReason",
                    true,
                    "missing room or equipment",
                    BuildTraceDecisionContext(patient, patientCase)
                    + ";next_diagnosis=" + nextDiagnosis.DiagnosisId
                    + ";target_department=" + nextDiagnosis.DepartmentId
                    + ";representative_procedure=" + (nextProcedure == null ? "-" : nextProcedure.DatabaseID.ToString())
                    + ";popup_reason=" + popupReason
                    + ";equipment_like=true");
                return "missing room or equipment";
            }

            if (nextDiagnosis.Status == CaseDiagnosisStatus.Hidden || nextDiagnosis.Status == CaseDiagnosisStatus.Suspected)
            {
                popupReason = BuildReferralStepReason(true, procedureName, diagnosisName, departmentName);
                TraceLoggingService.LogPatientDecision(
                    patient,
                    "GetBlockedCaseReferralReason",
                    true,
                    "diagnostic route unavailable",
                    BuildTraceDecisionContext(patient, patientCase)
                    + ";next_diagnosis=" + nextDiagnosis.DiagnosisId
                    + ";target_department=" + nextDiagnosis.DepartmentId
                    + ";representative_procedure=" + (nextProcedure == null ? "-" : nextProcedure.DatabaseID.ToString())
                    + ";popup_reason=" + popupReason);
                return "diagnostic route unavailable";
            }

            popupReason = BuildReferralStepReason(false, procedureName, diagnosisName, departmentName);
            TraceLoggingService.LogPatientDecision(
                patient,
                "GetBlockedCaseReferralReason",
                true,
                "treatment route unavailable",
                BuildTraceDecisionContext(patient, patientCase)
                + ";next_diagnosis=" + nextDiagnosis.DiagnosisId
                + ";target_department=" + nextDiagnosis.DepartmentId
                + ";representative_procedure=" + (nextProcedure == null ? "-" : nextProcedure.DatabaseID.ToString())
                + ";popup_reason=" + popupReason);
            return "treatment route unavailable";
        }

        public static bool ShouldSuppressNotification(GLib.Entity character, string titleLocId)
        {
            if (!Enabled || character == null || string.IsNullOrEmpty(titleLocId))
            {
                return false;
            }

            var patient = character.GetComponent<BehaviorPatient>();
            if (patient == null)
            {
                return false;
            }

            var patientCase = GetCase(patient);
            if (patientCase != null
                && (patientCase.Complete || IsCaseFullyTreated(patientCase))
                && (titleLocId == "NOTIF_COMPLICATED_DIAGNOSIS"
                    || titleLocId == "NOTIF_AMBIGUOUS_DIAGNOSIS"
                    || titleLocId == "NOTIF_UNCLEAR_DEPARTMENT"
                    || titleLocId == "NOTIF_NO_TREATMENT"))
            {
                TraceLoggingService.LogPatientDecision(patient, "ShouldSuppressNotification", true, "case_complete_or_fully_treated", BuildTraceDecisionContext(patient, patientCase) + ";title=" + titleLocId + ";mute_applied=false");
                return true;
            }

            if (!HasOpenCase(patient))
            {
                TraceLoggingService.LogPatientDecision(patient, "ShouldSuppressNotification", false, "no_open_case", "title=" + titleLocId);
                return false;
            }

            if (IsNotificationMuted(patient, titleLocId))
            {
                TraceLoggingService.LogPatientDecision(patient, "ShouldSuppressNotification", true, "notification_muted", BuildTraceDecisionContext(patient, patientCase) + ";title=" + titleLocId + ";mute_applied=false");
                return true;
            }

            var shouldSuppress = false;
            var hasExamination = false;
            var hasTreatment = false;
            var hasRoute = false;
            var canRefer = false;
            if (titleLocId == "NOTIF_COMPLICATED_DIAGNOSIS"
                || titleLocId == "NOTIF_AMBIGUOUS_DIAGNOSIS"
                || titleLocId == "NOTIF_UNCLEAR_DEPARTMENT")
            {
                hasExamination = HasCaseAvailableExamination(patient);
                hasRoute = HasCaseTransferOrHospitalizationRoute(patient);
                canRefer = CanReferBlockedCase(patient);
                shouldSuppress = hasExamination || hasRoute || canRefer;
            }
            else if (titleLocId == "NOTIF_NO_TREATMENT")
            {
                hasTreatment = HasCaseAvailableTreatmentOrProgress(patient);
                hasRoute = HasCaseTransferOrHospitalizationRoute(patient);
                hasExamination = HasCaseAvailableExamination(patient);
                canRefer = CanReferBlockedCase(patient);
                shouldSuppress = hasTreatment || hasRoute || hasExamination || canRefer;
            }

            if (shouldSuppress)
            {
                RememberNotificationMute(patient, titleLocId, 6f);
            }

            TraceLoggingService.LogPatientDecision(
                patient,
                "ShouldSuppressNotification",
                shouldSuppress,
                shouldSuppress ? "suppressed" : "not_suppressed",
                BuildTraceDecisionContext(patient, patientCase)
                + ";title=" + titleLocId
                + ";HasCaseAvailableExamination=" + hasExamination
                + ";HasCaseAvailableTreatmentOrProgress=" + hasTreatment
                + ";HasCaseTransferOrHospitalizationRoute=" + hasRoute
                + ";CanReferBlockedCase=" + canRefer
                + ";mute_applied=" + shouldSuppress);
            return shouldSuppress;
        }

        private static void MuteCaseProgressNotifications(BehaviorPatient patient, float seconds)
        {
            if (patient == null)
            {
                return;
            }

            RememberNotificationMute(patient, "NOTIF_COMPLICATED_DIAGNOSIS", seconds);
            RememberNotificationMute(patient, "NOTIF_AMBIGUOUS_DIAGNOSIS", seconds);
            RememberNotificationMute(patient, "NOTIF_UNCLEAR_DEPARTMENT", seconds);
            RememberNotificationMute(patient, "NOTIF_NO_TREATMENT", seconds);
        }

        private static bool CanReferBlockedCase(BehaviorPatient patient)
        {
            var patientCase = GetCase(patient);
            string popupReason;
            bool equipmentLike;
            try
            {
                return !string.IsNullOrEmpty(GetBlockedCaseReferralReason(patient, patientCase, out popupReason, out equipmentLike))
                    || !string.IsNullOrEmpty(BuildGenericBlockedCaseReferralReason(patient, patientCase, out popupReason, out equipmentLike));
            }
            catch
            {
                return !string.IsNullOrEmpty(BuildGenericBlockedCaseReferralReason(patient, patientCase, out popupReason, out equipmentLike));
            }
        }

        private static void RememberNotificationMute(BehaviorPatient patient, string titleLocId, float seconds)
        {
            if (patient == null || string.IsNullOrEmpty(titleLocId))
            {
                return;
            }

            var entity = ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            if (entity == null)
            {
                return;
            }

            NotificationMuteUntil[entity.GetEntityID().ToString(CultureInfo.InvariantCulture) + "|" + titleLocId] = Time.realtimeSinceStartup + Math.Max(0.5f, seconds);
        }

        private static bool IsNotificationMuted(BehaviorPatient patient, string titleLocId)
        {
            if (patient == null || string.IsNullOrEmpty(titleLocId))
            {
                return false;
            }

            var entity = ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            if (entity == null)
            {
                return false;
            }

            var key = entity.GetEntityID().ToString(CultureInfo.InvariantCulture) + "|" + titleLocId;
            if (!NotificationMuteUntil.TryGetValue(key, out var mutedUntil))
            {
                return false;
            }

            if (mutedUntil > Time.realtimeSinceStartup)
            {
                return true;
            }

            NotificationMuteUntil.Remove(key);
            return false;
        }

        private static bool CanRetryBlockedCaseRecovery(BehaviorPatient patient)
        {
            var entity = patient == null ? null : ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            if (entity == null)
            {
                return true;
            }

            float retryUntil;
            if (!BlockedCaseRetryUntil.TryGetValue(entity.GetEntityID(), out retryUntil))
            {
                return true;
            }

            if (Time.realtimeSinceStartup >= retryUntil)
            {
                BlockedCaseRetryUntil.Remove(entity.GetEntityID());
                return true;
            }

            return false;
        }

        private static void RememberBlockedCaseRetry(BehaviorPatient patient, float seconds)
        {
            var entity = patient == null ? null : ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            if (entity == null)
            {
                return;
            }

            BlockedCaseRetryUntil[entity.GetEntityID()] = Time.realtimeSinceStartup + Math.Max(0.5f, seconds);
        }

        private static void ClearBlockedCaseRetry(BehaviorPatient patient)
        {
            var entity = patient == null ? null : ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            if (entity == null)
            {
                return;
            }

            BlockedCaseRetryUntil.Remove(entity.GetEntityID());
        }

        private static int CalculateRisk(PatientCase patientCase)
        {
            var risk = 0;
            for (var i = 0; i < patientCase.Diagnoses.Count; i++)
            {
                risk += patientCase.Diagnoses[i].Hazard == "High" ? 4 : patientCase.Diagnoses[i].Hazard == "Medium" ? 2 : 1;
                if (patientCase.Diagnoses[i].CollapseCapable)
                {
                    risk += 6;
                }

                if (patientCase.Diagnoses[i].SurgeryLikely)
                {
                    risk += 3;
                }
            }

            return risk;
        }

        private static void DrawCaseWindowContents(int id)
        {
            var snapshot = GetStableCaseWindowSnapshot(SelectedPatient, SelectedCase);
            if (snapshot == null)
            {
                ShowCaseWindow = false;
                return;
            }

            GUILayout.BeginVertical(PanelStyle);
            GUILayout.Label(SelectedCase.PatientName, HeaderStyle);
            CaseWindowScroll = GUILayout.BeginScrollView(CaseWindowScroll, false, true);
            GUILayout.Label(snapshot.TitleLine, TextStyle);
            GUILayout.Space(6f);
            GUILayout.Label(ModText.T("MedicalCaseDiagnoses") + ": " + SelectedCase.Diagnoses.Count, HeaderStyle);
            for (var i = 0; i < snapshot.DiagnosisLines.Count; i++)
            {
                GUILayout.Label(snapshot.DiagnosisLines[i], TextStyle);
            }

            GUILayout.Space(6f);
            GUILayout.Label(ModText.T("MedicalCaseSymptoms"), HeaderStyle);
            for (var i = 0; i < snapshot.SymptomLines.Count; i++)
            {
                GUILayout.Label(snapshot.SymptomLines[i], TextStyle);
            }

            if (snapshot.BlockerLines.Count > 0)
            {
                GUILayout.Space(6f);
                GUILayout.Label(ModText.T("MedicalCaseBlockers"), HeaderStyle);
                for (var i = 0; i < snapshot.BlockerLines.Count; i++)
                {
                    GUILayout.Label(snapshot.BlockerLines[i], TextStyle);
                }
            }

            if (snapshot.TimelineLines.Count > 0)
            {
                GUILayout.Space(6f);
                GUILayout.Label(ModText.T("MedicalCaseTimeline"), HeaderStyle);
                for (var i = 0; i < snapshot.TimelineLines.Count; i++)
                {
                    GUILayout.Label(snapshot.TimelineLines[i], MutedStyle);
                }
            }
            GUILayout.EndScrollView();

            GUILayout.Space(6f);
            if (GUILayout.Button(ModText.T("Close"), ButtonStyle))
            {
                ShowCaseWindow = false;
            }

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        private static List<string> BuildCaseBlockers(BehaviorPatient patient, PatientCase patientCase)
        {
            var blockers = new List<string>();
            if (patientCase == null || patientCase.Complete)
            {
                return blockers;
            }

            var procedure = patient == null ? null : patient.GetComponent<ProcedureComponent>();
            var queue = procedure == null || procedure.m_state == null ? null : procedure.m_state.m_procedureQueue;
            if (queue != null)
            {
                if ((queue.m_activeExamination != null)
                    || queue.m_plannedExaminationStates.Count > 0
                    || queue.m_labProcedures.Count > 0)
                {
                    blockers.Add(ModText.T("MedicalCaseBlockerWaitingExam"));
                }

                if (queue.m_plannedTreatmentStates.Count > 0 || queue.m_activeTreatmentStates.Count > 0)
                {
                    blockers.Add(ModText.T("MedicalCaseBlockerWaitingTreatment"));
                }
            }

            var currentDepartmentId = patient == null || patient.GetDepartment() == null || patient.GetDepartment().GetDepartmentType() == null
                ? patientCase.ActiveDepartmentId
                : patient.GetDepartment().GetDepartmentType().DatabaseID.ToString();
            var nextDiagnosis = CaseCarePlanner.SelectNextDiagnosis(patientCase, currentDepartmentId);
            if (nextDiagnosis == null)
            {
                return blockers;
            }

            if (!string.IsNullOrEmpty(currentDepartmentId)
                && !string.IsNullOrEmpty(nextDiagnosis.DepartmentId)
                && nextDiagnosis.DepartmentId != currentDepartmentId)
            {
                blockers.Add(string.Format(ModText.T("MedicalCaseBlockerTransfer"), nextDiagnosis.DepartmentId));
            }

            if (nextDiagnosis.NeedsHospitalization)
            {
                blockers.Add(ModText.T("MedicalCaseBlockerHospitalization"));
            }

            var department = ResolveDepartment(nextDiagnosis.DepartmentId);
            if (department == null || department.IsClosed())
            {
                blockers.Add(string.Format(ModText.T("MedicalCaseBlockerDepartment"), nextDiagnosis.DepartmentId));
                return blockers;
            }

            if (!HasAnyDoctorCapacity(department))
            {
                blockers.Add(ModText.T("MedicalCaseBlockerDoctor"));
            }

            var entity = patient == null ? null : ReflectionHelpers.GetField(patient, "m_entity") as GLib.Entity;
            var nextProcedure = GetRepresentativeProcedure(nextDiagnosis);
            var availability = nextProcedure == null || entity == null
                ? ProcedureSceneAvailability.AVAILABLE
                : CreateProcedureAvailability(nextProcedure, entity, department);
            if (IsRoomOrEquipmentBlock(availability))
            {
                blockers.Add(ModText.T("MedicalCaseBlockerRoomEquipment"));
            }

            return blockers;
        }

        private static string FormatSymptoms(CaseDiagnosis diagnosis)
        {
            if (diagnosis == null || diagnosis.SymptomIds.Count == 0)
            {
                return "-";
            }

            int known;
            int hidden;
            int treated;
            GetDisplaySymptomCounts(diagnosis, out known, out hidden, out treated);
            if (treated > 0)
            {
                return string.Format(ModText.T("MedicalCaseSymptomCountsWithTreated"), known, hidden, treated);
            }

            if (known <= 0)
            {
                return string.Format(ModText.T("MedicalCaseHiddenSymptoms"), hidden);
            }

            return string.Format(ModText.T("MedicalCaseSymptomCounts"), known, hidden);
        }

        private static string FormatInteractionSummary(CaseDiagnosis diagnosis)
        {
            if (diagnosis == null || diagnosis.ActiveInteractions.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(96);
            builder.Append(" | interactions ");
            for (var i = 0; i < diagnosis.ActiveInteractions.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(diagnosis.ActiveInteractions[i].Kind);
            }

            return builder.ToString();
        }

        private static string FormatCollapseWindow(CaseDiagnosis diagnosis)
        {
            if (diagnosis == null || !diagnosis.CollapseCapable || diagnosis.CollapseDeadlineHours <= 0f)
            {
                return string.Empty;
            }

            var hours = Math.Max(0f, diagnosis.CollapseDeadlineHours - GetCaseClockHours());
            return " | " + string.Format(ModText.T("MedicalCaseCollapseWindow"), hours);
        }

        private static string GetStatusLabel(CaseDiagnosisStatus status)
        {
            switch (status)
            {
                case CaseDiagnosisStatus.Active:
                    return ModText.T("MedicalCaseStatusActive");
                case CaseDiagnosisStatus.Suspected:
                    return ModText.T("MedicalCaseStatusSuspected");
                case CaseDiagnosisStatus.Diagnosed:
                    return ModText.T("MedicalCaseStatusDiagnosed");
                case CaseDiagnosisStatus.Treated:
                    return ModText.T("MedicalCaseStatusTreated");
                default:
                    return ModText.T("MedicalCaseStatusHidden");
            }
        }

        private static string GetHazardLabel(string hazard)
        {
            if (string.Equals(hazard, "High", StringComparison.OrdinalIgnoreCase))
            {
                return ModText.T("MedicalCaseHazardHigh");
            }

            if (string.Equals(hazard, "Medium", StringComparison.OrdinalIgnoreCase))
            {
                return ModText.T("MedicalCaseHazardMedium");
            }

            return ModText.T("MedicalCaseHazardLow");
        }

        private static void EnsureStyles()
        {
            if (WindowStyle != null)
            {
                return;
            }

            WhiteTexture = MakeTexture(new Color(1f, 1f, 1f, 0.94f));
            DarkHeaderTexture = MakeTexture(new Color(0.35f, 0.35f, 0.35f, 0.98f));
            WindowStyle = new GUIStyle(GUI.skin.window);
            WindowStyle.normal.textColor = Color.white;
            WindowStyle.padding = new RectOffset(8, 8, 22, 8);

            PanelStyle = new GUIStyle(GUI.skin.box);
            PanelStyle.normal.background = WhiteTexture;
            PanelStyle.padding = new RectOffset(12, 12, 10, 10);

            HeaderStyle = new GUIStyle(GUI.skin.label);
            HeaderStyle.normal.background = DarkHeaderTexture;
            HeaderStyle.normal.textColor = Color.white;
            HeaderStyle.alignment = TextAnchor.MiddleCenter;
            HeaderStyle.fontStyle = FontStyle.Bold;
            HeaderStyle.padding = new RectOffset(6, 6, 3, 3);

            TextStyle = new GUIStyle(GUI.skin.label);
            TextStyle.normal.textColor = new Color(0.25f, 0.25f, 0.25f, 1f);
            TextStyle.wordWrap = true;

            MutedStyle = new GUIStyle(TextStyle);
            MutedStyle.normal.textColor = new Color(0.42f, 0.42f, 0.42f, 1f);

            ButtonStyle = new GUIStyle(GUI.skin.button);
            ButtonStyle.normal.textColor = Color.white;
        }

        private static Texture2D MakeTexture(Color color)
        {
            var texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        private static void QueueReferralPopup(string patientName, string reason)
        {
            ReferralPopupQueue.Enqueue(new ReferralPopupMessage
            {
                PatientName = string.IsNullOrEmpty(patientName) ? ModText.T("MedicalCaseReferralUnknownPatient") : patientName,
                Reason = string.IsNullOrEmpty(reason) ? ModText.T("MedicalCaseReferralReasonDiagnostic") : reason
            });
        }

        private static void DrawReferralPopup()
        {
            if (ActiveReferralPopup == null && ReferralPopupQueue.Count > 0)
            {
                ActiveReferralPopup = ReferralPopupQueue.Dequeue();
            }

            if (ActiveReferralPopup == null)
            {
                return;
            }

            var screenWidth = Screen.width;
            var screenHeight = Screen.height;
            if (screenWidth > 0 && screenHeight > 0)
            {
                ReferralPopupWindow.x = Mathf.Clamp((screenWidth - ReferralPopupWindow.width) * 0.5f, 20f, Math.Max(20f, screenWidth - ReferralPopupWindow.width - 20f));
                ReferralPopupWindow.y = Mathf.Clamp((screenHeight - ReferralPopupWindow.height) * 0.28f, 20f, Math.Max(20f, screenHeight - ReferralPopupWindow.height - 20f));
            }

            ReferralPopupWindow = GUI.ModalWindow(871239, ReferralPopupWindow, DrawReferralPopupContents, ModText.T("MedicalCaseReferralPopupTitle"), WindowStyle);
        }

        private static void DrawReferralPopupContents(int id)
        {
            if (ActiveReferralPopup == null)
            {
                return;
            }

            GUILayout.BeginVertical(PanelStyle);
            GUILayout.Label(ActiveReferralPopup.PatientName, HeaderStyle);
            GUILayout.Space(10f);
            GUILayout.Label(string.Format(ModText.T("MedicalCaseReferralPopupBody"), ActiveReferralPopup.PatientName, ActiveReferralPopup.Reason), TextStyle);
            GUILayout.Space(12f);
            if (GUILayout.Button(ModText.T("Close"), ButtonStyle, GUILayout.Height(30f)))
            {
                ActiveReferralPopup = null;
            }
            GUILayout.EndVertical();
        }

        private static void AddTimeline(PatientCase patientCase, string text)
        {
            var day = DayTime.Instance == null ? 0 : DayTime.Instance.GetDay();
            var hour = DayTime.Instance == null ? 0f : DayTime.Instance.GetDayTimeHours();
            patientCase.Timeline.Add(new CaseTimelineEvent
            {
                Day = day,
                Hour = hour,
                Text = text
            });
            patientCase.TimelineEntries.Add(new TimelineEntry
            {
                Day = day,
                Hour = hour,
                Category = "case",
                ProblemId = patientCase.MaterializedSlice.VisibleProblemIds.Count > 0 ? patientCase.MaterializedSlice.VisibleProblemIds[0] : null,
                ClusterId = patientCase.MaterializedSlice.ClusterId,
                Reason = patientCase.Disposition == null ? string.Empty : patientCase.Disposition.Reason,
                Text = text
            });
            if (patientCase.Timeline.Count > MaxTimelineEntries)
            {
                patientCase.Timeline.RemoveAt(0);
            }

            if (patientCase.TimelineEntries.Count > MaxTimelineEntries)
            {
                patientCase.TimelineEntries.RemoveAt(0);
            }

            TraceLoggingService.LogCaseTimeline(patientCase, text);
        }

        private static void EnsureLoaded()
        {
            var path = GetStoragePath();
            if (path != LoadedPath)
            {
                Cases.Clear();
                ConditionCases.Clear();
                PatientPanelSnapshots.Clear();
                CaseWindowSnapshots.Clear();
                BlockedCaseRetryUntil.Clear();
                SelectedCase = null;
                ShowCaseWindow = false;
                LoadedPath = path;
            }

            if (path == null || LoadAttempted.ContainsKey(path))
            {
                return;
            }

            LoadAttempted[path] = true;
            Load(path);
        }

        private static void Load(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                PatientCase current = null;
                foreach (var line in File.ReadAllLines(path))
                {
                    if (line.StartsWith("CASE|", StringComparison.Ordinal))
                    {
                        var parts = line.Split('|');
                        if (parts.Length < 8)
                        {
                            continue;
                        }

                        float collapseTimerMultiplier;
                        if (parts.Length < 9
                            || !float.TryParse(parts[8], NumberStyles.Float, CultureInfo.InvariantCulture, out collapseTimerMultiplier)
                            || collapseTimerMultiplier <= 0f)
                        {
                            collapseTimerMultiplier = 1f;
                        }

                        current = new PatientCase
                        {
                            CaseId = parts[1],
                            PatientEntityId = uint.Parse(parts[2], CultureInfo.InvariantCulture),
                            PatientName = parts[3],
                            Hopeless = bool.Parse(parts[4]),
                            Complete = bool.Parse(parts[5]),
                            ActiveDepartmentId = parts[6],
                            RiskScore = int.Parse(parts[7], CultureInfo.InvariantCulture),
                            CollapseTimerMultiplier = collapseTimerMultiplier
                        };
                        if (!current.Complete)
                        {
                            Cases[current.PatientEntityId] = current;
                        }
                        else
                        {
                            current = null;
                        }
                    }
                    else if (line.StartsWith("DX|", StringComparison.Ordinal) && current != null)
                    {
                        var parts = line.Split('|');
                        if (parts.Length < 7)
                        {
                            continue;
                        }

                        var diagnosis = new CaseDiagnosis
                        {
                            DiagnosisId = parts[1],
                            DepartmentId = parts[2],
                            Hazard = parts[3],
                            CollapseCapable = bool.Parse(parts[4]),
                            SurgeryLikely = bool.Parse(parts[5]),
                            Status = (CaseDiagnosisStatus)Enum.Parse(typeof(CaseDiagnosisStatus), parts[6])
                        };
                        if (parts.Length >= 8 && !string.IsNullOrEmpty(parts[7]))
                        {
                            var symptoms = parts[7].Split(',');
                            for (var i = 0; i < symptoms.Length; i++)
                            {
                                if (!string.IsNullOrEmpty(symptoms[i]))
                                {
                                    diagnosis.SymptomIds.Add(symptoms[i]);
                                }
                            }
                        }

                        if (parts.Length >= 9 && !string.IsNullOrEmpty(parts[8]))
                        {
                            var knownSymptoms = parts[8].Split(',');
                            for (var i = 0; i < knownSymptoms.Length; i++)
                            {
                                if (!string.IsNullOrEmpty(knownSymptoms[i]))
                                {
                                    diagnosis.KnownSymptomIds.Add(knownSymptoms[i]);
                                }
                            }
                        }

                        if (parts.Length >= 10 && !string.IsNullOrEmpty(parts[9]))
                        {
                            var treatedSymptoms = parts[9].Split(',');
                            for (var i = 0; i < treatedSymptoms.Length; i++)
                            {
                                if (!string.IsNullOrEmpty(treatedSymptoms[i]))
                                {
                                    diagnosis.TreatedSymptomIds.Add(treatedSymptoms[i]);
                                }
                            }
                        }

                        if (parts.Length >= 11 && !string.IsNullOrEmpty(parts[10]))
                        {
                            float.TryParse(parts[10], NumberStyles.Float, CultureInfo.InvariantCulture, out diagnosis.CollapseDeadlineHours);
                        }

                        BackfillDiagnosisData(diagnosis);
                        if (diagnosis.Status == CaseDiagnosisStatus.Active)
                        {
                            diagnosis.Status = CaseDiagnosisStatus.Suspected;
                        }

                        current.Diagnoses.Add(diagnosis);
                    }
                    else if (line.StartsWith("EV|", StringComparison.Ordinal) && current != null)
                    {
                        var parts = line.Split('|');
                        if (parts.Length < 4)
                        {
                            continue;
                        }

                        current.Timeline.Add(new CaseTimelineEvent
                        {
                            Day = int.Parse(parts[1], CultureInfo.InvariantCulture),
                            Hour = float.Parse(parts[2], CultureInfo.InvariantCulture),
                            Text = parts[3]
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Failed to load patient cases: " + ex.Message);
            }
        }

        private static void Save()
        {
            var path = GetStoragePath();
            if (path == null)
            {
                return;
            }

            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var lines = new List<string>();
                foreach (var patientCase in Cases.Values)
                {
                    lines.Add("CASE|" + patientCase.CaseId + "|" + patientCase.PatientEntityId + "|" + Sanitize(patientCase.PatientName)
                        + "|" + patientCase.Hopeless + "|" + patientCase.Complete + "|" + patientCase.ActiveDepartmentId + "|" + patientCase.RiskScore
                        + "|" + patientCase.CollapseTimerMultiplier.ToString(CultureInfo.InvariantCulture));
                    for (var i = 0; i < patientCase.Diagnoses.Count; i++)
                    {
                        var dx = patientCase.Diagnoses[i];
                        lines.Add("DX|" + dx.DiagnosisId + "|" + dx.DepartmentId + "|" + dx.Hazard + "|" + dx.CollapseCapable + "|" + dx.SurgeryLikely + "|" + dx.Status + "|" + string.Join(",", dx.SymptomIds.ToArray()) + "|" + string.Join(",", dx.KnownSymptomIds.ToArray()) + "|" + string.Join(",", dx.TreatedSymptomIds.ToArray()) + "|" + dx.CollapseDeadlineHours.ToString(CultureInfo.InvariantCulture));
                    }

                    for (var i = 0; i < patientCase.Timeline.Count; i++)
                    {
                        var ev = patientCase.Timeline[i];
                        lines.Add("EV|" + ev.Day + "|" + ev.Hour.ToString(CultureInfo.InvariantCulture) + "|" + Sanitize(ev.Text));
                    }
                }

                File.WriteAllLines(path, lines.ToArray());
            }
            catch (Exception ex)
            {
                Log("Failed to save patient cases: " + ex.Message);
            }
        }

        private static string GetStoragePath()
        {
            var hospital = Hospital.Instance;
            if (hospital == null)
            {
                return null;
            }

            var state = ReflectionHelpers.GetField(hospital, "m_state");
            var name = ReflectionHelpers.GetField(state, "m_hospitalName") as string;
            if (string.IsNullOrEmpty(name))
            {
                name = hospital.Name;
            }

            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            return Path.Combine(Path.Combine(Paths.ConfigPath, "AutoLabBalancer.PatientCases"), MakeSafeFileName(name) + ".cases");
        }

        private static string GetDepartmentDisplayName(string departmentId)
        {
            EnsureDepartmentCache();
            DepartmentTypeCache.TryGetValue(departmentId, out var departmentType);
            if (departmentType != null)
            {
                var localized = SafeGetLocalizedText(departmentType);
                if (!string.IsNullOrEmpty(localized))
                {
                    return localized;
                }
            }

            return string.IsNullOrEmpty(departmentId) ? ModText.T("MedicalCaseReferralUnknownDepartment") : departmentId;
        }

        private static string GetDiagnosisDisplayName(string diagnosisId)
        {
            var condition = ResolveDiagnosis(diagnosisId);
            if (condition == null)
            {
                return string.IsNullOrEmpty(diagnosisId) ? ModText.T("MedicalCaseReferralUnknownDiagnosis") : diagnosisId;
            }

            var localized = SafeGetLocalizedText(condition);
            return string.IsNullOrEmpty(localized) ? (SafeDatabaseId(condition) ?? diagnosisId) : localized;
        }

        private static string GetProcedureDisplayName(GameDBProcedure procedure)
        {
            if (procedure == null)
            {
                return null;
            }

            try
            {
                var localized = SafeGetLocalizedText(procedure);
                return string.IsNullOrEmpty(localized) ? (SafeDatabaseId(procedure) ?? ModText.T("MedicalCaseReferralUnknownProcedure")) : localized;
            }
            catch
            {
                return ModText.T("MedicalCaseReferralUnknownProcedure");
            }
        }

        private static string SafeDatabaseId(DatabaseEntry entry)
        {
            if (entry == null)
            {
                return null;
            }

            try
            {
                return entry.DatabaseID == null ? null : entry.DatabaseID.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static string SafeGetLocalizedText(DatabaseEntry entry)
        {
            if (entry == null)
            {
                return null;
            }

            try
            {
                var stringTable = StringTable.GetInstance();
                return stringTable == null ? null : stringTable.GetLocalizedText(entry);
            }
            catch
            {
                return null;
            }
        }

        private static string SafeGetLocalizedText(string localizationId)
        {
            if (string.IsNullOrEmpty(localizationId))
            {
                return null;
            }

            try
            {
                var stringTable = StringTable.GetInstance();
                return stringTable == null ? null : stringTable.GetLocalizedText(localizationId);
            }
            catch
            {
                return null;
            }
        }

        private static string DescribeRoomEquipmentReferralReason(ProcedureSceneAvailability availability, string procedureName, string departmentName, string diagnosisName)
        {
            var text = availability.ToString();
            var hasRoom = text.IndexOf("ROOM", StringComparison.OrdinalIgnoreCase) >= 0;
            var hasEquipment = text.IndexOf("EQUIPMENT", StringComparison.OrdinalIgnoreCase) >= 0;
            var useProcedure = IsMeaningfulProcedureName(procedureName);
            var useDepartment = !string.IsNullOrEmpty(departmentName) && !string.Equals(departmentName, ModText.T("MedicalCaseReferralUnknownDepartment"), StringComparison.Ordinal);
            var targetName = useProcedure
                ? procedureName
                : (useDepartment ? departmentName : diagnosisName);

            if (hasRoom && hasEquipment)
            {
                if (useProcedure)
                {
                    return string.Format(ModText.T("MedicalCaseReferralReasonRoomEquipment"), targetName);
                }

                if (useDepartment)
                {
                    return string.Format(ModText.T("MedicalCaseReferralReasonRoomEquipmentDepartment"), targetName);
                }

                return ModText.T("MedicalCaseReferralReasonRoomEquipmentGeneric");
            }

            if (hasRoom)
            {
                if (useProcedure)
                {
                    return string.Format(ModText.T("MedicalCaseReferralReasonRoom"), targetName);
                }

                if (useDepartment)
                {
                    return string.Format(ModText.T("MedicalCaseReferralReasonRoomDepartment"), targetName);
                }

                return ModText.T("MedicalCaseReferralReasonRoomGeneric");
            }

            if (hasEquipment)
            {
                if (useProcedure)
                {
                    return string.Format(ModText.T("MedicalCaseReferralReasonEquipment"), targetName);
                }

                if (useDepartment)
                {
                    return string.Format(ModText.T("MedicalCaseReferralReasonEquipmentDepartment"), targetName);
                }

                return ModText.T("MedicalCaseReferralReasonEquipmentGeneric");
            }

            if (useProcedure)
            {
                return string.Format(ModText.T("MedicalCaseReferralReasonRoomEquipment"), targetName);
            }

            if (useDepartment)
            {
                return string.Format(ModText.T("MedicalCaseReferralReasonRoomEquipmentDepartment"), targetName);
            }

            return ModText.T("MedicalCaseReferralReasonRoomEquipmentGeneric");
        }

        private static string BuildReferralStepReason(bool diagnostic, string procedureName, string diagnosisName, string departmentName)
        {
            if (IsMeaningfulProcedureName(procedureName))
            {
                return string.Format(
                    ModText.T(diagnostic ? "MedicalCaseReferralReasonDiagnostic" : "MedicalCaseReferralReasonTreatment"),
                    procedureName);
            }

            if (!string.IsNullOrEmpty(diagnosisName) && !string.Equals(diagnosisName, ModText.T("MedicalCaseReferralUnknownDiagnosis"), StringComparison.Ordinal))
            {
                return string.Format(
                    ModText.T(diagnostic ? "MedicalCaseReferralReasonDiagnosticDiagnosis" : "MedicalCaseReferralReasonTreatmentDiagnosis"),
                    diagnosisName);
            }

            if (!string.IsNullOrEmpty(departmentName) && !string.Equals(departmentName, ModText.T("MedicalCaseReferralUnknownDepartment"), StringComparison.Ordinal))
            {
                return string.Format(
                    ModText.T(diagnostic ? "MedicalCaseReferralReasonDiagnosticDepartment" : "MedicalCaseReferralReasonTreatmentDepartment"),
                    departmentName);
            }

            return ModText.T(diagnostic ? "MedicalCaseReferralReasonDiagnosticGeneric" : "MedicalCaseReferralReasonTreatmentGeneric");
        }

        private static bool IsMeaningfulProcedureName(string procedureName)
        {
            return !string.IsNullOrEmpty(procedureName)
                && !string.Equals(procedureName, ModText.T("MedicalCaseReferralUnknownProcedure"), StringComparison.Ordinal);
        }

        private static void PayBlockedCaseReferral(BehaviorPatient patient, GLib.Entity entity, int payment)
        {
            if (payment <= 0)
            {
                return;
            }

            var department = patient.GetDepartment();
            var category = PaymentCategory.INSURANCE_CLINIC;
            if (department != null)
            {
                department.Pay(payment, category, entity);
                return;
            }

            if (Hospital.Instance != null)
            {
                Hospital.Instance.Pay(payment, category);
            }
        }

        private static void TryRemoveBookmark(GLib.Entity entity)
        {
            if (entity == null)
            {
                return;
            }

            var managerType = AccessTools.TypeByName("Lopital.BookmarkedCharacterManager") ?? AccessTools.TypeByName("BookmarkedCharacterManager");
            var instanceProperty = managerType == null ? null : AccessTools.Property(managerType, "Instance");
            var manager = instanceProperty == null ? null : instanceProperty.GetValue(null, null);
            var remove = manager == null ? null : AccessTools.Method(manager.GetType(), "RemoveCharacter", new[] { typeof(GLib.Entity) });
            if (remove != null)
            {
                remove.Invoke(manager, new object[] { entity });
            }
        }

        private static void PersistMirroredSymptoms(PatientCase patientCase)
        {
            if (patientCase == null)
            {
                return;
            }

            PatientPanelSnapshots.Remove(patientCase.PatientEntityId);
            CaseWindowSnapshots.Remove(patientCase.PatientEntityId);
            if (CaseWindowSnapshotPatientId == patientCase.PatientEntityId)
            {
                ActiveCaseWindowSnapshot = null;
                CaseWindowSnapshotFrame = -1;
                CaseWindowSnapshotPatientId = 0;
            }

            Save();
        }

        private static string Sanitize(string text)
        {
            return string.IsNullOrEmpty(text) ? string.Empty : text.Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
        }

        private static string MakeSafeFileName(string text)
        {
            var chars = text.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(Path.GetInvalidFileNameChars(), chars[i]) >= 0)
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static void Log(string message)
        {
            if (RuntimeSettings.Config != null && RuntimeSettings.Config.CaseRewriteDebugLog.Value && RuntimeSettings.Logger != null)
            {
                RuntimeSettings.Logger.LogInfo("[MedicalCaseRewrite] " + message);
            }
        }

        private static string DescribeException(Exception ex)
        {
            if (ex == null)
            {
                return "unknown";
            }

            var inner = ex.InnerException == null ? null : " Inner: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message;
            return ex.GetType().Name + ": " + ex.Message + inner;
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "RandomizeMedicalCondition")]
    internal static class MedicalCaseRandomizeMedicalConditionPatch
    {
        private static void Postfix(BehaviorPatient __instance, PatientMobility allowedMobility)
        {
            MedicalCaseRewriteService.OnPatientGenerated(__instance, allowedMobility);
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "Diagnose")]
    internal static class MedicalCaseDiagnosePatch
    {
        private static void Postfix(BehaviorPatient __instance, ref DiagnosisResult __result)
        {
            MedicalCaseRewriteService.MarkDiagnosed(__instance);
            if ((__result == DiagnosisResult.COMPLICATED
                    || __result == DiagnosisResult.NONE)
                && !MedicalCaseRewriteService.HasOpenCase(__instance))
            {
                __result = DiagnosisResult.NONE;
            }
            if (__result == DiagnosisResult.COMPLICATED && MedicalCaseRewriteService.TryResolveCaseDiagnosisContinuation(__instance))
            {
                __result = DiagnosisResult.NONE;
            }
            MedicalCaseRewriteService.HandleDiagnosisResult(__instance, __result);
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "TryToScheduleExamination")]
    internal static class MedicalCaseTryToScheduleExaminationPatch
    {
        private static bool Prefix(BehaviorPatient __instance, ref bool __result)
        {
            if (MedicalCaseRewriteService.ShouldBlockFurtherSameDepartmentExaminations(__instance))
            {
                __result = false;
                return false;
            }

            return true;
        }

        private static void Postfix(BehaviorPatient __instance, ref bool __result)
        {
            if (!__result && !MedicalCaseRewriteService.ShouldBlockFurtherSameDepartmentExaminations(__instance))
            {
                __result = MedicalCaseRewriteService.TryScheduleCaseAwareExamination(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "TryToScheduleTreatments")]
    internal static class MedicalCaseTryToScheduleTreatmentsPatch
    {
        private static bool Prefix(BehaviorPatient __instance)
        {
            return !MedicalCaseRewriteService.HandleDepartmentDiagnosticSweepBeforeTreatment(__instance);
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "Update")]
    internal static class MedicalCasePatientUpdatePatch
    {
        private static void Prefix(BehaviorPatient __instance)
        {
            MedicalCaseRewriteService.UpdateCaseCollapse(__instance);
            MedicalCaseRewriteService.ProcessPendingDiagnosticFocus(__instance);
            MedicalCaseRewriteService.RecoverBlockedCaseState(__instance);
            MedicalCaseRewriteService.RecoverStalledDoctorHandoff(__instance);
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "TryToCollapse")]
    internal static class MedicalCaseTryToCollapsePatch
    {
        private static void Postfix(BehaviorPatient __instance, bool __result)
        {
            if (__result)
            {
                MedicalCaseRewriteService.PostponeTriggeredCaseCollapse(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "UncoverSymptomsFromLastExamination")]
    internal static class MedicalCaseUncoverLastExaminationPatch
    {
        private static void Postfix(BehaviorPatient __instance, ProcedureScript procedureScript)
        {
            MedicalCaseRewriteService.RevealSymptomsFromLastExamination(__instance, procedureScript);
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "UncoverSymptomsFromLabExamination")]
    internal static class MedicalCaseUncoverLabExaminationPatch
    {
        private static void Postfix(BehaviorPatient __instance, GameDBExamination labExamination)
        {
            MedicalCaseRewriteService.RevealSymptomsFromExamination(__instance, labExamination);
        }
    }

    [HarmonyPatch(typeof(ProcedureComponent), "PlanAllTreatments")]
    internal static class MedicalCasePlanAllTreatmentsPatch
    {
        private static void Postfix(ProcedureComponent __instance, bool onlyCritical, ref TreatmentPlanningResult __result)
        {
            MedicalCaseRewriteService.PlanSecondaryTreatments(__instance, onlyCritical, ref __result);
        }
    }

    [HarmonyPatch(typeof(ProcedureComponent), "UpdateAllExaminationsForMedicalCondition")]
    internal static class MedicalCaseUpdateExaminationAvailabilityPatch
    {
        private static void Postfix(ProcedureComponent __instance, ref FakeMap<GameDBExamination, ProcedureSceneAvailability> __result)
        {
            MedicalCaseRewriteService.ApplyExaminationAvailabilityOverlay(__instance, __result);
        }
    }

    [HarmonyPatch(typeof(ProcedureComponent), "GetAllExaminationsForMedicalCondition")]
    internal static class MedicalCaseGetAllExaminationsPatch
    {
        private static void Postfix(ProcedureComponent __instance, ref List<GameDBExamination> __result)
        {
            MedicalCaseRewriteService.ApplyExaminationListOverlay(__instance, __result);
        }
    }

    [HarmonyPatch(typeof(ProcedureComponent), "GetAllTreatmentsForMedicalCondition")]
    internal static class MedicalCaseTreatmentAvailabilityPatch
    {
        private static void Postfix(ProcedureComponent __instance, TreatmentPlanningMode treatmentPlanningMode, ref FakeMap<GameDBTreatment, ProcedureSceneAvailability> __result)
        {
            MedicalCaseRewriteService.AddSecondaryTreatmentAvailability(__instance, __result, treatmentPlanningMode);
            MedicalCaseRewriteService.ApplyDepartmentDiagnosticSweepTreatmentGate(__instance, treatmentPlanningMode, __result);
        }
    }

    [HarmonyPatch(typeof(ProcedureComponent), "SuppressSymptoms")]
    internal static class MedicalCaseSuppressSymptomsPatch
    {
        private static void Postfix(ProcedureComponent __instance, GameDBTreatment gameDBTreatment)
        {
            MedicalCaseRewriteService.MarkTreatmentApplied(__instance, gameDBTreatment);
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "SendHome")]
    internal static class MedicalCaseSendHomePatch
    {
        private static bool Prefix(BehaviorPatient __instance)
        {
            return MedicalCaseRewriteService.TryAdvanceBeforeDischarge(__instance);
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "Leave")]
    internal static class MedicalCaseLeavePatch
    {
        private static bool Prefix(BehaviorPatient __instance, bool pay, bool leaveAfterHours)
        {
            if (!MedicalCaseRewriteService.ShouldAllowLeave(__instance, pay, leaveAfterHours))
            {
                return false;
            }

            MedicalCaseRewriteService.MarkLeaving(__instance);
            return true;
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "TriggerDeath")]
    internal static class MedicalCaseTriggerDeathPatch
    {
        private static void Postfix(BehaviorPatient __instance)
        {
            MedicalCaseRewriteService.MarkDead(__instance);
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "GetInsurancePayment")]
    [HarmonyPriority(Priority.First)]
    internal static class MedicalCaseInsurancePaymentPatch
    {
        private static void Postfix(BehaviorPatient __instance, bool checkDiagnosis, ref int __result)
        {
            if (!MedicalCaseRewriteService.Enabled || !MedicalCaseRewriteService.HasCaseRecord(__instance))
            {
                return;
            }

            if (MedicalCaseRewriteService.HasOpenCase(__instance))
            {
                if (!checkDiagnosis)
                {
                    __result = MedicalCaseRewriteService.GetVisibleCaseInsurancePayment(__instance, 100);
                }

                return;
            }

            var payment = MedicalCaseRewriteService.GetCaseInsurancePayment(__instance, 100);
            if (payment > 0)
            {
                __result = payment;
            }
        }
    }

    [HarmonyPatch(typeof(CharacterPanelPatientPanelController), "FillPatientData")]
    internal static class MedicalCasePatientPanelPatch
    {
        private static void Postfix(CharacterPanelPatientPanelController __instance, BehaviorPatient patient)
        {
            MedicalCaseRewriteService.SelectPatient(patient);
            MedicalCaseRewriteService.BindPatientPanelButton(__instance, patient);
            MedicalCaseRewriteService.ApplyAggregatedSymptomsPanel(__instance, patient);
            MedicalCaseRewriteService.ApplyCaseDiagnosisPanel(__instance, patient);
            TraceLoggingService.LogUiSnapshot(patient, "FillPatientData");
        }
    }

    internal sealed class QueueTracePatchState
    {
        public BehaviorPatient Patient;
        public TraceLoggingService.CaseTraceQueueState QueueBefore;
        public string QueueItemId;
        public GLib.Entity DoctorBefore;
    }

    internal static class QueueTracePatchHelpers
    {
        public static QueueTracePatchState CaptureForPatient(BehaviorPatient patient, string queueItemId)
        {
            return new QueueTracePatchState
            {
                Patient = patient,
                QueueBefore = TraceLoggingService.CaptureQueueState(patient),
                QueueItemId = queueItemId,
                DoctorBefore = ResolveDoctorEntity(patient)
            };
        }

        public static QueueTracePatchState CaptureForProcedure(ProcedureComponent procedureComponent)
        {
            return CaptureForPatient(ResolvePatient(procedureComponent), null);
        }

        public static BehaviorPatient ResolvePatient(ProcedureComponent procedureComponent)
        {
            var entity = procedureComponent == null ? null : ReflectionHelpers.GetField(procedureComponent, "m_entity") as GLib.Entity;
            return entity == null ? null : entity.GetComponent<BehaviorPatient>();
        }

        public static GLib.Entity ResolveDoctorEntity(BehaviorPatient patient)
        {
            return patient == null || patient.m_state == null || patient.m_state.m_doctor == null || !patient.m_state.m_doctor.CheckEntity()
                ? null
                : patient.m_state.m_doctor.GetEntity();
        }

        public static string GetPlannedExaminationIdAt(BehaviorPatient patient, int index)
        {
            var queueState = TraceLoggingService.CaptureQueueState(patient);
            return queueState == null || index < 0 || index >= queueState.PlannedExaminationIds.Count
                ? null
                : queueState.PlannedExaminationIds[index];
        }

        public static string GetPlannedTreatmentIdAt(BehaviorPatient patient, int index)
        {
            var queueState = TraceLoggingService.CaptureQueueState(patient);
            return queueState == null || index < 0 || index >= queueState.PlannedTreatmentIds.Count
                ? null
                : queueState.PlannedTreatmentIds[index];
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "ScheduleExamination", new[] { typeof(GameDBExamination) })]
    internal static class MedicalCaseScheduleExaminationTracePatch
    {
        private static void Prefix(BehaviorPatient __instance, GameDBExamination examination, ref QueueTracePatchState __state)
        {
            __state = QueueTracePatchHelpers.CaptureForPatient(__instance, examination == null ? null : examination.DatabaseID.ToString());
        }

        private static void Postfix(BehaviorPatient __instance, GameDBExamination examination, QueueTracePatchState __state)
        {
            var after = TraceLoggingService.CaptureQueueState(__instance);
            TraceLoggingService.LogQueueDiff(__instance, __state == null ? null : __state.QueueBefore, after, "BehaviorPatient.ScheduleExamination", "schedule_examination");
            var queueItemId = examination == null ? "-" : examination.DatabaseID.ToString();
            var added = after != null && after.PlannedExaminationIds.Contains(queueItemId);
            if (!added)
            {
                TraceLoggingService.LogQueueEvent(__instance, "queue_exam_add", __state == null ? null : __state.DoctorBefore, queueItemId, "BehaviorPatient.ScheduleExamination", "schedule_examination", "no_change");
            }
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "CancelPlannedExamination", new[] { typeof(GameDBExamination) })]
    internal static class MedicalCaseCancelPlannedExaminationByTypeTracePatch
    {
        private static void Prefix(BehaviorPatient __instance, GameDBExamination examination, ref QueueTracePatchState __state)
        {
            __state = QueueTracePatchHelpers.CaptureForPatient(__instance, examination == null ? null : examination.DatabaseID.ToString());
        }

        private static void Postfix(BehaviorPatient __instance, GameDBExamination examination, QueueTracePatchState __state)
        {
            var after = TraceLoggingService.CaptureQueueState(__instance);
            TraceLoggingService.LogQueueDiff(__instance, __state == null ? null : __state.QueueBefore, after, "BehaviorPatient.CancelPlannedExamination(GameDBExamination)", "cancel_planned_examination");
            var queueItemId = examination == null ? "-" : examination.DatabaseID.ToString();
            var existedBefore = __state != null && __state.QueueBefore != null && __state.QueueBefore.PlannedExaminationIds.Contains(queueItemId);
            var stillPresent = after != null && after.PlannedExaminationIds.Contains(queueItemId);
            if (!existedBefore || stillPresent)
            {
                TraceLoggingService.LogQueueEvent(__instance, "queue_exam_remove", __state == null ? null : __state.DoctorBefore, queueItemId, "BehaviorPatient.CancelPlannedExamination(GameDBExamination)", "cancel_planned_examination", !existedBefore ? "missing" : "failed");
            }
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "CancelPlannedExamination", new[] { typeof(int) })]
    internal static class MedicalCaseCancelPlannedExaminationByIndexTracePatch
    {
        private static void Prefix(BehaviorPatient __instance, int index, ref QueueTracePatchState __state)
        {
            __state = QueueTracePatchHelpers.CaptureForPatient(__instance, QueueTracePatchHelpers.GetPlannedExaminationIdAt(__instance, index));
        }

        private static void Postfix(BehaviorPatient __instance, int index, QueueTracePatchState __state)
        {
            var after = TraceLoggingService.CaptureQueueState(__instance);
            TraceLoggingService.LogQueueDiff(__instance, __state == null ? null : __state.QueueBefore, after, "BehaviorPatient.CancelPlannedExamination(Int32)", "cancel_planned_examination");
            var queueItemId = __state == null || string.IsNullOrEmpty(__state.QueueItemId) ? "-" : __state.QueueItemId;
            var existedBefore = __state != null && __state.QueueBefore != null && __state.QueueBefore.PlannedExaminationIds.Contains(queueItemId);
            var stillPresent = after != null && after.PlannedExaminationIds.Contains(queueItemId);
            if (!existedBefore || stillPresent)
            {
                TraceLoggingService.LogQueueEvent(__instance, "queue_exam_remove", __state == null ? null : __state.DoctorBefore, queueItemId, "BehaviorPatient.CancelPlannedExamination(Int32)", "cancel_planned_examination_index", !existedBefore ? "missing" : "failed");
            }
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "CancelPlannedTreatment", new[] { typeof(int) })]
    internal static class MedicalCaseCancelPlannedTreatmentTracePatch
    {
        private static void Prefix(BehaviorPatient __instance, int index, ref QueueTracePatchState __state)
        {
            __state = QueueTracePatchHelpers.CaptureForPatient(__instance, QueueTracePatchHelpers.GetPlannedTreatmentIdAt(__instance, index));
        }

        private static void Postfix(BehaviorPatient __instance, int index, QueueTracePatchState __state)
        {
            var after = TraceLoggingService.CaptureQueueState(__instance);
            TraceLoggingService.LogQueueDiff(__instance, __state == null ? null : __state.QueueBefore, after, "BehaviorPatient.CancelPlannedTreatment", "cancel_planned_treatment");
            var queueItemId = __state == null || string.IsNullOrEmpty(__state.QueueItemId) ? "-" : __state.QueueItemId;
            var existedBefore = __state != null && __state.QueueBefore != null && __state.QueueBefore.PlannedTreatmentIds.Contains(queueItemId);
            var stillPresent = after != null && after.PlannedTreatmentIds.Contains(queueItemId);
            if (!existedBefore || stillPresent)
            {
                TraceLoggingService.LogQueueEvent(__instance, "queue_treatment_remove", __state == null ? null : __state.DoctorBefore, queueItemId, "BehaviorPatient.CancelPlannedTreatment", "cancel_planned_treatment", !existedBefore ? "missing" : "failed");
            }
        }
    }

    [HarmonyPatch(typeof(ProcedureComponent), "ClearPlannedProcedures")]
    internal static class MedicalCaseClearPlannedProceduresTracePatch
    {
        private static void Prefix(ProcedureComponent __instance, ref QueueTracePatchState __state)
        {
            __state = QueueTracePatchHelpers.CaptureForProcedure(__instance);
        }

        private static void Postfix(ProcedureComponent __instance, QueueTracePatchState __state)
        {
            var patient = __state == null ? QueueTracePatchHelpers.ResolvePatient(__instance) : __state.Patient;
            TraceLoggingService.LogQueueDiff(patient, __state == null ? null : __state.QueueBefore, TraceLoggingService.CaptureQueueState(patient), "ProcedureComponent.ClearPlannedProcedures", "clear_planned_procedures");
        }
    }

    [HarmonyPatch(typeof(ProcedureComponent), "CheckLabProcedures")]
    internal static class MedicalCaseCheckLabProceduresTracePatch
    {
        private static void Prefix(ProcedureComponent __instance, ref QueueTracePatchState __state)
        {
            __state = QueueTracePatchHelpers.CaptureForProcedure(__instance);
        }

        private static void Postfix(ProcedureComponent __instance, QueueTracePatchState __state)
        {
            var patient = __state == null ? QueueTracePatchHelpers.ResolvePatient(__instance) : __state.Patient;
            TraceLoggingService.LogQueueDiff(patient, __state == null ? null : __state.QueueBefore, TraceLoggingService.CaptureQueueState(patient), "ProcedureComponent.CheckLabProcedures", "check_lab_procedures");
        }
    }

    [HarmonyPatch(typeof(ProcedureComponent), "FinishLabProceduresWithResultsReady")]
    internal static class MedicalCaseFinishLabProceduresTracePatch
    {
        private static void Prefix(ProcedureComponent __instance, ref QueueTracePatchState __state)
        {
            __state = QueueTracePatchHelpers.CaptureForProcedure(__instance);
        }

        private static void Postfix(ProcedureComponent __instance, QueueTracePatchState __state)
        {
            var patient = __state == null ? QueueTracePatchHelpers.ResolvePatient(__instance) : __state.Patient;
            TraceLoggingService.LogQueueDiff(patient, __state == null ? null : __state.QueueBefore, TraceLoggingService.CaptureQueueState(patient), "ProcedureComponent.FinishLabProceduresWithResultsReady", "finish_lab_procedures_with_results_ready");
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "SetDoctor", new[] { typeof(GLib.Entity) })]
    internal static class MedicalCaseSetDoctorTracePatch
    {
        private static void Prefix(BehaviorPatient __instance, ref QueueTracePatchState __state)
        {
            __state = QueueTracePatchHelpers.CaptureForPatient(__instance, null);
        }

        private static void Postfix(BehaviorPatient __instance, GLib.Entity doctor, QueueTracePatchState __state)
        {
            TraceLoggingService.LogQueueEvent(
                __instance,
                "doctor_assign",
                doctor,
                doctor == null ? "-" : doctor.GetEntityID().ToString(CultureInfo.InvariantCulture),
                "BehaviorPatient.SetDoctor",
                "doctor_assignment",
                doctor == null ? "failed" : "assigned");
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "FreeDoctor")]
    internal static class MedicalCaseFreeDoctorTracePatch
    {
        private static void Prefix(BehaviorPatient __instance, ref QueueTracePatchState __state)
        {
            __state = QueueTracePatchHelpers.CaptureForPatient(__instance, null);
        }

        private static void Postfix(BehaviorPatient __instance, QueueTracePatchState __state)
        {
            var doctor = __state == null ? null : __state.DoctorBefore;
            TraceLoggingService.LogQueueEvent(
                __instance,
                "doctor_unassign",
                doctor,
                doctor == null ? "-" : doctor.GetEntityID().ToString(CultureInfo.InvariantCulture),
                "BehaviorPatient.FreeDoctor",
                "doctor_unassignment",
                doctor == null ? "missing" : "removed");
        }
    }

    [HarmonyPatch(typeof(NotificationManager), "AddMessage", new[] { typeof(GLib.Entity), typeof(string), typeof(string), typeof(string), typeof(string), typeof(int), typeof(int), typeof(int), typeof(int), typeof(string), typeof(string) })]
    internal static class MedicalCaseNotificationPatch
    {
        private static bool Prefix(GLib.Entity character, string titleLocID)
        {
            return !MedicalCaseRewriteService.ShouldSuppressNotification(character, titleLocID);
        }
    }

    [HarmonyPatch]
    internal static class MedicalCaseDiagnosisPanelControllerPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("DiagnosisPanelController");
            return type == null ? null : AccessTools.Method(type, "UpdateDiagnoses", new[] { typeof(GLib.Entity) });
        }

        private static bool Prefix(GLib.Entity patient)
        {
            return !MedicalCaseRewriteService.ShouldSkipVanillaDiagnosisPanel(patient);
        }

        private static Exception Finalizer(GLib.Entity patient, Exception __exception)
        {
            if (__exception != null && MedicalCaseRewriteService.ShouldSkipVanillaDiagnosisPanel(patient))
            {
                return null;
            }

            return __exception;
        }
    }

    [HarmonyPatch(typeof(CharacterPanelPatientPanelController), "IsPatientTreated")]
    internal static class MedicalCasePatientPanelTreatedPatch
    {
        private static void Postfix(BehaviorPatient patient, ref bool __result)
        {
            if (patient != null && !MedicalCaseRewriteService.IsCaseTreated(patient))
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(MedicalCondition), "HasBeenTreated", new[] { typeof(ProcedureQueue) })]
    internal static class MedicalCaseConditionHasBeenTreatedPatch
    {
        private static void Postfix(MedicalCondition __instance, ref bool __result)
        {
            if (__result && MedicalCaseRewriteService.ShouldForceCompatibilityConditionUntreated(__instance))
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(CharacterPanelPatientPanelController), "SendPatientToAnotherHospital")]
    internal static class MedicalCaseManualPanelTransferPatch
    {
        private static void Postfix(CharacterPanelPatientPanelController __instance)
        {
            MedicalCaseRewriteService.MarkManualPanelTransfer(__instance);
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "GetSpeedModifier")]
    internal static class MedicalCasePatientSpeedPatch
    {
        private static void Postfix(BehaviorPatient __instance, ref float __result)
        {
            var effects = MedicalCaseRewriteService.GetEffects(__instance);
            if (effects == null)
            {
                return;
            }

            if (effects.WalkSpeedModifier > 0f)
            {
                __result = __result <= 0f ? effects.WalkSpeedModifier : Math.Min(__result, effects.WalkSpeedModifier);
            }
            else if (effects.Hazard > SymptomHazard.Medium)
            {
                __result = __result <= 0f ? 0.7f : Math.Min(__result, 0.7f);
            }
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "GetSpecificAnimation")]
    internal static class MedicalCasePatientAnimationPatch
    {
        private static void Postfix(BehaviorPatient __instance, string animationID, ref string __result)
        {
            var effects = MedicalCaseRewriteService.GetEffects(__instance);
            if (effects == null || animationID != "walk")
            {
                return;
            }

            if (!string.IsNullOrEmpty(effects.WalkAnimSuffix) && (__result == animationID || __result == animationID + "_sick"))
            {
                __result = animationID + effects.WalkAnimSuffix;
            }
            else if (string.IsNullOrEmpty(effects.WalkAnimSuffix) && effects.Hazard > SymptomHazard.Medium && __result == animationID)
            {
                __result = animationID + "_sick";
            }
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "GetWorstKnownHazard")]
    internal static class MedicalCasePatientWorstKnownHazardPatch
    {
        private static void Postfix(BehaviorPatient __instance, ref SymptomHazard __result)
        {
            var effects = MedicalCaseRewriteService.GetEffects(__instance);
            if (effects != null)
            {
                __result = (SymptomHazard)Math.Max((int)__result, (int)effects.Hazard);
            }
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "GetWorstKnownHazardIgnoreCodeBlue")]
    internal static class MedicalCasePatientWorstKnownHazardIgnoreCodeBluePatch
    {
        private static void Postfix(BehaviorPatient __instance, ref SymptomHazard __result)
        {
            var effects = MedicalCaseRewriteService.GetEffects(__instance);
            if (effects != null)
            {
                __result = (SymptomHazard)Math.Max((int)__result, (int)effects.Hazard);
            }
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "GetBleedingLevel")]
    internal static class MedicalCasePatientBleedingPatch
    {
        private static void Postfix(BehaviorPatient __instance, ref int __result)
        {
            var effects = MedicalCaseRewriteService.GetEffects(__instance);
            if (effects != null)
            {
                __result = Math.Max(__result, effects.BleedingLevel);
            }
        }
    }

    [HarmonyPatch(typeof(MedicalCondition), "GetWorstHazard")]
    internal static class MedicalCaseConditionWorstHazardPatch
    {
        private static void Postfix(MedicalCondition __instance, ref SymptomHazard __result)
        {
            var effects = MedicalCaseRewriteService.GetEffects(__instance);
            if (effects != null)
            {
                __result = (SymptomHazard)Math.Max((int)__result, (int)effects.Hazard);
            }
        }
    }

    [HarmonyPatch(typeof(MedicalCondition), "GetWorstKnownHazard")]
    internal static class MedicalCaseConditionWorstKnownHazardPatch
    {
        private static void Postfix(MedicalCondition __instance, ref SymptomHazard __result)
        {
            var effects = MedicalCaseRewriteService.GetEffects(__instance);
            if (effects != null)
            {
                __result = (SymptomHazard)Math.Max((int)__result, (int)effects.Hazard);
            }
        }
    }

    [HarmonyPatch(typeof(MedicalCondition), "GetWorstPossibleHazard")]
    internal static class MedicalCaseConditionWorstPossibleHazardPatch
    {
        private static void Postfix(MedicalCondition __instance, ref SymptomHazard __result)
        {
            var effects = MedicalCaseRewriteService.GetEffects(__instance);
            if (effects != null)
            {
                __result = (SymptomHazard)Math.Max((int)__result, (int)effects.Hazard);
            }
        }
    }

    [HarmonyPatch(typeof(MedicalCondition), "HasImmobileSymptom")]
    internal static class MedicalCaseConditionImmobilePatch
    {
        private static void Postfix(MedicalCondition __instance, ref bool __result)
        {
            var effects = MedicalCaseRewriteService.GetEffects(__instance);
            __result = __result || (effects != null && effects.Immobile);
        }
    }

    [HarmonyPatch(typeof(MedicalCondition), "HasCanNotTalkSymptom")]
    internal static class MedicalCaseConditionCanNotTalkPatch
    {
        private static void Postfix(MedicalCondition __instance, ref bool __result)
        {
            var effects = MedicalCaseRewriteService.GetEffects(__instance);
            __result = __result || (effects != null && effects.CanNotTalk);
        }
    }

    [HarmonyPatch(typeof(MedicalCondition), "AnySymptomNeedsHospitalization")]
    internal static class MedicalCaseConditionHospitalizationNeedPatch
    {
        private static void Postfix(MedicalCondition __instance, ref bool __result)
        {
            var effects = MedicalCaseRewriteService.GetEffects(__instance);
            __result = __result || (effects != null && effects.NeedsHospitalization);
        }
    }

    [HarmonyPatch(typeof(MedicalCondition), "HasActiveHazardSymptom")]
    internal static class MedicalCaseConditionActiveHazardPatch
    {
        private static void Postfix(MedicalCondition __instance, ref bool __result)
        {
            __result = __result || MedicalCaseRewriteService.HasDueCaseCollapse(__instance);
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "BelongsToDepartment")]
    internal static class MedicalCaseBelongsToDepartmentPatch
    {
        private static void Postfix(BehaviorPatient __instance, ref GameDBDepartment __result)
        {
            var projected = MedicalCaseRewriteService.GetProjectedDepartment(__instance);
            if (projected != null)
            {
                __result = projected;
            }
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "DepartmentIsUnclear")]
    internal static class MedicalCaseDepartmentIsUnclearPatch
    {
        private static void Postfix(BehaviorPatient __instance, ref bool __result)
        {
            __result = MedicalCaseRewriteService.ShouldDepartmentBeUnclear(__instance, __result);
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "ChangeDepartment", new[] { typeof(Department), typeof(bool), typeof(bool), typeof(HospitalizationLevel) })]
    internal static class MedicalCaseChangeDepartmentCommitPatch
    {
        private static void Postfix(BehaviorPatient __instance)
        {
            MedicalCaseRewriteService.OnDepartmentChangeCommitted(__instance);
        }
    }

    [HarmonyPatch(typeof(HospitalizationComponent), "IsHospitalizationOver")]
    internal static class MedicalCaseIsHospitalizationOverPatch
    {
        private static void Postfix(HospitalizationComponent __instance, ref bool __result)
        {
            __result = MedicalCaseRewriteService.ShouldHospitalizationBeOver(__instance, __result);
        }
    }

    [HarmonyPatch(typeof(HospitalizationComponent), "ReleaseFromObservation")]
    internal static class MedicalCaseReleaseFromObservationPatch
    {
        private static bool Prefix(HospitalizationComponent __instance, ref bool __result)
        {
            return MedicalCaseRewriteService.TryHandleReleaseFromObservation(__instance, ref __result);
        }
    }

    [HarmonyPatch(typeof(ProcedureScriptExaminationDoctorsInterview), "UpdateStatePatientTalking")]
    internal static class MedicalCaseInterviewEmitterPatch
    {
        private static void Postfix(ProcedureScriptExaminationDoctorsInterview __instance)
        {
            var patient = __instance == null || __instance.m_stateData == null || __instance.m_stateData.m_procedureScene == null || __instance.m_stateData.m_procedureScene.m_patient == null
                ? null
                : __instance.m_stateData.m_procedureScene.m_patient.GetEntity().GetComponent<BehaviorPatient>();
            if (patient != null)
            {
                MedicalCaseRewriteService.RevealSymptomsFromLastExamination(patient, __instance);
            }
        }
    }

    [HarmonyPatch(typeof(ProcedureScriptExaminationReceptionFast), "UpdateStatePatientTalking")]
    internal static class MedicalCaseReceptionFastEmitterPatch
    {
        private static void Postfix(ProcedureScriptExaminationReceptionFast __instance)
        {
            var patient = __instance == null || __instance.m_stateData == null || __instance.m_stateData.m_procedureScene == null || __instance.m_stateData.m_procedureScene.m_patient == null
                ? null
                : __instance.m_stateData.m_procedureScene.m_patient.GetEntity().GetComponent<BehaviorPatient>();
            if (patient != null)
            {
                MedicalCaseRewriteService.RevealSymptomsFromLastExamination(patient, __instance);
            }
        }
    }

    [HarmonyPatch(typeof(MedicalCondition), "Update")]
    internal static class MedicalCaseMonitoringEmitterPatch
    {
        private static void Postfix(MedicalCondition __instance, float deltaTime, GLib.Entity entity, MedicalConditionChange __result)
        {
            if (__result != MedicalConditionChange.SymptomActivated || entity == null)
            {
                return;
            }

            var patient = entity.GetComponent<BehaviorPatient>();
            if (patient == null || !MedicalCaseRewriteService.IsRewriteOwned(patient))
            {
                return;
            }

            MedicalCaseRewriteService.SyncKnownVanillaSymptoms(patient);
        }
    }

}
