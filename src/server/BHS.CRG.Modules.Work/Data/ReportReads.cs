using System.Data;
using System.Globalization;
using BHS.CRG.Modules.Ports;
using System.Linq.Expressions;
using BHS.CRG.Modules.Work.Crew;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Work.Closing;
using BHS.CRG.Modules.Work.Submission;
using BHS.CRG.Modules.Work.Devices;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Data;

/// <summary>
/// Кому отвечает чтение отчётов (ТЗ WORK-39, WORK-40; <c>work-07</c>): охват читателя и, у охвата «свои», его
/// сотрудник. Свои отчёты — <see cref="Own" /> (сотрудник учётной записи); ввод за сотрудника — <see cref="Of" />
/// с охватом менеджера (<c>ManagerSites.ReachAsync</c>): стройки его назначений или все.
/// </summary>
public sealed record ReportViewer
{
    private ReportViewer(ModuleReach reach, Guid? employeeId)
    {
        Reach = reach;
        EmployeeId = employeeId;
    }

    public ModuleReach Reach { get; }

    /// <summary>
    /// Сотрудник читателя. У охвата «свои» — чьи отчёты свои; у охвата шире — только у читателя журнала
    /// (<see cref="Of" /> с <c>alsoOwn</c>): его отчёты видны и вне охвата строек. Пути записи сотрудник при широком
    /// охвате не дают — <see cref="Covers" /> от него не расширяется.
    /// </summary>
    public Guid? EmployeeId { get; }

    /// <summary>Свои отчёты сотрудника учётной записи.</summary>
    public static ReportViewer Own(Guid employeeId) => new(ModuleReach.Own, employeeId);

    internal FieldReportDelivery? FieldDelivery { get; private init; }

    internal static ReportViewer ForField(Guid employeeId, FieldReportDelivery delivery) =>
        new(ModuleReach.Own, employeeId) { FieldDelivery = delivery };

    /// <summary>
    /// Отчёты охвата «стройки» или «все». Охват «свои» здесь без сотрудника — и потому не видит ничего: чьи отчёты
    /// «свои», знает только <see cref="Own" />.
    ///
    /// <para><paramref name="alsoOwn" /> — сотрудник читателя, чьи отчёты видны ему и вне охвата (журнал отчёта,
    /// <c>work-14</c>): права складываются, и менеджер со своими отчётами видит и стройки, и свои.</para>
    /// </summary>
    public static ReportViewer Of(ModuleReach reach, Guid? alsoOwn = null) => new(reach, alsoOwn);

    /// <summary>Видна ли читателю запись стройки <paramref name="site" /> сотрудника <paramref name="employee" />.</summary>
    public bool Covers(Guid site, Guid employee) => Reach.Covers(site, own: EmployeeId == employee);
}

/// <summary>Изменения своих отчётов после курсора (ТЗ MOB-16): лежащие отчёты и удалённые черновики — по номеру.</summary>
/// <param name="Cursor">Номер последнего отданного изменения — <c>since</c> следующего запроса.</param>
/// <param name="More">Отдано не всё: следующий запрос с <paramref name="Cursor" /> получит остальное.</param>
/// <param name="Versions">Версия каждого отчёта выдачи — прочитана ДО тела (см. <see cref="ReportReads.SinceAsync" />).</param>
public sealed record ReportFeed(
    IReadOnlyList<Report> Items, IReadOnlyDictionary<Guid, string> Versions, IReadOnlyList<ReportGone> Deleted, long Cursor,
    bool More);

/// <summary>Удалённый черновик в выдаче курсора: идентификатор и номер удаления.</summary>
public sealed record ReportGone(Guid Id, long Seq);

/// <summary>
/// Файл чтения отчёта монтажника (<c>IIsolatedRecord</c>, <c>work-07</c>; change <c>work-13-report-model</c>): здесь и
/// только здесь отчёт и его строки читаются напрямую (перепись — <c>IsolatedReadInventoryTests</c>).
///
/// <para><b>Чтение — только с охватом</b> (<see cref="ReportViewer" />): чужой и несуществующий отчёт дают один ответ
/// одним путём кода (<see cref="FindAsync" />), и адреса не могут разойтись в том, что отвечают.</para>
///
/// <para><b>Вопросы пути записи «занято ли»</b> охвата не берут, но и полей чужого отчёта не отдают: только факт
/// занятости, у ключа — идентификатор отчёта ТОГО ЖЕ сотрудника, у идентификатора отчёта — чей он, для сравнения
/// адресом, а не для ответа.</para>
/// </summary>
public sealed partial class ReportReads(WorkDbContext db)
{
    /// <summary>
    /// Отчёт по идентификатору, если он виден читателю, — с отслеживанием (версия строки — теневое свойство) и со
    /// строками, материалами и участниками. Чужой и несуществующий — одинаково <c>null</c>.
    /// </summary>
    public Task<Report?> FindAsync(ReportViewer viewer, Guid id, CancellationToken ct)
    {
        var query = Full(Visible(viewer).Where(r => r.Id == id));
        // Операция доставки держит read committed для проверки отзыва. Версию и строки читаем одним снимком.
        if (viewer.FieldDelivery is not null) query = query.AsSingleQuery();
        return query.FirstOrDefaultAsync(ct);
    }

