using BHS.CRG.Modules.Data;

namespace BHS.CRG.Modules.Costs;

/// <summary>
/// Колонки модуля, в которых лежат идентификаторы записей ядра (ТЗ CORE-34.1, issue #1094).
///
/// <para>Список — не то, чем ядро НАХОДИТ держателей: оно сканирует схему само, и колонка, забытая
/// здесь, удержит запись всё равно. Отсюда берутся слова отказа («строки счетов — 40, счета: № 12»)
/// вместо адреса таблицы. Полноту стережёт <c>ModuleReferenceInventoryTests</c>: каждая колонка с
/// идентификатором либо названа здесь, либо является ключом внутри схемы.</para>
/// </summary>
public static class CostsReferences
{
    /// <summary>Чем назвать счёт. Номер счёта — содержимое модуля, поэтому под правом чтения.</summary>
    private static ReferenceDocument Invoice(string via) =>
        new("invoices", "number", via, "счета", "costs.invoice.read");

    public static IReadOnlyList<ModuleReference> All =>
    [
        ModuleReference.Holding("invoices", "supplier_id", ReferenceTarget.Record,
            "счета этого поставщика", Invoice("id")),
        ModuleReference.Holding("invoices", "payer_id", ReferenceTarget.Record,
            "счета этого плательщика", Invoice("id")),
        ModuleReference.Holding("invoices", "document_type_id", ReferenceTarget.DocumentType,
            "счета этого типа", Invoice("id")),

        // Поля заказчика: тип счёта расширяется схемой, и поле-ссылку в него дописать можно (ТЗ
        // COST-4). Целью может оказаться что угодно, поэтому вид не назван.
        ModuleReference.Holding("invoices", "data", target: null,
            "счета, где запись выбрана в дополнительном поле"),

        // Автор счёта — справка, а не связь: удаление учётной записи держателей не спрашивает и
        // спрашивать не должно — иначе сотрудника, заводившего счета, нельзя было бы убрать никогда,
        // а освободить колонку нечем. Объявить её держащей значило бы обещать то, чего система не
        // делает (ревью PR #1188).
        ModuleReference.Remembering("invoices", "created_by", ReferenceTarget.User,
            "кто завёл счёт — справочное поле; удаление учётной записи счетов не касается"),

        ModuleReference.Holding("invoice_lines", "nomenclature_id", ReferenceTarget.Record,
            "строки счетов с этой позицией номенклатуры", Invoice("invoice_id")),

        ModuleReference.Holding("invoice_allocations", "construction_id", ReferenceTarget.Construction,
            "части разноски счетов на эту стройку", Invoice("invoice_id")),
        ModuleReference.Holding("invoice_allocations", "section_id", ReferenceTarget.Section,
            "части разноски счетов на этот раздел", Invoice("invoice_id")),
        ModuleReference.Holding("invoice_allocations", "article_id", ReferenceTarget.Record,
            "части разноски счетов на эту статью", Invoice("invoice_id")),
    ];
}
