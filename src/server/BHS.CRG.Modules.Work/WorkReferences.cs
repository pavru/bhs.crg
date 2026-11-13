using BHS.CRG.Modules.Data;

namespace BHS.CRG.Modules.Work;

/// <summary>
/// Колонки модуля, в которых лежат идентификаторы записей ядра (ТЗ CORE-34.1; change <c>work-11-assignments</c>).
///
/// <para>Список — не то, чем ядро НАХОДИТ держателей: оно сканирует схему само, и колонка, забытая здесь,
/// удержит запись всё равно. Отсюда берутся слова отказа («Учёт работ: назначения — 3») вместо адреса
/// таблицы. Полноту стережёт <c>ModuleReferenceInventoryTests</c>.</para>
///
/// <para>Назначение держит и сотрудника, и стройку: снятое молча, оно оставило бы монтажнику отчёты по
/// стройке, которой нет, а менеджеру — «свою» стройку без записи. Сотрудника с назначениями отправляют в
/// архив (CORE-34.4): новое назначение на архивного отвергается, существующие остаются.</para>
/// </summary>
public static class WorkReferences
{
    public static IReadOnlyList<ModuleReference> All =>
    [
        ModuleReference.Holding("assignments", "employee_id", ReferenceTarget.Record, "назначения"),
        ModuleReference.Holding("assignments", "construction_id", ReferenceTarget.Construction,
            "назначения на эту стройку"),
    ];
}
