using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using GLib;
using HarmonyLib;
using Lopital;
using UnityEngine;

namespace ProjectHospital.AutoLabBalancer
{
    internal static class CustomPerkService
    {
        public const string StaffDiagnosticAcuity = "ALB_PERK_DIAGNOSTIC_ACUITY";
        public const string StaffEmergencyFocus = "ALB_PERK_EMERGENCY_FOCUS";
        public const string StaffSurgicalHand = "ALB_PERK_SURGICAL_HAND";
        public const string StaffFastRounds = "ALB_PERK_FAST_ROUNDS";
        public const string StaffOrganizer = "ALB_PERK_ORGANIZER";
        public const string StaffLeanDiagnostics = "ALB_PERK_LEAN_DIAGNOSTICS";
        public const string StaffTransportAssistant = "ALB_PERK_TRANSPORT_ASSISTANT";
        public const string StaffSanitaryPerfectionist = "ALB_PERK_SANITARY_PERFECTIONIST";
        public const string StaffStressProof = "ALB_PERK_STRESS_PROOF";
        public const string StaffCheapSpecialist = "ALB_PERK_CHEAP_SPECIALIST";
        public const string PatientEducated = "ALB_PERK_EDUCATED_PATIENT";
        public const string PatientPunctual = "ALB_PERK_PUNCTUAL_PATIENT";
        public const string PatientPatient = "ALB_PERK_PATIENT_PATIENT";
        public const string PatientGenerous = "ALB_PERK_GENEROUS_PATIENT";
        public const string PatientStable = "ALB_PERK_STABLE_CONDITION";
        public const string PatientSecretive = "ALB_PERK_SECRETIVE_PATIENT";
        public const string PatientUntranslatable = "ALB_PERK_UNTRANSLATABLE";
        public const string PatientProcedureSaboteur = "ALB_PERK_PROCEDURE_SABOTEUR";
        public const string PatientFreeloader = "ALB_PERK_FREELOADER";
        public const string PatientHiddenRisk = "ALB_PERK_HIDDEN_RISK";
        public const string PatientPolluter = "ALB_PERK_POLLUTER";
        public const string PatientVerySlow = "ALB_PERK_VERY_SLOW_PATIENT";
        public const string PatientEscortRequired = "ALB_PERK_ESCORT_REQUIRED";
        public const string PatientPanicker = "ALB_PERK_PANICKER";
        public const string PatientQueueBreaker = "ALB_PERK_QUEUE_BREAKER";
        public const string StaffBurnout = "ALB_PERK_BURNOUT";
        public const string StaffExpensive = "ALB_PERK_EXPENSIVE_SPECIALIST";
        public const string StaffDiagnosticErrors = "ALB_PERK_DIAGNOSTIC_ERRORS";
        public const string StaffSlowCleaning = "ALB_PERK_SLOW_CLEANING";
        public const string StaffChaotic = "ALB_PERK_CHAOTIC";

