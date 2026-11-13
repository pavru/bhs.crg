using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Access;
using BHS.CRG.Modules.Work.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Endpoints;

/// <summary>Тело заведения и правки назначения.</summary>
/// <param name="EmployeeId">У правки — только ради отказа: сотрудника правкой не сменить.</param>
public sealed record AssignmentRequest(
    Guid? EmployeeId, Guid? ConstructionId, string? Kind, DateOnly? DateFrom, DateOnly? DateTo);

/// <summary>
/// Адреса назначений (ТЗ WORK-21, WORK-46, AUTH-8, AUTH-9; change <c>work-11-assignments</c>).
///
/// <para><b>Ворота.</b> Группа модуля закрыта воротами <c>module:work</c>; все адреса назначений — правом
/// <c>work.assignment.edit</c>, а стройку проверяет <see cref="AssignmentGate" /> — одним местом для записи и для
/// признаков чтения.</para>
///
/// <para><b>Версия.</b> Правка и снятие называют <c>If-Match</c> (значение поля <c>version</c>). Заведение — нет:
/// прежней версии у новой записи не бывает, а от двойного заведения защищает проверка пересечений — повтор того
/// же назначения получает 409 с названием уже заведённого (design).</para>
///
/// <para><b>Порядок отказов:</b> запрос (400) → запись (404) → версия (400/409) → стройка (403) → справочник
/// (409) → сотрудник (422) → пересечение и «последний менеджер» (409, под замком стройки).</para>
/// </summary>
public static class AssignmentEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var assign = AppPolicies.Permission(WorkModule.Assign);
        var group = endpoints.MapGroup("/api/work/assignments").WithTags("Назначения").RequireAuthorization(assign);
        group.MapGet("", ListAsync);
        group.MapPost("", CreateAsync);
        group.MapPut("/{id:guid}", ChangeAsync);
        group.MapDelete("/{id:guid}", RemoveAsync);
        AssignmentBulkEndpoints.Map(group);
    }

    /// <summary>Назначения стройки — с признаками «можно назначать», «можно изменить», «можно снять».</summary>
    private static async Task<IResult> ListAsync(
        Guid? constructionId, WorkDbContext db, AssignmentGate gate, IModuleConstructions constructions,
        IModuleEmployees employees, IModuleClock clock, CancellationToken ct)
    {
        if (constructionId is not { } siteId)
            throw new InvalidRequestException("Не названа стройка (constructionId): назначения читаются по стройке.");
        var site = await SiteAsync(constructions, siteId, ct);
        // С отслеживанием: версия строки — теневое свойство, и у строки без отслеживания её нет.
        var assignments = await db.Assignments.Where(a => a.ConstructionId == siteId).ToListAsync(ct);
        if (await employees.GetAsync([.. assignments.Select(a => a.EmployeeId).Distinct()], ct) is not { } found)
            return AssignmentRules.DirectoryMissing();

        var today = await clock.TodayOnSiteAsync(siteId, ct);
        var refusal = await gate.RefusalAsync(siteId, ct);
        return Results.Ok(new
        {
            construction = new AssignmentSiteDto(site.Id, site.Name, site.TimeZoneId, site.TimeZoneInherited, today,
                refusal is null, refusal),
            items = await AssignmentViews.ManyAsync(db, gate, assignments, found.ToDictionary(e => e.Id), today, ct),
        });
    }

    private static async Task<IResult> CreateAsync(
        AssignmentRequest body, WorkDbContext db, AssignmentGate gate, IModuleConstructions constructions,
        IModuleEmployees employees, IModuleClock clock, IModuleActivityLog log, TimeProvider time, CancellationToken ct)
    {
        var employeeId = body.EmployeeId ?? throw new InvalidRequestException("Не назван сотрудник (employeeId).");
        var siteId = body.ConstructionId ?? throw new InvalidRequestException("Не названа стройка (constructionId).");
        var kind = Assignment.ParseKind(body.Kind);
        var (from, to) = AssignmentRules.Period(kind, body.DateFrom, body.DateTo);

        var site = await SiteAsync(constructions, siteId, ct);
        await gate.EnsureAsync(siteId, ct);
        if (await employees.GetAsync([employeeId], ct) is not { } found) return AssignmentRules.DirectoryMissing();
        var employee = found.SingleOrDefault() ?? throw new NotFoundException("Сотрудник не найден.");
        if (AssignmentRules.Unfit(employee, kind, from, to) is { } reason) return AssignmentRules.Unprocessable(employee, reason);

        var assignment = Assignment.Create(employeeId, siteId, kind, from, to, time.GetUtcNow());
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.LockSiteAsync(siteId, ct);
            if (await AssignmentRules.OverlapAsync(db, employeeId, siteId, kind, from, to, null, ct) is { } existing)
                throw AssignmentRules.Overlapping(existing, employee.FullName);
            db.Assignments.Add(assignment);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        gate.Forget();

        await log.RecordAsync(AssignmentActions.Created, assignment.Id.ToString(),
            AssignmentActions.Label(employee.FullName, site.Name, kind), after: assignment.Period, ct: ct);
        return Results.Created($"/api/work/assignments/{assignment.Id}",
            await AssignmentViews.OneAsync(db, gate, assignment, employee, await clock.TodayOnSiteAsync(siteId, ct), ct));
    }

    /// <summary>Правка периода. Сотрудник, стройка и вид правкой не меняются — другое назначение это снятие и заведение.</summary>
    private static async Task<IResult> ChangeAsync(
        Guid id, AssignmentRequest body, HttpRequest request, WorkDbContext db, AssignmentGate gate,
        IModuleConstructions constructions, IModuleEmployees employees, IModuleClock clock, IModuleActivityLog log,
        TimeProvider time, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var assignment = await db.Assignments.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw NotFound();
        EnsureSame(body, assignment);
        var (from, to) = AssignmentRules.Period(assignment.Kind, body.DateFrom, body.DateTo);
        EnsureSeen(request, db.VersionOf(assignment));

        var site = await SiteAsync(constructions, assignment.ConstructionId, ct);
        await gate.EnsureAsync(assignment.ConstructionId, ct);
        if (await employees.GetAsync([assignment.EmployeeId], ct) is not { } found) return AssignmentRules.DirectoryMissing();
        var employee = found.SingleOrDefault() ?? throw new NotFoundException("Сотрудник назначения не найден.");
        if (AssignmentRules.Unfit(employee, assignment.Kind, from, to) is { } reason)
            return AssignmentRules.Unprocessable(employee, reason);

        await db.LockSiteAsync(assignment.ConstructionId, ct);
        if (await gate.LeavesNoManagerAsync(assignment, (from, to), ct: ct)) throw new ConflictException(AssignmentGate.LastManager);
        if (await AssignmentRules.OverlapAsync(db, assignment.EmployeeId, assignment.ConstructionId, assignment.Kind,
                from, to, assignment.Id, ct) is { } existing)
            throw AssignmentRules.Overlapping(existing, employee.FullName);

        var before = assignment.Period;
        assignment.ChangePeriod(from, to, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        // Менеджер мог сократить своё назначение: признаки ответа — по записанному.
        gate.Forget();

        await log.RecordAsync(AssignmentActions.Changed, assignment.Id.ToString(),
            AssignmentActions.Label(employee.FullName, site.Name, assignment.Kind), before, assignment.Period, ct);
        return Results.Ok(await AssignmentViews.OneAsync(db, gate, assignment, employee,
            await clock.TodayOnSiteAsync(assignment.ConstructionId, ct), ct));
    }

    /// <summary>Снятие. Последнего действующего менеджера стройки снимает только «на любых стройках» (WORK-46).</summary>
    private static async Task<IResult> RemoveAsync(
        Guid id, HttpRequest request, WorkDbContext db, AssignmentGate gate, IModuleConstructions constructions,
        IModuleEmployees employees, IModuleActivityLog log, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var assignment = await db.Assignments.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw NotFound();
        EnsureSeen(request, db.VersionOf(assignment));
        await db.LockSiteAsync(assignment.ConstructionId, ct);

        var (refusal, forbidden) = await gate.RemovalAsync(assignment, ct: ct);
        if (refusal is not null) throw forbidden ? new ForbiddenException(refusal) : new ConflictException(refusal);

        db.Assignments.Remove(assignment);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Имена — для журнала: стройку и сотрудника могли переименовать, запись помнит, как они звались сейчас.
        var site = (await constructions.ListAsync(ct)).FirstOrDefault(c => c.Id == assignment.ConstructionId);
        var employee = (await employees.GetAsync([assignment.EmployeeId], ct))?.SingleOrDefault();
        await log.RecordAsync(AssignmentActions.Removed, assignment.Id.ToString(),
            AssignmentActions.Label(employee?.FullName ?? "сотрудник", site?.Name ?? "стройка", assignment.Kind),
            before: assignment.Period, ct: ct);
        return Results.NoContent();
    }

    internal static async Task<ModuleConstruction> SiteAsync(
        IModuleConstructions constructions, Guid siteId, CancellationToken ct) =>
        (await constructions.ListAsync(ct)).FirstOrDefault(c => c.Id == siteId)
        ?? throw new NotFoundException("Стройка не найдена: её удалили или идентификатор неверен.");

    private static NotFoundException NotFound() => new("Назначение не найдено: его сняли или идентификатор неверен.");

    /// <summary>Правка не меняет сотрудника, стройку и вид — отказ называет поле.</summary>
    private static void EnsureSame(AssignmentRequest body, Assignment assignment)
    {
        const string why = " правкой не сменить: другое назначение — это снятие и заведение.";
        if (body.EmployeeId is { } e && e != assignment.EmployeeId)
            throw new InvalidRequestException("Сотрудника назначения (employeeId)" + why);
        if (body.ConstructionId is { } c && c != assignment.ConstructionId)
            throw new InvalidRequestException("Стройку назначения (constructionId)" + why);
        if (!string.IsNullOrWhiteSpace(body.Kind) && Assignment.ParseKind(body.Kind) != assignment.Kind)
            throw new InvalidRequestException("Вид назначения (kind)" + why);
    }

    private static void EnsureSeen(HttpRequest request, string current)
    {
        if (!SeenVersion.TryRead(request, out var seen)) throw new InvalidRequestException(SeenVersion.Malformed);
        if (seen is null)
            throw new InvalidRequestException(
                $"Не названа версия назначения, по которой собрана правка (заголовок {SeenVersion.Header}) — она " +
                "приходит в ответе чтения полем «version». Без неё правка записалась бы поверх чужой.");
        if (seen != current)
            throw new ConflictException(
                "Назначение тем временем изменили, и это действие не выполнено. Перечитайте назначения стройки и " +
                "повторите: вы видели прежнее состояние.");
    }
}
