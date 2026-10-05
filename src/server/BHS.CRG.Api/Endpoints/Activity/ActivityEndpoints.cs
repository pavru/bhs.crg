using System.Security.Claims;
using BHS.CRG.Api.Activity;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Activity;
using BHS.CRG.Modules;

namespace BHS.CRG.Api.Endpoints.Activity;

/// <summary>
/// Чтение журнала действий (ТЗ CORE-28, issue #950). Записи отдаются страницей и только по праву
/// <c>core.audit.read</c>.
///
/// ⚠️ Право на журнал открывает записи ЯДРА. Запись модуля видна тому, кому открыт модуль, а если
/// действие назвало право чтения — только с ним (issue #1104, <see cref="ActivityVisibility" />):
/// иначе журнал показывал бы, какие счета и кому оплачены, человеку без права на счета. Общее число
/// считается по тому же отбору — оно не выдаёт, сколько записей скрыто.
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

        g.MapGet("/", async (int? skip, int? take, string? action, ClaimsPrincipal principal,
            IUserPermissions permissions, IActivityLog log, ActivityActionCatalog actions, CancellationToken ct) =>
        {
            var visible = actions.VisibleTo(await permissions.ForAsync(principal, ct));
            var records = await log.ReadAsync(skip ?? 0, take ?? 50, visible, action, ct);
            return Results.Ok(new ActivityPageDto(
                await log.CountAsync(visible, action, ct),
                [.. records.Select(r => new ActivityRecordDto(
                    r.Id, r.OccurredAt, r.Action, actions.Title(r.Action),
                    r.ActorId, r.ActorName, r.TargetLabel, r.Before, r.After))]));
        });

        // Список действий для отбора на экране. Строится из каталога, а не из того, что уже
        // записано: иначе отбор показывал бы только случившееся, и «смен ролей не было» было бы не
        // отличить от «такого отбора нет».
        //
        // ⚠️ Каталог — общий: ядро И включённые модули (задача M2 этапа 2). Читай мы здесь только
        // ActivityActions, действия модулей остались бы без названия на экране и без строки в
        // отборе — то есть название, переданное модулем при записи, не доезжало бы никуда.
        g.MapGet("/actions", async (ClaimsPrincipal principal, IUserPermissions permissions,
            ActivityActionCatalog actions, CancellationToken ct) => Results.Ok(
            actions.Shown(actions.VisibleTo(await permissions.ForAsync(principal, ct)))
                .Select(a => new ActivityActionDto(a.Code, a.Title))));
    }

    private record ActivityPageDto(int Total, ActivityRecordDto[] Items);

    private record ActivityRecordDto(
        Guid Id, DateTimeOffset OccurredAt, string Action, string ActionTitle,
        Guid? ActorId, string ActorName, string? Target, string? Before, string? After);

    private record ActivityActionDto(string Code, string Title);
}
