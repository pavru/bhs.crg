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
            var rows = await log.ReadAsync(skip ?? 0, take ?? 50, visible, action, ct);
            return Results.Ok(new ActivityPageDto(
                await log.CountAsync(visible, action, ct),
                [.. rows.Select(r => new ActivityRowDto(
                    r.Id, r.OccurredAt, r.Action, actions.Title(r.Action),
                    r.ActorId, r.ActorName, r.TargetLabel, r.Before, r.After,
                    r.Count, r.LastAt, r.FirstTargetLabel,
                    // Ключ раскрытия — только у ряда, которому есть что раскрывать.
                    r.Count > 1 ? r.SessionId : null))]));
        });

        // Записи одного ряда (ТЗ CORE-28.2). Только чтение, поэтому ни If-Match, ни ворот модуля:
        // журнал — служба ядра, а отбор по праву вида — та же видимость, что у страницы.
        //
        // ⚠️ Пусто — 404 и для «нет такой серии», и для «скрыта от читающего»: различая их, ответ
        // сообщал бы, что серия есть, тому, кому её не видно.
        g.MapGet("/series/{sessionId:guid}", async (Guid sessionId, string? action, ClaimsPrincipal principal,
            IUserPermissions permissions, IActivityLog log, ActivityActionCatalog actions, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(action))
                return Results.BadRequest(new { error = "Назовите вид ряда: ?action=<код действия>." });

            var visible = actions.VisibleTo(await permissions.ForAsync(principal, ct));
            var records = await log.SeriesAsync(sessionId, action, visible, ct);
            return records.Count == 0
                ? Results.NotFound()
                : Results.Ok(records.Select(r => new ActivityRecordDto(
                    r.Id, r.OccurredAt, r.Action, actions.Title(r.Action),
                    r.ActorId, r.ActorName, r.TargetLabel, r.Before, r.After)));
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

    private record ActivityPageDto(int Total, ActivityRowDto[] Items);

    /// <summary>
    /// Ряд серии. <c>Target</c> — метка цели последней записи, <c>FirstTarget</c> — первой, если она
    /// менялась. ⚠️ Идентификатора цели здесь нет, как и у записи: журнал её показывает, а не ведёт к ней.
    /// </summary>
    private record ActivityRowDto(
        Guid Id, DateTimeOffset OccurredAt, string Action, string ActionTitle,
        Guid? ActorId, string ActorName, string? Target, string? Before, string? After,
        int Count, DateTimeOffset LastAt, string? FirstTarget, Guid? SessionId);

    private record ActivityRecordDto(
        Guid Id, DateTimeOffset OccurredAt, string Action, string ActionTitle,
        Guid? ActorId, string ActorName, string? Target, string? Before, string? After);

    private record ActivityActionDto(string Code, string Title);
}
