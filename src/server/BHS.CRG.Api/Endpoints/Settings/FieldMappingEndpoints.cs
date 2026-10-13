using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Schema;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Endpoints.Settings;

/// <summary>
/// Экран «Сопоставление полей» (ТЗ TYPE-23.1, issue #1101): что включённые модули читают из
/// справочников ядра и чем это сейчас обеспечено.
///
/// <para><b>Адрес только читает.</b> Назначение роли и поля — правка схемы типа, и идёт она тем же
/// адресом, что из редактора типов (<c>PUT /api/document-types/{id}/schema</c>): со всеми его
/// проверками, уровнем правки и записью в журнал. Второй пишущий путь в схему означал бы второе
/// место, где эти проверки можно забыть.</para>
///
/// <para>Право — <c>core.types.edit</c>, то же, что на записи схемы: кто видит экран, тот и может
/// сохранить. Сводку для страницы «Настройки» отдаёт адрес настроек модулей, своим правом.</para>
///
/// <para>Выключенный модуль сюда не попадает (ТЗ TYPE-22), как и модуль, который из справочников
/// ничего не читает.</para>
/// </summary>
public static class FieldMappingEndpoints
{
    public static void MapFieldMappingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/field-mapping", async (ModuleRegistry registry, TagCatalog tags, AppDbContext db,
                CancellationToken ct) =>
            {
                var modules = registry.Enabled.Where(m => m.Needs.Count > 0).ToList();
                var types = modules.Count > 0 ? await db.DocumentTypes.AsNoTracking().ToListAsync(ct) : [];

                return Results.Ok(new { modules = modules.Select(m => ModuleCoverage.Of(m, tags, types)) });
            })
            .RequireAuthorization(AppPolicies.Permission(CorePermissions.TypesEdit));
    }
}
