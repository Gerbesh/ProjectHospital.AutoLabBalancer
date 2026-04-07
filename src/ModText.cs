using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ProjectHospital.AutoLabBalancer
{
    internal static class ModText
    {
        private static readonly Dictionary<string, string> En = new Dictionary<string, string>
        {
            { "PluginName", "Project Hospital Productivity Tweaks" },
            { "WindowTitle", "Productivity Tweaks" },
            { "SettingsSaved", "Settings are saved to the BepInEx config." },
            { "PageSettings", "Settings" },
            { "PageCounters", "Counters" },
            { "PageBottlenecks", "Bottlenecks" },
            { "PageSurgery", "Surgery" },
            { "PagePerformance", "Performance" },
            { "Close", "Close" },
            { "BlockNegativePerks", "Block negative employee perks on generation" },
            { "DebugLog", "Debug log" },
            { "ProductivityTweaks", "Productivity Tweaks" },
            { "PlanMedication", "Plan all available medication for known active symptoms" },
            { "SuppressFreeTime", "Suppress free-time when department is busy" },
            { "PrioritizeOrCleanup", "Prioritize post-surgery OR cleanup" },
            { "CleanStuckReservations", "Clean stuck reservations watchdog" },
            { "FlexibleStretcherPickup", "Flexible stretcher pickup" },
            { "ChainDiagnostics", "Chain hospitalized diagnostics before returning to bed" },
            { "RetryTransportReservations", "Retry stale transport/procedure reservations" },
            { "NurseCheckDischarge", "Discharge ready inpatients after nurse check" },
            { "EmergencyRunSpeedBoost", "Emergency run speed boost" },
            { "NurseAssistedOrCleanup", "Nurse-assisted OR cleanup" },
            { "EquipmentReferral", "Refer equipment-blocked patients to another hospital" },
            { "UnsupportedDiagnosisReferral", "Refer unsupported diagnosed outpatients to another hospital" },
            { "ManualReferralPayment", "Pay partial fee for manual untreated referrals" },
            { "ProductivityDebugLog", "Productivity debug log" },
            { "ShowBottleneckOverlay", "Show bottleneck overlay diagnostics" },
            { "SurgeryAnalyticsLog", "Write surgery bottleneck analytics to BepInEx log" },
            { "FixSurgeryTooltip", "Fix vanilla surgery staff tooltip" },
            { "PerformanceProfiler", "Performance profiler" },
            { "EnablePerformanceProfiler", "Enable internal profiler" },
            { "ProfilerAutoResetAfterLog", "Auto-reset profiler samples after log" },
            { "EnablePerformanceOptimizations", "Enable performance optimizations" },
            { "EnableSchedulingEngine", "Enable central scheduling engine" },
            { "EnableSchedulingEngineGating", "Let scheduling engine gate AI scans" },
            { "EnableNurseTaskBoard", "Enable nurse task board" },
            { "PerformanceProfilerDisabled", "Performance profiler is disabled." },
            { "PerformanceProfilerNoSamples", "No profiler samples yet." },
            { "PerformanceProfilerReset", "Reset profiler samples" },
            { "SchedulingEngine", "Scheduling engine" },
            { "SchedulingEngineNotReady", "Scheduling engine is not ready: " },
            { "SchedulingEngineLine", "Index: departments {0}, tasks {1}, critical {2}, surgery {3}, meds {4}, transport {5}, nurse tasks {6}, doctor tasks {7}, dry-run nurse/doctor {8}/{9}, free staff {10}/{11}, rebuild {12}ms" },
            { "SchedulingEngineTopBoard", "Top department board: " },
            { "SchedulingEngineCountersLine", "Counters: rebuilds {0} avg/max {1}/{2}ms, board hit/miss/stale {3}/{4}/{5}, nurse skips {6}/{7}, outpatient skips {8}/{9}, doctor-search skips {10}/{11}" },
            { "PerfTag", "[Performance]" },
            { "MedicationAutoAdded", "Medication auto-added: " },
            { "FreeTimeSuppressed", "Free-time suppressed: " },
            { "OrCleanupPriorities", "OR cleanup priorities: " },
            { "NurseOrTilesCleaned", "Nurse OR tiles cleaned: " },
            { "StuckReservationsCleared", "Stuck reservations cleared: " },
            { "FlexibleTransportFallbacks", "Flexible transport fallbacks: " },
            { "TransportReservationsRetried", "Transport reservations retried: " },
            { "EmergencySpeedBoosts", "Emergency speed boosts: " },
            { "EquipmentReferrals", "Equipment referrals: " },
            { "ReferralIncome", "Referral income: $" },
            { "UnsupportedDiagnosisReferrals", "Unsupported diagnosis referrals: " },
            { "UnsupportedDiagnosisIncome", "Unsupported diagnosis income: $" },
            { "ManualReferralPayments", "Manual referral payments: " },
            { "ManualReferralIncome", "Manual referral income: $" },
            { "OverlayDisabled", "Bottleneck overlay is disabled." },
            { "GameNotReady", "Game state is not ready yet." },
            { "Patients", "Patients: " },
            { "HighRisk", "High-risk: " },
            { "PlannedMeds", "Planned meds: " },
            { "IdleLabQueue", "Idle lab queue: " },
            { "DepartmentsBusy", "Departments busy: " },
            { "FreeDoctors", "Free doctors: " },
            { "FreeNurses", "Free nurses: " },
            { "FreeLabs", "Free labs: " },
            { "FreeJanitors", "Free janitors: " },
            { "OrCleanupRooms", "OR cleanup rooms: " },
            { "NurseCleanupJobs", "Nurse cleanup jobs: " },
            { "SurgeryLine", "Surgery: planned {0} / critical {1} / waiting departments {2}" },
            { "SurgeryBlockersLine", "Surgery blockers: room {0}, staff {1}, transport {2}, critical queue {3}" },
            { "TransportWaitsLine", "Transport waits: exam {0}, treatment {1}, chained outside room {2}" },
            { "RadiologyQueueLine", "Exams: imaging {0}, CT {1}, MRI {2}, X-ray {3}, USG {4}, angio {5}, cardio {6}, neuro {7}, hema {8}, micro {9}, histo {10}, office {11}, other {12}" },
            { "IntakeLine", "Intake: clinic {0}/{1}, ambulance {2}/{3}, outpatient doctors {4}" },
            { "IntakeControl", "Intake Control" },
            { "EnableIntakeControl", "Cap daily insurance patient intake" },
            { "EnableDynamicIntakeByDoctors", "Calculate intake capacity from outpatient doctors" },
            { "DeveloperTools", "Developer tools" },
            { "DevCheapUpgrades", "DEV: cheap upgrade prices" },
            { "EnableAbsurdUpgrades", "Enable absurd upgrade tier" },
            { "SurgeryTooltipNote", "Note: vanilla surgery tooltip can understate surgery nurses; readiness uses actual RequiredNurseRoles from procedure DB." },
            { "SurgeryReadiness", "Surgery readiness:" },
            { "OverlayWarning", "Overlay warning: " },
            { "SurgeryTooltipFixed", "2x Surgeon\n1x Anesthesiologist\n2x Surgery nurse" },
            { "AnalyticsTag", "[SurgeryAnalytics]" },
            { "AnalyticsPlanned", "planned=" },
            { "AnalyticsCritical", " critical=" },
            { "AnalyticsWaitingDepartments", " waitingDepartments=" },
            { "AnalyticsBlockers", " blockers(room/staff/transport/criticalQueue)=" },
            { "AnalyticsTransportWaits", " transportWaits(exam/treatment/chainedOutside)=" },
            { "AnalyticsRadiology", " exams(img/CT/MRI/Xray/USG/angio/cardio/neuro/hema/micro/histo/office/other)=" },
            { "AnalyticsIntake", " intake(clinic/cap/ambulance/cap)=" },
            { "AnalyticsFreeStaff", " freeStaff(doctors/nurses/janitors)=" },
            { "AnalyticsReadiness", " readiness=" },
            { "CleanupFailed", "Project Hospital mod cleanup failed: " },
            { "TickFailed", "Project Hospital mod tick failed: " },
            { "AwakeStarted", " awake started." },
            { "HarmonyInstalled", "Harmony patches installed." },
            { "HarmonyFailed", "Harmony patching failed; continuing without optional patches. " },
            { "Loaded", " loaded." },
            { "UpgradesTab", "Upgrades" },
            { "UpgradesTitle", "Hospital upgrades" },
            { "UpgradeMax", "MAX" },
            { "UpgradeBuy", "Buy" },
            { "UpgradeLevel", "Lv. {0}/6" },
            { "UpgradeCost", "Cost: ${0}" },
            { "UpgradeAbsurdCost", "Absurd: ${0}" },
            { "UpgradeAbsurdBought", "ABSURD" },
            { "UpgradeEffect", "Effect: {0}" },
            { "UpgradeNotEnoughMoney", "Not enough money" },
            { "UpgradeBuyAbsurd", "Buy absurd" },
            { "UpgradeAbsurdLocked", "Buy level 6 first" },
            { "UpgradeClinical", "Clinical Overdrive" },
            { "UpgradeNursing", "Nursing Overdrive" },
            { "UpgradeLab", "Laboratory Conveyor" },
            { "UpgradeCleaning", "Sanitary Blitz" },
            { "UpgradeDiagnostics", "Turbo Diagnostics" },
            { "UpgradeTherapy", "Therapy Protocols" },
            { "UpgradeSurgery", "Surgery Conveyor" },
            { "UpgradeMonitoring", "Life Support Monitoring" },
            { "UpgradeInsurance", "Insurance Pressure" },
            { "UpgradeWages", "Financial Grinder" },
            { "UpgradeProcurement", "Procurement Cartel" }
        };

        private static readonly Dictionary<string, string> Ru = new Dictionary<string, string>
        {
            { "PluginName", "Project Hospital: улучшения продуктивности" },
            { "WindowTitle", "Улучшения продуктивности" },
            { "SettingsSaved", "Настройки сохраняются в конфиг BepInEx." },
            { "PageSettings", "Настройки" },
            { "PageCounters", "Счётчики" },
            { "PageBottlenecks", "Узкие места" },
            { "PageSurgery", "Операции" },
            { "PagePerformance", "Производительность" },
            { "Close", "Закрыть" },
            { "BlockNegativePerks", "Запрещать негативные перки у сотрудников при генерации" },
            { "DebugLog", "Подробный лог" },
            { "ProductivityTweaks", "Улучшения продуктивности" },
            { "PlanMedication", "Назначать все доступные лекарства для известных активных симптомов" },
            { "SuppressFreeTime", "Не отпускать в свободное время, когда отделение загружено" },
            { "PrioritizeOrCleanup", "Приоритетная уборка операционной после операции" },
            { "CleanStuckReservations", "Watchdog зависших резерваций" },
            { "FlexibleStretcherPickup", "Гибкий выбор каталки/кресла" },
            { "ChainDiagnostics", "Цепочка диагностик без возврата в палату" },
            { "RetryTransportReservations", "Повторять зависшие резервации транспорта/процедур" },
            { "NurseCheckDischarge", "Выписывать готовых стационарных пациентов после обхода медсестры" },
            { "EmergencyRunSpeedBoost", "Ускорение бега в экстренных случаях" },
            { "NurseAssistedOrCleanup", "Помощь операционных сестёр с уборкой операционной" },
            { "EquipmentReferral", "Направлять пациентов в другую больницу при нехватке оборудования" },
            { "UnsupportedDiagnosisReferral", "Направлять диагностированных внепрофильных амбулаторных пациентов в другую больницу" },
            { "ManualReferralPayment", "Частичная оплата за ручной перевод невылеченного пациента" },
            { "ProductivityDebugLog", "Подробный лог продуктивности" },
            { "ShowBottleneckOverlay", "Показывать диагностику узких мест" },
            { "SurgeryAnalyticsLog", "Писать аналитику операций в лог BepInEx" },
            { "FixSurgeryTooltip", "Исправить ванильную подсказку состава операционной бригады" },
            { "PerformanceProfiler", "Профилировщик производительности" },
            { "EnablePerformanceProfiler", "Включить внутренний профилировщик" },
            { "ProfilerAutoResetAfterLog", "Автосбрасывать сэмплы после записи в лог" },
            { "EnablePerformanceOptimizations", "Включить оптимизации производительности" },
            { "EnableSchedulingEngine", "Включить центральный планировщик" },
            { "EnableSchedulingEngineGating", "Разрешить планировщику скипать AI-сканы" },
            { "EnableNurseTaskBoard", "Включить доску задач медсестёр" },
            { "PerformanceProfilerDisabled", "Профилировщик производительности выключен." },
            { "PerformanceProfilerNoSamples", "Сэмплов профилировщика пока нет." },
            { "PerformanceProfilerReset", "Сбросить сэмплы профилировщика" },
            { "SchedulingEngine", "Центральный планировщик" },
            { "SchedulingEngineNotReady", "Центральный планировщик не готов: " },
            { "SchedulingEngineLine", "Индекс: отделения {0}, задачи {1}, критичные {2}, операции {3}, лекарства {4}, транспорт {5}, задачи сестёр {6}, задачи врачей {7}, dry-run сёстры/врачи {8}/{9}, свободный персонал {10}/{11}, пересчёт {12}мс" },
            { "SchedulingEngineTopBoard", "Топ доска отделения: " },
            { "SchedulingEngineCountersLine", "Счётчики: пересчёты {0} ср/макс {1}/{2}мс, board hit/miss/stale {3}/{4}/{5}, скипы сестёр {6}/{7}, скипы амбулаторки {8}/{9}, скипы поиска врача {10}/{11}" },
            { "PerfTag", "[Производительность]" },
            { "MedicationAutoAdded", "Автоназначено лекарств: " },
            { "FreeTimeSuppressed", "Свободное время подавлено: " },
            { "OrCleanupPriorities", "Приоритетов уборки операционных: " },
            { "NurseOrTilesCleaned", "Тайлов операционной убрано сестрами: " },
            { "StuckReservationsCleared", "Зависших резерваций очищено: " },
            { "FlexibleTransportFallbacks", "Подбор транспорта fallback: " },
            { "TransportReservationsRetried", "Резерваций транспорта повторено: " },
            { "EmergencySpeedBoosts", "Экстренных ускорений: " },
            { "EquipmentReferrals", "Переводов из-за оборудования: " },
            { "ReferralIncome", "Доход от переводов: $" },
            { "UnsupportedDiagnosisReferrals", "Переводов вне профиля: " },
            { "UnsupportedDiagnosisIncome", "Доход от внепрофильных переводов: $" },
            { "ManualReferralPayments", "Ручных частичных оплат: " },
            { "ManualReferralIncome", "Доход от ручных переводов: $" },
            { "OverlayDisabled", "Диагностика узких мест выключена." },
            { "GameNotReady", "Состояние игры ещё не готово." },
            { "Patients", "Пациенты: " },
            { "HighRisk", "Высокий риск: " },
            { "PlannedMeds", "Назначенные лекарства: " },
            { "IdleLabQueue", "Очередь idle лабораторий: " },
            { "DepartmentsBusy", "Загруженные отделения: " },
            { "FreeDoctors", "Свободные врачи: " },
            { "FreeNurses", "Свободные медсёстры: " },
            { "FreeLabs", "Свободные лаборанты: " },
            { "FreeJanitors", "Свободные уборщики: " },
            { "OrCleanupRooms", "Операционные на уборку: " },
            { "NurseCleanupJobs", "Задачи уборки для сестёр: " },
            { "SurgeryLine", "Операции: назначено {0} / критичных {1} / отделений с ожиданием {2}" },
            { "SurgeryBlockersLine", "Блокеры операций: комната {0}, персонал {1}, транспорт {2}, критичная очередь {3}" },
            { "TransportWaitsLine", "Ожидание транспорта: обследования {0}, лечения {1}, цепочка вне палаты {2}" },
            { "RadiologyQueueLine", "Исследования: визуализация {0}, КТ {1}, МРТ {2}, рентген {3}, УЗИ {4}, ангио {5}, кардио {6}, нейро {7}, гема {8}, микро {9}, гисто {10}, офис {11}, другое {12}" },
            { "IntakeLine", "Поток: клиника {0}/{1}, скорая {2}/{3}, амбулаторных врачей {4}" },
            { "IntakeControl", "Контроль входящего потока" },
            { "EnableIntakeControl", "Ограничивать дневной поток пациентов от страховых" },
            { "EnableDynamicIntakeByDoctors", "Считать пропускную способность по амбулаторным врачам" },
            { "DeveloperTools", "Инструменты разработчика" },
            { "DevCheapUpgrades", "DEV: дешёвые цены улучшений" },
            { "EnableAbsurdUpgrades", "Включить йобнутый уровень улучшений" },
            { "SurgeryTooltipNote", "Важно: ванильная подсказка может занижать число операционных сестёр; readiness считает реальные RequiredNurseRoles из базы процедур." },
            { "SurgeryReadiness", "Готовность операций:" },
            { "OverlayWarning", "Предупреждение overlay: " },
            { "SurgeryTooltipFixed", "2x Хирург\n1x Анестезиолог\n2x Операционная сестра" },
            { "AnalyticsTag", "[АналитикаОпераций]" },
            { "AnalyticsPlanned", "назначено=" },
            { "AnalyticsCritical", " критичных=" },
            { "AnalyticsWaitingDepartments", " отделенийСОжиданием=" },
            { "AnalyticsBlockers", " блокеры(комната/персонал/транспорт/критичнаяОчередь)=" },
            { "AnalyticsTransportWaits", " ожиданиеТранспорта(обследование/лечение/цепочкаВнеПалаты)=" },
            { "AnalyticsRadiology", " исследования(виз/КТ/МРТ/рентген/УЗИ/ангио/кардио/нейро/гема/микро/гисто/офис/другое)=" },
            { "AnalyticsIntake", " поток(клиника/лимит/скорая/лимит)=" },
            { "AnalyticsFreeStaff", " свободныйПерсонал(врачи/медсёстры/уборщики)=" },
            { "AnalyticsReadiness", " готовность=" },
            { "CleanupFailed", "Ошибка очистки мода Project Hospital: " },
            { "TickFailed", "Ошибка тика мода Project Hospital: " },
            { "AwakeStarted", " начал загрузку." },
            { "HarmonyInstalled", "Harmony-патчи установлены." },
            { "HarmonyFailed", "Не удалось установить Harmony-патчи; продолжаем без опциональных патчей. " },
            { "Loaded", " загружен." },
            { "UpgradesTab", "Улучшения" },
            { "UpgradesTitle", "Улучшения больницы" },
            { "UpgradeMax", "МАКС" },
            { "UpgradeBuy", "Купить" },
            { "UpgradeLevel", "Ур. {0}/6" },
            { "UpgradeCost", "Цена: ${0}" },
            { "UpgradeAbsurdCost", "Йоб-уровень: ${0}" },
            { "UpgradeAbsurdBought", "ЙОБ" },
            { "UpgradeEffect", "Эффект: {0}" },
            { "UpgradeNotEnoughMoney", "Недостаточно денег" },
            { "UpgradeBuyAbsurd", "Купить йоб" },
            { "UpgradeAbsurdLocked", "Сначала купи 6 уровень" },
            { "UpgradeClinical", "Клинический форсаж" },
            { "UpgradeNursing", "Сестринский форсаж" },
            { "UpgradeLab", "Лабораторный конвейер" },
            { "UpgradeCleaning", "Санитарный блиц" },
            { "UpgradeDiagnostics", "Турбо-диагностика" },
            { "UpgradeTherapy", "Терапевтический протокол" },
            { "UpgradeSurgery", "Операционный конвейер" },
            { "UpgradeMonitoring", "Система жизнеобеспечения" },
            { "UpgradeInsurance", "Страховой прессинг" },
            { "UpgradeWages", "Финансовая мясорубка" },
            { "UpgradeProcurement", "Закупочный картель" }
        };

        private static readonly Dictionary<string, string> RuLogPrefixes = new Dictionary<string, string>
        {
            { "Aggressive medication planning skipped: patient medication limit reached ", "Агрессивное планирование лекарств пропущено: достигнут лимит лекарств пациента " },
            { "Aggressive medication planning added ", "Агрессивное планирование лекарств добавило " },
            { "Aggressive medication planning failed: ", "Ошибка агрессивного планирования лекарств: " },
            { "Post-surgery cleanup priority failed: ", "Ошибка приоритета уборки после операции: " },
            { "Priority cleanup selection failed: ", "Ошибка выбора приоритетной уборки: " },
            { "Free-time suppression check failed: ", "Ошибка проверки подавления свободного времени: " },
            { "Nurse-assisted OR cleanup failed: ", "Ошибка уборки операционной медсестрой: " },
            { "Flexible stretcher pickup failed: ", "Ошибка гибкого выбора каталки/кресла: " },
            { "Emergency context check failed: ", "Ошибка проверки экстренного контекста: " },
            { "Emergency movement boost check failed: ", "Ошибка проверки ускорения движения: " },
            { "Emergency running extra step failed: ", "Ошибка дополнительного шага экстренного бега: " },
            { "Chained hospitalized examination handling failed: ", "Ошибка цепочки обследований госпитализированного пациента: " },
            { "Nurse-check discharge failed: ", "Ошибка выписки после обхода медсестры: " },
            { "Nurse-check discharged ready inpatient ", "После обхода медсестры выписан готовый стационарный пациент " },
            { "Reservation watchdog failed: ", "Ошибка watchdog резерваций: " },
            { "Cleaned stuck employee reservation for ", "Очищена зависшая резервация сотрудника " },
            { "Cleaned stuck room reservation for ", "Очищена зависшая резервация комнаты " },
            { "Equipment referral check for planned examination failed: ", "Ошибка проверки перевода из-за оборудования для назначенного обследования: " },
            { "Equipment referral check after scheduling failure failed: ", "Ошибка проверки перевода после неудачного назначения: " },
            { "Manual referral payment failed: ", "Ошибка выплаты за ручной перевод: " },
            { "Unsupported diagnosis referral check failed: ", "Ошибка проверки внепрофильного перевода: " },
            { "Paid manual untreated referral share. Payment=", "Выплачена доля за ручной перевод невылеченного пациента. Выплата=" },
            { "Referred unsupported diagnosed outpatient to another hospital. Payment=", "Внепрофильный диагностированный амбулаторный пациент направлен в другую больницу. Выплата=" },
            { "Skipped equipment referral for hospitalized patient; vanilla hospitalization state must own patient departure.", "Пропущен перевод госпитализированного пациента из-за оборудования: уходом пациента должен владеть ванильный hospitalization state." },
            { "Referred equipment-blocked patient to another hospital. Payment=", "Пациент с блокером оборудования направлен в другую больницу. Выплата=" },
            { "Intake control failed: ", "Ошибка контроля входящего потока: " }
        };

        public static string T(string key)
        {
            string value;
            var dictionary = IsRussian() ? Ru : En;
            if (dictionary.TryGetValue(key, out value))
            {
                return value;
            }

            return En.TryGetValue(key, out value) ? value : key;
        }

        public static string F(string key, params object[] args)
        {
            return string.Format(T(key), args);
        }

        public static bool IsRussian()
        {
            try
            {
                var type = AccessTools.TypeByName("StringTable");
                var getInstance = type == null ? null : AccessTools.Method(type, "GetInstance");
                var instance = getInstance == null ? null : getInstance.Invoke(null, null);
                var getLanguage = instance == null ? null : AccessTools.Method(type, "GetCurrentLanguage");
                var language = getLanguage == null ? null : getLanguage.Invoke(instance, null) as string;
                return !string.IsNullOrEmpty(language)
                    && language.StartsWith("ru", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static string Log(string message)
        {
            if (!IsRussian() || string.IsNullOrEmpty(message))
            {
                return message;
            }

            foreach (var pair in RuLogPrefixes)
            {
                if (message.StartsWith(pair.Key, StringComparison.Ordinal))
                {
                    return pair.Value + message.Substring(pair.Key.Length);
                }
            }

            return message;
        }
    }
}
