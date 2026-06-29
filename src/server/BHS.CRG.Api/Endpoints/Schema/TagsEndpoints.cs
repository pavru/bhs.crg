using BHS.CRG.Application.Schema;

namespace BHS.CRG.Api.Endpoints.Schema;

public static class TagsEndpoints
{
    public static void MapTagsEndpoints(this IEndpointRouteBuilder app)
    {
        // Реестр функциональных тэгов для UI (выбор тэгов поля/типа).
        app.MapGet("/api/tags", () => Results.Ok(TagRegistry.All))
            .RequireAuthorization();
    }
}