    internal async Task<Dictionary<Guid, string?>> ReturnCommentsAsync(
        ReportViewer viewer, Guid[] reportIds, CancellationToken ct)
    {
        if (reportIds.Length == 0) return [];
        var visible = Visible(viewer).Where(r => reportIds.Contains(r.Id)).Select(r => r.Id);
        var entries = db.Set<ReportJournalEntry>().AsNoTracking()
            .Where(e => e.Kind == ReportJournalKinds.Returned && visible.Contains(e.ReportId));
        return await entries.GroupBy(e => e.ReportId)
            .Select(g => new { Id = g.Key, Comment = g.OrderByDescending(e => e.Seq).Select(e => e.Comment).First() })
            .ToDictionaryAsync(e => e.Id, e => e.Comment, ct);
    }

    internal Task<Report?> ReviewHeaderAsync(ReportViewer viewer, Guid id, CancellationToken ct) =>
        Visible(viewer).FirstOrDefaultAsync(r => r.Id == id, ct);

    internal IQueryable<Report> TableReports(ReportViewer viewer) => Visible(viewer).AsNoTracking();

    internal IQueryable<Report> ReadableTableReports(ReportViewer viewer, Guid? employeeId, Guid? userId) =>
        TableReports(viewer).Where(ContentReadable(employeeId, userId));

    internal Task<Guid?> VisibleEmployeeAsync(ReportViewer viewer, Guid id, CancellationToken ct) =>
        Visible(viewer).Where(r => r.Id == id).Select(r => (Guid?)r.EmployeeId).SingleOrDefaultAsync(ct);

    /// <summary>
    /// Чей отчёт с этим идентификатором — сотрудник и стройка, — или <c>null</c>, если его нет. Только для сравнения
    /// при заведении (ТЗ MOB-15): «тот же сотрудник — повтор, другой — идентификатор занят»; наружу не уходит.
    /// </summary>
    public async Task<(Guid EmployeeId, Guid ConstructionId)?> HolderOfIdAsync(Guid id, CancellationToken ct)
    {
        var holder = await db.Set<Report>().AsNoTracking().Where(r => r.Id == id)
            .Select(r => new { r.EmployeeId, r.ConstructionId }).FirstOrDefaultAsync(ct);
        return holder is null ? null : (holder.EmployeeId, holder.ConstructionId);
    }

    /// <summary>Запись протокола, которой изменение <paramref name="changeId" /> уже применено к отчёту (ТЗ MOB-15).</summary>
    public Task<ReportChange?> AppliedAsync(Guid reportId, Guid changeId, CancellationToken ct) =>
        db.Set<ReportChange>().AsNoTracking().FirstOrDefaultAsync(c => c.ReportId == reportId && c.ChangeId == changeId, ct);

    internal Task<bool> HasFieldReceiptAsync(Guid reportId, CancellationToken ct) =>
        db.Set<ReportChange>().AnyAsync(c => c.ReportId == reportId && c.Kind == ReportChangeKind.FieldReceipt, ct);

