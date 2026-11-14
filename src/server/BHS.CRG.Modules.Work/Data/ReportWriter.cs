using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Ports;

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
/// Путь записи отчёта и его строк (ТЗ WORK-4, WORK-5, WORK-50, CORE-12, CORE-34.4; change <c>work-13-report-model</c>)
/// — один на заведение и правку, на свой отчёт и на ввод за сотрудника: правила у них общие, различаются только
/// права и охват, а их проверяют адреса до вызова.
///
/// <para><b>Порядок.</b> Сначала то, что ничего не пишет: шапка и строки по правилам, новые ссылки на архив, затем
/// позиции перечня в ядре (своей транзакцией, ТЗ CORE-4), и только потом — строки в контекст модуля, каждая после
/// охраны своих сведений. Сохраняет вызывающий: номер последовательности и протокол изменений — его дело (PR 2).
/// Отказ после заведения позиции оставляет её без держателя — допустимое состояние перечня, повтор найдёт её же.</para>
///
/// <para><b>Сведения строки</b> — JSON по схеме типа <see cref="WorkRecordTypes.LineDetailsCode" /> — проходят охрану
/// записи (<see cref="IModuleWriteGuard" />): правка сверяется с лежащим, новая строка — как создание. Перепись
/// <c>RecordWriteGuardCoverageTests</c> находит это место по именам <c>ReportLine.Create</c> и <c>ApplyLine</c>.</para>
/// </summary>
public sealed class ReportWriter(
    WorkDbContext db, IModuleConstructions constructions, IModuleWorkTypes workTypes, IModuleTypes types,
    IModuleWriteGuard guard, ReportReferenceCheck references, WorkPlanItemLink planItems)
{
    /// <summary>Завести отчёт со строками в контекст модуля (без сохранения).</summary>
    public async Task<Report> CreateAsync(
        Guid reportId, Guid employeeId, ReportHeaderValues header, IReadOnlyList<ReportLineValues> lines, long seq,
        DateTimeOffset now, CancellationToken ct)
    {
        var site = await CheckAsync(header, lines, ct);
        await references.EnsureAsync([],
            [.. ReportReferenceCheck.OfHeader(employeeId, header.ConstructionId), .. lines.SelectMany(ReportReferenceCheck.OfLine)], ct);

        var report = NewReport(reportId, employeeId, header, seq, now);
        await PlaceLinesAsync(report, site, storedSite: site.Id, lines, ct);
        db.Add(report);
        return report;
    }

    /// <summary>
    /// Править отчёт: шапку и строки целиком — строки, которых нет в запросе, удаляются. <paramref name="report" />
    /// обязан быть прочитан с отслеживанием и со строками, материалами и участниками.
    /// </summary>
    public async Task ChangeAsync(
        Report report, ReportHeaderValues header, IReadOnlyList<ReportLineValues> lines, long seq, DateTimeOffset now,
        CancellationToken ct)
    {
        var site = await CheckAsync(header, lines, ct);
        await references.EnsureAsync(
            [.. ReportReferenceCheck.OfHeader(report.EmployeeId, report.ConstructionId), .. report.Lines.SelectMany(ReportReferenceCheck.OfLine)],
            [.. ReportReferenceCheck.OfHeader(report.EmployeeId, header.ConstructionId), .. lines.SelectMany(ReportReferenceCheck.OfLine)], ct);

        // Ключи лежащих строк — на ПРЕЖНЕЙ стройке: отчёт, перенесённый на другую, меняет ключ каждой строки.
        var storedSite = report.ConstructionId;
        ChangeHeader(report, header, seq, now);
        await PlaceLinesAsync(report, site, storedSite, lines, ct);
    }

    // Шапка — колонки закрытого типа: данных по схеме у неё нет, охране сверять нечего (перепись: Free).
    private static Report NewReport(Guid id, Guid employeeId, ReportHeaderValues h, long seq, DateTimeOffset now) =>
        Report.Create(id, employeeId, h.ConstructionId, h.Date, h.Shift, h.Hours, h.Note, seq, now);

    private static void ChangeHeader(Report report, ReportHeaderValues h, long seq, DateTimeOffset now) =>
        report.ChangeHeader(h.ConstructionId, h.Date, h.Shift, h.Hours, h.Note, seq, now);

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
        if (lines.Count == 0) return site;

        var known = await workTypes.GetAsync([.. lines.Select(l => l.WorkTypeId).Distinct()], ct)
                    ?? throw new WorkDirectoryMissingException(IModuleWorkTypes.NotSetUp);
        for (var i = 0; i < lines.Count; i++)
            ReportRules.EnsureLine(i, lines[i], known.FirstOrDefault(w => w.Id == lines[i].WorkTypeId), site);
        return site;
    }

    /// <summary>Позиции перечня, затем строки — каждая после охраны своих сведений; лишние строки удаляются.</summary>
    private async Task PlaceLinesAsync(
        Report report, ModuleConstruction site, Guid storedSite, IReadOnlyList<ReportLineValues> lines, CancellationToken ct)
    {
        var stored = report.Lines.ToDictionary(l => l.Id);
        var items = await planItems.ResolveAsync(site.Id,
            [.. lines.Select((l, i) => stored.TryGetValue(l.Id, out var was)
                ? new PlanItemDemand(i, l, was.KeyOn(storedSite), was.WorkPlanItemId)
                : new PlanItemDemand(i, l, null, null))], ct);
        var typeId = await types.FindAsync(WorkRecordTypes.LineDetailsCode, ct);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var was = stored.GetValueOrDefault(line.Id);
            await EnsureAllowedAsync(typeId, i, was?.Details, line.Details, ct);
            if (was is null) report.Lines.Add(ReportLine.Create(report.Id, i, line, items[line.Id]));
            else was.ApplyLine(i, line, items[line.Id]);
        }

        var kept = lines.Select(l => l.Id).ToHashSet();
        report.Lines.RemoveAll(l => !kept.Contains(l.Id));
    }

    /// <summary>
    /// Охрана сведений строки по схеме типа (ТЗ CORE-19.1, CORE-20.2): запертое поле модуля не кладётся в JSON рядом с
    /// колонкой, поле заказчика — по своему виду. Пустые сведения при незаведённом типе — не отказ: проверять нечего.
    /// </summary>
    private async Task EnsureAllowedAsync(Guid? typeId, int position, string? stored, string? incoming, CancellationToken ct)
    {
        var json = string.IsNullOrWhiteSpace(incoming) ? "{}" : incoming;
        if (typeId is not { } type)
        {
            if (json.Trim() is "{}") return;
            throw new ConflictException(
                $"Строка {position + 1}: сведения не принять — тип «{WorkRecordTypes.LineDetailsCode}» на экземпляре не " +
                "заведён (его справочники-цели появятся позже). Причину называет журнал запуска.");
        }

        var refusals = await guard.RefusalsAsync(type, stored, json, ct);
        if (refusals.Count > 0)
            throw new InvalidRequestException(
                $"Строка {position + 1}, сведения: " + string.Join("; ", refusals.Select(r => r.Message)));
    }
}
