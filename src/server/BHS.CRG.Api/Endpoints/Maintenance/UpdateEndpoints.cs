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

        // withNotes=true — только для страницы настроек. По умолчанию заметки НЕ отдаются, и это не
        // экономия ради экономии: статус читает подвал боковой панели, то есть каждый заход в
        // систему. Заметки первого выпуска весят 70 КБ (`--generate-notes` собрал весь список
        // изменений), и без этого разделения столько уезжало бы на каждый экран ради строки
        // «доступна 0.138.0». Замерено живым вызовом: 75 146 байт против ~200.
        g.MapGet("/update", async (IUpdateCheck check, ClaimsPrincipal user, bool? withNotes,
            CancellationToken ct) =>
        {
            var s = await check.GetStatusAsync(ct);
            // Заметки и ссылка — администратору: остальным они ни к чему, а показывать всё, что
            // есть, — верный способ превратить полезное в фон.
            var notes = user.IsInRole("Admin") && withNotes == true;
            return Results.Ok(new
            {
                s.Installed,
                s.Latest,
                s.UpdateAvailable,
                s.LastCheckedAt,
                s.Enabled,
                releaseUrl = notes ? s.ReleaseUrl : null,
                releaseNotes = notes ? s.ReleaseNotes : null,
            });
        });

        // Проверка ПО ТРЕБОВАНИЮ. Без неё выключатель неопровержим: он выглядит одинаково и когда
        // проверка ходит, и когда она полгода падает на прокси, — а ждать шесть часов, чтобы это
        // выяснить, никто не станет.
        g.MapPost("/update/check", async (UpdateCheckService svc, CancellationToken ct) =>
        {
            var s = await svc.CheckAsync(ct);
            // JustChecked и LastError едут обязательно: служба глотает сбой сети и возвращает
            // ПРЕЖНЕЕ состояние, так что без них ответ на неудачную проверку неотличим от удачной —
            // а кнопка заведена ровно затем, чтобы делать выключатель «включено» опровержимым.
            return Results.Ok(new
            {
                s.Installed, s.Latest, s.UpdateAvailable, s.LastCheckedAt, s.Enabled,
                s.JustChecked, s.LastError,
            });
        }).RequireAuthorization("Admin");

        g.MapPut("/update/settings", async (UpdateCheckSettings input, IIntegrationSettings settings) =>
        {
            await settings.SaveUpdatesAsync(input);
            return Results.NoContent();
        }).RequireAuthorization("Admin");
    }
}