    /// <summary>
    /// Сотрудник, чей отчёт с этим идентификатором был удалён, или <c>null</c>. Идентификатор удалённого черновика
    /// не заводится снова (ТЗ MOB-15): опоздавший повтор исходного заведения иначе воскресил бы удалённое.
    /// </summary>
    public Task<Guid?> DeletedHolderAsync(Guid id, CancellationToken ct) =>
        db.Set<ReportChange>().AsNoTracking()
            .Where(c => c.ReportId == id && c.Kind == ReportChangeKind.Deleted)
            .OrderByDescending(c => c.Seq)
            .Select(c => (Guid?)c.EmployeeId)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Свои отчёты, изменённые после номера <paramref name="since" />, и черновики, удалённые после него (ТЗ MOB-16), —
    /// не больше <paramref name="limit" /> записей вместе, по возрастанию номера. Номер отчёта — номер его последнего
    /// изменения, поэтому отчёт, правленый трижды, приходит один раз.
    ///
    /// <para><b>Без отслеживания</b>: выдача только читается. Версии читаются ПЕРВЫМИ, тела — вторыми: правка между
    /// двумя чтениями даёт тело новее версии, и следующая правка клиента получит 409, а не запишется поверх чужой.
    /// Обратный порядок дал бы версию новее тела — правку по устаревшему виду без отказа.</para>
    /// </summary>
    internal async Task<ReportFeed> SinceAsync(ReportViewer viewer, long since, int limit, CancellationToken ct)
    {
        // Номер отчёта в выдаче — больший из его номера и номеров доставки фото (work-17): доставка объявленного фото
        // отчёт не трогает, а клиент с курсором обязан узнать, что файл дошёл.
        var visible = Visible(viewer).AsNoTracking();
        var heads = await visible
            .Where(r => r.Seq > since || r.Lines.Any(l => l.Photos.Any(p => p.StoredSeq > since)))
            .Select(r => new
            {
                r.Id,
                Seq = Math.Max(r.Seq, r.Lines.SelectMany(l => l.Photos).Max(p => p.StoredSeq) ?? 0L),
                Version = EF.Property<uint>(r, WorkDbContext.RowVersion),
            })
            .OrderBy(h => h.Seq).Take(limit + 1)
            .ToListAsync(ct);
        var deleted = await VisibleDeletions(viewer)
            .Where(c => c.Seq > since)
            .OrderBy(c => c.Seq).Take(limit + 1)
            .Select(c => new ReportGone(c.ReportId, c.Seq)).ToListAsync(ct);

        var merged = heads.Select(h => (h.Seq, Report: (Guid?)h.Id, Gone: (ReportGone?)null))
            .Concat(deleted.Select(d => (d.Seq, Report: (Guid?)null, Gone: (ReportGone?)d)))
            .OrderBy(e => e.Seq).ToList();
        var page = merged.Take(limit).ToList();
        var ids = page.Where(e => e.Report is not null).Select(e => e.Report!.Value).ToList();
        var bodies = ids.Count == 0
            ? new Dictionary<Guid, Report>()
            : await Full(visible.Where(r => ids.Contains(r.Id))).ToDictionaryAsync(r => r.Id, ct);
        // Отчёт, удалённый между двумя чтениями, пропускается: его удаление придёт следующей выдачей — номер больше.
        return new ReportFeed(
            [.. ids.Where(bodies.ContainsKey).Select(id => bodies[id])],
            heads.ToDictionary(h => h.Id, h => h.Version.ToString(CultureInfo.InvariantCulture)),
            [.. page.Where(e => e.Gone is not null).Select(e => e.Gone!)],
            page.Count == 0 ? since : page[^1].Seq,
            merged.Count > limit);
    }

    private IQueryable<ReportChange> VisibleDeletions(ReportViewer viewer)
    {
        var deleted = db.Set<ReportChange>().AsNoTracking().Where(c => c.Kind == ReportChangeKind.Deleted);
        var employee = viewer.EmployeeId;
        var sites = viewer.Reach.Sites.ToArray();
        return viewer.Reach.Kind switch
        {
            ModuleReachKind.All => deleted,
            ModuleReachKind.Own => deleted.Where(c => c.EmployeeId == employee),
            _ => deleted.Where(c => c.EmployeeId == employee || sites.Contains(c.ConstructionId)),
        };
    }

    /// <summary>
    /// Журнал отчёта (ТЗ WORK-11, WORK-12, WORK-39, WORK-54; change <c>work-14-report-journal</c>), если отчёт виден
    /// читателю, по номеру; невидимый и несуществующий — одинаково <c>null</c>. Черновик — отчёт без момента подачи —
    /// виден только сотруднику отчёта и автору его ввода <paramref name="userId" />: содержимое черновика менеджер видит
    /// после подачи. Судит момент, а не статус: подача ставит их одной записью (<c>work-15</c>), а возвращённый на правку
    /// отчёт (<c>work-16</c>) момент сохраняет — его содержимое уже видели.
    ///
    /// <para><b>Удалённый черновик</b> журнал оставляет записью «удалён» (WORK-12), и прочесть её можно: удаляется только
    /// черновик, поэтому и правило у него черновика — сотрудник отчёта и автор ввода, в охвате стройки и сотрудника из
    /// записи протокола об удалении.</para>
    /// </summary>
    public async Task<IReadOnlyList<ReportJournalEntry>?> JournalAsync(
        ReportViewer viewer, Guid? userId, Guid id, CancellationToken ct)
    {
        var live = Visible(viewer).AsNoTracking().Where(r => r.Id == id);
        var readable = await live.AnyAsync(ct)
            ? await live.AnyAsync(ContentReadable(viewer.EmployeeId, userId), ct)
            : await DeletedDraftReadableAsync(viewer, userId, id, ct);
        if (!readable) return null;
        return await db.Set<ReportJournalEntry>().AsNoTracking()
            .Where(e => e.ReportId == id).OrderBy(e => e.Seq).ToListAsync(ct);
    }

    /// <summary>
    /// Виден ли журнал удалённого черновика: стройка и сотрудник — из записи протокола об удалении, автор ввода — из
    /// записи журнала о заведении (у отчёта, введённого за сотрудника, её автор и есть автор ввода).
    /// </summary>
    private async Task<bool> DeletedDraftReadableAsync(ReportViewer viewer, Guid? userId, Guid id, CancellationToken ct)
    {
        var gone = await db.Set<ReportChange>().AsNoTracking()
            .Where(c => c.ReportId == id && c.Kind == ReportChangeKind.Deleted)
            .OrderByDescending(c => c.Seq)
            .Select(c => new { c.EmployeeId, c.ConstructionId })
            .FirstOrDefaultAsync(ct);
        if (gone is null) return false;
        var me = viewer.EmployeeId;
        var reached = viewer.Covers(gone.ConstructionId, gone.EmployeeId) || me == gone.EmployeeId;
        if (!reached) return false;
        if (me == gone.EmployeeId) return true;
        if (userId is null) return false;
        return await db.Set<ReportJournalEntry>().AsNoTracking()
            .AnyAsync(e => e.ReportId == id && e.Kind == ReportJournalKinds.Created && e.EnteredFor && e.AuthorId == userId, ct);
    }

