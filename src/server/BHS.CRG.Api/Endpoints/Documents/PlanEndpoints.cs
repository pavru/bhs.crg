using BHS.CRG.Api.Endpoints.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules;
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

        sets.MapGet("/{id:guid}/plan", async (Guid id, IMediator m, CancellationToken ct) =>
        {
            try { return Results.Ok(await m.Send(new GetDocumentSetPlanQuery(id), ct)); }
            catch (NotFoundException) { return Results.NotFound(); }
        });

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
        })
        // Замена плана — правка комплекта (задача A3, issue #1074): ворота модуля проходит и тот, у
        // кого в модуле одно чтение, в том числе владелец составного «читать всё».
        .RequireAuthorization(AppPolicies.Permission("id.document.edit"));

        var plans = app.MapGroup("/api/plans").RequireAuthorization();

        plans.MapGet("/summary", async (string? scope, Guid? scopeId, IMediator m, CancellationToken ct) =>
        {
            // «?scope=99» без проверки доехал бы до обработчика уровнем, которого нет, и ответом «плана
            // нет» вместо отказа — разбор общий и это отсекает (ScopeParameter).
            if (!ScopeParameter.TryParse(scope, out var parsed))
                return Results.BadRequest(new { error = $"Неизвестная область: «{scope}»." });

            // Уровень без идентификатора (кроме System) — не «пустая стройка», а вопрос ни о чём:
            // спуск по поддереву вернёт пусто, и ответ «плана нет» неотличим от настоящего уровня
            // без плана. Сводка проблем отказывает здесь так же.
            if (parsed != CatalogScope.System && scopeId is null)
                return Results.BadRequest(new { error = "Для этой области нужен scopeId." });

            return Results.Ok(await m.Send(new GetPlanSummaryQuery(parsed, scopeId), ct));
        });
    }
}
