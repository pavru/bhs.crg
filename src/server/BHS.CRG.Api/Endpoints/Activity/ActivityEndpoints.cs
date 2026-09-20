using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Activity;
using BHS.CRG.Modules;

namespace BHS.CRG.Api.Endpoints.Activity;

/// <summary>
/// Чтение журнала действий (ТЗ CORE-28, issue #950). Записи отдаются страницей и только по праву
/// <c>core.audit.read</c>.
///
/// ⚠️ Адресов записи здесь НЕТ и не будет: журнал пишут издатели событий через <c>IActivityLog</c>,
/// а не запросом снаружи. Адрес «добавить запись» означал бы, что в журнал можно положить что
/// угодно от чьего угодно имени, — и обесценил бы остальные записи заодно.
/// </summary>
public static class ActivityEndpoints
{
    public static void MapActivityEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/activity")
            .RequireAuthorization(AppPolicies.Permission(CorePermissions.AuditRead));

        g.MapGet("/", async (int? skip, int? take, string? action, IActivityLog log, CancellationToken ct) =>
        {
            var records = await log.ReadAsync(skip ?? 0, take ?? 50, action, ct);
            return Results.Ok(new ActivityPageDto(
                await log.CountAsync(action, ct),
                [.. records.Select(r => new ActivityRecordDto(
                    r.Id, r.OccurredAt, r.Action, ActivityActions.Title(r.Action),
                    r.ActorId, r.ActorName, r.TargetLabel, r.Before, r.After))]));
        });

        // Список действий для отбора на экране. Строится из каталога, а не из того, что уже
        // записано: иначе отбор показывал бы только случившееся, и «смен ролей не было» было бы не
        // отличить от «такого отбора нет».
        g.MapGet("/actions", () => Results.Ok(
            ActivityActions.All.Select(a => new ActivityActionDto(a.Code, a.Title))));
    }

    private record ActivityPageDto(int Total, ActivityRecordDto[] Items);

    private record ActivityRecordDto(
        Guid Id, DateTimeOffset OccurredAt, string Action, string ActionTitle,
        Guid? ActorId, string ActorName, string? Target, string? Before, string? After);

    private record ActivityActionDto(string Code, string Title);
}
