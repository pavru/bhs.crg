using System.Security.Claims;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Modules.Tables;
using BHS.CRG.Application.Tables;
using BHS.CRG.Modules;

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

        // Готовые отборы таблицы с числом строк под каждым (issue #1186). Отдельным адресом, а не
        // частью описания или страницы: число даёт запрос к строкам, а у колонки по требованию — ещё и
        // опрос ядра, и платить за него на каждую страницу и сортировку незачем.
        g.MapGet("/{address}/shortcuts", async (
            string address, ClaimsPrincipal user, DataAccessResolver access, ModuleTableService tables,
            CancellationToken ct) =>
        {
            var (shortcuts, refusal) = await tables.ShortcutsAsync(address, await access.ForAsync(user, ct), ct);
            return shortcuts is not null
                ? Results.Ok(shortcuts)
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

        MapSavedViews(g);
    }

    /// <summary>
    /// Сохранённые представления таблицы: свои и общие (G3a, issue #1095). Правка и удаление обязаны
    /// назвать версию заголовком <c>If-Match</c> — и у личного тоже: одно правило проще двух, а две
    /// вкладки одного человека затирают друг друга так же, как два человека.
    ///
    /// <para><c>{view}</c> — строка, а не идентификатор: по коду представления МОДУЛЯ адрес обязан
    /// ответить отказом с выходом «сохраните как своё», а ограничение маршрута дало бы 404.</para>
    /// </summary>
    private static void MapSavedViews(RouteGroupBuilder g)
    {
        g.MapGet("/{address}/views", async (
            string address, ClaimsPrincipal user, DataAccessResolver access, SavedTableViewService views,
            CancellationToken ct) =>
        {
            var (list, refusal) = await views.ListAsync(address, await access.ForAsync(user, ct), ct);
            return list is not null ? Results.Ok(list) : Refused(refusal!);
        });

        g.MapPost("/{address}/views", async (
            string address, SaveTableView body, ClaimsPrincipal user, DataAccessResolver access,
            SavedTableViewService views, CancellationToken ct) =>
        {
            var (view, refusal) = await views.CreateAsync(address, await access.ForAsync(user, ct), body, ct);
            return view is not null ? Results.Ok(view) : Refused(refusal!);
        });

        g.MapPut("/{address}/views/{view}", async (
            string address, string view, SaveTableView body, HttpRequest request, ClaimsPrincipal user,
            DataAccessResolver access, SavedTableViewService views, CancellationToken ct) =>
        {
            if (SeenRefusal(request, out var seen) is { } unnamed) return unnamed;
            var (saved, refusal) = await views.UpdateAsync(address, view, seen, await access.ForAsync(user, ct), body, ct);
            return saved is not null ? Results.Ok(saved) : Refused(refusal!);
        });

        g.MapDelete("/{address}/views/{view}", async (
            string address, string view, HttpRequest request, ClaimsPrincipal user, DataAccessResolver access,
            SavedTableViewService views, CancellationToken ct) =>
        {
            if (SeenRefusal(request, out var seen) is { } unnamed) return unnamed;
            var refusal = await views.DeleteAsync(address, view, seen, await access.ForAsync(user, ct), ct);
            return refusal is null ? Results.NoContent() : Refused(refusal);
        });
    }

    private const string VersionRequired =
        "Представление не изменено: правка обязана назвать версию, по которой собрана, — заголовком If-Match " +
        "(значение — поле version из списка представлений). Правка заменяет представление целиком, и без версии " +
        "она затёрла бы изменения, сделанные после того, как его прочли.";

    private static IResult? SeenRefusal(HttpRequest request, out string seen)
    {
        var wellFormed = SeenVersion.TryRead(request, out var named);
        seen = named ?? "";
        if (!wellFormed) return Results.BadRequest(new { error = SeenVersion.Malformed });
        return named is null ? Results.BadRequest(new { error = VersionRequired }) : null;
    }

    private static IResult Refused(TableRefusal refusal) =>
        Results.Json(new { error = refusal.Error }, statusCode: refusal.Status);

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
