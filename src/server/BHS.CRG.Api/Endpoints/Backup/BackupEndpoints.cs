using System.Security.Claims;
using BHS.CRG.Application.Jobs;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Jobs;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Updates;

using BHS.CRG.Api.Configuration;
using Microsoft.AspNetCore.Http.Features;

namespace BHS.CRG.Api.Endpoints.Backup;

public static class BackupEndpoints
{
    public static void MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/backup").RequireAuthorization("Admin");

        // Вес копии и предел, на котором откажет ЗАГРУЗКА через браузер, — одним ответом (issue #711).
        // Считается по требованию: раздел настроек свёрнут по умолчанию, и запрос уходит, когда его
        // раскрыли, а не при каждой загрузке страницы.
        g.MapGet("/size", async (BackupService svc, BackupSizeLimits limits, CancellationToken ct) =>
            Results.Ok(await svc.EstimateSizeAsync(limits.ArchiveBytes, ct)));

        // ── Каталог копий на сервере (issue #831) ─────────────────────────────

        // Список копий плюс всё, что о нём нужно знать интерфейсу: вместимость каталога, расписание
        // и его последний исход, и какие из копий сняло расписание (issue #832) — уборка трогает
        // только их, и человек вправе видеть это до того, как удивится.
        g.MapGet("/files", async (
            BackupFileStore store, IIntegrationSettings settings, ServiceStateStore stateStore,
            IJobService jobs, CancellationToken ct) =>
        {
            var schedule = (await settings.GetEffectiveAsync(ct)).Backup;
            var state = await stateStore.LoadAsync<BackupScheduleState>(BackupScheduleStateKeys.Schedule, ct);
            return Results.Ok(new
            {
                files = store.List(),
                keepCount = store.KeepCount,
                directory = store.Directory,
                scheduledFiles = state.Managed,
                schedule = new
                {
                    schedule.Enabled,
                    schedule.TimeOfDay,
                    schedule.KeepCount,
                    state.LastRunAt,
                    state.LastSuccessAt,
                    state.LastFileName,
                    state.LastError,
                    state.LastErrorAt,
                    running = await jobs.HasActiveOfKindAsync(JobKind.CreateBackup, ct),
                },
            });
        });

        // Расписание — продуктовая настройка (в БД, рядом с прочими), а не параметр развёртывания.
        // Проверяем ЗДЕСЬ, а не в службе: негодное время суток, принятое молча, означало бы
        // расписание, которое выглядит настроенным и не срабатывает никогда.
        g.MapPut("/schedule", async (
            BackupScheduleSettings input, BackupFileStore store, IIntegrationSettings settings,
            CancellationToken ct) =>
        {
            if (BackupScheduleService.ParseTimeOfDay(input.TimeOfDay) is null)
                return Results.BadRequest(new { error = "Время указывают в виде ЧЧ:ММ, например 03:00." });

            if (input.KeepCount < 1)
                return Results.BadRequest(new { error = "Хранить нужно хотя бы одну плановую копию." });

            // Больше, чем вмещает каталог, хранить нельзя — и молчать об этом нельзя тем более:
            // расписание упиралось бы в предел каждую ночь, а выглядело бы настроенным.
            if (input.KeepCount > store.KeepCount)
                return Results.BadRequest(new
                {
                    error = $"Каталог вмещает {store.KeepCount} копий — больше плановых хранить негде. " +
                            "Уменьшите число или поднимите вместимость каталога (BACKUP_KEEP_COUNT в " +
                            "deploy/.env), если на диске есть место."
                });

            await settings.SaveBackupScheduleAsync(input, ct);
            return Results.NoContent();
        });

