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

    /// <summary>Чем назвать накладную — под её правом чтения, а не правом на счета (COST-29).</summary>
    private static ReferenceDocument Waybill(string via) =>
        new("waybills", "number", via, "накладные", "costs.waybill.read");

    public static IReadOnlyList<ModuleReference> All =>
    [
        // Накладные (D1, issue #1083).
        ModuleReference.Holding("waybills", "construction_id", ReferenceTarget.Construction,
            "накладные на эту стройку", Waybill("id")),
        ModuleReference.Holding("waybill_lines", "nomenclature_id", ReferenceTarget.Record,
            "строки накладных с этой позицией номенклатуры", Waybill("waybill_id")),
        ModuleReference.Remembering("waybills", "created_by", ReferenceTarget.User,
            "кто завёл накладную — справочное поле; удаление учётной записи накладных не касается"),
        ModuleReference.Remembering("waybills", "posted_by", ReferenceTarget.User,
            "кто провёл накладную — справочное поле; удаление учётной записи накладных не касается"),
        ModuleReference.Remembering("waybill_versions", "content", target: null,
            "версия накладной помнит стройку и позиции номенклатуры такими, какими они были до исправления; " +
            "их держат сама накладная и её строки, а не история (issue #1340)"),
        ModuleReference.Remembering("waybill_versions", "author_id", ReferenceTarget.User,
            "кто исправил накладную — справочное поле; удаление учётной записи версии не касается"),

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
        // То же с отметившим оплату (C5): кто нажал — справка для разбора, а не связь.
        ModuleReference.Remembering("invoices", "paid_by", ReferenceTarget.User,
            "кто отметил оплату — справочное поле; удаление учётной записи счетов не касается"),

        // Запись о распознавании скана (issue #1077). Идентификатор в ней один — фоновой задачи, и
        // он справка: задачи ядро чистит само и держателей не спрашивает. Колонки JSON — текст,
        // прочитанный со скана, как напечатано; идентификаторов записей ядра в них не бывает.
        ModuleReference.Remembering("invoice_recognitions", "job_id", target: null,
            "фоновая задача распознавания — справка о ходе; задачи ядро убирает само, счёт от неё не зависит"),
        ModuleReference.Remembering("invoice_recognitions", "values", target: null,
            "прочитанное в шапке файла счёта — текст как напечатано; идентификаторов записей в нём нет"),
        ModuleReference.Remembering("invoice_recognitions", "offers", target: null,
            "прочитанное, но не записанное в поле, — текст из файла счёта; идентификаторов записей в нём нет"),
        ModuleReference.Remembering("invoice_recognitions", "lines", target: null,
            "распознанные строки, не добавленные в счёт, — текст из файла счёта; позиция номенклатуры в них не выбрана"),
        ModuleReference.Remembering("invoice_recognitions", "unplaced", target: null,
            "прочитанное, что в счёт не легло, с причиной — ключи профиля и текст из файла; идентификаторов записей в нём нет"),

        ModuleReference.Holding("invoice_lines", "nomenclature_id", ReferenceTarget.Record,
            "строки счетов с этой позицией номенклатуры", Invoice("invoice_id")),

        // Пометка «(запомнено)» (C3, issue #1079) — идентификатор соответствия, записи модуля, а не ядра.
        ModuleReference.Remembering("invoice_lines", "matched_by", target: null,
            "соответствие, из которого подставлена позиция строки, — запись самого модуля; записей ядра в ней нет"),

        // Соответствия наименований поставщика (C3, issue #1079).
        //
        // ДЕРЖАТ и поставщика, и позицию (решение владельца продукта от 09.10.2026): «помнящая» колонка
        // при слиянии дублей номенклатуры даёт тихую смерть соответствий. Снимает держателя список
        // соответствий на странице счетов — «Забыть» либо «Сменить позицию»; слова отказа туда и ведут.
        // Документа-держателя нет: соответствие — не документ, и назвать его номером нечем.
        ModuleReference.Holding("supplier_matches", "supplier_id", ReferenceTarget.Record,
            "запомненные соответствия наименований этого поставщика (их забывают в списке «Соответствия» на странице счетов)"),
        ModuleReference.Holding("supplier_matches", "nomenclature_id", ReferenceTarget.Record,
            "запомненные соответствия поставщиков, ведущие на эту позицию (их меняют и забывают в списке «Соответствия» на странице счетов)"),
        ModuleReference.Remembering("supplier_matches", "updated_by", ReferenceTarget.User,
            "кто запомнил соответствие — справочное поле; удаление учётной записи соответствий не касается"),

        // Условия поставщика (C4, issue #1080). Держат поставщика — как соответствия: условия удалённой
        // записи — мусор, который никто не увидит, а снять их можно в том же окне, где их завели.
        ModuleReference.Holding("supplier_terms", "supplier_id", ReferenceTarget.Record,
            "условия оплаты этого поставщика (их снимают в окне «Условия поставщиков» на странице счетов)"),
        ModuleReference.Remembering("supplier_terms", "updated_by", ReferenceTarget.User,
            "кто записал условия поставщика — справочное поле; удаление учётной записи условий не касается"),

        ModuleReference.Holding("invoice_allocations", "construction_id", ReferenceTarget.Construction,
            "части разноски счетов на эту стройку", Invoice("invoice_id")),
        ModuleReference.Holding("invoice_allocations", "section_id", ReferenceTarget.Section,
            "части разноски счетов на этот раздел", Invoice("invoice_id")),
        ModuleReference.Holding("invoice_allocations", "article_id", ReferenceTarget.Record,
            "части разноски счетов на эту статью", Invoice("invoice_id")),

        // Книга проводок счёта (E2, issue #1100) — история: она только дописывается, и держи она стройку,
        // статью или поставщика, удалить их не дала бы уже никакая правка счёта. Держит запись доля
        // разноски — пока деньги на ней; книга помнит, куда они легли на момент записи.
        ModuleReference.Remembering("invoice_book_entries", "author_id", ReferenceTarget.User,
            "кто сделал запись книги — справочное поле; удаление учётной записи книги не касается"),
        ModuleReference.Remembering("invoice_book_lines", "construction_id", ReferenceTarget.Construction,
            "книга проводок помнит стройку, на которую легли деньги; её держит доля разноски, а не история"),
        ModuleReference.Remembering("invoice_book_lines", "section_id", ReferenceTarget.Section,
            "книга проводок помнит раздел, на который легли деньги; его держит доля разноски, а не история"),
        ModuleReference.Remembering("invoice_book_lines", "article_id", ReferenceTarget.Record,
            "книга проводок помнит статью, на которую легли деньги; её держит доля разноски, а не история"),
        ModuleReference.Remembering("invoice_book_lines", "supplier_id", ReferenceTarget.Record,
            "книга проводок помнит поставщика счёта на момент записи; его держит сам счёт, а не история"),
        ModuleReference.Remembering("invoice_versions", "content", target: null,
            "версия счёта помнит позиции, стройки, статьи и стороны такими, какими они были до исправления; " +
            "их держат строки и разноска счёта, а не история (E2, issue #1100)"),
        ModuleReference.Remembering("invoice_versions", "author_id", ReferenceTarget.User,
            "кто исправил счёт — справочное поле; удаление учётной записи версии не касается"),
    ];
}
