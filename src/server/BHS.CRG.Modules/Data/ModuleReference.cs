namespace BHS.CRG.Modules.Data;

/// <summary>На что ссылается колонка модуля — вид записи ядра (ТЗ CORE-34.1).</summary>
/// <remarks>
/// Обнаружению держателей вид цели НЕ нужен: скан ищет идентификатор, а они у ядра не пересекаются.
/// Нужен он обратному опросу — «существует ли цель» — потому что искать цель надо в своей таблице
/// ядра для каждого вида.
/// </remarks>
public enum ReferenceTarget
{
    /// <summary>Запись общих данных или документ — <c>domain_objects</c>.</summary>
    Record,
    Construction,
    Section,
    DocumentSet,
    DocumentType,
    WorkPlanItem,
    /// <summary>Учётная запись. Путями, которые спрашивают держателей, не удаляется (CORE-7).</summary>
    User,
}

/// <summary>Чем назвать документ-держатель в отказе: «счета № 12, 15».</summary>
/// <param name="Table">Таблица документа в схеме модуля: <c>invoices</c>.</param>
/// <param name="LabelColumn">Колонка с тем, что человек узнает: номер счёта.</param>
/// <param name="Via">Колонка ТАБЛИЦЫ ССЫЛКИ, в которой лежит ключ документа: <c>invoice_id</c> у строки
/// счёта, <c>id</c> — если ссылка стоит в самом документе.</param>
/// <param name="Noun">Как назвать документы в перечне, во множественном числе: «счета».</param>
/// <param name="Permission">Право, без которого названия не показываются (ТЗ STG-6): число держателей
/// видно всем, а номер счёта — уже содержимое модуля.</param>
/// <param name="Key">Колонка-ключ таблицы документа.</param>
public sealed record ReferenceDocument(
    string Table, string LabelColumn, string Via, string Noun, string Permission, string Key = "id");

/// <summary>
/// Объявление модуля о колонке, в которой лежит идентификатор записи ядра (ТЗ CORE-34.1, задача G2,
/// issue #1094).
///
/// <para><b>Объявление — не способ обнаружения.</b> Держателей записи находит скан схемы базы: держит
/// любая колонка модуля, где встречается идентификатор. Объявление даёт то, чего скан дать не может, —
/// слова («строки счетов: 40», номера счетов), и единственное, чем модуль может колонку ОСВОБОДИТЬ:
/// сказать, что она помнит, но не держит. Колонка, о которой модуль промолчал, держит — и отказ
/// называет её так, как видит база.</para>
///
/// <para>Так сделано потому, что список, выписанный руками, расходится с данными молча и в сторону
/// потери: схема выключенного модуля отстаёт от его кода, и объявление под нынешнее имя колонки
/// прежнего не нашло бы.</para>
/// </summary>
/// <param name="Table">Таблица в схеме модуля.</param>
/// <param name="Column">Колонка: идентификатор, массив идентификаторов или JSON.</param>
/// <param name="Holds">Держит ли колонка запись от удаления.</param>
/// <param name="Target">Вид цели. У колонки JSON, где цели разные, — <c>null</c>.</param>
/// <param name="What">Что держит, словами и во множественном числе: «строки счетов». У не держащей
/// колонки — ПРИЧИНА, по которой она не держит.</param>
/// <param name="Document">Чем назвать документ-держатель; <c>null</c> — только число.</param>
public sealed record ModuleReference(
    string Table, string Column, bool Holds, ReferenceTarget? Target, string What, ReferenceDocument? Document)
{
    /// <summary>Колонка держит запись: пока в ней стоит идентификатор, запись ядра удалить нельзя.</summary>
    public static ModuleReference Holding(
        string table, string column, ReferenceTarget? target, string what, ReferenceDocument? document = null) =>
        new(table, column, Holds: true, target, what, document);

    /// <summary>
    /// Колонка помнит идентификатор, но удалению не мешает: история, снимок распознанного. Причина
    /// обязательна — «не держит» без объяснения неотличимо от забытой ссылки.
    /// </summary>
    public static ModuleReference Remembering(string table, string column, ReferenceTarget? target, string reason) =>
        new(table, column, Holds: false, target, reason, Document: null);
}
