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

        ModuleReference.Holding("estimate_row_materials", "nomenclature_id", ReferenceTarget.Record,
            "материалы строк смет с этой позицией номенклатуры"),

        // Импортировавший — справка, а не связь: удаление учётной записи редакций не касается, как у
        // автора счёта (ревью PR #1188).
        ModuleReference.Remembering("estimate_revisions", "imported_by", ReferenceTarget.User,
            "кто импортировал редакцию сметы — справочное поле; удаление учётной записи редакций не касается"),
    ];
}
