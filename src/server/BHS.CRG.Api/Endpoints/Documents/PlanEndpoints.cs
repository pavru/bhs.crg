using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Documents;

/// <summary>
/// План по документам (issue #796): строки плана на комплекте и сводка готовности по уровням.
///
/// Запись плана доступна роли User, а не только Admin: план — часть работы над комплектом
/// («сколько актов должно быть в этом разделе»), а не настройка системы.
/// </summary>
public static class PlanEndpoints
{
    public static void MapPlanEndpoints(this IEndpointRouteBuilder app)
    {
        var sets = app.MapGroup("/api/document-sets").RequireAuthorization();

        sets.MapGet("/{id:guid}/plan", async (Guid id, IMediator m, CancellationToken ct)
            => Results.Ok(await m.Send(new GetDocumentSetPlanQuery(id), ct)));

        // Замена целиком: пришло — то и осталось. Пустой список означает «плана нет» и убирает
        // проценты с экранов — отдельной команды «удалить план» для этого не нужно.
        sets.MapPut("/{id:guid}/plan", async (Guid id, PlanRow[] rows, IMediator m, CancellationToken ct) =>
        {
            try
            {
                await m.Send(new ReplaceDocumentSetPlanCommand(id, rows ?? []), ct);
                return Results.NoContent();
            }
            catch (NotFoundException) { return Results.NotFound(); }
            catch (InvalidRequestException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        var plans = app.MapGroup("/api/plans").RequireAuthorization();

        plans.MapGet("/summary", async (string? scope, Guid? scopeId, IMediator m, CancellationToken ct) =>
        {
            if (!Enum.TryParse<CatalogScope>(scope, ignoreCase: true, out var parsed))
                return Results.BadRequest(new { error = "Неизвестный уровень: " + scope });

            return Results.Ok(await m.Send(new GetPlanSummaryQuery(parsed, scopeId), ct));
        });
    }
}
