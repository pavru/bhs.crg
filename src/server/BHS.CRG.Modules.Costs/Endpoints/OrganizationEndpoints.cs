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
/// верное утверждение — «здесь его не видно». Сопоставление поставщика по ИНН живёт в
/// <see cref="InvoiceParties" /> и спрашивает ИНН с уже разрешённым наследованием
/// (<c>IModuleCatalog.FieldValuesAsync</c>, issue #1077); этому списку он по-прежнему не нужен.</para>
///
/// <para><b>Назначение называет звавший</b> (issue #1185): <c>purpose=choice</c> — список, из которого
/// выбирают, архивных организаций в нём нет; <c>purpose=display</c> — все, с признаком. Умолчания
/// нет, как и у общих данных ядра: один адрес кормит и выбор, и показ, и забытое «скрыть» вернуло
/// бы архивного поставщика в новые счета. Название организации, которая в счёте УЖЕ стоит, форма
/// берёт не отсюда, а из ответа счёта (<see cref="InvoiceReferencesView" />) — иначе счёт с архивным
/// поставщиком открылся бы с пустым полем, и сохранение стёрло бы ссылку.</para>
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
        IModuleCatalog catalog, CancellationToken ct, string? purpose = null)
    {
        RecordsFor records = purpose switch
        {
            "choice" => RecordsFor.Choice,
            "display" => RecordsFor.Display,
            _ => throw new InvalidRequestException(
                "Не названо назначение чтения: параметр purpose обязателен и принимает choice (список " +
                "на выбор, без архивных организаций) либо display (показ уже выбранного, архивные на " +
                "месте с признаком)."),
        };

        var entries = await catalog.ListAsync(CostsRecordTypes.OrganizationCode, records, ct)
            ?? throw new ConflictException(
                $"Тип «{CostsRecordTypes.OrganizationCode}» в системе не заведён, поэтому выбрать " +
                "поставщика не из чего. Этот тип ведёт человек — заведите его в разделе типов, и " +
                "счета заработают со следующего запуска приложения. Пустой список здесь означал бы " +
                "«организаций ещё не завели», а это другое.");

        return TypedResults.Ok<IReadOnlyList<CostsOrganization>>(
            [.. entries.Select(e => new CostsOrganization(e.Id, e.DisplayName, e.EntityType, e.Archived))]);
    }
}

/// <summary>Организация в выборе поставщика и плательщика.</summary>
/// <param name="Type">Код типа записи — у подтипа свой («ОрганизацияСРО»). Форме он нужен затем,
/// чтобы человек различил однофамильцев, а не для отбора: отбор уже сделан.</param>
/// <param name="Archived">Организация в архиве — бывает только в списке на показ.</param>
public sealed record CostsOrganization(Guid Id, string Name, string Type, bool Archived);