    /// <summary>
    /// Ночные отчёты читателя с даты <paramref name="from" /> — стройка и дата (ТЗ MOB-35, <c>work-15</c>): по ним «мои
    /// стройки» предлагают продолжить вчерашнюю ночь. Охват — читателя («свои»).
    /// </summary>
    public async Task<IReadOnlyList<(Guid Site, DateOnly Date)>> NightReportsAsync(
        ReportViewer viewer, DateOnly from, CancellationToken ct)
    {
        var found = await Visible(viewer).AsNoTracking()
            .Where(r => r.Shift == ReportShift.Night && r.Date >= from)
            .Select(r => new { r.ConstructionId, r.Date }).ToListAsync(ct);
        return [.. found.Select(f => (f.ConstructionId, f.Date))];
    }

    /// <summary>
    /// Черновики с истёкшим окном подачи (ТЗ WORK-10, <c>work-15</c>) — по порядку окна, не больше
    /// <paramref name="limit" />, кроме <paramref name="except" /> (пропущенных этим прогоном). Читает только автоподача
    /// — от имени системы с причиной (<see cref="ModuleReader.OfSystem" />): охват у неё «все», и человеку этот метод не
    /// годится. Возвращённые не выбираются: автоподача их не трогает. Сотрудник — чтобы имена исполнителей порции
    /// прочесть одним вызовом порта.
    /// </summary>
    public async Task<IReadOnlyList<(Guid Id, Guid EmployeeId)>> ExpiredDraftsAsync(
        ModuleReader system, DateTimeOffset now, IReadOnlyCollection<Guid> except, int limit, CancellationToken ct)
    {
        EnsureSystem(system);
        var skipped = except.ToArray();
        var found = await Expired(now).AsNoTracking().Where(r => !skipped.Contains(r.Id))
            .OrderBy(r => r.WindowEndsAt).ThenBy(r => r.Id).Take(limit)
            .Select(r => new { r.Id, r.EmployeeId }).ToListAsync(ct);
        return [.. found.Select(f => (f.Id, f.EmployeeId))];
    }

