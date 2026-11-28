using System.Linq.Expressions;
using BHS.CRG.Modules.Work.Submission;

namespace BHS.CRG.Modules.Work.Data;

/// <summary>
/// Из чего считается пометка: журнал отчёта, назначения и учётная запись читающего. Наборы даёт файл чтения
/// (<see cref="ReportReads" />): каталог их не открывает сам — изолированная запись читается только там.
/// </summary>
/// <param name="Journal">Журнал отчётов (<c>work-14</c>).</param>
/// <param name="Assignments">Назначения (<c>work-11</c>).</param>
/// <param name="Viewer">Учётная запись читающего; <c>null</c> — система.</param>
/// <param name="CrewMismatchReports">Отчёты с расхождением в группе, чей итог требует утверждения.</param>
public sealed record ReviewMarkSources(
    IQueryable<ReportJournalEntry> Journal, IQueryable<Assignment> Assignments, Guid? Viewer,
    IQueryable<Guid> CrewMismatchReports);

/// <summary>Пометка в ответе: код для отбора и счётчика, текст — для человека.</summary>
public sealed record ReviewMarkDto(string Code, string Text);

/// <summary>
/// Пометка очереди приёмки (ТЗ WORK-28; change <c>work-16-review</c>).
/// </summary>
/// <param name="Code">Постоянный код: им клиент отбирает и читает счётчик. Текст может поменяться, код — нет.</param>
/// <param name="Text">Текст для человека.</param>
/// <param name="Source">Из каких данных пометка считается. Код без источника не регистрируется: пометку, за которой
/// нет данных, нельзя отличить от «таких отчётов нет».</param>
/// <param name="Has">Предикат отчёта для запроса — им считаются и пометки строки, и счётчик по всему отбору.
/// <c>null</c> — вычисляется по действующей схеме в памяти или источник ещё не подключён.</param>
public sealed record ReviewMark(
    string Code, string Text, string Source, Func<ReviewMarkSources, Expression<Func<Report, bool>>>? Has)
{
    public ReviewMarkDto Dto => new(Code, Text);
}

/// <summary>
/// Каталог пометок очереди приёмки (ТЗ WORK-28, MOB-21, WORK-34; change <c>work-16-review</c>) — один, на сервере, и
/// открыт для дописывания.
///
/// <para><b>Пометка считается запросом, а не на клиенте.</b> Счётчики очереди — по всему отбору, а не по странице:
/// предикат одной пометки даёт и её строки, и её число. Клиент знает только коды (<c>WORK_MARK_CODES</c> в
/// <c>src/client/src/shared/api/work.ts</c>; сверяет <c>ReviewMarkCatalogTests</c>).</para>
///
/// <para><b>Пометки — о текущей подаче.</b> Правка после подачи считается с момента ПОСЛЕДНЕЙ подачи: исправления по
/// возврату — не подозрение (<c>work-15</c>, PR #1987). Правка менеджером и «ввёл и принял один человек» — факты отчёта,
/// а не подачи: они остаются.</para>
/// </summary>
public static class ReviewMarks
{
    public const string EditedAfterSubmit = "edited-after-submit";
    public const string SamePerson = "same-person";
    public const string ManagerEdit = "manager-edit";
    public const string SiteNotAssigned = "site-not-assigned";
    public const string CrewMismatch = "crew-mismatch";
    public const string AcceptedChanged = "accepted-changed";
    public const string PhotosPending = "photos-pending";
    public const string FieldsMissing = "fields-missing";
    public const string RequiredMissing = "required-missing";
    public const string LateDelivery = "late-delivery";
    public const string ClockDifference = "clock-difference";
    public const string ClockWentBack = "clock-went-back";
    internal const int ClockToleranceMinutes = 5;