        private static readonly CustomPerkDefinition[] Definitions =
        {
            new CustomPerkDefinition(StaffDiagnosticAcuity, "Точный диагност", "После интервью или обследования иногда раскрывает дополнительный скрытый симптом.", true, false, PerkType.POSITIVE, 342, "diag", "doctor"),
            new CustomPerkDefinition(StaffEmergencyFocus, "Экстренный приоритет", "В критических случаях откладывает личные потребности и быстрее работает с пациентом.", true, false, PerkType.POSITIVE, 346, "emergency", "doctor,nurse,lab"),
            new CustomPerkDefinition(StaffSurgicalHand, "Хирургическая рука", "Снижает шанс осложнений после операций.", true, false, PerkType.POSITIVE, 343, "surgery", "surgery"),
            new CustomPerkDefinition(StaffFastRounds, "Быстрый обход", "Быстрее выполняет процедуры и обходы у госпитализированных пациентов.", true, false, PerkType.POSITIVE, 349, "rounds", "doctor,nurse"),
            new CustomPerkDefinition(StaffOrganizer, "Организатор", "Чуть быстрее выполняет задачи отдела и реже простаивает.", true, false, PerkType.POSITIVE, 326, "workstyle", "employee"),
            new CustomPerkDefinition(StaffLeanDiagnostics, "Без лишних анализов", "Сильнее склоняется к постановке диагноза, когда картина почти ясна.", true, false, PerkType.POSITIVE, 344, "diagstyle", "doctor"),
            new CustomPerkDefinition(StaffTransportAssistant, "Транспортный ассистент", "Быстрее движется при перевозке пациентов.", true, false, PerkType.POSITIVE, 349, "transport", "nurse"),
            new CustomPerkDefinition(StaffSanitaryPerfectionist, "Санитарный перфекционист", "Иногда очищает соседние грязные клетки после уборки.", true, false, PerkType.POSITIVE, 345, "clean", "janitor"),
            new CustomPerkDefinition(StaffStressProof, "Стрессоустойчивый", "Игнорирует часть негативного настроения от сложных пациентов.", true, false, PerkType.POSITIVE, 329, "stress", "doctor,nurse,lab"),
            new CustomPerkDefinition(StaffCheapSpecialist, "Дешевый специалист", "Получает меньшую зарплату без потери эффективности.", true, false, PerkType.POSITIVE, 518, "salary", "employee"),
            new CustomPerkDefinition(PatientEducated, "Образованный пациент", "Помогает врачу раскрыть наиболее опасный скрытый симптом.", false, true, PerkType.POSITIVE, 321, "patient-info"),
            new CustomPerkDefinition(PatientPunctual, "Пунктуальный пациент", "Быстрее перемещается по маршруту лечения.", false, true, PerkType.POSITIVE, 349, "patient-speed"),
            new CustomPerkDefinition(PatientPatient, "Терпеливый пациент", "Дольше ждет перед уходом из больницы.", false, true, PerkType.POSITIVE, 329, "patience"),
            new CustomPerkDefinition(PatientGenerous, "Щедрый пациент", "Приносит больше страховой выплаты.", false, true, PerkType.POSITIVE, 518, "payment"),
            new CustomPerkDefinition(PatientStable, "Стабильное состояние", "Коллапс наступает позже.", false, true, PerkType.POSITIVE, 344, "collapse"),
            new CustomPerkDefinition(PatientSecretive, "Скрытный пациент", "Сильнее скрывает деликатные симптомы.", false, true, PerkType.NEGATIVE, 338, "patient-info"),
            new CustomPerkDefinition(PatientUntranslatable, "Непереводимый", "Интервью занимает больше времени.", false, true, PerkType.NEGATIVE, 325, "language"),
            new CustomPerkDefinition(PatientProcedureSaboteur, "Саботажник процедур", "Чаще задерживает обследования и лечение.", false, true, PerkType.NEGATIVE, 337, "cooperation"),
            new CustomPerkDefinition(PatientFreeloader, "Неплательщик", "Заметно снижает выплату за лечение.", false, true, PerkType.NEGATIVE, 521, "payment"),
            new CustomPerkDefinition(PatientHiddenRisk, "Неотложный скрытый риск", "Скрытые опасные симптомы быстрее приводят к коллапсу.", false, true, PerkType.NEGATIVE, 340, "collapse"),
            new CustomPerkDefinition(PatientPolluter, "Загрязнитель", "Оставляет намного больше грязи.", false, true, PerkType.NEGATIVE, 335, "feet"),
            new CustomPerkDefinition(PatientVerySlow, "Тяжелый на подъем", "Передвигается заметно медленнее.", false, true, PerkType.NEGATIVE, 341, "patient-speed"),
            new CustomPerkDefinition(PatientEscortRequired, "Требует сопровождения", "Чаще нуждается в транспортировке и сильнее загружает медсестер.", false, true, PerkType.NEGATIVE, 348, "transport"),
            new CustomPerkDefinition(PatientPanicker, "Паникер", "При ожидании портит настроение себе и персоналу.", false, true, PerkType.NEGATIVE, 517, "mood"),
            new CustomPerkDefinition(PatientQueueBreaker, "Нарушает очередь", "Иногда теряет место в очереди и создает лишнюю работу.", false, true, PerkType.NEGATIVE, 337, "queue"),
            new CustomPerkDefinition(StaffBurnout, "Выгорающий", "При сильной усталости работает медленнее.", true, false, PerkType.NEGATIVE, 517, "workstyle", "employee"),
            new CustomPerkDefinition(StaffExpensive, "Дорогой специалист", "Требует повышенную зарплату.", true, false, PerkType.NEGATIVE, 333, "salary", "employee"),
            new CustomPerkDefinition(StaffDiagnosticErrors, "Ошибающийся диагност", "Работает как врач с более слабой диагностикой.", true, false, PerkType.NEGATIVE, 328, "diag", "doctor"),
            new CustomPerkDefinition(StaffSlowCleaning, "Медленная чистка", "Уборка занимает больше времени.", true, false, PerkType.NEGATIVE, 335, "clean", "janitor"),
            new CustomPerkDefinition(StaffChaotic, "Хаотичный сотрудник", "Иногда теряет темп и выполняет задачи медленнее.", true, false, PerkType.NEGATIVE, 340, "emergency", "doctor,nurse,lab")
        };

        private static readonly Dictionary<string, CustomPerkDefinition> DefinitionById = CreateDefinitionMap();
        private static readonly Dictionary<string, string> RussianStrings = CreateRussianStrings();
        private static readonly System.Random Rng = new System.Random();
        private static ProcedureComponent sm_currentProcedureComponent;

        public static void EnsureDatabaseEntries(Database database)
        {
            if (database == null)
            {
                return;
            }

            foreach (var definition in Definitions)
            {
                if (database.GetEntry<GameDBPerk>(definition.Id) != null)
                {
                    continue;
                }

                var perk = new GameDBPerk();
                perk.DatabaseID = ID.CreateID(definition.Id);
                SetAutoProperty(perk, "AbbreviationLocID", definition.Id + "_DESCRIPTION");
                SetAutoProperty(perk, "HiddenByDefault", false);
                SetAutoProperty(perk, "IconIndex", definition.IconIndex);
                SetAutoProperty(perk, "PerkType", definition.Type);
                SetAutoProperty(perk, "NotRemovable", false);
                SetAutoProperty(perk, "LosingPerkLocID", definition.Id + "_LOST_DESCRIPTION");
                InvokeAddEntry(database, perk);
                perk.PostLoad(database);
            }
        }

        public static bool TryGetLocalizedText(string stringId, out string text)
        {
            return RussianStrings.TryGetValue(stringId, out text);
        }

