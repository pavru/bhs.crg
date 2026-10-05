using System.Globalization;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;

namespace BHS.CRG.Modules.Costs.Tables;

/// <summary>Учётные месяцы счёта в реестре.</summary>
/// <param name="All">Все месяцы счёта — клетка «Учётный период»: это факт о счёте, отбор его не сужает.</param>
/// <param name="Named">Деньги по месяцам, НАЗВАННЫЕ отбором, — «Сумма» и её расшифровка «Суммы по
/// периодам»: под отбором по объекту — только доли на него, без остатка (остаток не лежит ни на одном
/// объекте), под отбором по учётному периоду — только названные месяцы. Отбор не сужает — то же, что
/// <paramref name="All" />.</param>
internal sealed record InvoiceMonths(IReadOnlyList<PostedMonth> All, IReadOnlyList<PostedMonth> Named);

/// <summary>
/// Учётные месяцы счетов в реестре (задача C5, issue #1082, ТЗ COST-16, COST-20.1).
///
/// <para><b>Клетку считает та же функция, что и форму счёта</b> (<see cref="PaymentPosting.Months" />) —
/// в памяти, из денег счетов (<see cref="InvoiceMoney" />). Деньги доли не хранятся: их даёт арифметика разноски, и пропорция в
/// запросе разошлась бы с ней на копейки округления.</para>
///
/// <para>⚠️ <b>Отбор и сортировка идут по ЗАПИСАННЫМ датам долей, а клетка — по долям с деньгами.</b>
/// Расходятся они на одном случае: у оплаченного счёта есть доля без денег (в строке не вписана цена,
/// разноска ждёт пересчёта) в месяце, куда больше ничего не легло. Такой счёт отбор по этому месяцу
/// найдёт, а в клетке месяца не будет — и «Сумма» под таким отбором у него пуста: в итог он не идёт, и
/// итог называет число счетов, вошедших в него, а не число строк отбора. Посчитать «есть ли у доли
/// деньги» запросом нельзя — по той же причине, по какой нельзя посчитать долю.</para>
/// </summary>
internal static class InvoicePeriods
{
    /// <summary>Как назван месяц вне <see cref="Labels(DateOnly)" /> — раньше первого года перечня.</summary>
    public const string Unknown = "период не определён";

    /// <summary>
    /// С какого года перечень месяцев. Учётная дата — день платежа или первый открытый день после
    /// закрытия, то есть не раньше начала работы системы; год взят с запасом.
    /// </summary>
    private const int FirstYear = 2020;

    public static IReadOnlyDictionary<Guid, InvoiceMonths> None { get; } = new Dictionary<Guid, InvoiceMonths>();

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>
    /// Ключ месяца: 202609. Число, а не дата: сравнивается и сортируется как есть.
    /// ⚠️ Та же формула — в выражении колонки (<c>InvoiceTableRows.Sql</c>): запрос считает ключ в
    /// базе, и вынести выражение в общий метод нельзя — его не перевести. Что обе формулы одна, держит
    /// тест реестра: разойдись они, отбор по месяцу перестал бы находить счёт.
    /// </summary>
    public static int Key(DateOnly date) => date.Year * 100 + date.Month;

    /// <summary>
    /// Названия месяцев по ключам — по ним ядро сверяет условие отбора и подставляет в запрос подошедшие
    /// ключи. Перечень — календарь, а не чтение базы (ревью PR #1192): название месяца — чистая функция
    /// ключа, и два прохода по долям и счетам на КАЖДОЕ чтение таблицы были платой ни за что.
    /// </summary>
    public static IReadOnlyDictionary<int, string> Labels(DateOnly today)
    {
        var labels = new Dictionary<int, string>();
        for (var month = First; month <= Last(today); month = month.AddMonths(1))
            labels[Key(month)] = PaymentViews.Month(month);
        return labels;
    }

    /// <summary>Первый месяц перечня.</summary>
    public static DateOnly First { get; } = new(FirstYear, 1, 1);

    /// <summary>
    /// Последний месяц перечня — декабрь следующего года: учётная дата бывает позже «сегодня» только на
    /// первый открытый день.
    /// </summary>
    public static DateOnly Last(DateOnly today) => new(today.Year + 1, 12, 1);

    /// <summary>Есть ли месяц в перечне — то есть назовёт ли его реестр и найдёт ли по нему отбор.</summary>
    public static bool Covers(DateOnly month, DateOnly today) => month >= First && month <= Last(today);

    /// <summary>
    /// Как месяц назван ОТБОРУ: названием из перечня, а вне перечня — <see cref="Unknown" />. То же
    /// название ядро сверяет с условием, отбирая счета, — иначе счёт с месяцем вне календаря отбор
    /// нашёл бы, а деньги его под тем же условием не подошли бы.
    /// </summary>
    public static string Label(IReadOnlyDictionary<int, string> labels, DateOnly day) =>
        labels.TryGetValue(Key(day), out var label) ? label : Unknown;

    /// <summary>
    /// Учётные месяцы счёта из его денег. «Названные» складывают РОВНО те деньги, что и клетка «Сумма»
    /// под сужающим отбором (<see cref="InvoiceMoney.Named" />): число и его расшифровка разойтись не
    /// могут — и строки у них одни, и вопрос «названо ли» один.
    /// </summary>
    /// <param name="named">Какие деньги отбор назвал (<see cref="InvoiceMoney.IsNamed" />); null — отбор
    /// не сужает.</param>
    public static InvoiceMonths Of(IReadOnlyList<PostedMoney> money, Func<PostedMoney, bool>? named)
    {
        var all = PaymentPosting.Months(money);
        return new(all, named is null ? all : PaymentPosting.Months(money.Where(named)));
    }

    /// <summary>Клетка «Учётный период»: месяцы по возрастанию.</summary>
    public static IReadOnlyList<string> Cell(IReadOnlyList<PostedMonth> months) =>
        [.. months.Select(m => PaymentViews.Month(m.Month))];

    /// <summary>
    /// Клетка «Суммы по периодам»: «40 000,00 (09.2026) + 60 000,00 (10.2026)»; пусто — счёт не оплачен
    /// либо отбор не назвал ни одного его месяца.
    /// </summary>
    public static string? Sums(IReadOnlyList<PostedMonth> months) => months.Count == 0
        ? null
        : string.Join(" + ", months.Select(m => $"{m.Amount.ToString("N2", Russian)} ({PaymentViews.Month(m.Month)})"));
}