    /// <summary>
    /// Черновик с истёкшим окном — с отслеживанием и со строками, для подачи; уже не черновик или окно не истекло —
    /// <c>null</c> (его успел подать или поправить другой). Тот же параметр системы, что у
    /// <see cref="ExpiredDraftsAsync" />.
    /// </summary>
    public Task<Report?> ExpiredDraftAsync(ModuleReader system, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        EnsureSystem(system);
        return Full(Expired(now)).FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    /// <summary>
    /// Поданные отчёты, ждущие сводки менеджерам (ТЗ WORK-56, <c>work-16</c>): окно подачи закрылось, а этот момент подачи
    /// в сводку ещё не входил (<see cref="ReviewDigestMark" />). Читает только задача такта — от имени системы с
    /// причиной, как <see cref="ExpiredDraftsAsync" />.
    /// </summary>
    public async Task<IReadOnlyList<(Guid Id, Guid ConstructionId, DateTimeOffset SubmittedAt)>> DigestPendingAsync(
        ModuleReader system, DateTimeOffset now, CancellationToken ct)
    {
        EnsureSystem(system);
        var at = now.ToUniversalTime();
        var marks = db.Set<ReviewDigestMark>();
        var found = await db.Set<Report>().AsNoTracking()
            .Where(r => r.Status == ReportStatus.Submitted && r.WindowEndsAt <= at && r.SubmittedAt != null
                        && !marks.Any(m => m.ReportId == r.Id && m.SubmittedAt == r.SubmittedAt))
            .OrderBy(r => r.ConstructionId).ThenBy(r => r.Id)
            .Select(r => new { r.Id, r.ConstructionId, SubmittedAt = r.SubmittedAt!.Value })
            .ToListAsync(ct);
        return [.. found.Select(f => (f.Id, f.ConstructionId, f.SubmittedAt))];
    }

    /// <summary>
    /// Недоставленные сводки (<see cref="ReviewDigestRetry" />) — с отслеживанием: доставленные и устаревшие задача такта
    /// удаляет. <c>Live</c> — отчёт всё ещё подан тем же моментом: решённый или поданный заново повтора не ждёт.
    /// </summary>
    public async Task<IReadOnlyList<(ReviewDigestRetry Row, Guid ConstructionId, bool Live)>> DigestRetriesAsync(
        ModuleReader system, CancellationToken ct)
    {
        EnsureSystem(system);
        var rows = await db.Set<ReviewDigestRetry>().ToListAsync(ct);
        if (rows.Count == 0) return [];
        var ids = rows.Select(r => r.ReportId).Distinct().ToArray();
        var reports = await db.Set<Report>().AsNoTracking().Where(r => ids.Contains(r.Id))
            .Select(r => new { r.Id, r.ConstructionId, r.Status, r.SubmittedAt })
            .ToDictionaryAsync(r => r.Id, ct);
        return
        [
            .. rows.Select(row => reports.TryGetValue(row.ReportId, out var r)
                ? (row, r.ConstructionId, r.Status == ReportStatus.Submitted && r.SubmittedAt == row.SubmittedAt)
                : (row, Guid.Empty, false)),
        ];
    }

    /// <summary>
    /// Очередь приёмки (ТЗ WORK-28, WORK-39; <c>work-16</c>): поданные отчёты охвата читателя по отбору, страница по
    /// моменту подачи — дольше всех ждущий первым. Счётчики — по всему отбору БЕЗ отбора по пометке: так счётчик пометки
    /// не обнуляется, когда выбрана другая, и говорит, сколько даст выбор. Сотрудники — по всем ждущим
    /// отчётам охвата, независимо от отбора и страницы.
    /// </summary>
    public async Task<ReviewQueueRead> QueueAsync(
        ReportViewer viewer, ReviewQueueFilter filter, Guid? userId, int offset, int limit,
        IModuleWorkTypes workTypes, IModulePeriods periods, CancellationToken ct, ModuleAcceptanceSchema? acceptance = null)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var sources = Sources(userId);
        var pending = Visible(viewer).AsNoTracking().Where(r => r.Status == ReportStatus.Submitted);
        var chosen = pending;
        if (filter.ConstructionId is { } site) chosen = chosen.Where(r => r.ConstructionId == site);
        if (filter.EmployeeId is { } employee) chosen = chosen.Where(r => r.EmployeeId == employee);
        if (filter.From is { } from) chosen = chosen.Where(r => r.Date >= from);
        if (filter.To is { } to) chosen = chosen.Where(r => r.Date <= to);

        // Пометки, чьи источники есть в Work, считаются одной агрегацией по отбору.
        var counted = await chosen.GroupBy(_ => 1).Select(ReviewMarks.Counts(sources)).FirstOrDefaultAsync(ct);
        var counts = ReviewMarks.All.ToDictionary(m => m.Code, _ => 0);
        for (var i = 0; i < ReviewMarks.Counted.Count; i++) counts[ReviewMarks.Counted[i].Code] = counted?[i] ?? 0;
        var missingFields = await CountMissingFieldsAsync(chosen, acceptance, ct);
        counts[ReviewMarks.FieldsMissing] = missingFields.Count;
        var required = await RequiredGapsAsync(chosen, workTypes, periods, ct);
        counts[ReviewMarks.RequiredMissing] = required.Count;

        var missingIds = missingFields.Keys.ToArray();
        var requiredIds = required.Keys.ToArray();
        var selected = filter.Mark switch
        {
            null => chosen,
            { Code: ReviewMarks.FieldsMissing } => chosen.Where(r => missingIds.Contains(r.Id)),
            { Code: ReviewMarks.RequiredMissing } => chosen.Where(r => requiredIds.Contains(r.Id)),
            { Has: { } has } => chosen.Where(has(sources)),
            _ => chosen.Where(_ => false),
        };
        // Число по отбору — независимый подзапрос, общий для всех сотрудников в охвате.
        var summary = await pending.Select(r => new { r.EmployeeId, Total = selected.Count() }).Distinct().ToListAsync(ct);
        var page = await selected.OrderBy(r => r.SubmittedAt).ThenBy(r => r.Id).Skip(offset).Take(limit)
            .Select(r => new
            {
                Report = r, Version = EF.Property<uint>(r, WorkDbContext.RowVersion), Lines = r.Lines.Count,
                Pending = r.Lines.SelectMany(l => l.Photos).Count(p => p.State == LinePhotoState.Pending),
            })
            .ToListAsync(ct);
        Guid[] ids = [.. page.Select(p => p.Report.Id)];
        var marks = await MarksAsync(viewer, ids, userId, ct);
        var delivery = await DeliveryMarksAsync(viewer, ids, ct);
        return new ReviewQueueRead(
            [
                .. page.Select(p => new ReviewQueueItem(
                    p.Report, p.Version.ToString(CultureInfo.InvariantCulture), p.Lines, marks[p.Report.Id], p.Pending,
                    missingFields.GetValueOrDefault(p.Report.Id), required.GetValueOrDefault(p.Report.Id),
                    delivery.GetValueOrDefault(p.Report.Id))),
            ],
            summary.FirstOrDefault()?.Total ?? 0, counts, [.. summary.Select(row => row.EmployeeId)]);
    }