        public static void FillPerks(object perkSet, object character)
        {
            if (!IsCustomPerksEnabled() || perkSet == null || character == null)
            {
                return;
            }

            var perks = GetPerkList(perkSet);
            if (perks == null)
            {
                return;
            }

            RemoveInapplicableCustomPerks(perks, character);

            var max = Math.Max(0, RuntimeSettings.Config.MaxPerksPerCharacter.Value);
            if (perks.Count >= max)
            {
                RemoveConflictingExtras(perks);
                return;
            }

            var isEmployee = HasComponent(character, "Lopital.EmployeeComponent");
            var isPatient = HasComponent(character, "Lopital.BehaviorPatient");
            if (!isEmployee && !isPatient)
            {
                return;
            }

            var candidates = new List<CustomPerkDefinition>();
            foreach (var definition in Definitions)
            {
                if (((isEmployee && definition.Employee) || (isPatient && definition.Patient)) && IsApplicableToCharacter(definition, character))
                {
                    candidates.Add(definition);
                }
            }

            Shuffle(candidates);
            foreach (var candidate in candidates)
            {
                if (perks.Count >= max)
                {
                    break;
                }

                if (HasPerk(perkSet, candidate.Id) || HasConflict(perkSet, candidate))
                {
                    continue;
                }

                var dbPerk = Database.Instance.GetEntry<GameDBPerk>(candidate.Id);
                if (dbPerk != null)
                {
                    perks.Add(new Perk(dbPerk));
                }
            }

            RemoveConflictingExtras(perks);
        }

        public static bool HasPerk(object characterOrComponent, string id)
        {
            var perkSet = GetPerkSet(characterOrComponent);
            return perkSet != null && InvokeHasPerk(perkSet, id);
        }

        public static void ProcedureUpdatePrefix(ProcedureComponent component)
        {
            sm_currentProcedureComponent = component;
        }

        public static void ProcedureUpdatePostfix()
        {
            sm_currentProcedureComponent = null;
        }

        public static void AdjustSpeed(object behavior, ref float result)
        {
            if (behavior == null)
            {
                return;
            }

            if (HasPerk(behavior, PatientPunctual))
            {
                result *= 1.15f;
            }

            if (HasPerk(behavior, PatientVerySlow))
            {
                result *= 0.65f;
            }

            if (HasPerk(behavior, PatientEscortRequired))
            {
                result *= 0.85f;
            }

            if (HasPerk(behavior, StaffTransportAssistant) && IsTransportState(behavior))
            {
                result *= 1.25f;
            }

            if (HasPerk(behavior, StaffEmergencyFocus) && ProductivityTweaksService.IsEmergencyContext(behavior))
            {
                result *= 1.15f;
            }
        }

        public static void AdjustFeetDirt(object behavior, ref float result)
        {
            if (HasPerk(behavior, PatientPolluter))
            {
                result *= 4f;
            }
        }

