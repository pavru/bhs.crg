using System.Text.Json;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Distribution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Modules.Plan.Endpoints;

/// <summary>
/// Распределение редакции сметы по разделам стройки (change <c>plan-10-distribution</c>, ТЗ PLAN-5.1, PLAN-5,
/// AUTH-8, AUTH-9): чтение — <c>GET …/distribution</c>; правило раздела ЛСР — <c>PUT …/distribution/rules</c>; набор
/// строк — <c>PUT …/distribution/lines</c>; решение по позиции отчёта переноса — <c>POST
/// …/distribution/carry-over/{itemId}</c>.
///
/// <para><b>Ворота и версия.</b> Группа модуля закрыта воротами <c>module:plan</c>; чтение — правом
/// <c>plan.estimate.read</c> (с ним видны и правила, и части строк), запись — <c>plan.estimate.edit</c> и версией
/// РАСПРЕДЕЛЕНИЯ заголовком <c>If-Match</c>: без него — 400, с устаревшей — 409 с текущей версией, кем и когда она
/// изменена. Зафиксированная и не действующая редакции правятся так же.</para>
///
/// <para><b>Отказы — структурой</b>, а не только словами: превышение объёма — 400 со списком строк
/// (<c>errors[]</c>), смета на разделе — 400 с разделом, чужая правка — 409 с её автором. Экран подсвечивает строку
/// и пишет «изменила Петрова О. — перечитайте», не разбирая фразу.</para>
/// </summary>
public static class DistributionEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var edit = AppPolicies.Permission(PlanModule.EstimateEdit);
        endpoints.MapGet("/api/plan/revisions/{id:guid}/distribution", ReadAsync)
            .WithTags("Сметы").RequireAuthorization(AppPolicies.Permission(PlanModule.EstimateRead));
        endpoints.MapPut("/api/plan/revisions/{id:guid}/distribution/rules", RulesAsync)
            .WithTags("Сметы").RequireAuthorization(edit);
        endpoints.MapPut("/api/plan/revisions/{id:guid}/distribution/lines", LinesAsync)
            .WithTags("Сметы").RequireAuthorization(edit);
        endpoints.MapPost("/api/plan/revisions/{id:guid}/distribution/carry-over/{itemId:guid}", DecideAsync)
            .WithTags("Сметы").RequireAuthorization(edit);
    }

    private static async Task<IResult> ReadAsync(Guid id, DistributionReader reader, CancellationToken ct) =>
        Results.Ok(await reader.ReadAsync(id, ct));

    private static Task<IResult> RulesAsync(
        Guid id, JsonElement body, HttpRequest request, DistributionWriter writer, CancellationToken ct) =>
        GuardedAsync(async () => Results.Ok(await writer.RulesAsync(id, Seen(request), DistributionRequests.Rule(body), ct)));

    private static Task<IResult> LinesAsync(
        Guid id, JsonElement body, HttpRequest request, DistributionWriter writer, CancellationToken ct) =>
        GuardedAsync(async () => Results.Ok(await writer.LinesAsync(id, Seen(request), DistributionRequests.Lines(body), ct)));

    private static Task<IResult> DecideAsync(
        Guid id, Guid itemId, JsonElement body, HttpRequest request, DistributionWriter writer, CancellationToken ct) =>
        GuardedAsync(async () =>
            Results.Ok(await writer.DecideAsync(id, itemId, Seen(request), DistributionRequests.Decision(body), ct)));

    private static QuantityDto Both(BalanceError error, decimal quantity)
    {
        var (row, plan) = error.Both(quantity);
        return new QuantityDto(row, plan);
    }

    /// <summary>Версия распределения из <c>If-Match</c>; без заголовка — 400: правка записалась бы поверх чужой.</summary>
    private static string Seen(HttpRequest request)
    {
        if (!SeenVersion.TryRead(request, out var seen)) throw new InvalidRequestException(SeenVersion.Malformed);
        return seen ?? throw new InvalidRequestException(
            $"Не названа версия распределения, по которой собрана правка (заголовок {SeenVersion.Header}) — она приходит " +
            "в ответе чтения распределения полем «version». Без неё правка записалась бы поверх чужой.");
    }

    /// <summary>Отказы с полями — телом, которое экран читает без разбора фразы; остальные идут общим путём.</summary>
    private static async Task<IResult> GuardedAsync(Func<Task<IResult>> write)
    {
        try
        {
            return await write();
        }
        catch (DistributionBalanceException e)
        {
            return Results.BadRequest(new
            {
                error = e.Message, code = "balance",
                errors = e.Errors.Select(x => new
                {
                    rowId = x.RowId, number = x.Number, name = x.Name, code = x.Code,
                    sum = Both(x, x.Sum), volume = x.Volume is { } volume ? Both(x, volume) : null,
                    unit = x.Unit == PartUnit.Plan ? "plan" : "row", text = x.Text,
                }),
            });
        }
        catch (EstimateOnSectionException e)
        {
            return Results.BadRequest(new
            {
                error = e.Message, code = EstimateOnSectionException.Code, sectionId = e.SectionId, sectionName = e.SectionName,
            });
        }
        catch (DistributionChangedException e)
        {
            return Results.Conflict(new
            {
                error = e.Message, code = DistributionChangedException.Code, version = e.Version, changedBy = e.ChangedBy,
                changedAt = e.ChangedAt,
            });
        }
    }
}
