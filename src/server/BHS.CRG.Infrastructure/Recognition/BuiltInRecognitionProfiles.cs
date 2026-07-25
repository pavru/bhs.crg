using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Domain.Schema;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>Стабильные коды встроенных профилей — ключи ре-сидинга, в БД не переименовываются.</summary>
public static class BuiltInProfileCodes
{
    public const string TitleBlock = "titleblock";
    public const string CoverTitle = "cover-title";
    public const string InvoiceHeader = "invoice-header";
    public const string InvoiceLineItems = "invoice-lineitems";
    public const string SpecificationTable = "spec-table";
    public const string CableJournal = "cable-journal";
}

/// <summary>
/// Встроенные профили распознавания (issue #406) — покрывают функционал текущей версии. Собираются
/// ИЗ СУЩЕСТВУЮЩИХ КОНСТАНТ (<see cref="GostTitleBlockFields"/>, <see cref="GostCoverTitleFields"/>,
/// <see cref="InvoiceFields"/>, <see cref="GostTableFields"/>), а не переписываются рядом — поэтому
/// сидированный профиль даёт побуквенно тот же промпт, что и до переезда, и разойтись они не могут.
///
/// Служебные поля в профили НЕ входят и остаются в коде, потому что это не параметры пользователя,
/// а внутренняя маршрутизация: классификаторы <c>ТипСтраницы</c>/<c>Форма</c> (выбор источника и
/// границы документа) и синтетические поля-массивы <c>Строки</c>/<c>Товары</c> (форма ответа модели).
/// Они подмешиваются к полям профиля при композиции вызова.
/// </summary>
public static class BuiltInRecognitionProfiles
{
    /// <summary>Поля штампа, на которые завязан код ниже по течению: <c>НаименованиеДокумента</c> —
    /// авто-тэггер таблиц (<c>GostDocumentTagger</c>), проекция «Документы» и склейка продолжений;
    /// <c>Шифр</c> — определение границы документа при группировке (<c>GostPageGrouper</c>).
    /// Их удаление/переименование молча ломает разбиение альбома, поэтому они системные.</summary>
    private static readonly HashSet<string> TitleBlockSystemFields = ["Шифр", "НаименованиеДокумента"];

    public record Definition(
        string Code,
        string Name,
        RecognitionProfileKind Kind,
        IReadOnlyList<RecognitionProfileField> Fields,
        RecognitionTableShape? Shape = null);

    public static IReadOnlyList<Definition> All =>
    [
        new(BuiltInProfileCodes.TitleBlock, "Штамп ГОСТ (основная надпись)", RecognitionProfileKind.TitleBlock,
            Map(GostTitleBlockFields.All, TitleBlockSystemFields)),

        new(BuiltInProfileCodes.CoverTitle, "Обложка / титульный лист", RecognitionProfileKind.CoverTitle,
            Map(GostCoverTitleFields.All)),

        new(BuiltInProfileCodes.InvoiceHeader, "Счёт на оплату: шапка", RecognitionProfileKind.InvoiceHeader,
            Map(InvoiceFields.HeaderFields)),

        new(BuiltInProfileCodes.InvoiceLineItems, "Счёт на оплату: товары", RecognitionProfileKind.InvoiceLineItems,
            Map(InvoiceFields.LineItemColumns)),

        new(BuiltInProfileCodes.SpecificationTable, "Спецификация / ведомость материалов", RecognitionProfileKind.Table,
            Map(GostTableFields.SpecificationColumns), new RecognitionTableShape()),

        new(BuiltInProfileCodes.CableJournal, "Кабельный журнал", RecognitionProfileKind.CableJournal,
            Map(GostTableFields.CableJournalColumns), new RecognitionTableShape()),
    ];

    /// <summary>Код встроенного табличного профиля по функциональному тэгу документа — замена
    /// <see cref="GostTableFields.ColumnsForTag"/> в новой цепочке приоритета. null — тэг не табличный.</summary>
    public static string? CodeForTag(string tag) => tag switch
    {
        FunctionalTag.GostDocSpecification => BuiltInProfileCodes.SpecificationTable,
        FunctionalTag.GostDocCableJournal => BuiltInProfileCodes.CableJournal,
        _ => null,
    };

    private static IReadOnlyList<RecognitionProfileField> Map(
        IReadOnlyList<RecognitionField> fields, HashSet<string>? systemFields = null) =>
        [.. fields.Select(f => new RecognitionProfileField(
            f.Path, f.Title, f.Type, f.Options, systemFields?.Contains(f.Path) ?? false))];
}
