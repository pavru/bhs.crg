using System.Text.Json;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Crew;
using BHS.CRG.Modules.Work.Photos;
using BHS.CRG.Modules.Work.Submission;

namespace BHS.CRG.Modules.Work.Data;

/// <summary>Шапка отчёта из запроса: всё, кроме сотрудника — его задаёт связь учётной записи или адрес ввода.</summary>
public sealed record ReportHeaderValues(Guid ConstructionId, DateOnly Date, ReportShift Shift, decimal? Hours, string? Note);

/// <summary>
/// Справочник, без которого отчёт не записать, на экземпляре не заведён (ТЗ CORE-20.4): отказ, а не «вид работы не
/// найден». Код — тот же, что у назначений и двери сотрудников, <see cref="IModuleEmployees.NotSetUpCode" />: адрес
/// отвечает по нему 409 <c>{ code, error }</c>.
/// </summary>
public sealed class WorkDirectoryMissingException(string message) : ConflictException(message)
{
    public string Code => IModuleEmployees.NotSetUpCode;
}

/// <summary>
/// Четвёрка «сотрудник, стройка, дата, смена» уже занята другим отчётом того же сотрудника (ТЗ WORK-4, MOB-35):
/// адрес отвечает 409 с <see cref="ExistingId" />, чтобы клиент открыл существующий отчёт, а не завёл второй.
/// </summary>
public sealed class ReportKeyTakenException(Guid existingId) : ConflictException(
    "У сотрудника уже есть отчёт на этой стройке за эту дату и смену — правьте его, а не заводите второй.")
{
    public Guid ExistingId => existingId;
}