    /// <summary>Все пометки — в порядке, в котором их показывает очередь.</summary>
    public static IReadOnlyList<ReviewMark> All { get; } =
    [
        new(EditedAfterSubmit, "правка после подачи",
            "журнал отчёта (work-14): правка исполнителем после последней подачи",
            // «После последней подачи» — по номеру журнала, а не по часам: время подачи и правки бывает временем
            // устройства, а номер — порядок записи на сервере.
            s => r => s.Journal.Any(e => e.ReportId == r.Id && e.Kind == ReportJournalKinds.Updated && e.AfterSubmission
                                         && !s.Journal.Any(x => x.ReportId == r.Id && x.Seq > e.Seq
                                                                && (x.Kind == ReportJournalKinds.Submitted
                                                                    || x.Kind == ReportJournalKinds.AutoSubmitted)))),
        // «Ввёл и принял один человек» (WORK-31.1): у поданного — правило AcceptWouldMarkRule (ввёл читающий), у
        // принятого — запись журнала, сделанная приёмкой.
        new(SamePerson, "ввёл и принял один человек",
            "автор ввода отчёта (work-13) и журнал отчёта: запись «ввёл и принял один человек»",
            s => Either(WouldMark(s), r => s.Journal.Any(e => e.ReportId == r.Id && e.Kind == ReportJournalKinds.SamePerson))),
        new(ManagerEdit, "правка менеджером",
            "журнал отчёта: правка строк менеджером (work-16)",
            s => r => s.Journal.Any(e => e.ReportId == r.Id && e.Kind == ReportJournalKinds.ManagerEdited)),
        // Назначение любого вида на дату отчёта (Assignment.Covers). assignedOnDate ответа отчёта — эта же пометка, а не
        // второй расчёт (ReportViews).
        new(SiteNotAssigned, "стройка не назначена на дату",
            "назначения (work-11): у сотрудника нет назначения на стройку отчёта на его дату",
            s => r => !s.Assignments.Any(a => a.EmployeeId == r.EmployeeId && a.ConstructionId == r.ConstructionId
                                              && a.DateFrom <= r.Date && (a.DateTo == null || r.Date <= a.DateTo))),
        // Отчёт уходит, не дожидаясь фото (WORK-34): строка объявила фото, файл ещё не дошёл. Ответы называют число —
        // «фото не загружены: 2» (Dtos).
        new(PhotosPending, "фото не загружены",
            "фото строк отчёта (work-17): объявлены строкой, файл ещё не дошёл",
            _ => r => r.Lines.Any(l => l.Photos.Any(p => p.State == LinePhotoState.Pending))),
        new(CrewMismatch, "расхождение в бригаде",
            "расходящиеся объёмы в группе, чей итог требует утверждения (work-18)",
            s => r => s.CrewMismatchReports.Contains(r.Id)),
        new(AcceptedChanged, "изменение принятого",
            "журнал отчёта: исполнитель изменил принятый отчёт и тот снова ждёт приёмки (work-19)",
            s => r => r.Status == ReportStatus.Submitted
                      && s.Journal.Any(e => e.ReportId == r.Id && e.Kind == ReportJournalKinds.AcceptedChanged
                          && !s.Journal.Any(x => x.ReportId == r.Id && x.Seq > e.Seq
                              && (x.Kind == ReportJournalKinds.Accepted || x.Kind == ReportJournalKinds.Returned)))),
        new(LateDelivery, "поздняя доставка",
            "последняя ручная подача из журнала и конец окна подачи",
            s => r => s.Journal.Any(e => e.ReportId == r.Id && e.AuthorId == null
                && (e.Kind == ReportJournalKinds.Submitted || e.Kind == ReportJournalKinds.FieldReceipt)
                && e.DeviceTime <= r.WindowEndsAt && e.ServerTime > r.WindowEndsAt
                && !s.Journal.Any(next => next.ReportId == r.Id && next.Seq > e.Seq
                    && (next.Kind == ReportJournalKinds.Submitted || next.Kind == ReportJournalKinds.AutoSubmitted)))),
        new(ClockDifference, "разница между временем действия и приёма",
            "время устройства и сервера в журнале",
            s => r => s.Journal.Any(e => e.ReportId == r.Id && e.AuthorId == null && e.DeviceTime != null
                && (e.DeviceTime < e.ServerTime.AddMinutes(-ClockToleranceMinutes)
                    || e.DeviceTime > e.ServerTime.AddMinutes(ClockToleranceMinutes)))),
        new(ClockWentBack, "время действия раньше предыдущего",
            "соседние отметки времени устройства в порядке записей журнала",
            s => r => s.Journal.Any(e => e.ReportId == r.Id && e.AuthorId == null && e.DeviceTime != null
                && e.DeviceTime < s.Journal.Where(previous => previous.ReportId == r.Id && previous.Seq < e.Seq
                        && previous.AuthorId == null && previous.DeviceTime != null)
                    .OrderByDescending(previous => previous.Seq).Select(previous => previous.DeviceTime).FirstOrDefault())),
        new(FieldsMissing, "не заполнены поля",
            "поля заказчика с признаком «нужно к приёмке» в действующей схеме (work-24)", null),
        new(RequiredMissing, "не заполнены место или материалы",
            "место и материалы, обязательные по виду работы в текущих строках автоподанного отчёта (work-31)", null),
    ];

