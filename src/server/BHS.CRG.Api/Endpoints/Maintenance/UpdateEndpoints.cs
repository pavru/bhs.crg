using BHS.CRG.Modules;
using BHS.CRG.Api.Auth;
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
/// под правом обслуживания: первое незачем всем, второе меняет поведение системы.
///
/// ⚠️ Поэтому группа здесь РАЗДЕЛЕНА, а не закрыта целиком (issue #947). Ворота на всю группу
/// выглядели безобидно — «обновления это обслуживание», — но статус версии читает подвал боковой
/// панели, то есть КАЖДЫЙ экран каждого пользователя. Закрытая группа означала 403 на всех экранах
/// у всех, кроме администратора, и пропавший индикатор версии. Ровно это здесь и произошло; ниже
/// стоит тест, который не даст повторить.
/// </summary>
public static class UpdateEndpoints
{
    public static void MapUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        // Статус версии — всем вошедшим (см. доккомментарий). Отдельного права нет намеренно:
        // разграничивать нечего, а право, выданное каждой роли, ничего не проверяет.
        var g = app.MapGroup("/api/system").RequireAuthorization();

        // Управление проверкой обновлений — под правом обслуживания.
        var upkeep = app.MapGroup("/api/system")
            .RequireAuthorization(AppPolicies.Permission(CorePermissions.SystemManage));

        // withNotes=true — только для страницы настроек. По умолчанию заметки НЕ отдаются, и это не
        // экономия ради экономии: статус читает подвал боковой панели, то есть каждый заход в
        // систему. Заметки первого выпуска весят 70 КБ (`--generate-notes` собрал весь список
        // изменений), и без этого разделения столько уезжало бы на каждый экран ради строки
        // «доступна 0.138.0». Замерено живым вызовом: 75 146 байт против ~200.
        g.MapGet("/update", async (IUpdateCheck check, IIntegrationSettings settings, ClaimsPrincipal user,
            IUserPermissions permissions, bool? withNotes, CancellationToken ct) =>
        {
            var s = await check.GetStatusAsync(ct);
            // Заметки и ссылка — тому, кто обслуживает экземпляр: остальным они ни к чему, а
            // показывать всё, что есть, — верный способ превратить полезное в фон.
            //
            // ⚠️ Спрашивается ПРАВО, а не роль. Иначе обладатель core.system.manage без роли Admin
            // получал бы полуоткрытую дверь: адрес отвечает 200, но без заметок и без галки прокси —
            // то есть страница настроек выглядит рабочей и молча недоукомплектованной.
            var granted = await permissions.ForAsync(user, ct);
            var notes = withNotes == true
                && granted.Contains(CorePermissions.SystemManage, StringComparer.OrdinalIgnoreCase);
            return Results.Ok(new
            {
                s.Installed,
                s.Latest,
                s.UpdateAvailable,
                s.LastCheckedAt,
                s.Enabled,
                releaseUrl = notes ? s.ReleaseUrl : null,
                releaseNotes = notes ? s.ReleaseNotes : null,
                // Галка «через прокси» — администратору, на странице настроек: без неё переключатель
                // проверки, сохраняя себя, сбрасывал бы галку (секция сохраняется целиком, issue #936).
                useProxy = notes && (await settings.GetEffectiveAsync(ct)).Updates.UseProxy,
            });
        });

        // Проверка ПО ТРЕБОВАНИЮ. Без неё выключатель неопровержим: он выглядит одинаково и когда
        // проверка ходит, и когда она полгода падает на прокси, — а ждать шесть часов, чтобы это
        // выяснить, никто не станет.
        upkeep.MapPost("/update/check", async (UpdateCheckService svc, CancellationToken ct) =>
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
        });

        upkeep.MapPut("/update/settings", async (UpdateCheckSettings input, IIntegrationSettings settings) =>
        {
            await settings.SaveUpdatesAsync(input);
            return Results.NoContent();
        });
    }
}
