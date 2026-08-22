using BHS.CRG.Api.Updates;
using BHS.CRG.Application.Settings;
using BHS.CRG.Application.Updates;
using System.Security.Claims;

namespace BHS.CRG.Api.Endpoints.Maintenance;

/// <summary>
/// Что система знает о версиях (issue #813).
///
/// Читать может ЛЮБОЙ вошедший, и это не упущение: номер доступной версии показывается всем в
/// подвале боковой панели — пассивно, никого не дёргая. Тревожит только администратора, у которого
/// есть путь к действию, — ему приходит уведомление. А вот заметки выпуска и настройка проверки —
/// под Admin: первое незачем всем, второе меняет поведение системы.
/// </summary>
public static class UpdateEndpoints
{
    public static void MapUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/system").RequireAuthorization();

        g.MapGet("/update", async (IUpdateCheck check, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var s = await check.GetStatusAsync(ct);
            var admin = user.IsInRole("Admin");
            return Results.Ok(new
            {
                s.Installed,
                s.Latest,
                s.UpdateAvailable,
                s.LastCheckedAt,
                s.Enabled,
                // Заметки и ссылка — администратору: остальным они ни к чему, а показывать всё, что
                // есть, — верный способ превратить полезное в фон.
                releaseUrl = admin ? s.ReleaseUrl : null,
                releaseNotes = admin ? s.ReleaseNotes : null,
            });
        });

        // Проверка ПО ТРЕБОВАНИЮ. Без неё выключатель неопровержим: он выглядит одинаково и когда
        // проверка ходит, и когда она полгода падает на прокси, — а ждать шесть часов, чтобы это
        // выяснить, никто не станет.
        g.MapPost("/update/check", async (UpdateCheckService svc, CancellationToken ct) =>
        {
            var s = await svc.CheckAsync(ct);
            return Results.Ok(new { s.Installed, s.Latest, s.UpdateAvailable, s.LastCheckedAt, s.Enabled });
        }).RequireAuthorization("Admin");

        g.MapPut("/update/settings", async (UpdateCheckSettings input, IIntegrationSettings settings) =>
        {
            await settings.SaveUpdatesAsync(input);
            return Results.NoContent();
        }).RequireAuthorization("Admin");
    }
}
