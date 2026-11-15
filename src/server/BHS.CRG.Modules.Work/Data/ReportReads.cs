using System.Globalization;
using BHS.CRG.Modules.Access;
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
public sealed class ReportReads(WorkDbContext db)
{
    /// <summary>
    /// Отчёт по идентификатору, если он виден читателю, — с отслеживанием (версия строки — теневое свойство) и со
    /// строками, материалами и участниками. Чужой и несуществующий — одинаково <c>null</c>.
    /// </summary>
    public Task<Report?> FindAsync(ReportViewer viewer, Guid id, CancellationToken ct) =>
        Full(Visible(viewer)).FirstOrDefaultAsync(r => r.Id == id, ct);

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
    public async Task<ReportFeed> SinceAsync(Guid employeeId, long since, int limit, CancellationToken ct)
    {
        var heads = await db.Set<Report>().AsNoTracking()
            .Where(r => r.EmployeeId == employeeId && r.Seq > since)
            .OrderBy(r => r.Seq).Take(limit + 1)
            .Select(r => new { r.Id, r.Seq, Version = EF.Property<uint>(r, WorkDbContext.RowVersion) })
            .ToListAsync(ct);
        var deleted = await db.Set<ReportChange>().AsNoTracking()
            .Where(c => c.EmployeeId == employeeId && c.Seq > since && c.Kind == ReportChangeKind.Deleted)
            .OrderBy(c => c.Seq).Take(limit + 1)
            .Select(c => new ReportGone(c.ReportId, c.Seq)).ToListAsync(ct);

        var merged = heads.Select(h => (h.Seq, Report: (Guid?)h.Id, Gone: (ReportGone?)null))
            .Concat(deleted.Select(d => (d.Seq, Report: (Guid?)null, Gone: (ReportGone?)d)))
            .OrderBy(e => e.Seq).ToList();
        var page = merged.Take(limit).ToList();
        var ids = page.Where(e => e.Report is not null).Select(e => e.Report!.Value).ToList();
        var bodies = ids.Count == 0
            ? new Dictionary<Guid, Report>()
            : await Full(db.Set<Report>().AsNoTracking().Where(r => ids.Contains(r.Id))).ToDictionaryAsync(r => r.Id, ct);
        // Отчёт, удалённый между двумя чтениями, пропускается: его удаление придёт следующей выдачей — номер больше.
        return new ReportFeed(
            [.. ids.Where(bodies.ContainsKey).Select(id => bodies[id])],
            heads.ToDictionary(h => h.Id, h => h.Version.ToString(CultureInfo.InvariantCulture)),
            [.. page.Where(e => e.Gone is not null).Select(e => e.Gone!)],
            page.Count == 0 ? since : page[^1].Seq,
            merged.Count > limit);
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
        var me = viewer.EmployeeId;
        var live = await Visible(viewer).AsNoTracking().Where(r => r.Id == id)
            .Select(r => new { r.SubmittedAt, r.EmployeeId, r.EnteredByUserId })
            .FirstOrDefaultAsync(ct);
        var readable = live is not null
            ? live.SubmittedAt != null || live.EmployeeId == me || (userId != null && live.EnteredByUserId == userId)
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
    /// годится. Возвращённые не выбираются: автоподача их не трогает.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ExpiredDraftsAsync(
        ModuleReader system, DateTimeOffset now, IReadOnlyCollection<Guid> except, int limit, CancellationToken ct)
    {
        EnsureSystem(system);
        var skipped = except.ToArray();
        return await Expired(now).AsNoTracking().Where(r => !skipped.Contains(r.Id))
            .OrderBy(r => r.WindowEndsAt).ThenBy(r => r.Id).Take(limit)
            .Select(r => r.Id).ToListAsync(ct);
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
                "Черновики с истёкшим окном читает только автоподача — от имени системы с причиной: у человека охват свой.");
    }

    private IQueryable<Report> Visible(ReportViewer viewer)
    {
        var reports = db.Set<Report>();
        switch (viewer.Reach.Kind)
        {
            case ModuleReachKind.All:
                return reports;
            case ModuleReachKind.Own:
                var me = viewer.EmployeeId ?? Guid.Empty;
                return reports.Where(r => r.EmployeeId == me);
            default:
                var sites = viewer.Reach.Sites.ToArray();
                var own = viewer.EmployeeId;
                return reports.Where(r => sites.Contains(r.ConstructionId) || (own != null && r.EmployeeId == own));
        }
    }

    private static IQueryable<Report> Full(IQueryable<Report> reports) =>
        reports.Include(r => r.Lines).ThenInclude(l => l.Materials)
            .Include(r => r.Lines).ThenInclude(l => l.Participants)
            .AsSplitQuery();

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