        // Снятие копии — фоновой задачей: минуты чтения базы и перекачки сканов, HTTP-запрос столько
        // не живёт. Предел числа копий проверяем ЗДЕСЬ, чтобы отказ пришёл ответом на нажатие
        // кнопки, а не уведомлением через минуту; в самой задаче он проверяется ещё раз.
        g.MapPost("/files", async (
            BackupFileStore store, IJobService jobs, ClaimsPrincipal user, CancellationToken ct) =>
        {
            // Одна копия за раз, и проверка — по ВИДУ задачи, а не по владельцу: список активных
            // задач у каждого свой, поэтому второй администратор о снятии, начатом первым, не
            // знает вовсе, а кнопка в интерфейсе гаснет лишь на следующем опросе — двойное нажатие
            // успевает поставить две. Цена дубля не только в лишних минутах работы: копии
            // адресуются по имени с точностью до секунды.
            if (await jobs.HasActiveOfKindAsync(JobKind.CreateBackup, ct))
                return Results.Conflict(new
                {
                    error = "Копия уже снимается — дождитесь окончания. Ход виден в списке задач " +
                            "рядом с колокольчиком; о завершении система сообщит."
                });

            store.EnsureRoomForNewCopy();
            var jobId = await jobs.EnqueueAsync(
                JobKind.CreateBackup, UserId(user), Guid.Empty, "Резервное копирование", null, ct);
            return Results.Accepted("/api/jobs/active", new { jobId });
        });

        g.MapGet("/files/{fileName}", (string fileName, BackupFileStore store) =>
        {
            var stream = store.OpenRead(fileName);
            // enableRangeProcessing: копия — это гигабайты, и оборванная закачка должна
            // возобновляться, а не начинаться сначала.
            return Results.File(stream, "application/zip", fileName, enableRangeProcessing: true);
        });

        g.MapDelete("/files/{fileName}", (string fileName, BackupFileStore store) =>
        {
            store.Delete(fileName);
            return Results.NoContent();
        });

        // Копия, принесённая через браузер: кладём в тот же каталог, дальше она ничем не отличается
        // от снятой здесь — восстановление у обеих одно (issue #831). Предел размера остаётся
        // прежним (BACKUP_MAX_ARCHIVE_MB): это предел ТРАНСПОРТА, и крупную копию кладут в каталог
        // на хосте, о чём и говорит отказ.
        //
        // Форму читаем сами из HttpRequest, а не принимаем IFormFile параметром: связывание
        // параметра читает тело ДО входа в обработчик, а поднять предел нужно ДО чтения. Архив
        // восстановления — единственное, чему нужен потолок в сотни мегабайт, и глобально задирать
        // его нельзя (issue #482): тогда любой пользователь заставил бы сервер выписать на диск
        // сотни мегабайт прежде, чем получить отказ.
        g.MapPost("/files/upload", async (
            HttpRequest request, BackupFileStore store, BackupSizeLimits limits, CancellationToken ct) =>
        {
            var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
                sizeFeature.MaxRequestBodySize = limits.RequestBytes;

            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Ожидается multipart/form-data" });

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null) return Results.BadRequest(new { error = "Файл не указан" });
            if (file.Length > limits.ArchiveBytes) return TooLargeForUpload(limits);

            await using var stream = file.OpenReadStream();
            var info = await store.AcceptUploadAsync(stream, file.FileName, ct);
            return Results.Ok(info);
        }).DisableAntiforgery();

        // Восстановление — ТОЛЬКО из каталога: файл уже на сервере, сеть не пересекается, предела
        // размера этому пути не нужно вовсе (issue #831). Имя проверяет хранилище (простое имя
        // файла внутри каталога), поэтому отсюда путь наружу не адресуется.
        g.MapPost("/restore", async (
            RestoreRequest req, BackupService svc, BackupFileStore store, CancellationToken ct) =>
        {
            await using var stream = store.OpenRead(req.FileName);
            var report = await svc.ImportAsync(stream, ct);
            return Results.Ok(report);
        });
    }

    /// <summary>
    /// Отказ по размеру называет ВЫХОД, а не только предел: «файл превышает 500 МБ» без продолжения
    /// оставляет администратора с копией, которую система сняла сама и принимать отказывается.
    /// Крупную копию не загружают — её кладут в каталог на хосте, и оттуда она видна в списке.
    /// </summary>
    private static IResult TooLargeForUpload(BackupSizeLimits limits) =>
        Results.Json(new
        {
            error = $"Файл превышает {limits.ArchiveMb} МБ — это предел загрузки через браузер. " +
                    "Положите копию в каталог резервных копий на сервере (BACKUP_DIR в deploy/.env): " +
                    "она появится в списке, и восстановить её можно будет прямо оттуда, без загрузки."
        }, statusCode: StatusCodes.Status413PayloadTooLarge);

    private static Guid UserId(ClaimsPrincipal user)
        => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub")!);

    private record RestoreRequest(string FileName);
}