/// <summary>
/// Путь записи отчёта и его строк (ТЗ WORK-4, WORK-5, WORK-7, WORK-50, CORE-12, CORE-34.4; change
/// <c>work-13-report-model</c>) — один на заведение и правку, на свой отчёт и на ввод за сотрудника: правила у них
/// общие, различаются только права и охват, а их проверяют адреса до вызова.
///
/// <para><b>Порядок.</b> Сначала всё, что ничего не пишет: шапка и строки по правилам, новые ссылки на архив,
/// занятость ключа отчёта и идентификаторов строк, охрана сведений. Затем позиции перечня в ядре (своей
/// транзакцией, ТЗ CORE-4) — последним, потому что это единственный шаг с побочным следом, — и только потом строки в
/// контекст модуля. Сохраняет вызывающий: номер последовательности и протокол изменений — его дело (PR 2). Сбой
/// сохранения после заведения позиции оставляет её без держателя — допустимое состояние перечня, повтор найдёт её
/// же.</para>
///
/// <para><b>Сведения строки</b> — JSON-объект по схеме типа <see cref="WorkRecordTypes.LineDetailsCode" /> — проходят
/// охрану записи (<see cref="IModuleWriteGuard" />): новые строки одним пакетом, правленые — сверкой с лежащим, и
/// только когда сведения изменились. Перепись <c>RecordWriteGuardCoverageTests</c> находит это место по именам
/// <c>ReportLine.Create</c> и <c>ApplyLine</c>.</para>
///
/// <para><b>Фото строки</b> (<c>work-17</c>, ТЗ WORK-34): строка называет идентификаторы своих фото, новое ждёт файла,
/// не названное снимается. Пути снятых файлов правка возвращает: удаляет их вызывающий ПОСЛЕ фиксации — ссылка уходит
/// первой, и сбой оставляет осиротевший файл, а не фото, которое не открывается.</para>
/// </summary>
public sealed class ReportWriter(
    WorkDbContext db, IModuleConstructions constructions, IModuleWorkTypes workTypes, IModuleTypes types,
    IModuleWriteGuard guard, ReportReferenceCheck references, WorkPlanItemLink planItems, ReportReads reads,
    IModuleSettings settings, CrewCandidates crew)
{
    /// <summary>Завести отчёт со строками в контекст модуля (без сохранения).</summary>
    public async Task<Report> CreateAsync(
        Guid reportId, Guid employeeId, ReportHeaderValues header, IReadOnlyList<ReportLineValues> lines, long seq,
        DateTimeOffset now, CancellationToken ct)
    {
        var site = await CheckAsync(header, lines, ct);
        await references.EnsureAsync([],
            [.. ReportReferenceCheck.OfHeader(employeeId, header.ConstructionId), .. LineReferences(lines)], ct);
        await EnsureFreeAsync(reportId, employeeId, header, lines, ct);

        var report = NewReport(reportId, employeeId, header, seq, now, site);
        await PlaceLinesAsync(report, site, storedSite: site.Id, lines, now, closingId: null, ct);
        db.Add(report);
        return report;
    }

    /// <summary>
    /// Править отчёт: шапку и строки целиком — строки, которых нет в запросе, удаляются. <paramref name="report" />
    /// обязан быть прочитан с отслеживанием и со строками, материалами, участниками и фото. Возвращает пути файлов
    /// снятых фото — их удаляют после фиксации.
    /// </summary>
    public async Task<IReadOnlyList<string>> ChangeAsync(
        Report report, ReportHeaderValues header, IReadOnlyList<ReportLineValues> lines, long seq, DateTimeOffset now,
        CancellationToken ct, Guid? closingId = null)
    {
        // Ключ поданного не меняется (ТЗ MOB-35) — до всего, что оставляет след в ядре.
        if (report.KeyChanges(header.ConstructionId, header.Date, header.Shift) && !report.KeyEditable())
            throw new ConflictException(Report.KeyLocked);
        var site = await CheckAsync(header, lines, ct);
        await references.EnsureAsync(
            [.. ReportReferenceCheck.OfHeader(report.EmployeeId, report.ConstructionId), .. report.Lines.SelectMany(ReportReferenceCheck.OfLine)],
            [.. ReportReferenceCheck.OfHeader(report.EmployeeId, header.ConstructionId), .. LineReferences(lines)], ct);
        await EnsureFreeAsync(report.Id, report.EmployeeId, header, lines, ct);

        // Ключи лежащих строк — на ПРЕЖНЕЙ стройке: отчёт, перенесённый на другую, меняет ключ каждой строки.
        var storedSite = report.ConstructionId;
        var dropped = await PlaceLinesAsync(report, site, storedSite, lines, now, closingId, ct);
        ChangeHeader(report, header, seq, now, site);
        return dropped;
    }

    // Шапка — колонки закрытого типа: данных по схеме у неё нет, охране сверять нечего (перепись: Free). Конец окна
    // подачи — по поясу стройки отчёта (ТЗ WORK-52): стройка в другом поясе со старым окном подала бы по чужим суткам.
    private static Report NewReport(
        Guid id, Guid employeeId, ReportHeaderValues h, long seq, DateTimeOffset now, ModuleConstruction site) =>
        Report.Create(id, employeeId, h.ConstructionId, h.Date, h.Shift, h.Hours, h.Note, seq, now,
            SubmissionWindow.EndsAt(site.TimeZoneId, h.Date, h.Shift));

    private static void ChangeHeader(Report report, ReportHeaderValues h, long seq, DateTimeOffset now, ModuleConstruction site) =>
        report.ChangeHeader(h.ConstructionId, h.Date, h.Shift, h.Hours, h.Note, seq, now,
            SubmissionWindow.EndsAt(site.TimeZoneId, h.Date, h.Shift));

    private static IEnumerable<ReportReference> LineReferences(IReadOnlyList<ReportLineValues> lines) =>
        lines.SelectMany((line, position) => ReportReferenceCheck.OfLine(line, position));

    /// <summary>Правила шапки и строк по ответам портов; стройка отчёта — для раздела и ключа позиции.</summary>
    private async Task<ModuleConstruction> CheckAsync(
        ReportHeaderValues header, IReadOnlyList<ReportLineValues> lines, CancellationToken ct)
    {
        if (header.ConstructionId == Guid.Empty) throw new InvalidRequestException(ReportRules.NoSite);
        ReportRules.EnsureHours(header.Hours);
        var site = (await constructions.ListAsync(ct)).FirstOrDefault(c => c.Id == header.ConstructionId)
                   ?? throw new InvalidRequestException("Стройка отчёта (constructionId) не найдена.");

        if (lines.GroupBy(l => l.Id).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new InvalidRequestException($"Строка {twice.Key} названа в отчёте дважды: идентификатор строки — один на строку.");
        if (lines.SelectMany(l => l.Photos ?? []).GroupBy(p => p.Id).FirstOrDefault(g => g.Count() > 1) is { } repeatedPhoto)
            throw new InvalidRequestException($"Одно фото ({repeatedPhoto.Key}) может быть названо только в одной строке отчёта.");
        if (lines.Count == 0) return site;
        await EnsurePhotoCountAsync(lines, ct);

        var known = await workTypes.GetAsync([.. lines.Select(l => l.WorkTypeId).Distinct()], ct)
                    ?? throw new WorkDirectoryMissingException(IModuleWorkTypes.NotSetUp);
        for (var i = 0; i < lines.Count; i++)
            ReportRules.EnsureLine(i, lines[i], known.FirstOrDefault(w => w.Id == lines[i].WorkTypeId), site);
        await crew.EnsureAssignedAsync(header, lines, ct);
        return site;
    }

    /// <summary>
    /// Ключ отчёта и идентификаторы строк свободны — до позиций перечня, чтобы отказ не оставил следа в ядре.
    /// Последний рубеж для двух запросов разом — уникальные индексы базы.
    /// </summary>
    private async Task EnsureFreeAsync(
        Guid reportId, Guid employeeId, ReportHeaderValues header, IReadOnlyList<ReportLineValues> lines, CancellationToken ct)
    {
        if (await reads.KeyHolderAsync(employeeId, header.ConstructionId, header.Date, header.Shift, reportId, ct) is { } holder)
            throw new ReportKeyTakenException(holder);

        var taken = (await reads.LinesOfOtherReportsAsync(reportId, [.. lines.Select(l => l.Id)], ct)).ToHashSet();
        for (var i = 0; i < lines.Count; i++)
            if (taken.Contains(lines[i].Id))
                throw new ConflictException(
                    $"Строка {i + 1}: идентификатор строки (id) уже занят строкой другого отчёта — у новой строки " +
                    "идентификатор новый.");

        // Идентификатор фото — ключ таблицы: фото другой строки (и того же отчёта тоже) не переносится, а заводится новым.
        var named = lines.SelectMany(l => (l.Photos ?? []).Select(p => (Photo: p.Id, Line: l.Id))).ToList();
        var busy = (await reads.PhotosOfOtherLinesAsync(named, ct)).ToHashSet();
        for (var i = 0; i < lines.Count; i++)
            if ((lines[i].Photos ?? []).Any(p => busy.Contains(p.Id)))
                throw new ConflictException($"Строка {i + 1}: {LinePhotos.IdTaken}");
    }

    /// <summary>Фото на строку — не больше настройки модуля (ТЗ WORK-33): объявленные считаются вместе с пришедшими.</summary>
    private async Task EnsurePhotoCountAsync(IReadOnlyList<ReportLineValues> lines, CancellationToken ct)
    {
        if (lines.All(l => l.Photos is not { Count: > 0 })) return;
        var max = await LinePhotos.PerLineAsync(settings, ct);
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].Photos is { } photos && photos.Count > max)
                throw new InvalidRequestException($"Строка {i + 1}: {LinePhotos.TooMany(max)}");
    }

    /// <summary>
    /// Охрана сведений, затем позиции перечня, затем строки и их фото; строки, не названные в правке, удаляются. Пути
    /// файлов снятых фото — снятых строкой и ушедших вместе с удалённой строкой — в ответе.
    /// </summary>
    private async Task<IReadOnlyList<string>> PlaceLinesAsync(
        Report report, ModuleConstruction site, Guid storedSite, IReadOnlyList<ReportLineValues> lines, DateTimeOffset now,
        Guid? closingId, CancellationToken ct)
    {
        var stored = report.Lines.ToDictionary(l => l.Id);
        await EnsureAllowedAsync(lines, stored, ct);
        var items = await planItems.ResolveAsync(site.Id,
            [.. lines.Select((l, i) => stored.TryGetValue(l.Id, out var was)
                ? new PlanItemDemand(i, l, was.KeyOn(storedSite), was.WorkPlanItemId)
                : new PlanItemDemand(i, l, null, null))], ct);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var was = stored.GetValueOrDefault(line.Id);
            if (was is null) report.Lines.Add(ReportLine.Create(report.Id, i, line, items[line.Id]));
            else was.ApplyLine(i, line, items[line.Id]);
        }

        // Фото, снятые строкой, оставляют след: их поздний файл не вернёт фото (LinePhotoRemoval). Снова названное — след
        // снимает: фото объявлено заново и ждёт файла.
        var named = lines.SelectMany(l => l.Photos ?? []).Select(p => p.Id).ToHashSet();
        foreach (var removal in await reads.RemovalsAsync(named, ct)) db.Remove(removal);
        var dropped = new List<string>();
        foreach (var line in lines)
            foreach (var photo in report.Lines.First(l => l.Id == line.Id).ApplyPhotos(line.Photos, now, closingId))
            {
                db.Add(LinePhotoRemoval.Of(photo, now));
                dropped.AddRange(photo.Paths());
            }

        var kept = lines.Select(l => l.Id).ToHashSet();
        dropped.AddRange(report.Lines.Where(l => !kept.Contains(l.Id)).SelectMany(l => l.Photos).SelectMany(p => p.Paths()));
        report.Lines.RemoveAll(l => !kept.Contains(l.Id));
        return dropped;
    }

    /// <summary>
    /// Сведения строк — JSON-объект без часов (ТЗ WORK-7) и по схеме типа (ТЗ CORE-19.1, CORE-20.2): запертое поле
    /// модуля не кладётся в JSON рядом с колонкой, поле заказчика — по своему виду. Новые строки — одним пакетом
    /// охраны, правленые — по одной и только с изменёнными сведениями. Пустые сведения при незаведённом типе — не
    /// отказ: проверять нечего. Отказ называет первую по порядку строку.
    /// </summary>
    private async Task EnsureAllowedAsync(
        IReadOnlyList<ReportLineValues> lines, IReadOnlyDictionary<Guid, ReportLine> stored, CancellationToken ct)
    {
        var details = new string[lines.Count];
        for (var i = 0; i < lines.Count; i++) details[i] = DetailsOf(i, lines[i].Details);

        var typeId = await types.FindAsync(WorkRecordTypes.LineDetailsCode, ct);
        if (typeId is not { } type)
        {
            var at = Array.FindIndex(details, d => d != "{}");
            if (at >= 0)
                throw new ConflictException(
                    $"Строка {at + 1}: сведения не принять — тип «{WorkRecordTypes.LineDetailsCode}» на экземпляре не " +
                    "заведён (его справочники-цели появятся позже). Причину называет журнал запуска.");
            return;
        }

        var refused = new SortedDictionary<int, IReadOnlyList<ModuleWriteRefusal>>();
        var fresh = Enumerable.Range(0, lines.Count).Where(i => !stored.ContainsKey(lines[i].Id)).ToList();
        if (fresh.Count > 0)
        {
            var verdicts = await guard.CreationRefusalsAsync(type, [.. fresh.Select(i => details[i])], ct);
            foreach (var (line, verdict) in fresh.Zip(verdicts))
                if (verdict.Count > 0) refused[line] = verdict;
        }
        for (var i = 0; i < lines.Count; i++)
        {
            if (!stored.TryGetValue(lines[i].Id, out var was) || was.Details == details[i]) continue;
            var verdict = await guard.RefusalsAsync(type, was.Details, details[i], ct);
            if (verdict.Count > 0) refused[i] = verdict;
        }

        if (refused.Count == 0) return;
        var (position, refusals) = refused.First();
        throw new InvalidRequestException(
            $"Строка {position + 1}, сведения: " + string.Join("; ", refusals.Select(r => r.Message)));
    }

    /// <summary>Сведения строки как они лягут в колонку: пусто — <c>{}</c>; не объект или часы в нём — отказ.</summary>
    private static string DetailsOf(int position, string? details)
    {
        if (string.IsNullOrWhiteSpace(details)) return "{}";
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(details);
        }
        catch (JsonException)
        {
            throw new InvalidRequestException($"Строка {position + 1}: сведения (details) — не JSON.");
        }
        using (parsed)
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidRequestException($"Строка {position + 1}: сведения (details) — не JSON-объект.");
            ReportRules.EnsureNoLineHours(position, parsed.RootElement.EnumerateObject().Select(p => p.Name));
        }
        return details;
    }
}
