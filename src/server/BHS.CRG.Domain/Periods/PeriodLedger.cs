using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Periods;

/// <summary>
/// Границы закрытия, сведённые из записей (ТЗ CORE-35; задача E1a, issue #1081): до какой даты
/// закрыта компания и каждая стройка, что можно закрыть следующим и что — отменить.
///
/// <para>Формула границы живёт ЗДЕСЬ и только здесь: служба закрытия, адрес <c>/api/periods</c> и
/// порт модулей считают ею. Заведи клиент или модуль свою — «закрыто» у них разошлось бы на день, и
/// именно на границе месяца.</para>
///
/// <para><b>Действующая граница стройки — позднейшая из двух</b>, своей и компании: закрытие
/// компании закрывает все стройки. Поэтому следующее закрытие стройки начинается после действующей
/// границы, а не после своей — иначе запись стройки заново «закрывала» бы закрытое компанией.</para>
///
/// <para><b>Порядок записей не нужен.</b> Действующие закрытия контура — те, что не названы ни одной
/// отменой; каждое следующее обязано уходить дальше действующей границы, а отменить можно только
/// последнее, так что среди действующих граница — попросту наибольшая. Считать «стопкой по времени»
/// значило бы доверить результат часам сервера и порядку строк после восстановления копии.</para>
/// </summary>
public sealed class PeriodLedger
{
    private readonly Dictionary<PeriodContour, PeriodClosure> _last;

    private PeriodLedger(Dictionary<PeriodContour, PeriodClosure> last) => _last = last;

    public static PeriodLedger Empty { get; } = new([]);

    public static PeriodLedger From(IEnumerable<PeriodClosure> rows)
    {
        var all = rows as IReadOnlyCollection<PeriodClosure> ?? [.. rows];
        var cancelled = all
            .Where(r => r is { Kind: PeriodClosureKind.Reopen, CancelsId: not null })
            .Select(r => r.CancelsId!.Value)
            .ToHashSet();

        var last = all
            .Where(r => r.Kind == PeriodClosureKind.Close && !cancelled.Contains(r.Id))
            .GroupBy(r => r.ContourRef)
            .ToDictionary(g => g.Key, g => g.MaxBy(r => r.Through)!);

        return new(last);
    }

    /// <summary>Граница компании: закрыто всё по эту дату включительно. <c>null</c> — не закрыто ничего.</summary>
    public DateOnly? Company => Own(PeriodContour.Company);

    /// <summary>Стройки, у которых есть СВОЁ действующее закрытие.</summary>
    public IEnumerable<Guid> ConstructionsWithOwn => _last.Keys
        .Where(c => c.Kind == PeriodContourKind.Construction)
        .Select(c => c.ConstructionId!.Value);

    /// <summary>Собственная граница контура — без оглядки на компанию.</summary>
    public DateOnly? Own(PeriodContour contour) =>
        _last.TryGetValue(contour, out var row) ? row.Through : null;

    /// <summary>Действующая граница: у стройки — позднейшая из своей и границы компании.</summary>
    public DateOnly? ClosedThrough(PeriodContour contour)
    {
        var own = Own(contour);
        if (contour.Kind == PeriodContourKind.Company) return own;

        var company = Company;
        if (own is null) return company;
        return company is null || own > company ? own : company;
    }

    /// <summary>Закрыт ли день. «По дату включительно» — условие, которое каждый второй пишет со сдвигом.</summary>
    public bool IsClosed(DateOnly date, PeriodContour contour) =>
        ClosedThrough(contour) is { } through && date <= through;

    /// <summary>
    /// С какого дня начнётся следующее закрытие контура. <c>null</c> — закрытий ещё не было, и начало
    /// первого называет человек.
    /// </summary>
    public DateOnly? ExpectedFrom(PeriodContour contour) => ClosedThrough(contour)?.AddDays(1);