    private static async Task<Dictionary<Guid, RequiredGaps>> RequiredGapsAsync(
        IQueryable<Report> chosen, IModuleWorkTypes workTypes, IModulePeriods periods, CancellationToken ct)
    {
        var lines = await chosen.Where(report => report.SubmittedBy == ReportSubmitter.Auto)
            .SelectMany(report => report.Lines, (report, line) => new
            {
                ReportId = report.Id, report.ConstructionId, report.Date,
                line.WorkTypeId, line.Location, HasMaterials = line.Materials.Any(),
            }).ToListAsync(ct);
        if (lines.Count == 0) return [];

        // Если период закрыли без Work, поданный отчёт может сохраниться в очереди.
        var boundaries = await periods.BoundariesAsync(ct);
        lines.RemoveAll(line => ReportPeriods.IsClosed(boundaries, line.ConstructionId, line.Date));
        if (lines.Count == 0) return [];
        var known = await workTypes.GetAsync([.. lines.Select(line => line.WorkTypeId).Distinct()], ct);
        if (known is null) return [];

        var types = known.ToDictionary(type => type.Id);
        var reports = new Dictionary<Guid, RequiredGaps>();
        foreach (var line in lines)
        {
            var gaps = RequiredFields.Check(types.GetValueOrDefault(line.WorkTypeId), line.Location, line.HasMaterials);
            if (gaps != RequiredGaps.None) reports[line.ReportId] = reports.GetValueOrDefault(line.ReportId) | gaps;
        }
        return reports;
    }

    private static async Task<Dictionary<Guid, int>> CountMissingFieldsAsync(
        IQueryable<Report> chosen, ModuleAcceptanceSchema? acceptance, CancellationToken ct)
    {
        var counts = new Dictionary<Guid, int>();
        if (acceptance is not { HasFields: true }) return counts;

        var details = chosen.SelectMany(report => report.Lines, (report, line) => new { report.Id, line.Details });
        await foreach (var line in details.AsAsyncEnumerable().WithCancellation(ct))
        {
            using var data = System.Text.Json.JsonDocument.Parse(line.Details);
            var missing = acceptance.Missing(data.RootElement).Count;
            if (missing > 0) counts[line.Id] = counts.GetValueOrDefault(line.Id) + missing;
        }
        return counts;
    }

    /// <summary>
    /// Пометки отчётов (ТЗ WORK-28) одним запросом: коды каталога по порядку, у каждого отчёта — список, пустой тоже.
    ///
    /// <para>Тот же охват, что у чтения отчёта: чужие и несуществующие идентификаторы в ответ не входят.
    /// Получить объект отчёта недостаточно для доказательства охвата — запрос проверяет его сам.</para>
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> MarksAsync(
        ReportViewer viewer, IReadOnlyCollection<Guid> ids, Guid? userId, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToArray();
        if (wanted.Length == 0) return new Dictionary<Guid, IReadOnlyList<string>>();
        var flags = await Visible(viewer).AsNoTracking().Where(r => wanted.Contains(r.Id))
            .Select(ReviewMarks.Flags(Sources(userId))).ToListAsync(ct);
        return flags.ToDictionary(f => f.Id, f => f.Codes());
    }

    private IQueryable<ReportJournalEntry> JournalRows() => db.Set<ReportJournalEntry>().AsNoTracking();

    private ReviewMarkSources Sources(Guid? userId) =>
        new(JournalRows(), db.Assignments.AsNoTracking(), userId, CrewMismatchReports());

    private IQueryable<Guid> CrewMismatchReports()
    {
        var submitted = db.Set<Report>().Where(r => r.Status == ReportStatus.Submitted || r.Status == ReportStatus.Accepted);
        return from report in submitted
               from line in report.Lines
               where line.IsCrew && db.Set<CrewGroupRecord>().Any(g => g.ConstructionId == report.ConstructionId
                   && g.Date == report.Date && g.Shift == report.Shift && g.WorkTypeId == line.WorkTypeId
                   && (g.ApprovedRevision == null || g.ApprovedRevision != g.InputRevision))
               where submitted.Any(other => other.ConstructionId == report.ConstructionId
                   && other.Date == report.Date && other.Shift == report.Shift
                   && other.Lines.Any(l => l.IsCrew && l.WorkTypeId == line.WorkTypeId && l.Volume != line.Volume))
               select report.Id;
    }

    internal IQueryable<CrewGroupRecord> CrewGroups(ModuleReach reach)
    {
        var groups = db.Set<CrewGroupRecord>();
        if (reach.Kind == ModuleReachKind.All) return groups;
        var sites = reach.Sites.ToArray();
        return groups.Where(g => sites.Contains(g.ConstructionId));
    }

    internal Task<List<CrewGroupRecord>> CrewGroupsOnShiftAsync(Report report, Guid[] workTypes, CancellationToken ct) =>
        CrewGroups(ModuleReach.OfSites([report.ConstructionId]))
            .Where(g => g.Date == report.Date && g.Shift == report.Shift && workTypes.Contains(g.WorkTypeId))
            .ToListAsync(ct);

