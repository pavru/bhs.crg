using System.Security.Claims;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Modules.Tables;

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
        // колонкой с причиной, а не пропадает.
        g.MapGet("/{address}", async (string address, string? columns, ClaimsPrincipal user,
            DataAccessResolver access, ModuleTableService tables, CancellationToken ct) =>
        {
            var requested = string.IsNullOrWhiteSpace(columns)
                ? null
                : columns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var (table, refusal) = await tables.ReadAsync(address, await access.ForAsync(user, ct), requested, ct);
            return table is not null
                ? Results.Ok(table)
                : Results.Json(new { error = refusal!.Error }, statusCode: refusal.Status);
        });
    }
}
