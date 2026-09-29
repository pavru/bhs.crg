using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Организации для формы счёта — узкое чтение справочника ядра (задача C1, issue #1076).
///
/// <para><b>Зачем свой адрес, когда есть общий.</b> Форме нужен выбор поставщика и плательщика, то
/// есть список «идентификатор + название». Общий адрес общих данных такого списка не даёт: отбор у
/// него по ОДНОМУ типу, без подтипов, — а подтип организации остаётся организацией, и поставщиком
/// заказчика может быть «ОрганизацияСРО». Значит клиенту пришлось бы либо спрашивать по типу на
/// каждый подтип, либо тянуть общие данные ЦЕЛИКОМ и отбирать у себя. Второе дороже, чем кажется: в
/// одной установке список общих данных весил 5,43 МБ, из них 99,6 % — картинки в base64 (issue
/// #1015). Здесь же уезжает ровно то, что нужно выпадающему списку.</para>
///
/// <para>⚠️ ИНН в ответе НЕТ, хотя он у организации есть. Причина — в живых данных: часть записей
/// хранит только <c>_baseRef</c>, то есть свои реквизиты наследует от другой записи, и прочитать ИНН
/// из такой записи напрямую нельзя. Отдай мы «ИНН: пусто», форма показала бы «без ИНН» там, где
/// верное утверждение — «здесь его не видно». Сопоставление поставщика по ИНН приезжает вместе с
/// распознаванием (B1b), и разрешение наследования — его забота.</para>
/// </summary>
public static class OrganizationEndpoints
{
    public static void MapOrganizations(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/costs/organizations", ListAsync)
            .RequireAuthorization(AppPolicies.Permission("costs.invoice.read"))
            .WithTags("Счета на оплату");
    }

    private static async Task<Ok<IReadOnlyList<CostsOrganization>>> ListAsync(
        IModuleCatalog catalog, CancellationToken ct)
    {
        var entries = await catalog.ListAsync(CostsRecordTypes.OrganizationCode, ct);

        return TypedResults.Ok<IReadOnlyList<CostsOrganization>>(
            [.. entries.Select(e => new CostsOrganization(e.Id, e.DisplayName, e.EntityType))]);
    }
}

/// <summary>Организация в выборе поставщика и плательщика.</summary>
/// <param name="Type">Код типа записи — у подтипа свой («ОрганизацияСРО»). Форме он нужен затем,
/// чтобы человек различил однофамильцев, а не для отбора: отбор уже сделан.</param>
public sealed record CostsOrganization(Guid Id, string Name, string Type);
