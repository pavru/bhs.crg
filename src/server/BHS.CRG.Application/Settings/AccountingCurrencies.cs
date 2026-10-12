namespace BHS.CRG.Application.Settings;

/// <summary>Валюта, в которой экземпляр может вести учёт.</summary>
/// <param name="Code">Код ISO 4217 — его хранит настройка и по нему клиент рисует знак.</param>
/// <param name="Title">Название для списка выбора.</param>
public sealed record AccountingCurrency(string Code, string Title);

/// <summary>
/// Валюты учёта (ТЗ COST-7.2, issue #1248): одна на экземпляр, по умолчанию рубль, многовалютного
/// учёта нет.
///
/// <para>⚠️ <b>Список закрытый, и это не скупость.</b> Код уходит клиенту прямо в
/// <c>Intl.NumberFormat</c>, а тот на строке не по форме бросает исключение — на каждом экране с
/// суммой сразу. Свободный текст в этой настройке означал бы, что одна опечатка администратора
/// кладёт весь учёт. Состав — решение владельца от 12.10.2026.</para>
///
/// <para>Знаков здесь нет нарочно: «₽», «Br», «₸» рисует клиент по коду и языку форматирования.
/// Вторая таблица знаков на сервере расходилась бы с первой молча.</para>
/// </summary>
public static class AccountingCurrencies
{
    /// <summary>Умолчание: настройку никто не задавал — учёт в рублях, как было до неё.</summary>
    public const string Default = "RUB";

    public static IReadOnlyList<AccountingCurrency> All { get; } =
    [
        new("RUB", "Российский рубль"),
        new("BYN", "Белорусский рубль"),
        new("KZT", "Казахстанский тенге"),
        new("USD", "Доллар США"),
        new("EUR", "Евро"),
        new("CNY", "Китайский юань"),
    ];

    /// <summary>Есть ли код в списке. Регистр значим: хранится и сравнивается одна запись.</summary>
    public static bool IsKnown(string? code) =>
        code is not null && All.Any(c => string.Equals(c.Code, code, StringComparison.Ordinal));

    /// <summary>
    /// Действующая валюта по сохранённой строке. Нет строки или её нет в списке — рубль: негодное
    /// значение может лежать в базе после правки руками, и ронять из-за него каждый экран нельзя.
    /// </summary>
    public static string Effective(string? stored) => IsKnown(stored) ? stored! : Default;

    /// <summary>Валюта словами — для журнала действий и текстов: «Российский рубль (RUB)».</summary>
    public static string Display(string code) =>
        All.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.Ordinal)) is { } known
            ? $"{known.Title} ({known.Code})"
            : code;
}