        public static void AdjustActionTime(ProcedureScript script, Entity character, ref float result)
        {
            if (character == null)
            {
                return;
            }

            var multiplier = 1f;
            if (HasPerk(character, StaffOrganizer))
            {
                multiplier *= 0.95f;
            }

            if (HasPerk(character, StaffFastRounds))
            {
                multiplier *= 0.90f;
            }

            if (HasPerk(character, StaffEmergencyFocus) && IsEmergencyProcedure(script))
            {
                multiplier *= 0.85f;
            }

            if (HasPerk(character, StaffChaotic) && UnityEngine.Random.Range(0, 100) < 8)
            {
                multiplier *= 1.35f;
            }

            var patient = GetProcedurePatient(script);
            if (HasPerk(patient, PatientProcedureSaboteur))
            {
                multiplier *= 1.25f;
            }

            if (HasPerk(patient, PatientEscortRequired))
            {
                multiplier *= 1.10f;
            }

            if (HasPerk(patient, PatientQueueBreaker))
            {
                multiplier *= 1.05f;
            }

            if (HasPerk(patient, PatientUntranslatable)
                && script != null
                && script.GetType().FullName.IndexOf("DoctorsInterview", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                multiplier *= 1.50f;
            }

            result *= multiplier;
        }

        public static void AdjustEfficiency(object employeeComponent, ref float result)
        {
            if (HasPerk(employeeComponent, StaffBurnout))
            {
                var entity = GetEntity(employeeComponent);
                var mood = entity == null ? null : entity.GetComponent<MoodComponent>();
                if (mood != null && mood.m_state.m_needs != null && mood.m_state.m_needs.Count > 2 && mood.m_state.m_needs[2].m_currentValue > 70f)
                {
                    result *= 1.25f;
                }
            }
        }

        public static void AdjustExperience(object character, ref int points)
        {
            if (HasPerk(character, StaffDiagnosticAcuity))
            {
                points = Math.Max(1, points * 115 / 100);
            }
        }

        public static void AdjustSalary(object employeeComponent)
        {
            var state = ReflectionHelpers.GetField(employeeComponent, "m_state");
            var salary = ReflectionHelpers.GetField(state, "m_salary");
            if (!(salary is int))
            {
                return;
            }

            var value = (int)salary;
            if (HasPerk(employeeComponent, StaffCheapSpecialist))
            {
                value = value * 90 / 100;
            }

            if (HasPerk(employeeComponent, StaffExpensive))
            {
                value = value * 125 / 100;
            }

            SetField(state, "m_salary", value);
        }

        public static void AdjustDoctorDiagnosticApproach(object doctor, ref float certainty)
        {
            if (HasPerk(doctor, StaffLeanDiagnostics))
            {
                certainty = Math.Max(35f, certainty - 10f);
            }

            if (HasPerk(doctor, StaffDiagnosticErrors))
            {
                certainty = Math.Min(100f, certainty + 15f);
            }
        }

        public static void AdjustShame(object patient, ref float result)
        {
            if (HasPerk(patient, PatientSecretive))
            {
                result *= 1.5f;
            }

            if (HasPerk(patient, PatientEducated))
            {
                result *= 0.75f;
            }
        }

        public static void AdjustInsurancePayment(object patient, ref int result)
        {
            if (HasPerk(patient, PatientGenerous))
            {
                result = result * 125 / 100;
            }

            if (HasPerk(patient, PatientFreeloader))
            {
                result = result * 50 / 100;
            }
        }

        public static void AdjustPatientPay(object patient, ref bool result)
        {
            if (HasPerk(patient, PatientFreeloader) && UnityEngine.Random.Range(0, 100) < 15)
            {
                result = false;
            }
        }

        public static void AdjustCollapseTimers(BehaviorPatient patient)
        {
            if (patient == null)
            {
                return;
            }

            var multiplier = 1f;
            if (HasPerk(patient, PatientStable))
            {
                multiplier *= 1.35f;
            }

            if (HasPerk(patient, PatientHiddenRisk))
            {
                multiplier *= 0.70f;
            }

            if (Math.Abs(multiplier - 1f) < 0.01f)
            {
                return;
            }

            if (patient.m_state == null || patient.m_state.m_medicalCondition == null)
            {
                return;
            }

            foreach (var symptom in patient.m_state.m_medicalCondition.m_symptoms)
            {
                if (symptom != null && symptom.m_collapseTriggerTimeHours > 0f)
                {
                    symptom.m_collapseTriggerTimeHours *= multiplier;
                }
            }
        }

        public static void AdjustSurgerySkill(ref float skillLevel)
        {
            var component = sm_currentProcedureComponent;
            if (component == null || component.m_state == null || component.m_state.m_currentProcedureScript == null)
            {
                return;
            }

            var script = component.m_state.m_currentProcedureScript.GetEntity();
            var scene = script == null ? null : script.m_stateData.m_procedureScene;
            if (scene == null)
            {
                return;
            }

            foreach (var employee in GetSceneEmployees(scene))
            {
                if (HasPerk(employee, StaffSurgicalHand))
                {
                    skillLevel = Math.Min(5f, skillLevel + 1f);
                    return;
                }
            }
        }

        public static void AdjustCleaningTime(object janitor)
        {
            var state = ReflectionHelpers.GetField(janitor, "m_state");
            var cleaningTime = ReflectionHelpers.GetField(state, "m_cleaningTime");
            if (!(cleaningTime is float))
            {
                return;
            }

            var value = (float)cleaningTime;
            if (HasPerk(janitor, StaffSanitaryPerfectionist))
            {
                value *= 0.85f;
            }

            if (HasPerk(janitor, StaffSlowCleaning))
            {
                value *= 1.5f;
            }

            SetField(state, "m_cleaningTime", value);
        }

        public static void TryCleanAdjacentTile(object janitor)
        {
            if (!HasPerk(janitor, StaffSanitaryPerfectionist) || UnityEngine.Random.Range(0, 100) >= 35)
            {
                return;
            }

            var entity = GetEntity(janitor);
            var walk = entity == null ? null : entity.GetComponent<WalkComponent>();
            if (walk == null)
            {
                return;
            }

            var tile = walk.GetCurrentTile();
            var floor = walk.GetFloorIndex();
            var offsets = new[] { new Vector2i(1, 0), new Vector2i(-1, 0), new Vector2i(0, 1), new Vector2i(0, -1) };
            Shuffle(offsets);
            foreach (var offset in offsets)
            {
                var nearby = new Vector2i(tile.m_x + offset.m_x, tile.m_y + offset.m_y);
                try
                {
                    MapScriptInterface.Instance.CleanTile(nearby, floor);
                    return;
                }
                catch
                {
                }
            }
        }

        public static bool ShouldSkipNeeds(object behavior)
        {
            return HasPerk(behavior, StaffEmergencyFocus) && ProductivityTweaksService.IsEmergencyContext(behavior);
        }

        public static void ApplyMoodInteraction(object patient, object employee)
        {
            var patientEntity = patient as Entity ?? GetEntity(patient);
            var employeeEntity = employee as Entity ?? GetEntity(employee);
            if (patientEntity == null || employeeEntity == null)
            {
                return;
            }

            if (HasPerk(employeeEntity, StaffStressProof))
            {
                var employeeMood = employeeEntity.GetComponent<MoodComponent>();
                if (employeeMood != null)
                {
                    if (employeeMood.HasSatisfactionModifier("SAT_MOD_DEMOTIVATOR"))
                    {
                        employeeMood.RemoveSatisfactionModifier("SAT_MOD_DEMOTIVATOR");
                    }
                    if (employeeMood.HasSatisfactionModifier("SAT_MOD_MEAN_STAFF"))
                    {
                        employeeMood.RemoveSatisfactionModifier("SAT_MOD_MEAN_STAFF");
                    }
                }
            }

            if (HasPerk(patientEntity, PatientPanicker))
            {
                var employeeMood = employeeEntity.GetComponent<MoodComponent>();
                var patientMood = patientEntity.GetComponent<MoodComponent>();
                if (employeeMood != null && !employeeMood.HasSatisfactionModifier("SAT_MOD_DEMOTIVATOR"))
                {
                    employeeMood.AddSatisfactionModifier("SAT_MOD_DEMOTIVATOR");
                }
                if (patientMood != null && !patientMood.HasSatisfactionModifier("SAT_MOD_DISCOMFORTABLE"))
                {
                    patientMood.AddSatisfactionModifier("SAT_MOD_DISCOMFORTABLE");
                }
            }
        }

        public static void AdjustPatientVisitDuration(object patientObject, float deltaTime)
        {
            if (deltaTime <= 0f)
            {
                return;
            }

            var patient = patientObject as BehaviorPatient;
            if (patient == null || !patient.IsWaiting())
            {
                return;
            }

            var state = ReflectionHelpers.GetField(patient, "m_state");
            var value = ReflectionHelpers.GetField(state, "m_fVisitDuration");
            if (!(value is float))
            {
                return;
            }

            var visitDuration = (float)value;
            if (HasPerk(patient, PatientPatient))
            {
                visitDuration = Math.Max(0f, visitDuration - deltaTime * 0.25f);
            }

            if (HasPerk(patient, PatientQueueBreaker) || HasPerk(patient, PatientPanicker))
            {
                visitDuration += deltaTime * 0.25f;
            }

            SetField(state, "m_fVisitDuration", visitDuration);
        }

        public static void TryRevealExtraSymptom(object patientObject, object doctorObject)
        {
            var patient = patientObject as BehaviorPatient;
            if (patient == null && patientObject is Entity)
            {
                patient = ((Entity)patientObject).GetComponent<BehaviorPatient>();
            }

            if (patient == null || patient.m_state == null || patient.m_state.m_medicalCondition == null)
            {
                return;
            }

            if (!HasPerk(patient, PatientEducated) && !HasPerk(doctorObject, StaffDiagnosticAcuity))
            {
                return;
            }

            if (UnityEngine.Random.Range(0, 100) >= 35)
            {
                return;
            }

            Symptom selected = null;
            foreach (var symptom in patient.m_state.m_medicalCondition.m_symptoms)
            {
                if (symptom == null || !symptom.m_hidden || !symptom.m_spawned)
                {
                    continue;
                }

                if (selected == null || symptom.m_symptom.Entry.Hazard > selected.m_symptom.Entry.Hazard)
                {
                    selected = symptom;
                }
            }

            if (selected == null)
            {
                return;
            }

            var entity = GetEntity(patient);
            if (entity == null)
            {
                return;
            }

            selected.m_hidden = false;
            patient.m_state.m_medicalCondition.UpdatePossibleDiagnoses(entity);
            patient.GetComponent<MoodComponent>().UpdateSymptomDiscomfortModifiers();
        }

        private static IEnumerable<Entity> GetSceneEmployees(ProcedureScene scene)
        {
            if (scene.EmployeeCharacter != null)
            {
                yield return scene.EmployeeCharacter;
            }

            foreach (EntityIDPointer<Entity> doctor in scene.m_doctors)
            {
                if (doctor.GetEntity() != null)
                {
                    yield return doctor.GetEntity();
                }
            }

            foreach (EntityIDPointer<Entity> nurse in scene.m_nurses)
            {
                if (nurse.GetEntity() != null)
                {
                    yield return nurse.GetEntity();
                }
            }
        }

        private static Entity GetProcedurePatient(ProcedureScript script)
        {
            if (script == null || script.m_stateData == null || script.m_stateData.m_procedureScene == null || script.m_stateData.m_procedureScene.m_patient == null)
            {
                return null;
            }

            return script.m_stateData.m_procedureScene.m_patient.GetEntity();
        }

        private static bool IsEmergencyProcedure(ProcedureScript script)
        {
            if (script == null || script.m_stateData == null || script.m_stateData.m_procedureScene == null || script.m_stateData.m_procedureScene.m_patient == null)
            {
                return false;
            }

            var patientEntity = script.m_stateData.m_procedureScene.m_patient.GetEntity();
            var patient = patientEntity == null ? null : patientEntity.GetComponent<BehaviorPatient>();
            return IsCriticalOrCollapsedPatient(patientEntity) || (patient != null && patient.GetWorstKnownHazard() == SymptomHazard.High);
        }

        private static bool IsCriticalOrCollapsedPatient(object patientEntity)
        {
            var behaviorPatient = ReflectionHelpers.GetComponentByTypeName(patientEntity, "Lopital.BehaviorPatient") as BehaviorPatient;
            if (behaviorPatient == null)
            {
                return false;
            }

            var hospitalization = ReflectionHelpers.GetComponentByTypeName(patientEntity, "Lopital.HospitalizationComponent");
            return behaviorPatient.GetWorstKnownHazard() == SymptomHazard.High
                || (hospitalization != null && ReflectionHelpers.InvokeBool(hospitalization, "WillCollapse"));
        }

        private static bool IsTransportState(object behavior)
        {
            var state = ReflectionHelpers.GetField(behavior, "m_state");
            var nurseState = ReflectionHelpers.GetField(state, "m_nurseState");
            return nurseState != null && nurseState.ToString().IndexOf("Stretcher", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HasConflict(object perkSet, CustomPerkDefinition candidate)
        {
            foreach (var vanillaId in GetVanillaConflictIds(candidate))
            {
                if (InvokeHasPerk(perkSet, vanillaId))
                {
                    return true;
                }
            }

            foreach (var definition in Definitions)
            {
                if (definition.Id != candidate.Id && definition.Group == candidate.Group && InvokeHasPerk(perkSet, definition.Id))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsApplicableToCharacter(CustomPerkDefinition definition, object character)
        {
            if (definition == null || !definition.Employee)
            {
                return true;
            }

            var target = definition.EmployeeTarget;
            if (string.IsNullOrEmpty(target) || target == "employee")
            {
                return HasComponent(character, "Lopital.EmployeeComponent");
            }

            var parts = target.Split(',');
            foreach (var rawPart in parts)
            {
                var part = rawPart.Trim();
                if (part == "doctor" && HasComponent(character, "Lopital.BehaviorDoctor"))
                {
                    return true;
                }

                if (part == "nurse" && HasComponent(character, "Lopital.BehaviorNurse"))
                {
                    return true;
                }

                if (part == "lab" && HasComponent(character, "Lopital.BehaviorLabSpecialist"))
                {
                    return true;
                }

                if (part == "janitor" && HasComponent(character, "Lopital.BehaviorJanitor"))
                {
                    return true;
                }

                if (part == "surgery" && IsSurgeryEmployee(character))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSurgeryEmployee(object character)
        {
            var employee = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.EmployeeComponent");
            return HasEmployeeRole(employee, "EMPL_ROLE_SURGERY")
                || HasEmployeeRole(employee, "EMPL_ROLE_SURGERY_ANESTHESIOLOGY")
                || HasEmployeeRole(employee, "EMPL_ROLE_SURGERY_ASSIST")
                || HasEmployeeRole(employee, "EMPL_ROLE_SURGERY_NURSE");
        }

        private static bool HasEmployeeRole(object employee, string roleId)
        {
            try
            {
                var role = Database.Instance == null ? null : Database.Instance.GetEntry<GameDBEmployeeRole>(roleId);
                var method = employee == null || role == null
                    ? null
                    : employee.GetType().GetMethod("HasRole", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(GameDBEmployeeRole) }, null);
                return method != null && Equals(method.Invoke(employee, new object[] { role }), true);
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<string> GetVanillaConflictIds(CustomPerkDefinition candidate)
        {
            if (candidate.Id == PatientSecretive)
            {
                yield return "PERK_SHAMELESS";
                yield return "PERK_MEDICAL_EDUCATION";
            }
            else if (candidate.Id == PatientEducated)
            {
                yield return "PERK_SHY";
            }
            else if (candidate.Id == PatientProcedureSaboteur)
            {
                yield return "PERK_COOPERATIVE";
            }
            else if (candidate.Id == PatientPolluter)
            {
                yield return "PERK_CLEAN_FEET";
            }
            else if (candidate.Id == PatientPunctual)
            {
                yield return "PERK_SLOW";
            }
            else if (candidate.Id == PatientVerySlow)
            {
                yield return "PERK_FAST";
            }
            else if (candidate.Id == PatientGenerous || candidate.Id == PatientFreeloader)
            {
                yield return "PERK_PIRATE";
            }
            else if (candidate.Id == StaffDiagnosticErrors)
            {
                yield return "PERK_DIAGNOSTIC_GENIUS";
            }
            else if (candidate.Id == StaffSlowCleaning)
            {
                yield return "PERK_CHEMIST";
            }
            else if (candidate.Id == StaffBurnout || candidate.Id == StaffChaotic)
            {
                yield return "PERK_HARD_WORKER";
            }
        }

        private static void RemoveConflictingExtras(IList perks)
        {
            var seen = new HashSet<string>();
            for (var i = perks.Count - 1; i >= 0; i--)
            {
                var id = GetPerkId(perks[i]);
                CustomPerkDefinition definition;
                if (id == null || !DefinitionById.TryGetValue(id, out definition))
                {
                    continue;
                }

                if (seen.Contains(definition.Group))
                {
                    perks.RemoveAt(i);
                }
                else
                {
                    seen.Add(definition.Group);
                }
            }
        }

        private static void RemoveInapplicableCustomPerks(IList perks, object character)
        {
            for (var i = perks.Count - 1; i >= 0; i--)
            {
                var id = GetPerkId(perks[i]);
                CustomPerkDefinition definition;
                if (id == null || !DefinitionById.TryGetValue(id, out definition))
                {
                    continue;
                }

                if (!IsApplicableToCharacter(definition, character))
                {
                    perks.RemoveAt(i);
                }
            }
        }

        private static IList GetPerkList(object perkSet)
        {
            return ReflectionHelpers.GetField(perkSet, "m_perks") as IList;
        }

        private static object GetPerkSet(object characterOrComponent)
        {
            if (characterOrComponent == null)
            {
                return null;
            }

            if (characterOrComponent is PerkSet)
            {
                return characterOrComponent;
            }

            var entity = characterOrComponent as Entity ?? GetEntity(characterOrComponent);
            var component = entity == null
                ? ReflectionHelpers.GetComponentByTypeName(characterOrComponent, "Lopital.PerkComponent")
                : entity.GetComponent<PerkComponent>();
            return component == null ? null : ReflectionHelpers.GetField(component, "m_perkSet");
        }

        private static Entity GetEntity(object componentOrEntity)
        {
            if (componentOrEntity is Entity)
            {
                return (Entity)componentOrEntity;
            }

            return ReflectionHelpers.GetField(componentOrEntity, "m_entity") as Entity;
        }

        private static bool InvokeHasPerk(object perkSet, string id)
        {
            var method = AccessTools.Method(perkSet.GetType(), "HasPerk", new[] { typeof(string) });
            return method != null && Equals(method.Invoke(perkSet, new object[] { id }), true);
        }

        private static bool HasComponent(object entity, string typeName)
        {
            return ReflectionHelpers.GetComponentByTypeName(entity, typeName) != null;
        }

        private static string GetPerkId(object perk)
        {
            var pointer = ReflectionHelpers.GetField(perk, "m_perk");
            var dbPerk = ReflectionHelpers.ResolvePointer(pointer) as GameDBPerk;
            return dbPerk == null ? null : dbPerk.DatabaseID.ToString();
        }

        private static void InvokeAddEntry(Database database, DatabaseEntry entry)
        {
            var method = AccessTools.Method(typeof(Database), "AddEntry");
            if (method != null)
            {
                method.Invoke(database, new object[] { entry });
            }
        }

        private static void SetAutoProperty(object instance, string propertyName, object value)
        {
            var field = AccessTools.Field(instance.GetType(), "<" + propertyName + ">k__BackingField");
            if (field != null)
            {
                field.SetValue(instance, value);
            }
        }

        private static void SetField(object instance, string fieldName, object value)
        {
            var field = instance == null ? null : AccessTools.Field(instance.GetType(), fieldName);
            if (field != null)
            {
                field.SetValue(instance, value);
            }
        }

        private static void Shuffle<T>(IList<T> items)
        {
            for (var i = items.Count - 1; i > 0; i--)
            {
                var j = Rng.Next(i + 1);
                var value = items[i];
                items[i] = items[j];
                items[j] = value;
            }
        }

        private static bool IsCustomPerksEnabled()
        {
            return RuntimeSettings.Config != null && RuntimeSettings.Config.Enabled.Value && RuntimeSettings.Config.EnableCustomPerks.Value;
        }

        private static Dictionary<string, CustomPerkDefinition> CreateDefinitionMap()
        {
            var result = new Dictionary<string, CustomPerkDefinition>();
            foreach (var definition in Definitions)
            {
                result[definition.Id] = definition;
            }
            return result;
        }

        private static Dictionary<string, string> CreateRussianStrings()
        {
            var result = new Dictionary<string, string>();
            foreach (var definition in Definitions)
            {
                result[definition.Id] = definition.Name;
                result[definition.Id + "_DESCRIPTION"] = definition.Description;
                result[definition.Id + "_LOST_DESCRIPTION"] = "Черта исчезла после обучения.";
            }
            return result;
        }

        private sealed class CustomPerkDefinition
        {
            public CustomPerkDefinition(string id, string name, string description, bool employee, bool patient, PerkType type, int iconIndex, string group)
                : this(id, name, description, employee, patient, type, iconIndex, group, employee ? "employee" : null)
            {
            }

            public CustomPerkDefinition(string id, string name, string description, bool employee, bool patient, PerkType type, int iconIndex, string group, string employeeTarget)
            {
                Id = id;
                Name = name;
                Description = description;
                Employee = employee;
                Patient = patient;
                Type = type;
                IconIndex = iconIndex;
                Group = group;
                EmployeeTarget = employeeTarget;
            }

            public string Id { get; private set; }
            public string Name { get; private set; }
            public string Description { get; private set; }
            public bool Employee { get; private set; }
            public bool Patient { get; private set; }
            public PerkType Type { get; private set; }
            public int IconIndex { get; private set; }
            public string Group { get; private set; }
            public string EmployeeTarget { get; private set; }
        }
    }

    [HarmonyPatch(typeof(Database), "OnLoad")]
    internal static class CustomPerksDatabasePatch
    {
        private static void Postfix(Database __instance)
        {
            CustomPerkService.EnsureDatabaseEntries(__instance);
        }
    }

    [HarmonyPatch(typeof(StringTable), "GetLocalizedText", new[] { typeof(string), typeof(string[]) })]
    internal static class CustomPerksStringPatch
    {
        private static void Postfix(string stringID, ref string __result)
        {
            string text;
            if (CustomPerkService.TryGetLocalizedText(stringID, out text))
            {
                __result = text;
            }
        }
    }

    [HarmonyPatch(typeof(StringTable), "GetLocalizedText", new[] { typeof(DatabaseEntry) })]
    internal static class CustomPerksDatabaseEntryStringPatch
    {
        private static void Postfix(DatabaseEntry databaseItem, ref string __result)
        {
            string text;
            if (databaseItem != null && CustomPerkService.TryGetLocalizedText(databaseItem.DatabaseID.ToString(), out text))
            {
                __result = text;
            }
        }
    }

    [HarmonyPatch(typeof(Behavior), "GetSpeedModifier")]
    internal static class CustomPerksBehaviorSpeedPatch
    {
        private static void Postfix(object __instance, ref float __result)
        {
            CustomPerkService.AdjustSpeed(__instance, ref __result);
        }
    }

    [HarmonyPatch(typeof(BehaviorNurse), "GetSpeedModifier")]
    internal static class CustomPerksNurseSpeedPatch
    {
        private static void Postfix(object __instance, ref float __result)
        {
            CustomPerkService.AdjustSpeed(__instance, ref __result);
        }
    }

    [HarmonyPatch(typeof(Behavior), "GetFeetPerkModifier")]
    internal static class CustomPerksFeetPatch
    {
        private static void Postfix(object __instance, ref float __result)
        {
            CustomPerkService.AdjustFeetDirt(__instance, ref __result);
        }
    }

    [HarmonyPatch(typeof(ProcedureScript), "GetActionTime")]
    internal static class CustomPerksActionTimePatch
    {
        private static void Postfix(ProcedureScript __instance, Entity character, ref float __result)
        {
            CustomPerkService.AdjustActionTime(__instance, character, ref __result);
        }
    }

    [HarmonyPatch(typeof(EmployeeComponent), "GetEfficiencyTimeMultiplier")]
    internal static class CustomPerksEfficiencyPatch
    {
        private static void Postfix(object __instance, ref float __result)
        {
            CustomPerkService.AdjustEfficiency(__instance, ref __result);
        }
    }

    [HarmonyPatch(typeof(EmployeeComponent), "AddExperiencePoints")]
    internal static class CustomPerksExperiencePatch
    {
        private static void Prefix(object __instance, ref int points)
        {
            CustomPerkService.AdjustExperience(__instance, ref points);
        }
    }

    [HarmonyPatch(typeof(EmployeeComponent), "ComputeSalary")]
    internal static class CustomPerksSalaryPatch
    {
        private static void Postfix(object __instance)
        {
            CustomPerkService.AdjustSalary(__instance);
        }
    }

    [HarmonyPatch(typeof(BehaviorDoctor), "SelectNextDiagnosticApproach")]
    internal static class CustomPerksDiagnosticApproachPatch
    {
        private static void Prefix(object __instance, ref float certainty)
        {
            CustomPerkService.AdjustDoctorDiagnosticApproach(__instance, ref certainty);
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "GetShameLevelMultiplier")]
    internal static class CustomPerksShamePatch
    {
        private static void Postfix(object __instance, ref float __result)
        {
            CustomPerkService.AdjustShame(__instance, ref __result);
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "GetInsurancePayment")]
    internal static class CustomPerksInsurancePatch
    {
        private static void Postfix(object __instance, ref int __result)
        {
            CustomPerkService.AdjustInsurancePayment(__instance, ref __result);
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "ShouldPatientPay")]
    internal static class CustomPerksPaymentPatch
    {
        private static void Postfix(object __instance, ref bool __result)
        {
            CustomPerkService.AdjustPatientPay(__instance, ref __result);
        }
    }

    [HarmonyPatch(typeof(MedicalCondition), "ResetCollapseTimes")]
    internal static class CustomPerksCollapsePatch
    {
        private static void Postfix(BehaviorPatient behaviorPatient)
        {
            CustomPerkService.AdjustCollapseTimers(behaviorPatient);
        }
    }

    [HarmonyPatch(typeof(MedicalCondition), "AddSurgeryComplications")]
    internal static class CustomPerksSurgeryComplicationsPatch
    {
        private static void Prefix(ref float skillLevel)
        {
            CustomPerkService.AdjustSurgerySkill(ref skillLevel);
        }
    }

    [HarmonyPatch(typeof(ProcedureComponent), "Update")]
    internal static class CustomPerksProcedureComponentPatch
    {
        private static void Prefix(ProcedureComponent __instance)
        {
            CustomPerkService.ProcedureUpdatePrefix(__instance);
        }

        private static void Postfix()
        {
            CustomPerkService.ProcedureUpdatePostfix();
        }
    }

    [HarmonyPatch]
    internal static class CustomPerksJanitorCleaningTimePatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(BehaviorJanitor), "UpdateCleaningTime");
        }

        private static void Postfix(object __instance)
        {
            CustomPerkService.AdjustCleaningTime(__instance);
        }
    }

    [HarmonyPatch]
    internal static class CustomPerksJanitorCleanAdjacentPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(BehaviorJanitor), "UpdateStateCleaning");
        }

        private static void Postfix(object __instance)
        {
            CustomPerkService.TryCleanAdjacentTile(__instance);
        }
    }

    [HarmonyPatch]
    internal static class CustomPerksDoctorNeedsPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(BehaviorDoctor), "CheckNeeds");
        }

