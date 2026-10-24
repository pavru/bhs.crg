namespace BHS.CRG.Modules.Plan.GrandSmeta;

/// <summary>Разобранная ведомость объёмов работ (ТЗ PLAN-7). Вложения (PDF чертежей) не читаются: в модели
/// только их имена и ссылки на листы. Что делать с ВОР в смете — <c>plan-07</c> (решение В-7).</summary>
public sealed record ParsedVolumeStatement(
    GrandSmetaSource Source,
    VolumeStatementRequisites Requisites,
    IReadOnlyList<VolumeChapter> Chapters,
    IReadOnlyList<VolumeVariable> Variables,
    IReadOnlyList<VolumeHyperlink> Hyperlinks,
    IReadOnlyList<VolumeAttachment> Attachments,
    IReadOnlyList<Unparsed> Unparsed)
{
    public IEnumerable<VolumeWork> Works => Chapters.SelectMany(c => c.Works);

    public int PositionsInFile => Chapters.Sum(c => c.Works.Count) + Unparsed.Count;
}

public sealed record VolumeStatementRequisites(
    string? Number,
    string? Name,
    string? Construction,
    string? Object,
    string? Stage,
    string? Comment,
    DateOnly? CreationDate);

/// <summary>Раздел ведомости. <see cref="Notes" /> — заголовки (<c>Header</c>) и примечания (<c>Comment</c>)
/// раздела по порядку.</summary>
public sealed record VolumeChapter(
    int Index,
    string Caption,
    string? CustomNumber,
    IReadOnlyList<string> Notes,
    IReadOnlyList<VolumeWork> Works);

/// <summary>
/// Работа ведомости: объём — результат (<c>Quantity/@Result</c>), <see cref="Formula" /> — формула текстом, как
/// её записал сметчик (со ссылками на переменные и другие работы), <see cref="CalcRows" /> — строки расчёта.
/// Обоснование — <see cref="Code" />, комментарии сметчика (<see cref="Comment1" />: ссылка на чертежи,
/// <see cref="Comment2" />: дополнительная информация) и ссылки на листы <see cref="Sheets" />.
/// <see cref="Header" /> — заголовок раздела, под которым стоит работа.
/// </summary>
public sealed record VolumeWork(
    int IndexInChapter,
    string Number,
    string? Identifier,
    string Caption,
    string? Code,
    string? Units,
    decimal Quantity,
    string? Formula,
    IReadOnlyList<VolumeCalcRow> CalcRows,
    string? Comment1,
    string? Comment2,
    IReadOnlyList<SheetReference> Sheets,
    string? Header,
    bool IsSubordinate);

/// <summary>Строка расчёта объёма: <see cref="Value" /> — выражение («Г1+Г2+Г3»), <see cref="IsTotal" /> —
/// строка-итог расчёта.</summary>
public sealed record VolumeCalcRow(string? Identifier, string? Caption, string? Units, string? Value, bool IsTotal);

/// <summary>Ссылка на лист обосновывающего документа (<c>SupportingDocs/Item</c>, в 15.3.7 —
/// <c>SupportingDoc</c>): имя файла, адрес, страницы через пробел, комментарий.</summary>
public sealed record SheetReference(string? FileName, string? Url, string? Pages, string? Comment);

public sealed record VolumeVariable(string? Identifier, string? Caption, string? Value);

public sealed record VolumeHyperlink(string? Caption, string? Url);

/// <summary>Вложение документа — только имя и размер; содержимое из ZIP не читается.</summary>
public sealed record VolumeAttachment(string? Guid, string? FileName, long? Size);
