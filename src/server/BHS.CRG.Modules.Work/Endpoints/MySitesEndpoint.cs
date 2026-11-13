using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Endpoints;

/// <summary>Своя стройка спрашивающего: вид его назначений и «сегодня» по поясу стройки.</summary>
/// <param name="Kinds">Виды действующих назначений: <c>installer</c>, <c>manager</c> — бывают оба.</param>
/// <param name="Today">Сегодняшняя дата на стройке (ТЗ WORK-8) — клиент её не вычисляет.</param>
public sealed record MySiteDto(Guid Id, string Name, IReadOnlyList<string> Kinds, DateOnly Today, string TimeZoneId);

/// <summary>
/// Граница выдачи (ТЗ CORE-24.3, D15 зонтика): «ваши стройки (N)» и причина пустоты.
/// </summary>
/// <param name="Reason"><c>null</c> — стройки есть; <c>not-assigned</c> — назначений на дату нет; <c>not-linked</c>
/// — учётная запись не связана с сотрудником; <c>directory-missing</c> — справочника сотрудников нет.</param>
public sealed record MySitesBoundary(string Text, int Count, string? Reason);

/// <summary>
/// <c>GET /api/work/sites/mine?date=</c> — стройки, на которые сотрудник спрашивающего назначен на дату (ТЗ WORK-24,
/// WORK-8; change <c>work-11-assignments</c>). Не названа дата — «сегодня» каждой стройки по её поясу.
///
/// <para><b>Только назначенные.</b> Остальные стройки не перечисляются ни именем, ни числом: граница — «ваши
/// стройки», а не «N из 40».</para>
///
/// <para><b>Пустота названа.</b> Пустой список без причины выглядел бы как «назначений нет» и у того, кого не
/// связали с сотрудником, и там, где справочника нет вовсе, — а выход из этих трёх положений разный.</para>
///
/// <para>Ворота — модуля целиком: свои стройки нужны и монтажнику (подача), и менеджеру (приёмка), и прав
/// назначения не требуют.</para>
/// </summary>
internal static class MySitesEndpoint
{
    public const string NotAssigned = "not-assigned";
    public const string NotLinked = "not-linked";

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/work/sites/mine", MineAsync).WithTags("Назначения");

    private static async Task<IResult> MineAsync(
        DateOnly? date, WorkDbContext db, IModuleUser user, IModuleEmployees employees,
        IModuleConstructions constructions, IModuleClock clock, CancellationToken ct)
    {
        if (user.Id is not { } userId || await employees.OfAccountAsync(userId, ct) is not { } me)
        {
            // Справочника нет — нет и связи: различить их можно только вопросом о справочнике.
            var reason = await employees.GetAsync([], ct) is null ? IModuleEmployees.NotSetUpCode : NotLinked;
            return Results.Ok(new { items = Array.Empty<MySiteDto>(), boundary = Boundary(0, reason) });
        }

        var assignments = await db.Assignments.AsNoTracking().Where(a => a.EmployeeId == me.Id).ToListAsync(ct);
        var sites = (await constructions.ListAsync(ct)).ToDictionary(c => c.Id);
        var items = new List<MySiteDto>();
        foreach (var group in assignments.GroupBy(a => a.ConstructionId))
        {
            if (!sites.TryGetValue(group.Key, out var site)) continue;
            if (await clock.TodayOnSiteAsync(site.Id, ct) is not { } today) continue;
            var day = date ?? today;
            // Уволенный (или ещё не принятый) на этот день своих строек не имеет: увольнение ставят позже
            // назначения, и действующее на бумаге назначение не делает стройку своей.
            if (me.Archived || me.NotWorkingOn(day) is not null) continue;
            var kinds = group.Where(a => a.Covers(day)).Select(a => a.Kind).Distinct().Order()
                .Select(Assignment.KindCode).ToList();
            if (kinds.Count > 0) items.Add(new MySiteDto(site.Id, site.Name, kinds, today, site.TimeZoneId));
        }

        var ordered = items.OrderBy(s => s.Name, StringComparer.CurrentCulture).ToList();
        return Results.Ok(new { items = ordered, boundary = Boundary(ordered.Count, ordered.Count == 0 ? NotAssigned : null) });
    }

    private static MySitesBoundary Boundary(int count, string? reason) => new($"ваши стройки ({count})", count, reason);
}
