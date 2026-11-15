using System.Text.Json;
using BHS.CRG.Modules.Work.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Endpoints;

public sealed record ReportMaterialDto(Guid NomenclatureId, string? Note);

public sealed record ReportLineDto(
    Guid Id, int Position, Guid WorkTypeId, Guid? VariantId, decimal Volume, Guid UnitId, Guid? SectionId,
    string? Location, string? Comment, bool IsCrew, Guid WorkPlanItemId, JsonElement Details,
    IReadOnlyList<ReportMaterialDto> Materials, IReadOnlyList<Guid> Participants);

/// <summary>Кто ввёл отчёт за сотрудника (ТЗ WORK-31): учётная запись и имя снимком на момент заведения.</summary>
public sealed record ReportEnteredByDto(Guid UserId, string Name);

/// <summary>
/// Отчёт в ответе (ТЗ WORK-4, WORK-31, MOB-15, MOB-16, MOB-21).
/// </summary>
/// <param name="Seq">Номер последнего изменения в серверной последовательности — по нему курсор <c>since</c>.</param>
/// <param name="Version">Значение для <c>If-Match</c> правки и удаления.</param>
/// <param name="AssignedOnDate">Назначен ли сотрудник на стройку на дату отчёта (MOB-21) — пометка, не отказ.</param>
/// <param name="EnteredBy">Автор ввода за сотрудника; <c>null</c> — отчёт завёл сам сотрудник.</param>
/// <param name="Replayed">Ответ — повтор уже применённого запроса (MOB-15): ничего не записано второй раз.</param>
public sealed record ReportDto(
    Guid Id, Guid EmployeeId, Guid ConstructionId, DateOnly Date, string Shift, decimal? Hours, string Status,
    string? Note, long Seq, string Version, bool AssignedOnDate, ReportEnteredByDto? EnteredBy,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<ReportLineDto> Lines, bool Replayed);

/// <summary>
/// Отчёты в ответах адресов. <c>assignedOnDate</c> считается здесь, на чтении, по назначениям <c>work-11</c> и не
/// хранится: назначение могут поправить задним числом, и пометка обязана это увидеть (design <c>work-13</c>).
/// Назначение любого вида — монтажник или менеджер, — как у <c>/api/work/sites/mine</c>.
/// </summary>
public sealed class ReportViews(WorkDbContext db)
{
    /// <param name="version">Версия из протокола у повтора правки (MOB-15); иначе — текущая версия строки.</param>
    public async Task<ReportDto> OneAsync(Report report, bool replayed = false, string? version = null, CancellationToken ct = default) =>
        (await ManyAsync([report], ct, replayed, version))[0];

    public async Task<IReadOnlyList<ReportDto>> ManyAsync(
        IReadOnlyList<Report> reports, CancellationToken ct, bool replayed = false, string? version = null)
    {
        var employees = reports.Select(r => r.EmployeeId).Distinct().ToList();
        var assignments = employees.Count == 0
            ? []
            : await db.Assignments.AsNoTracking().Where(a => employees.Contains(a.EmployeeId)).ToListAsync(ct);
        return
        [
            .. reports.Select(r => Dto(r,
                assignments.Any(a => a.EmployeeId == r.EmployeeId && a.ConstructionId == r.ConstructionId && a.Covers(r.Date)),
                replayed, version ?? db.VersionOf(r))),
        ];
    }

    private static ReportDto Dto(Report r, bool assigned, bool replayed, string version) => new(
        r.Id, r.EmployeeId, r.ConstructionId, r.Date, Report.ShiftCode(r.Shift), r.Hours, Report.StatusCode(r.Status),
        r.Note, r.Seq, version, assigned,
        r.EnteredByUserId is { } by ? new ReportEnteredByDto(by, r.EnteredByName ?? by.ToString()) : null,
        r.CreatedAt, r.UpdatedAt,
        [.. r.Lines.OrderBy(l => l.Position).Select(Line)],
        replayed);

    private static ReportLineDto Line(ReportLine l)
    {
        using var details = JsonDocument.Parse(l.Details);
        return new ReportLineDto(
            l.Id, l.Position, l.WorkTypeId, l.VariantId, l.Volume, l.UnitId, l.SectionId, l.Location, l.Comment,
            l.IsCrew, l.WorkPlanItemId, details.RootElement.Clone(),
            [.. l.Materials.OrderBy(m => m.Position).Select(m => new ReportMaterialDto(m.NomenclatureId, m.Note))],
            [.. l.Participants.Select(p => p.EmployeeId)]);
    }
}