    /// <summary>Пометки, которые считаются в запросе базы, — по порядку каталога.</summary>
    public static IReadOnlyList<ReviewMark> Counted { get; } = [.. All.Where(m => m.Has is not null)];

    /// <summary>
    /// «Ввёл и принял один человек» у поданного (WORK-31.1) — ОДНО правило: им считаются и <c>acceptWouldMark</c> ответа,
    /// и пометка <see cref="SamePerson" /> запросом. Поданный отчёт, автор ввода которого — учётная запись читающего.
    /// </summary>
    public static readonly Expression<Func<Report, Guid?, bool>> AcceptWouldMarkRule =
        (r, viewer) => r.Status == ReportStatus.Submitted && viewer != null && r.EnteredByUserId == viewer;

    private static readonly Func<Report, Guid?, bool> AcceptWouldMarkCompiled = AcceptWouldMarkRule.Compile();

    /// <summary><see cref="AcceptWouldMarkRule" /> над отчётом в памяти.</summary>
    public static bool AcceptWouldMark(Report report, Guid? viewer) => AcceptWouldMarkCompiled(report, viewer);

    /// <summary>
    /// Пометки отчёта для ответа — по порядку каталога. У <see cref="PhotosPending" /> текст с числом: «фото не загружены:
    /// 2» — сколько ждут, а не только что ждут.
    /// </summary>
    public static IReadOnlyList<ReviewMarkDto> Dtos(
        IReadOnlyCollection<string> codes, int photosPending, int missingFields = 0, RequiredGaps required = RequiredGaps.None) =>
    [
        .. All.Where(m => m.Code switch
            {
                FieldsMissing => missingFields > 0,
                RequiredMissing => required != RequiredGaps.None,
                _ => codes.Contains(m.Code),
            })
            .Select(m => m.Code switch
            {
                PhotosPending => new ReviewMarkDto(m.Code, $"{m.Text}: {photosPending}"),
                FieldsMissing => new ReviewMarkDto(m.Code, $"{m.Text}: {missingFields}"),
                RequiredMissing => new ReviewMarkDto(m.Code, RequiredText(required)),
                _ => m.Dto,
            }),
    ];

    private static string RequiredText(RequiredGaps required) => required switch
    {
        RequiredGaps.Location => "не указано обязательное место работ",
        RequiredGaps.Materials => "не указаны обязательные материалы",
        _ => "не указаны обязательные место работ и материалы",
    };

    internal static IReadOnlyList<ReviewMarkDto> WithText(IReadOnlyList<ReviewMarkDto> marks,
        IReadOnlyList<ReviewMarkDto>? texts) =>
        texts is null or { Count: 0 } ? marks
            : [.. marks.Select(mark => texts.FirstOrDefault(text => text.Code == mark.Code) ?? mark)];

    /// <summary>Пометка по коду; неизвестный код — <c>null</c>.</summary>
    public static ReviewMark? Of(string code) => All.FirstOrDefault(m => m.Code == code);