    internal Task<List<CrewGroupLine>> CrewLinesAsync(ReportViewer viewer, IQueryable<CrewGroupRecord> groups,
        CancellationToken ct) =>
        (from crew in groups
         from report in Visible(viewer).AsNoTracking()
         where report.ConstructionId == crew.ConstructionId && report.Date == crew.Date && report.Shift == crew.Shift
               && (report.Status == ReportStatus.Submitted || report.Status == ReportStatus.Accepted)
         from line in report.Lines
         where line.IsCrew && line.WorkTypeId == crew.WorkTypeId
         orderby report.Id, line.Position
         select new CrewGroupLine(crew.Id, report.Id, report.EmployeeId, line.Id, line.VariantId, line.Volume,
             line.UnitId, line.CrewRole!, line.Participants.Select(p => p.EmployeeId).ToList(),
             line.WorkPlanItemId, line.SectionId, report.Status == ReportStatus.Accepted)).ToListAsync(ct);

    internal Task<List<CrewSubmittedEmployee>> CrewSubmittedEmployeesAsync(ReportViewer viewer,
        IQueryable<CrewGroupRecord> groups, IReadOnlyList<CrewGroupLine> lines, CancellationToken ct)
    {
        var participants = lines.SelectMany(l => l.Participants).Distinct().ToArray();
        if (participants.Length == 0) return Task.FromResult(new List<CrewSubmittedEmployee>());

        // Достаточно одного отчёта сотрудника за смену, бригадные строки для этого не обязательны.
        return Visible(viewer).AsNoTracking()
            .Where(r => participants.Contains(r.EmployeeId)
                && (r.Status == ReportStatus.Submitted || r.Status == ReportStatus.Accepted)
                && groups.Any(g => g.ConstructionId == r.ConstructionId && g.Date == r.Date && g.Shift == r.Shift))
            .Select(r => new CrewSubmittedEmployee(new(r.ConstructionId, r.Date, r.Shift), r.EmployeeId)).ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<ReportStatus, int>> ClosingCountsAsync(
        ModuleReader system, ModuleClosingScope scope, CancellationToken ct)
    {
        EnsureSystem(system);
        var reports = db.Set<Report>().Where(r => r.Date <= scope.Through);
        if (scope.From is { } from) reports = reports.Where(r => r.Date >= from);
        if (scope.ConstructionId is { } site) reports = reports.Where(r => r.ConstructionId == site);

        // Черновик остаётся препятствием до подачи, включая задержку между концом окна и тактом.
        var counts = await reports.GroupBy(r => new { r.ConstructionId, r.Date, r.Status })
            .Select(g => new { g.Key.ConstructionId, g.Key.Date, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);
        return counts.Where(r => scope.ClosesAnew(r.ConstructionId, r.Date)).GroupBy(r => r.Status)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Count));
    }

    private IQueryable<Report> Expired(DateTimeOffset now)
    {
        var at = now.ToUniversalTime();
        return db.Set<Report>().Where(r => r.Status == ReportStatus.Draft && r.WindowEndsAt <= at);
    }

    /// <summary>Чтение «все отчёты» — только у системы с причиной: человек с ним прочёл бы чужое без охвата.</summary>
    private static void EnsureSystem(ModuleReader reader)
    {
        if (!reader.IsSystem || string.IsNullOrWhiteSpace(reader.SystemReason))
            throw new InvalidOperationException(
                "Автоподача, сводка и перечень закрытия читают все отчёты от имени системы с причиной: у человека охват свой.");
    }

    private IQueryable<Report> Visible(ReportViewer viewer)
    {
        var reports = db.Set<Report>();
        switch (viewer.Reach.Kind)
        {
            case ModuleReachKind.All:
                return reports;
            case ModuleReachKind.Own:
                return viewer.EmployeeId is { } me ? reports.Where(r => r.EmployeeId == me) : reports.Where(_ => false);
            default:
                var sites = viewer.Reach.Sites.ToArray();
                var own = viewer.EmployeeId;
                return reports.Where(r => sites.Contains(r.ConstructionId) || (own != null && r.EmployeeId == own));
        }
    }

    private static IQueryable<Report> Full(IQueryable<Report> reports) =>
        reports.Include(r => r.Lines).ThenInclude(l => l.Materials)
            .Include(r => r.Lines).ThenInclude(l => l.Participants)
            .Include(r => r.Lines).ThenInclude(l => l.Photos)
            .AsSplitQuery();

    /// <summary>
    /// Видно ли читателю СОДЕРЖИМОЕ отчёта (ТЗ WORK-54): поданный — всем, кому виден отчёт; черновик — только сотруднику
    /// отчёта и автору его ввода <paramref name="userId" />. То же правило, что у журнала (<see cref="JournalAsync" />), —
    /// им судят фото (<c>work-17</c>).
    /// </summary>
    public static bool ContentVisible(Report report, ReportViewer viewer, Guid? userId) =>
        ContentReadable(viewer.EmployeeId, userId).Compile()(report);