        private static bool Prefix(object __instance, ref bool __result)
        {
            if (!CustomPerkService.ShouldSkipNeeds(__instance))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class CustomPerksNurseNeedsPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(BehaviorNurse), "CheckNeeds");
        }

        private static bool Prefix(object __instance, ref bool __result)
        {
            if (!CustomPerkService.ShouldSkipNeeds(__instance))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class CustomPerksLabNeedsPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(BehaviorLabSpecialist), "CheckNeeds");
        }

        private static bool Prefix(object __instance, ref bool __result)
        {
            if (!CustomPerkService.ShouldSkipNeeds(__instance))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class CustomPerksDoctorInterviewExtraSymptomPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.ProcedureScriptExaminationDoctorsInterview");
            return type == null ? null : AccessTools.Method(type, "TryToResolveASymptom");
        }

        private static void Postfix(object __instance)
        {
            var stateData = ReflectionHelpers.GetField(__instance, "m_stateData");
            var scene = ReflectionHelpers.GetField(stateData, "m_procedureScene");
            var patientPointer = ReflectionHelpers.GetField(scene, "m_patient");
            var doctorPointer = ReflectionHelpers.GetField(scene, "m_doctor");
            var patient = ReflectionHelpers.ResolvePointer(patientPointer);
            var doctor = ReflectionHelpers.ResolvePointer(doctorPointer);
            CustomPerkService.TryRevealExtraSymptom(patient, doctor);
        }
    }

    [HarmonyPatch(typeof(ProcedureScript), "CheckPerkModifiers")]
    internal static class CustomPerksMoodInteractionPatch
    {
        private static void Postfix(Entity patient, Entity employee)
        {
            CustomPerkService.ApplyMoodInteraction(patient, employee);
        }
    }

    [HarmonyPatch(typeof(BehaviorPatient), "Update")]
    internal static class CustomPerksPatientPatiencePatch
    {
        private static void Postfix(object __instance, float deltaTime)
        {
            CustomPerkService.AdjustPatientVisitDuration(__instance, deltaTime);
        }
    }
}
