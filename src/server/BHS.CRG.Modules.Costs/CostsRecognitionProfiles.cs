using BHS.CRG.Modules.Recognition;

namespace BHS.CRG.Modules.Costs;

/// <summary>
/// Заводские профили распознавания модуля счетов (ТЗ CORE-Q6, issue #1077).
///
/// <para>«Счёт на оплату» до этой задачи объявляло ЯДРО — отступление от ТЗ, принятое, пока у модуля
/// не было потребителя. Теперь потребитель есть: путь «скан → черновик счёта». Решение владельца
/// продукта 08.10.2026 — профиль живёт здесь, как в ТЗ.</para>
///
/// <para>⚠️ Следствие, о котором знали заранее: на установке без модуля счетов профиль не
/// предлагается и в наборах данных — PDF-источник «Счёт на оплату» читает тем же видом, а вид
/// доступен, пока включён его владелец. Заведённые наборы-счета остаются на месте; перераспознать их
/// можно, включив модуль.</para>
///
/// <para>⚠️ Ключи и подсказки перенесены ДОСЛОВНО: запрос к модели печатается из них, а по хешу
/// содержимого ядро решает, ушёл ли заводской вариант вперёд. Изменись здесь буква — у каждого, кто
/// профиль правил, зажглось бы «заводской профиль обновился», хотя переезд содержимого не менял.</para>
///
/// <para>Один профиль — ОДИН вызов распознавания: шапка и товары лежат в одном профиле (поля и
/// колонки), потому что запрос к модели единый. Поле-массив, которым модель возвращает таблицу, в
/// профиль не входит: это форма ответа, её подмешивает ядро.</para>
/// </summary>
public static class CostsRecognitionProfiles
{
    /// <summary>Стабильный код — ключ строки в базе, не переименовывается.</summary>
    public const string InvoiceCode = "invoice";

    // Ключи полей — константами: по ним обработчик раскладывает ответ в реквизиты счёта. Профиль
    // правит администратор, и ключа в ответе может не оказаться — это «не спрашивали», а не «пусто».
    public const string Number = "НомерСчёта";
    public const string Date = "ДатаСчёта";
    public const string Supplier = "Поставщик";
    public const string SupplierTaxId = "ИННПоставщика";
    public const string Payer = "Плательщик";
    public const string PayerTaxId = "ИННПлательщика";
    public const string Basis = "Основание";
    public const string Total = "СуммаКОплате";
    public const string VatTotal = "ВТомЧислеНДС";

    public const string LineName = "Наименование";
    public const string LineUnit = "ЕдиницаИзмерения";
    public const string LineQuantity = "Количество";
    public const string LinePrice = "Цена";
    public const string LineAmount = "Сумма";

    public static readonly IReadOnlyList<ModuleRecognitionField> InvoiceHeader =
    [
        new(Number, "Номер счёта"),
        new(Date, "Дата счёта"),
        new(Supplier, "Поставщик (исполнитель)"),
        new(SupplierTaxId, "ИНН поставщика"),
        new(Payer, "Плательщик (заказчик)"),
        new(PayerTaxId, "ИНН плательщика"),
        new(Basis, "Основание (договор/назначение платежа)"),
        new(Total, "Сумма к оплате (итого)"),
        new(VatTotal, "В том числе НДС"),
    ];

    public static readonly IReadOnlyList<ModuleRecognitionField> InvoiceLines =
    [
        new(LineName, "Наименование товара/услуги"),
        new(LineUnit, "Единица измерения"),
        new(LineQuantity, "Количество"),
        new(LinePrice, "Цена за единицу"),
        new(LineAmount, "Сумма по строке"),
    ];

    public static IReadOnlyList<ModuleRecognitionProfile> All =>
    [
        new(InvoiceCode, "Счёт на оплату", "Invoice", InvoiceHeader, InvoiceLines),
    ];
}
