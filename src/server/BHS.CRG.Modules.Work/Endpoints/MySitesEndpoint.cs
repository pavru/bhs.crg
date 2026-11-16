using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Submission;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Endpoints;

/// <summary>Своя стройка спрашивающего: вид его назначений и «сегодня» по поясу стройки.</summary>
/// <param name="Kinds">Виды действующих назначений: <c>installer</c>, <c>manager</c> — бывают оба.</param>
/// <param name="Today">Сегодняшняя дата на стройке (ТЗ WORK-8) — клиент её не вычисляет.</param>
/// <param name="Suggested">Предлагаемая пара нового отчёта и подпись кнопки (ТЗ MOB-35, <c>work-15</c>) — клиент её не
/// сочиняет.</param>
public sealed record MySiteDto(
    Guid Id, string Name, IReadOnlyList<string> Kinds, DateOnly Today, string TimeZoneId, SuggestedDto Suggested);

/// <summary>Предлагаемая пара «дата · смена»: <c>shift</c> — <c>day</c> | <c>night</c>, <c>label</c> — подпись кнопки.</summary>
public sealed record SuggestedDto(DateOnly Date, string Shift, string Label);

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
/// <para><b>Предлагаемая пара</b> (<c>work-15</c>) — по правилу <see cref="SubmissionWindow.Suggested" /> от «сейчас»
/// тех же часов, что «сегодня»; ночные отчёты за вчера ищутся среди своих отчётов спрашивающего (охват «свои»).</para>
///
/// <para>Ворота — модуля целиком: свои стройки нужны и монтажнику (подача), и менеджеру (приёмка), и прав
/// назначения не требуют.</para>
/// </summary>
internal static class MySitesEndpoint
{
    public const string NotAssigned = "not-assigned";
    public const string NotLinked = WorkAccounts.NotLinked;

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/work/sites/mine", MineAsync).WithTags("Назначения");

    private static async Task<IResult> MineAsync(
        DateOnly? date, WorkDbContext db, IModuleUser user, IModuleEmployees employees,
        IModuleConstructions constructions, IModuleClock clock, ReportReads reads, CancellationToken ct)
    {
        var (found, reason) = await WorkAccounts.OfUserAsync(user, employees, ct);
        if (found is not { } me)
            return Results.Ok(new { items = Array.Empty<MySiteDto>(), boundary = Boundary(0, reason) });

        var assignments = await db.Assignments.AsNoTracking().Where(a => a.EmployeeId == me.Id).ToListAsync(ct);
        var sites = (await constructions.ListAsync(ct)).ToDictionary(c => c.Id);
        var now = clock.UtcNow();
        // Ночные отчёты за последние сутки любого пояса: вчера на стройке не раньше позавчера по UTC.
        var nights = (await reads.NightReportsAsync(ReportViewer.Own(me.Id),
            DateOnly.FromDateTime(now.UtcDateTime).AddDays(-2), ct)).ToHashSet();
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
            if (kinds.Count == 0) continue;
            // «Сегодня» — от тех же часов и пояса, что и мгновение правила: вчера стройки — день перед ним.
            var pair = SubmissionWindow.Suggested(site.TimeZoneId, now, nights.Contains((site.Id, today.AddDays(-1))));
            items.Add(new MySiteDto(site.Id, site.Name, kinds, today, site.TimeZoneId,
                new SuggestedDto(pair.Date, Report.ShiftCode(pair.Shift), pair.Label)));
        }

        var ordered = items.OrderBy(s => s.Name, StringComparer.CurrentCulture).ToList();
        return Results.Ok(new { items = ordered, boundary = Boundary(ordered.Count, ordered.Count == 0 ? NotAssigned : null) });
    }

    private static MySitesBoundary Boundary(int count, string? reason) => new($"ваши стройки ({count})", count, reason);
}
