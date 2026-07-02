using BHS.CRG.Application.QualityDocs;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Профиль распознавания «Счёт на оплату» — один многостраничный PDF = один документ, не реестр
/// по страницам (в отличие от <see cref="GostTitleBlockFields"/>). Не ГОСТ — строгого стандарта
/// формы счёта нет, это обычная деловая конвенция (проверено с пользователем).
/// </summary>
public static class InvoiceFields
{
    public static readonly IReadOnlyList<RecognitionField> HeaderFields =
    [
        new("НомерСчёта", "Номер счёта", "string"),
        new("ДатаСчёта", "Дата счёта", "string"),
        new("Поставщик", "Поставщик (исполнитель)", "string"),
        new("ИННПоставщика", "ИНН поставщика", "string"),
        new("Плательщик", "Плательщик (заказчик)", "string"),
        new("ИННПлательщика", "ИНН плательщика", "string"),
        new("Основание", "Основание (договор/назначение платежа)", "string"),
        new("СуммаКОплате", "Сумма к оплате (итого)", "string"),
        new("ВТомЧислеНДС", "В том числе НДС", "string"),
    ];

    public static readonly IReadOnlyList<RecognitionField> LineItemColumns =
    [
        new("Наименование", "Наименование товара/услуги", "string"),
        new("ЕдиницаИзмерения", "Единица измерения", "string"),
        new("Количество", "Количество", "string"),
        new("Цена", "Цена за единицу", "string"),
        new("Сумма", "Сумма по строке", "string"),
    ];

    /// <summary>JSON-ключ, под которым распознаватель должен вернуть таблицу товаров (JSON-массив).</summary>
    public const string LineItemsPath = "Товары";

    /// <summary>Все поля для одного вызова распознавания — шапка + таблица товаров одним полем.</summary>
    public static readonly IReadOnlyList<RecognitionField> All =
    [
        .. HeaderFields,
        new(LineItemsPath,
            "Таблица товаров/услуг — JSON-массив объектов с полями "
            + string.Join('/', LineItemColumns.Select(f => f.Path)),
            "json-array"),
    ];
}
