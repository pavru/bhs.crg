using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Recognition;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>К чему применяется вид профиля: к НАБОРУ целиком (штамп/обложка/счёт — они читают весь
/// файл) или к отдельной ГРУППЕ ЛИСТОВ (таблицы документа). Определяет, куда профиль вообще можно
/// привязать; «есть ли табличная часть» для этого не годится — у счёта она есть, но привязывается он
/// к файлу.</summary>
public enum RecognitionProfileScope { File = 1, PageGroup = 2 }

/// <summary>
/// Что вид профиля означает ДЛЯ КОДА (issue #406) — реестр в духе <c>TagRegistry</c>. Без него
/// проверки вида (`if (kind == Table)`) расползлись бы по сервисам и фронту, и вид перестал бы быть
/// абстракцией: UI и валидация должны читать дескриптор, а не знать частные случаи.
/// </summary>
/// <param name="RowsKey">Ключ, под которым модель возвращает массив строк табличной части;
/// null — табличной части у вида нет. Это форма ответа, а не параметр пользователя.</param>
/// <param name="SupportsShape">Применимы ли структурные флаги формы таблицы.</param>
/// <param name="HasScalarFields">Есть ли у вида скалярные поля (у чисто табличных — нет).</param>
/// <param name="SystemFieldNames">Поля, на которых завязан код ниже по течению: удалять и
/// переименовывать нельзя. Живут ЗДЕСЬ, а не в пользовательском jsonb, — иначе признак снимался бы
/// через импорт/восстановление бэкапа и защита обходилась бы.</param>
/// <param name="ListRowColumnsAsFields">Перечислять ли колонки строк ещё и отдельными полями промпта.
/// Унаследованная асимметрия существующих промптов, сохранена дословно: у таблиц документа колонки
/// печатаются ДВАЖДЫ (по одной + склейкой в описании массива), у счёта — только склейкой в описании
/// «Товары». Менять здесь = менять промпт, т.е. качество распознавания; отдельное решение.</param>
/// <param name="Label">Человекочитаемое имя вида — для выбора при создании профиля.</param>
/// <param name="Scope">Куда привязывается профиль этого вида: к набору или к группе листов.</param>
public record RecognitionKindDescriptor(
    RecognitionProfileKind Kind,
    string? RowsKey,
    bool SupportsShape,
    bool HasScalarFields,
    IReadOnlyList<string> SystemFieldNames,
    bool ListRowColumnsAsFields = false,
    string Label = "",
    RecognitionProfileScope Scope = RecognitionProfileScope.File);

public static class RecognitionKinds
{
    /// <summary>Графы штампа, на которых держится разбиение альбома: <c>НаименованиеДокумента</c> —
    /// авто-тэггер таблиц (<c>GostDocumentTagger</c>), имя группы и проекция «Документы»;
    /// <c>Шифр</c> — определение границы документа (<c>GostPageGrouper</c>).</summary>
    private static readonly string[] TitleBlockSystemFields = ["Шифр", "НаименованиеДокумента"];

    private static readonly Dictionary<RecognitionProfileKind, RecognitionKindDescriptor> ByKind = new()
    {
        [RecognitionProfileKind.TitleBlock] = new(
            RecognitionProfileKind.TitleBlock, RowsKey: null,
            SupportsShape: false, HasScalarFields: true, TitleBlockSystemFields,
            Label: "Штамп чертежа (основная надпись)"),

        [RecognitionProfileKind.CoverTitle] = new(
            RecognitionProfileKind.CoverTitle, RowsKey: null,
            SupportsShape: false, HasScalarFields: true, [], Label: "Обложка / титульный лист"),

        [RecognitionProfileKind.Invoice] = new(
            RecognitionProfileKind.Invoice, RowsKey: InvoiceFields.LineItemsPath,
            SupportsShape: false, HasScalarFields: true, [], Label: "Счёт на оплату"),

        [RecognitionProfileKind.Table] = new(
            RecognitionProfileKind.Table, RowsKey: GostTableFields.RowsPath,
            SupportsShape: true, HasScalarFields: false, [], ListRowColumnsAsFields: true,
            Label: "Таблица документа",
            Scope: RecognitionProfileScope.PageGroup),

        [RecognitionProfileKind.CableJournal] = new(
            RecognitionProfileKind.CableJournal, RowsKey: GostTableFields.RowsPath,
            SupportsShape: true, HasScalarFields: false, [], ListRowColumnsAsFields: true,
            Label: "Кабельный журнал",
            Scope: RecognitionProfileScope.PageGroup),
    };

    public static RecognitionKindDescriptor Describe(RecognitionProfileKind kind) =>
        ByKind.TryGetValue(kind, out var d) ? d
            : throw new InvalidOperationException($"Вид профиля распознавания {kind} не описан в реестре.");

    public static IReadOnlyCollection<RecognitionKindDescriptor> All => ByKind.Values;

    /// <summary>Системное ли поле — вычисляется по виду, не читается из данных профиля.</summary>
    public static bool IsSystemField(RecognitionProfileKind kind, string fieldName)
        => Describe(kind).SystemFieldNames.Contains(fieldName);

    /// <summary>Табличные виды — те, у которых распознаётся массив строк.</summary>
    public static bool IsTabular(RecognitionProfileKind kind) => Describe(kind).RowsKey is not null;

    /// <summary>Привязывается ли вид к группе листов (а не к набору целиком).</summary>
    public static bool IsGroupScoped(RecognitionProfileKind kind)
        => Describe(kind).Scope == RecognitionProfileScope.PageGroup;

    /// <summary>Собирает полный список полей ОДНОГО вызова распознавания: скаляры профиля +
    /// синтетическое поле-массив под строки (если вид табличный). Само поле-массив в профиль не
    /// входит — это форма ответа модели.</summary>
    public static IReadOnlyList<RecognitionField> ComposeCallFields(ResolvedRecognitionProfile profile)
        => ComposeCallFields(profile.Kind, profile.ToRecognitionFields(), profile.ToRowColumns());

    /// <summary>То же с явными колонками — табличные колонки могут приходить не из профиля, а из типа
    /// документа, объявившего тэг (механизм #29), при том же виде и той же форме вызова.</summary>
    public static IReadOnlyList<RecognitionField> ComposeCallFields(
        RecognitionProfileKind kind,
        IReadOnlyList<RecognitionField> scalars,
        IReadOnlyList<RecognitionField> rowColumns)
    {
        var descriptor = Describe(kind);
        if (descriptor.RowsKey is null) return scalars;
        return
        [
            .. scalars,
            .. descriptor.ListRowColumnsAsFields ? rowColumns : [],
            new(descriptor.RowsKey, RowsFieldTitle(descriptor.RowsKey, rowColumns), "json-array"),
        ];
    }

    /// <summary>Описание поля-массива строк. Формулировки сохранены дословно из прежних констант —
    /// от них зависит побайтовое совпадение промпта до/после переезда.</summary>
    private static string RowsFieldTitle(string rowsKey, IReadOnlyList<RecognitionField> rowColumns)
        => rowsKey == InvoiceFields.LineItemsPath
            ? "Таблица товаров/услуг — JSON-массив объектов с полями "
              + string.Join('/', rowColumns.Select(f => f.Path))
            : "Строки таблицы — JSON-массив объектов с полями "
              + string.Join('/', rowColumns.Select(f => f.Path));
}
