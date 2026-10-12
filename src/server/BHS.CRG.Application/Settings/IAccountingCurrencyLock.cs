using BHS.CRG.Application.Backup;

namespace BHS.CRG.Application.Settings;

/// <summary>Сколько документов одного вида держит валюту: «счетов — 12».</summary>
public sealed record CurrencyHeldCount(string Noun, long Count);

/// <summary>Модуль, документы которого держат валюту учёта.</summary>
/// <param name="Enabled">Включён ли модуль: выключенный держит валюту так же, но найти его документы
/// на экране нельзя — причина обязана сказать об этом.</param>
public sealed record CurrencyHolder(string Module, string Title, bool Enabled, IReadOnlyList<CurrencyHeldCount> Counts);

/// <summary>
/// Можно ли сменить валюту учёта (issue #1248). Суммы при смене не пересчитываются: «412 500,00 ₽»
/// молча стало бы «412 500,00 $». Поэтому менять можно, пока документов с суммами нет ни у одного
/// модуля; потом настройка заперта (решение владельца 12.10.2026).
///
/// <para>Интерфейс приложения, а не порт модулей: спрашивает ядро, и отвечает оно же — по
/// объявлениям <c>IAppModule.AccountingDocuments</c>, считая строки само. Спросить модуль нельзя:
/// службы выключенного модуля не зарегистрированы, и он ответил бы тишиной — то есть «документов
/// нет» при счетах, лежащих в его схеме.</para>
/// </summary>
public interface IAccountingCurrencyLock
{
    /// <summary>
    /// Кто держит валюту; пусто — менять можно. Читает в текущей транзакции контекста ядра, если она
    /// открыта. База не ответила — исключение, а не «пусто»: неизвестность смену не разрешает.
    /// </summary>
    Task<IReadOnlyList<CurrencyHolder>> HoldersAsync(CancellationToken ct = default);

    /// <summary>
    /// Сменить действующую валюту — ЕДИНСТВЕННЫЙ путь записи настройки, один на экран и на
    /// восстановление копии (ревью PR #1306): две копии последовательности «сверить, записать,
    /// занести в журнал» расходились — замок был в одной, уборка негодной строки тоже.
    ///
    /// <para>Работает внутри транзакции вызывающего и сам её не фиксирует. Порядок: очередь смен
    /// (две одновременные не пишут обе), замок учётных таблиц от записи, сверка держателей, запись и
    /// журнал. Между сверкой и фиксацией документ появиться не может — иначе счёт, внесённый в
    /// рублях, получил бы новый знак.</para>
    /// </summary>
    /// <param name="next">Код из <see cref="AccountingCurrencies" />.</param>
    /// <param name="note">Приписка к «стало» в журнале: « (восстановление копии)».</param>
    Task<CurrencyChangeOutcome> TryChangeAsync(string next, string? note = null, CancellationToken ct = default);

    /// <summary>
    /// Несёт ли копия документы с суммами, которые восстановление ПОЛОЖИТ в базу: строки учётных
    /// таблиц включённого модуля с тем же именем схемы. Остальные секции оно пропускает.
    /// </summary>
    bool Carries(IEnumerable<BackupModuleSchema> data);
}

/// <summary>Чем кончилась попытка сменить валюту.</summary>
public abstract record CurrencyChangeOutcome
{
    /// <summary>Действует то же, что и было. Негодная строка в базе при этом заменена годной.</summary>
    public sealed record Unchanged : CurrencyChangeOutcome;

    /// <summary>Сменена и записана в журнал.</summary>
    /// <param name="Created">Строки настройки раньше не было.</param>
    public sealed record Changed(SettingChange Change, bool Created) : CurrencyChangeOutcome;

    /// <summary>Не сменена: документы с суммами уже есть.</summary>
    public sealed record Held(SettingChange Change, IReadOnlyList<CurrencyHolder> Holders) : CurrencyChangeOutcome;

    /// <summary>Не сменена: в учётные таблицы пишут либо идёт другая смена — дольше отведённого срока.</summary>
    public sealed record Busy(SettingChange Change) : CurrencyChangeOutcome
    {
        public const string Message =
            "В счета или накладные сейчас идёт запись — валюта не изменена. Повторите через минуту.";
    }
}

/// <summary>Слова о запрете — одни на экран настройки, отказ адреса и восстановление копии.</summary>
public static class AccountingCurrencyLockText
{
    /// <summary>Кто держит валюту, одной фразой без точки: «в модуле «Счета и накладные» — счетов: 12».</summary>
    public static string Holders(IReadOnlyList<CurrencyHolder> holders) =>
        string.Join("; ", holders.Select(h =>
            (h.Enabled ? $"в модуле «{h.Title}»" : $"в данных выключенного модуля «{h.Title}»") + " — " +
            string.Join(", ", h.Counts.Select(c => $"{c.Noun}: {c.Count}"))));

    /// <summary>Причина запрета под полем и в отказе.</summary>
    public static string Reason(IReadOnlyList<CurrencyHolder> holders) =>
        $"Валюту учёта сменить нельзя: документы с суммами уже есть ({Holders(holders)}). Суммы при " +
        "смене не пересчитываются — прежние документы получили бы другой знак при тех же числах.";
}
