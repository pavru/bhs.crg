using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Objects;

namespace BHS.CRG.Api.Endpoints.Documents;

/// <summary>
/// Требует, чтобы документ из маршрута действительно лежал в комплекте из того же маршрута.
///
/// Адреса вида <c>/{setId}/documents/{id}</c> называют оба, но обработчики брали только <c>id</c>:
/// комплект в адресе был украшением, и документ доставался по идентификатору откуда угодно.
/// Сегодня это ничего не пересекает — общей границы доступа в системе нет, — но станет дырой в
/// первый же день, когда она появится, причём сразу в полутора десятках маршрутов.
///
/// Фильтр на ГРУППУ, а не проверка в каждом обработчике, и это главное в нём: пятнадцать одинаковых
/// проверок означают, что шестнадцатую забудут. Здесь новый маршрут получает проверку тем, что
/// объявлен в этой группе.
///
/// Маршруты без пары <c>setId</c>+<c>id</c> фильтр пропускает — ему нечего сверять.
/// Образец поведения — <c>PrintFormEndpoints</c>, где эта сверка была сделана с самого начала.
/// </summary>
public class DocumentBelongsToSetFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var route = http.Request.RouteValues;

        if (!TryGuid(route, "setId", out var setId) || !TryGuid(route, "id", out var documentId))
            return await next(ctx);

        var repo = http.RequestServices.GetRequiredService<IRepository<DomainObject>>();
        var instance = await repo.GetByIdAsync(documentId, http.RequestAborted);

        // Отсутствующий и «не из этого комплекта» отвечают ОДИНАКОВО. Отдельный код на «есть, но
        // чужой» подтверждал бы существование документа тому, кому его не показывают.
        if (instance is null || instance.ScopeId != setId)
            return Results.NotFound();

        return await next(ctx);
    }

    private static bool TryGuid(RouteValueDictionary route, string key, out Guid value)
    {
        value = Guid.Empty;
        return route.TryGetValue(key, out var raw) && Guid.TryParse(raw?.ToString(), out value);
    }
}
