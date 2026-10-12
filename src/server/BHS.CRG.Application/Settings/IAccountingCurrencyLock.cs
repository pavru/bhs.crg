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

    /// <summary>Несёт ли копия документы с суммами — строки объявленных учётных таблиц.</summary>
    bool Carries(IEnumerable<BackupModuleSchema> data);
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
