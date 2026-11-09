using BHS.CRG.Modules.Data;

namespace BHS.CRG.Modules.Plan;

/// <summary>
/// Колонки модуля, в которых лежат идентификаторы записей ядра (ТЗ CORE-34.1, change
/// <c>plan-06-estimate-model</c>).
///
/// <para>Список — не то, чем ядро НАХОДИТ держателей: оно сканирует схему само, и колонка, забытая
/// здесь, удержит запись всё равно. Отсюда берутся слова отказа («строки смет с этим видом работы —
/// 40, сметы: «Основная») вместо адреса таблицы. Полноту стережёт <c>ModuleReferenceInventoryTests</c>.</para>
///
/// <para>Новая ссылка на запись в архиве не записывается (ТЗ CORE-34.4): это правило стоит в
/// единственном месте записи строк — <c>EstimateRevisionWriter</c> — и у заведения сметы.</para>
/// </summary>
public static class PlanReferences
{
    /// <summary>Чем назвать смету-держатель: её название — содержимое модуля, под правом чтения смет.</summary>
    private static ReferenceDocument Estimate(string via) =>
        new("estimates", "name", via, "сметы", PlanModule.EstimateRead);

    /// <summary>Чем назвать импорт-держатель: именем файла — по нему его узнает загрузивший.</summary>
    private static ReferenceDocument Import() =>
        new("estimate_imports", "file_name", "id", "импорты смет", PlanModule.EstimateEdit);

    /// <summary>
    /// Чем назвать редакцию, распределённую на раздел: ««Основная», ред. 3» — название в заголовке распределения, на
    /// момент его последней правки (<c>plan-10</c>). Число в отказе — строк частей или правил разделов ЛСР.
    /// </summary>
    private static ReferenceDocument Distribution() =>
        new("revision_distributions", "label", "revision_id", "сметы и редакции", PlanModule.EstimateRead, Key: "revision_id");