    /// <summary>
    /// Правило <see cref="ContentVisible" /> выражением — одно на запрос к базе и на отчёт в памяти: поданный — всем, кому
    /// виден отчёт; черновик — сотруднику отчёта <paramref name="me" /> и автору ввода <paramref name="userId" />.
    /// </summary>
    private static Expression<Func<Report, bool>> ContentReadable(Guid? me, Guid? userId) =>
        r => r.SubmittedAt != null || r.EmployeeId == me || (userId != null && r.EnteredByUserId == userId);

    /// <summary>
    /// Фото по идентификатору, если читателю видно содержимое его отчёта (ТЗ WORK-39, WORK-54; <c>work-17</c>), — без
    /// отслеживания. Фото чужого отчёта, фото черновика неавтору и несуществующее — одинаково <c>null</c>.
    /// </summary>
    public Task<LinePhoto?> PhotoAsync(ReportViewer viewer, Guid? userId, Guid photoId, CancellationToken ct)
    {
        var readable = Visible(viewer).Where(ContentReadable(viewer.EmployeeId, userId));
        return db.Set<LinePhoto>().AsNoTracking()
            .Where(p => p.Id == photoId && readable.Any(r => r.Lines.Any(l => l.Id == p.LineId)))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Строка, которой принадлежит фото с этим идентификатором, или <c>null</c>. Только для сравнения при загрузке (ТЗ
    /// MOB-15): «та же строка — повтор, другая — идентификатор занят»; наружу не уходит.
    /// </summary>
    public Task<Guid?> PhotoLineAsync(Guid photoId, CancellationToken ct) =>
        db.Set<LinePhoto>().AsNoTracking().Where(p => p.Id == photoId).Select(p => (Guid?)p.LineId).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Снято ли фото с этим идентификатором со строки (<see cref="LinePhotoRemoval" />): поздний файл снятого фото не
    /// возвращает его в строку (ТЗ MOB-15).
    /// </summary>
    public Task<bool> PhotoRemovedAsync(Guid photoId, CancellationToken ct) =>
        db.Set<LinePhotoRemoval>().AnyAsync(r => r.PhotoId == photoId, ct);

    /// <summary>Следы снятия у фото, которые строки называют снова, — с отслеживанием: правка их снимает.</summary>
    public async Task<IReadOnlyList<LinePhotoRemoval>> RemovalsAsync(IReadOnlyCollection<Guid> photoIds, CancellationToken ct) =>
        photoIds.Count == 0 ? [] : await db.Set<LinePhotoRemoval>().Where(r => photoIds.Contains(r.PhotoId)).ToListAsync(ct);

    /// <summary>
    /// Идентификаторы фото, которые уже принадлежат ДРУГИМ строкам: идентификатор от клиента и ключ таблицы, и без
    /// вопроса отказ пришёл бы нарушением ключа при сохранении.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> PhotosOfOtherLinesAsync(
        IReadOnlyCollection<(Guid Photo, Guid Line)> named, CancellationToken ct)
    {
        if (named.Count == 0) return [];
        var ids = named.Select(n => n.Photo).Distinct().ToArray();
        var held = await db.Set<LinePhoto>().AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.LineId }).ToListAsync(ct);
        var lineOf = named.ToDictionary(n => n.Photo, n => n.Line);
        return [.. held.Where(h => lineOf[h.Id] != h.LineId).Select(h => h.Id)];
    }

    /// <summary>
    /// Отчёт сотрудника с той же четвёркой «стройка, дата, смена» (ТЗ WORK-4, MOB-35), кроме
    /// <paramref name="except" />; <c>null</c> — четвёрка свободна. Вопрос задаёт путь записи ДО позиций перечня:
    /// уникальный индекс <c>ux_reports_key</c> остаётся последним рубежом для двух запросов разом.
    /// </summary>
    public Task<Guid?> KeyHolderAsync(
        Guid employeeId, Guid constructionId, DateOnly date, ReportShift shift, Guid except, CancellationToken ct) =>
        db.Set<Report>().AsNoTracking()
            .Where(r => r.EmployeeId == employeeId && r.ConstructionId == constructionId && r.Date == date
                        && r.Shift == shift && r.Id != except)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Идентификаторы строк, которые уже стоят в ДРУГИХ отчётах. Идентификатор строки — от клиента и ключ таблицы:
    /// занятый не вставить, и без вопроса отказ пришёл бы нарушением ключа при сохранении.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> LinesOfOtherReportsAsync(
        Guid reportId, IReadOnlyCollection<Guid> lineIds, CancellationToken ct) =>
        lineIds.Count == 0
            ? []
            : await db.Set<ReportLine>().AsNoTracking()
                .Where(l => lineIds.Contains(l.Id) && l.ReportId != reportId)
                .Select(l => l.Id)
                .ToListAsync(ct);
}
