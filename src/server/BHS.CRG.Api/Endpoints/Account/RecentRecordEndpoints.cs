using System.Security.Claims;
using BHS.CRG.Api.Search;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Search;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Endpoints.Account;

/// <summary>Что прислал экран записи: тип записи и её идентификатор.</summary>
public sealed record RecentTouchRequest(string? RecordType, Guid Id);

/// <summary>
/// Недавняя запись, как её показывает Главная (ТЗ UI-35): карточка владельца типа и когда открыта.
/// </summary>
/// <param name="Group">Код группы поиска — по нему клиент знает раздел записи.</param>
/// <param name="GroupTitle">Раздел для человека: «Счета», «Стройки».</param>
/// <param name="Opens">Под какой записью открывается экран и что открыть — у комплекта и шаблона (shell-09).</param>
public sealed record RecentRecordView(
    string RecordType, string Group, string GroupTitle, string Module,
    Guid Id, string Title, string? Detail, string? Place, decimal? Amount, RecordSearchLevel? Level,
    RecordSearchOpens? Opens, DateTimeOffset OpenedAt);

/// <summary>Группа, которая не ответила, — её записи не показаны, и это сказано, а не умолчано.</summary>
public sealed record RecentRefusal(string Group, string Title, string Reason);

public sealed record RecentRecordsView(IReadOnlyList<RecentRecordView> Items, IReadOnlyList<RecentRefusal> Refused);

/// <summary>
/// «Недавние» Главной (shell-08, В-3 — Б; ТЗ CORE-25.3, UI-35): личные адреса под <c>/api/account</c> —
/// пользователь берётся из принципала, чужого списка адрес не принимает и не отдаёт.
///
/// <para><b>Без <c>If-Match</c> — названное исключение</b> (<c>design.md</c>): отметка личная, а не правка
/// записи модуля или общих данных. Потерянного обновления нет — последнее открытие всегда верно, — и
/// запись, которую открыли, она не меняет. Так же устроены личные настройки.</para>
///
/// <para><b>Помнится ссылка, карточку называет владелец</b>: при чтении — контракт «Поиск записей»
/// (<see cref="RecordSearchService.CardsAsync" />) в правах спрашивающего. Удалённой и закрытой записи в
/// ответе нет; записи выключенного модуля не отдаются, но в таблице остаются — включат, и они вернутся.</para>
/// </summary>
public static class RecentRecordEndpoints
{
    /// <summary>Сколько записей помнится на пользователя (ТЗ CORE-25.3).</summary>
    public const int Limit = 20;

    public static void MapRecentRecordEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/account/recent").RequireAuthorization().WithTags("Главная");

        g.MapPost("", async (RecentTouchRequest req, RecordSearchCatalog catalog, RecentRecords recent,
            UserManager<ApplicationUser> users, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            if (await CurrentAsync(users, principal) is not { } userId) return Results.Unauthorized();
            // Тип без владельца не принимается: строка, которую никто не покажет, копилась бы молча, а
            // опечатка в коде типа выглядела бы удавшейся отметкой (тот же довод, что у ключей настроек).
            if (string.IsNullOrWhiteSpace(req.RecordType) || catalog.ForRecordType(req.RecordType) is null)
                return Results.BadRequest(new { error = $"Неизвестный тип записи: {req.RecordType}" });
            if (req.Id == Guid.Empty) return Results.BadRequest(new { error = "Не назван идентификатор записи" });

            await recent.TouchAsync(userId, req.RecordType, req.Id, ct);
            return Results.NoContent();
        });

        g.MapGet("", async (AppDbContext db, RecordSearchService search,
            UserManager<ApplicationUser> users, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            if (await CurrentAsync(users, principal) is not { } userId) return Results.Unauthorized();
            return Results.Ok(await ReadAsync(db, search, userId, ct));
        });
    }

    /// <summary>
    /// Свои отметки, новые первыми, — только те, что владелец типа показывает спрашивающему.
    ///
    /// <para><b>Свёртка</b> (shell-09, Р-5): строки, ведущие на одно и то же, — одна строка с поздним
    /// открытием. Ключ — <c>(тип, Opens.Id ?? Id)</c>: версии одного шаблона ведут на его текущую версию, а
    /// без <c>Opens.Id</c> (комплект, счёт, стройка, набор) ключ — сама запись, и разные записи не
    /// сворачиваются. Это второй пояс: одну отметку на шаблон держит уже запись (<see cref="RecentRecords.TouchAsync" />),
    /// а две остаются лишь от открытия двух версий разом.</para>
    /// </summary>
    internal static async Task<RecentRecordsView> ReadAsync(
        AppDbContext db, RecordSearchService search, Guid userId, CancellationToken ct)
    {
        var marks = await db.UserRecentRecords.AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.OpenedAt).ThenBy(r => r.RecordId)
            .Take(Limit)
            .ToListAsync(ct);
        if (marks.Count == 0) return new([], []);

        var answers = await search.CardsAsync([.. marks.Select(m => new RecordRef(m.RecordType, m.RecordId))], ct);
        var cards = answers
            .SelectMany(a => a.Items.Select(hit => (Answer: a, Hit: hit)))
            .ToDictionary(x => (x.Answer.RecordType, x.Hit.Id));

        RecentRecordView? View(Domain.Settings.UserRecentRecord m) =>
            cards.TryGetValue((m.RecordType, m.RecordId), out var c)
                ? new(m.RecordType, c.Answer.Code, c.Answer.Title, c.Answer.Module,
                    c.Hit.Id, c.Hit.Title, c.Hit.Detail, c.Hit.Place, c.Hit.Amount, c.Hit.Level, c.Hit.Opens, m.OpenedAt)
                : null;

        // Отметки уже новые первыми — первая строка ключа и есть позднее открытие.
        return new(
            [.. marks.Select(View).OfType<RecentRecordView>().DistinctBy(v => (v.RecordType, v.Opens?.Id ?? v.Id))],
            [.. answers.Where(a => a.Refused is not null).Select(a => new RecentRefusal(a.Code, a.Title, a.Refused!))]);
    }

    /// <summary>Вошедший — тем же разбором, что у остальных адресов <c>/api/account</c>.</summary>
    private static async Task<Guid?> CurrentAsync(UserManager<ApplicationUser> users, ClaimsPrincipal p) =>
        (await AccountEndpoints.FindCurrent(users, p))?.Id;
}
