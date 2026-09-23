using BHS.CRG.Api.Auth;
using BHS.CRG.Modules;
using BHS.CRG.Application.Schema;

namespace BHS.CRG.Api.Endpoints.Schema;

public static class TagsEndpoints
{
    public static void MapTagsEndpoints(this IEndpointRouteBuilder app)
    {
        // Реестр функциональных тэгов для UI (выбор тэгов поля/типа) — ЭТОГО экземпляра: ядро
        // плюс включённые модули. Тэги выключенного модуля не отдаются (ТЗ TYPE-22, issue #959):
        // предлагать метку, которую на этом экземпляре некому прочитать, значит обещать поведение.
        app.MapGet("/api/tags", (TagCatalog tags) => Results.Ok(tags.All))
            .RequireAuthorization(AppPolicies.Permission(CorePermissions.TypesRead));
    }
}