    public static IReadOnlyList<ModuleReference> All =>
    [
        ModuleReference.Holding("estimates", "construction_id", ReferenceTarget.Construction,
            "сметы этой стройки", Estimate("id")),
        ModuleReference.Holding("estimates", "section_id", ReferenceTarget.Section,
            "сметы этого раздела", Estimate("id")),

        // Строки называют свою редакцию, а не смету: документ-держатель по ключу строки не найти. Число
        // строк отказ назовёт, а какие сметы держат запись, покажет экран смет (plan-09).
        ModuleReference.Holding("estimate_rows", "work_type_id", ReferenceTarget.Record,
            "строки смет с этим видом работы"),
        ModuleReference.Holding("estimate_rows", "unit_id", ReferenceTarget.Record,
            "строки смет с этой единицей измерения"),
        ModuleReference.Holding("estimate_rows", "plan_item_id", ReferenceTarget.WorkPlanItem,
            "строки смет, сопоставленные с этой позицией перечня работ"),
        // Поля заказчика: тип «Позиция сметы» открыт, и поле-ссылку в него дописать можно (ТЗ TYPE-16).
        // Целью может оказаться что угодно, поэтому вид не назван.
        ModuleReference.Holding("estimate_rows", "data", target: null,
            "строки смет, где запись выбрана в поле сметного наполнения"),

        // Пометка «запомнено» называет запись этого же модуля, а не ядра; запомненное забывают — строка остаётся.
        ModuleReference.Remembering("estimate_rows", "rate_match_id", target: null,
            "какое запомненное сопоставление подставило строку — запись модуля, а не справочника ядра"),

        // Запомненное держит вид работы и единицу (plan-08): снимают его в списке — «забыть» или «перенацелить».
        ModuleReference.Holding("rate_matches", "work_type_id", ReferenceTarget.Record,
            "запомненные сопоставления шифров расценок с этим видом работы"),
        ModuleReference.Holding("rate_matches", "unit_id", ReferenceTarget.Record,
            "запомненные сопоставления шифров расценок с этой единицей измерения"),
        ModuleReference.Remembering("rate_matches", "updated_by", ReferenceTarget.User,
            "кто запомнил сопоставление шифра — справочное поле; удаление учётной записи его не касается"),

        ModuleReference.Holding("estimate_row_materials", "nomenclature_id", ReferenceTarget.Record,
            "материалы строк смет с этой позицией номенклатуры"),
        ModuleReference.Remembering("estimate_row_materials", "resource_match_id", target: null,
            "какое запомненное сопоставление подставило материал — запись модуля, а не справочника ядра"),

        // Запомненное держит позицию номенклатуры (plan-08): снимают его в списке — «забыть» или «перенацелить».
        ModuleReference.Holding("resource_matches", "nomenclature_id", ReferenceTarget.Record,
            "запомненные сопоставления ресурсов смет с этой позицией номенклатуры"),
        ModuleReference.Remembering("resource_matches", "updated_by", ReferenceTarget.User,
            "кто запомнил сопоставление ресурса — справочное поле; удаление учётной записи его не касается"),

        // Импорт держит стройку и раздел новой сметы: из него ещё заведётся смета на них (plan-07). Отменённый
        // импорт держать перестаёт — его запись уходит.
        ModuleReference.Holding("estimate_imports", "construction_id", ReferenceTarget.Construction,
            "импорты смет в эту стройку", Import()),
        ModuleReference.Holding("estimate_imports", "new_estimate_section_id", ReferenceTarget.Section,
            "импорты, которые заведут смету на этот раздел", Import()),
        ModuleReference.Remembering("estimate_imports", "created_by", ReferenceTarget.User,
            "кто загрузил файл импорта — справочное поле; удаление учётной записи импортов не касается"),
        ModuleReference.Remembering("estimate_imports", "job_id", target: null,
            "номер фоновой задачи разбора — по нему отчёт показывает ход; записи справочников это не касается"),
        ModuleReference.Remembering("estimate_imports", "parsed", target: null,
            "модель разбора файла сметы — тексты и числа файла, идентификаторов записей в ней нет"),
        ModuleReference.Remembering("estimate_imports", "unparsed", target: null,
            "не разобранные позиции файла с причиной — тексты файла, идентификаторов записей в нём нет"),
        ModuleReference.Remembering("estimate_imports", "skipped", target: null,
            "пропущенные позиции файла (неактивные, plan-31) — тексты файла, идентификаторов записей в них нет"),

        // Распределение держит раздел стройки (plan-10, CORE-34.1): раздел, на который распределены строки, не удалить,
        // пока распределение не снято — иначе часть объёма корпуса молча ушла бы «в никуда».
        ModuleReference.Holding("distribution_rules", "section_id", ReferenceTarget.Section,
            "правила разделов ЛСР смет, распределённые на этот раздел", Distribution()),
        ModuleReference.Holding("distribution_parts", "section_id", ReferenceTarget.Section,
            "строки смет, части которых распределены на этот раздел", Distribution()),
        ModuleReference.Remembering("revision_distributions", "changed_by", ReferenceTarget.User,
            "кто менял распределение последним — справочное поле; удаление учётной записи его не касается"),
        // Отчёт переноса помнит источник словами и числами (plan-10): источник могут удалить, а позиция обязана сказать,
        // что было. На распределение он не влияет — решение по позиции проверяет раздел заново.
        ModuleReference.Remembering("distribution_carry_over", "source_row_id", target: null,
            "строка редакции-источника переноса — запись модуля, а не справочника ядра"),
        ModuleReference.Remembering("distribution_carry_over", "parts", ReferenceTarget.Section,
            "прежние части строки источника в отчёте переноса — справка; решение по позиции проверяет раздел заново"),
        ModuleReference.Remembering("distribution_carry_over", "rule_section_id", ReferenceTarget.Section,
            "прежняя цель правила раздела ЛСР в отчёте переноса — справка, правило уже перенесено или потеряно"),
        ModuleReference.Remembering("distribution_carry_over", "candidates", target: null,
            "строки-кандидаты новой редакции в отчёте переноса — записи модуля, а не справочника ядра"),

        // Импортировавший — справка, а не связь: удаление учётной записи редакций не касается, как у
        // автора счёта (ревью PR #1188).
        ModuleReference.Remembering("estimate_revisions", "imported_by", ReferenceTarget.User,
            "кто импортировал редакцию сметы — справочное поле; удаление учётной записи редакций не касается"),

        // Приложения сметы (plan-11): кто приложил — справка, как у редакции; ключ импорта — запись модуля, а не ядра.
        ModuleReference.Remembering("conjuncture_analyses", "imported_by", ReferenceTarget.User,
            "кто приложил конъюнктурный анализ к смете — справочное поле; удаление учётной записи приложений не касается"),
        ModuleReference.Remembering("conjuncture_analyses", "import_key", target: null,
            "импорт, из которого заведён анализ, — по нему повторное принятие находит приложение; импорт после принятия уходит"),
        ModuleReference.Remembering("volume_statements", "imported_by", ReferenceTarget.User,
            "кто приложил ведомость объёмов работ к смете — справочное поле; удаление учётной записи приложений не касается"),
        ModuleReference.Remembering("volume_statements", "import_key", target: null,
            "импорт, из которого заведена ведомость, — по нему повторное принятие находит приложение; импорт после принятия уходит"),
    ];
}
