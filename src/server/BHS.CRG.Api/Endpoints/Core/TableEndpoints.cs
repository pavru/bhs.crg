using System.Security.Claims;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Modules.Tables;
using BHS.CRG.Application.Tables;

namespace BHS.CRG.Api.Endpoints.Core;

/// <summary>
/// Таблицы модулей для экрана (ТЗ CORE-33; задача G1b, issue #1089). Отбор, сортировка и итоги
/// приедут в G1c, экран — в G1e; здесь — состав колонок с причинами и строки.
///
/// <para>Группа закрыта только входом, и это не забытое право: таблицы разных модулей открываются
/// разными ключами, и ключ проверяет служба по объявлению таблицы — тем же путём, что у набора
/// данных на ней. Право группы здесь было бы вторым, расходящимся правилом доступа к тем же
/// строкам.</para>
/// </summary>
public static class TableEndpoints
{
    public static void MapTableEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/tables").RequireAuthorization();

        g.MapGet("/", async (ClaimsPrincipal user, DataAccessResolver access, ModuleTableService tables,
            CancellationToken ct) => Results.Ok(tables.List(await access.ForAsync(user, ct))));

        // ?columns=Номер,Итого — колонки сохранённого представления: исчезнувшая из типа приходит
        // колонкой с причиной, а не пропадает. Отбор — тем же деревом условий, что у наборов данных
        // (?filter=…), сортировка — ?sort=Итого:desc,Номер, итоги — ?totals=Итого.
        g.MapGet("/{address}", async (
            string address, string? columns, string? filter, string? sort, string? totals, int? offset, int? limit,
            ClaimsPrincipal user, DataAccessResolver access, ModuleTableService tables, CancellationToken ct) =>
        {
            // Страница есть ВСЕГДА: экран, забывший её попросить, получил бы таблицу целиком.
            var request = new TableRequest(
                List(columns), filter,
                [.. (List(sort) ?? []).Select(SortOf)],
                Math.Max(offset ?? 0, 0),
                Math.Clamp(limit ?? DefaultPage, 1, MaxPage),
                List(totals));
            var (table, refusal) = await tables.ReadAsync(address, await access.ForAsync(user, ct), request, ct);
            return table is not null
                ? Results.Ok(table)
                : Results.Json(new { error = refusal!.Error }, statusCode: refusal.Status);
        });
    }

    private const int DefaultPage = 100;
    private const int MaxPage = 1000;

    private static string[]? List(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>«Итого:desc» — по убыванию; без пометки или с «asc» — по возрастанию.</summary>
    private static TableSortRequest SortOf(string item)
    {
        var colon = item.LastIndexOf(':');
        return colon > 0 && item[(colon + 1)..] is "desc" or "asc"
            ? new(item[..colon], item[(colon + 1)..] == "desc")
            : new(item, false);
    }
}
