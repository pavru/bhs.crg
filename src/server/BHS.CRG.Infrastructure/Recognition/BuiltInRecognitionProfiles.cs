using System.Security.Cryptography;
using System.Text;
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
    public const string Invoice = "invoice";
    public const string SpecificationTable = "spec-table";
    public const string CableJournal = "cable-journal";
}

/// <summary>
/// Встроенные профили распознавания (issue #406) — покрывают функционал текущей версии. Собираются
/// ИЗ СУЩЕСТВУЮЩИХ КОНСТАНТ (<see cref="GostTitleBlockFields"/>, <see cref="GostCoverTitleFields"/>,
/// <see cref="InvoiceFields"/>, <see cref="GostTableFields"/>), а не переписываются рядом — поэтому
/// сидированный профиль даёт побуквенно тот же промпт, что и до переезда, и разойтись они не могут.
///
/// Один профиль = ОДИН вызов распознавания: у счёта шапка и товары лежат в одном профиле
/// (<c>Fields</c> + <c>RowColumns</c>), потому что <c>BuildInvoicePrompt</c> — единый вызов; два вида
/// на один вызов заставили бы код знать, что их надо склеить, и вид перестал бы выбирать стратегию.
///
/// Служебное в профили НЕ входит: классификаторы <c>ТипСтраницы</c>/<c>Форма</c> (внутренняя
/// маршрутизация) и поля-массивы <c>Строки</c>/<c>Товары</c> (форма ответа) подмешиваются кодом.
/// </summary>
public static class BuiltInRecognitionProfiles
{
    public record Definition(
        string Code,
        string Name,
        RecognitionProfileKind Kind,
        IReadOnlyList<RecognitionProfileField> Fields,
        IReadOnlyList<RecognitionProfileField> RowColumns,
        RecognitionTableShape? Shape = null);

    public static IReadOnlyList<Definition> All =>
    [
        new(BuiltInProfileCodes.TitleBlock, "Штамп ГОСТ (основная надпись)", RecognitionProfileKind.TitleBlock,
            Map(GostTitleBlockFields.All), []),

        new(BuiltInProfileCodes.CoverTitle, "Обложка / титульный лист", RecognitionProfileKind.CoverTitle,
            Map(GostCoverTitleFields.All), []),

        new(BuiltInProfileCodes.Invoice, "Счёт на оплату", RecognitionProfileKind.Invoice,
            Map(InvoiceFields.HeaderFields), Map(InvoiceFields.LineItemColumns)),

        new(BuiltInProfileCodes.SpecificationTable, "Спецификация / ведомость материалов", RecognitionProfileKind.Table,
            [], Map(GostTableFields.SpecificationColumns), new RecognitionTableShape()),

        new(BuiltInProfileCodes.CableJournal, "Кабельный журнал", RecognitionProfileKind.CableJournal,
            [], Map(GostTableFields.CableJournalColumns), new RecognitionTableShape()),
    ];

    /// <summary>Код встроенного табличного профиля по функциональному тэгу документа — замена
    /// <see cref="GostTableFields.ColumnsForTag"/> в новой цепочке приоритета. null — тэг не табличный.</summary>
    public static string? CodeForTag(string tag) => tag switch
    {
        FunctionalTag.GostDocSpecification => BuiltInProfileCodes.SpecificationTable,
        FunctionalTag.GostDocCableJournal => BuiltInProfileCodes.CableJournal,
        _ => null,
    };

    /// <summary>Хеш заводского содержимого — по нему сидер понимает, что дефолт ушёл вперёд, пока
    /// пользователь держит свою правку (см. <c>RecognitionProfile.BuiltInHash</c>).</summary>
    public static string HashOf(Definition def)
        => Hash(RecognitionProfileJson.Canonical(def.Fields, def.RowColumns, def.Shape), def.Name);

    /// <summary>Хеш ТЕКУЩЕГО содержимого профиля в БД — по той же рецептуре, что <see cref="HashOf"/>,
    /// поэтому их можно сравнивать напрямую. Нужен, чтобы понять «профиль отличается от заводского»:
    /// сравнивать со СТАРЫМ сохранённым хешем для этого нельзя — он описывает заводскую версию, а не
    /// то, что лежит в строке (после «сбросить к заводским» они как раз расходятся).</summary>
    public static string HashOfCurrent(Domain.Recognition.RecognitionProfile profile)
        => Hash(RecognitionProfileJson.Canonical(
                RecognitionProfileJson.ReadFields(profile.Fields),
                RecognitionProfileJson.ReadFields(profile.RowColumns),
                RecognitionProfileJson.ReadShape(profile.Shape)),
            profile.Name);

    private static string Hash(string canonical, string name)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical + '|' + name)));

    private static IReadOnlyList<RecognitionProfileField> Map(IReadOnlyList<RecognitionField> fields) =>
        [.. fields.Select(f => new RecognitionProfileField(f.Path, f.Title, f.Type, f.Options))];
}