    /// <summary>
    /// Все пометки отчёта одной проекцией: <c>Flags[i]</c> — есть ли у отчёта <see cref="Counted" />[i]. Один запрос на
    /// любую выдачу отчётов, а не по запросу на пометку.
    /// </summary>
    internal static Expression<Func<Report, ReviewMarkFlags>> Flags(ReviewMarkSources sources, bool includeDelivery)
    {
        var r = Expression.Parameter(typeof(Report), "r");
        var flags = Expression.NewArrayInit(typeof(bool), Counted.Select(m => Inline(m.Has!(sources), r)));
        List<MemberBinding> fields =
        [
            Expression.Bind(typeof(ReviewMarkFlags).GetProperty(nameof(ReviewMarkFlags.Id))!,
                Expression.Property(r, nameof(Report.Id))),
            Expression.Bind(typeof(ReviewMarkFlags).GetProperty(nameof(ReviewMarkFlags.Marks))!, flags),
        ];
        if (includeDelivery)
        {
            Expression<Func<Report, List<ReportDeliveryEntry>>> entries = report => sources.Journal
                .Where(e => e.ReportId == report.Id && ((e.AuthorId == null && e.DeviceTime != null)
                    || e.Kind == ReportJournalKinds.Submitted || e.Kind == ReportJournalKinds.AutoSubmitted))
                .OrderBy(e => e.Seq)
                .Select(e => new ReportDeliveryEntry(report.WindowEndsAt, e.Kind,
                    e.AuthorId == null ? e.DeviceTime : null, e.ServerTime)).ToList();
            fields.Add(Expression.Bind(typeof(ReviewMarkFlags).GetProperty(nameof(ReviewMarkFlags.Delivery))!,
                Inline(entries, r)));
        }
        return Expression.Lambda<Func<Report, ReviewMarkFlags>>(
            Expression.MemberInit(Expression.New(typeof(ReviewMarkFlags)), fields), r);
    }

    /// <summary>
    /// Счётчики всех пометок по отбору одной агрегацией: <c>[i]</c> — число отчётов с <see cref="Counted" />[i].
    /// </summary>
    public static Expression<Func<IGrouping<int, Report>, int[]>> Counts(ReviewMarkSources sources)
    {
        var g = Expression.Parameter(typeof(IGrouping<int, Report>), "g");
        var counts = Counted.Select(m => (Expression)Expression.Call(
            typeof(Enumerable), nameof(Enumerable.Count), [typeof(Report)], g, m.Has!(sources)));
        return Expression.Lambda<Func<IGrouping<int, Report>, int[]>>(Expression.NewArrayInit(typeof(int), counts), g);
    }

    /// <summary><see cref="AcceptWouldMarkRule" /> с учётной записью из <paramref name="sources" /> — параметром запроса.</summary>
    private static Expression<Func<Report, bool>> WouldMark(ReviewMarkSources sources)
    {
        var r = Expression.Parameter(typeof(Report), "r");
        var viewer = Expression.Property(Expression.Constant(sources), nameof(ReviewMarkSources.Viewer));
        var body = new Swap(new Dictionary<ParameterExpression, Expression>
        {
            [AcceptWouldMarkRule.Parameters[0]] = r, [AcceptWouldMarkRule.Parameters[1]] = viewer,
        }).Visit(AcceptWouldMarkRule.Body);
        return Expression.Lambda<Func<Report, bool>>(body, r);
    }

    private static Expression<Func<Report, bool>> Either(Expression<Func<Report, bool>> a, Expression<Func<Report, bool>> b)
    {
        var r = Expression.Parameter(typeof(Report), "r");
        return Expression.Lambda<Func<Report, bool>>(Expression.OrElse(Inline(a, r), Inline(b, r)), r);
    }

    /// <summary>Тело предиката над другим параметром отчёта.</summary>
    private static Expression Inline<T>(Expression<Func<Report, T>> selector, ParameterExpression r) =>
        new Swap(new Dictionary<ParameterExpression, Expression> { [selector.Parameters[0]] = r }).Visit(selector.Body);

    private sealed class Swap(IReadOnlyDictionary<ParameterExpression, Expression> map) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            map.TryGetValue(node, out var to) ? to : base.VisitParameter(node);
    }
}

/// <summary>Пометки одного отчёта из проекции <see cref="ReviewMarks.Flags" />.</summary>
internal sealed class ReviewMarkFlags
{
    public Guid Id { get; init; }

    /// <summary>По порядку <see cref="ReviewMarks.Counted" />.</summary>
    public bool[] Marks { get; init; } = [];

    public List<ReportDeliveryEntry> Delivery { get; init; } = [];

    /// <summary>Коды пометок отчёта по порядку каталога.</summary>
    public IReadOnlyList<string> Codes() => [.. ReviewMarks.Counted.Where((_, i) => Marks[i]).Select(m => m.Code)];
}

internal sealed record ReportDeliveryEntry(DateTimeOffset WindowEndsAt, string Kind,
    DateTimeOffset? DeviceTime, DateTimeOffset ServerTime);