    /// <summary>
    /// Отказ, если период закрыть нельзя.
    /// </summary>
    /// <param name="today">Сегодня в поясе компании: закрыть будущее или текущий день нельзя.</param>
    public void EnsureCanClose(PeriodContour contour, DateOnly from, DateOnly through, DateOnly today)
    {
        if (from > through)
            throw new InvalidRequestException(
                $"Начало периода ({Text(from)}) позже его конца ({Text(through)}).");

        if (through >= today)
            throw new InvalidRequestException(
                $"Закрыть можно только прошедшие дни: сегодня {Text(today)} по часам компании, " +
                $"а период заканчивается {Text(through)}.");

        if (ClosedThrough(contour) is not { } closed) return;

        if (through <= closed)
            throw new ConflictException(
                $"Период по {Text(through)} уже закрыт: {Whose(contour)} закрыт по {Text(closed)}.");

        var expected = closed.AddDays(1);
        if (from != expected)
            throw new InvalidRequestException(
                $"Периоды закрываются подряд, без пропусков: {Whose(contour)} закрыт по {Text(closed)}, " +
                $"следующий период начинается {Text(expected)}, а прислано начало {Text(from)}.");
    }

    /// <summary>
    /// Закрытие, которое можно отменить, — последнее действующее у контура. Отказ, если отменять
    /// нечего или отмена ничего не изменит.
    /// </summary>
    public PeriodClosure Reopenable(PeriodContour contour)
    {
        if (!_last.TryGetValue(contour, out var last))
            throw new ConflictException(contour.Kind == PeriodContourKind.Company
                ? "У компании нет закрытых периодов — отменять нечего."
                : "У стройки нет своих закрытых периодов — отменять нечего. Если она закрыта, то " +
                  "закрытием компании: отменяют его.");

        if (CoveredByCompany(last))
            throw new ConflictException(
                $"Закрытие стройки по {Text(last.Through)} перекрыто закрытием компании по {Text(Company!.Value)}: " +
                "отмена ничего не откроет. Сначала отмените закрытие компании.");

        return last;
    }

    /// <summary>Можно ли отменить последнее закрытие контура — то же правило, без отказа.</summary>
    public PeriodClosure? ReopenableOrNull(PeriodContour contour) =>
        _last.TryGetValue(contour, out var last) && !CoveredByCompany(last) ? last : null;

    /// <summary>
    /// Стройки, которым отмена закрытия КОМПАНИИ не откроет отменяемые дни: они закрыты своим
    /// закрытием, а закрыто — префикс. Стройка, закрывшая ноябрь после октября компании, держит и
    /// октябрь; отмена октября у компании её не откроет, и молчать об этом нельзя (ревью PR #1189).
    /// </summary>
    public IReadOnlyList<Guid> KeptClosedByOwn(PeriodClosure companyClosure) =>
        [.. _last.Where(p => p.Key.Kind == PeriodContourKind.Construction && p.Value.Through >= companyClosure.From)
            .Select(p => p.Key.ConstructionId!.Value)];

    /// <summary>
    /// Стройки, у которых закрытие КОМПАНИИ с этого дня часть дней закроет не впервые: они закрыты своим
    /// закрытием — по названную дату включительно. Перечень диалога закрытия эти дни не считает: их
    /// документы уже заперты, и названные второй раз, они легли бы в неизменяемую запись лишними (ревью
    /// PR #1201).
    /// </summary>
    /// <param name="from">Первый день, который закрытие компании закроет; <c>null</c> — у компании не
    /// закрыто ничего, и закрывается всё.</param>
    public IReadOnlyDictionary<Guid, DateOnly> ClosedAheadOfCompany(DateOnly? from) =>
        _last.Where(p => p.Key.Kind == PeriodContourKind.Construction && (from is null || p.Value.Through >= from))
            .ToDictionary(p => p.Key.ConstructionId!.Value, p => p.Value.Through);

    /// <summary>Закрытие стройки, которое не дальше границы компании: его отмена не открыла бы ни дня.</summary>
    private bool CoveredByCompany(PeriodClosure last) =>
        last.Contour == PeriodContourKind.Construction && Company is { } company && company >= last.Through;

    private static string Whose(PeriodContour contour) =>
        contour.Kind == PeriodContourKind.Company ? "учёт компании" : "учёт стройки";

    private static string Text(DateOnly date) => date.ToString("dd.MM.yyyy");
}
