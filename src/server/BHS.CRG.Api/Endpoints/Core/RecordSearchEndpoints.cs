using BHS.CRG.Api.Search;
using BHS.CRG.Modules.Exchange;

namespace BHS.CRG.Api.Endpoints.Core;

/// <summary>
/// Поиск записей для палитры (Ctrl K): <c>GET /api/search?q=&amp;group=</c> (shell-05, В-1 — Б; ТЗ AUTH-16.1,
/// CORE-34).
///
/// <para><b>На адресе — только вход, права — у каждой группы</b> ключом её списка (AUTH-8): группы разных
/// модулей стоят под разными правами, и потребовать на общем адресе можно только вход. Строка в корзине
/// «ворота внутри» переписи адресов называет сторожа.</para>
///
/// <para>⚠️ Ответы различаются нарочно: запрос короче двух знаков и неизвестная группа — 400 (это дефект
/// вызывающего, а не «ничего не нашлось»); отказ одной группы — 200 и <c>refused</c> у неё; закрытой правами
/// группы и группы выключенного модуля в ответе нет вовсе.</para>
/// </summary>
public static class RecordSearchEndpoints
{
    /// <summary>Сколько знаков запроса нужно, чтобы искать: из одного знака подходит почти всё.</summary>
    public const int MinLength = 2;

    public static void MapRecordSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/search", async (string? q, string? group, RecordSearchCatalog catalog,
            RecordSearchService search, CancellationToken ct) =>
        {
            if ((q?.Trim().Length ?? 0) < MinLength)
                return Results.BadRequest(new { error = $"Запрос поиска — от {MinLength} знаков, параметром q." });
            if (!string.IsNullOrEmpty(group) && catalog.Find(group) is null)
                return Results.BadRequest(new { error = $"Неизвестная группа поиска «{group}»." });

            var groups = await search.SearchAsync(RecordSearchText.Words(q),
                string.IsNullOrEmpty(group) ? null : group, ct);
            return Results.Ok(new { groups });
        }).RequireAuthorization();
    }
}
