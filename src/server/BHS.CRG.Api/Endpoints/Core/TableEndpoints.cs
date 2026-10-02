using System.Security.Claims;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Modules.Tables;
using BHS.CRG.Application.Tables;

namespace BHS.CRG.Api.Endpoints.Core;

/// <summary>
/// Таблицы модулей для экрана (ТЗ CORE-33; задачи G1b–G1e, issue #1089–#1092): состав колонок с
/// причинами, строки под отбором и сортировкой, итоги по всему отбору, одна строка по ключу.
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

        // Таблица без строк: все её колонки с причинами. Экрану она нужна отдельно — выбор колонок и
        // чипы отбора знают все колонки, а не показанные, и остаются на месте, когда отбор отказал.
        g.MapGet("/{address}/columns", async (
            string address, ClaimsPrincipal user, DataAccessResolver access, ModuleTableService tables,
            CancellationToken ct) =>
        {
            var (table, refusal) = await tables.DescribeAsync(address, await access.ForAsync(user, ct), ct);
            return table is not null
                ? Results.Ok(table)
                : Results.Json(new { error = refusal!.Error }, statusCode: refusal.Status);
        });

        // ?columns=Номер,Итого — колонки сохранённого представления: исчезнувшая из типа приходит
        // колонкой с причиной, а не пропадает. Отбор — тем же деревом условий, что у наборов данных
        // (?filter=…), сортировка — ?sort=Итого:desc,Номер, итоги — ?totals=Итого, одна строка —
        // ?row=ключ (под тем же отбором: строка вне отбора не приходит).
        g.MapGet("/{address}", async (
            string address, string? columns, string? filter, string? sort, string? totals, int? offset, int? limit,
            string? row,
            ClaimsPrincipal user, DataAccessResolver access, ModuleTableService tables, CancellationToken ct) =>
        {
            var request = Paged(
                List(columns), filter, [.. (List(sort) ?? []).Select(SortOf)], offset, limit, List(totals), row);
            return Answer(await tables.ReadAsync(address, await access.ForAsync(user, ct), request, ct));
        });

        // То же телом запроса. Дерево условий в адресе упирается в потолок длины строки запроса —
        // а его ставит сервер перед приложением, и отказ пришёл бы без причины: перечень из
        // семидесяти поставщиков кириллицей — уже около 8 КБ. Экран с длинным отбором идёт сюда.
        g.MapPost("/{address}/query", async (
            string address, TableQueryBody body,
            ClaimsPrincipal user, DataAccessResolver access, ModuleTableService tables, CancellationToken ct) =>
        {
            var request = Paged(
                body.Columns, body.Filter, body.Sort ?? [], body.Offset, body.Limit, body.Totals, body.Row);
            return Answer(await tables.ReadAsync(address, await access.ForAsync(user, ct), request, ct));
        });
    }

    /// <summary>Запрос к таблице телом. <c>Filter</c> — строка с JSON, как и отбор источника набора.</summary>
    public sealed record TableQueryBody(
        string[]? Columns, string? Filter, TableSortRequest[]? Sort, int? Offset, int? Limit, string[]? Totals,
        string? Row = null);

    /// <summary>Страница есть ВСЕГДА: экран, забывший её попросить, получил бы таблицу целиком.</summary>
    private static TableRequest Paged(
        IReadOnlyList<string>? columns, string? filter, IReadOnlyList<TableSortRequest> sort,
        int? offset, int? limit, IReadOnlyList<string>? totals, string? row) => new(
        columns, filter, sort, Math.Max(offset ?? 0, 0), Math.Clamp(limit ?? DefaultPage, 1, MaxPage), totals, row);

    private static IResult Answer((TableDto? Table, TableRefusal? Refusal) read) => read.Table is not null
        ? Results.Ok(read.Table)
        : Results.Json(new { error = read.Refusal!.Error }, statusCode: read.Refusal.Status);

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
