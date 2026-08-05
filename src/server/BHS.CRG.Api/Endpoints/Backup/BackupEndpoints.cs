using BHS.CRG.Infrastructure.Backup;

using BHS.CRG.Api.Endpoints.Common;
using Microsoft.AspNetCore.Http.Features;

namespace BHS.CRG.Api.Endpoints.Backup;

public static class BackupEndpoints
{
    public static void MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/backup").RequireAuthorization("Admin");

        g.MapGet("/", async (BackupService svc, CancellationToken ct) =>
        {
            var (stream, fileName) = await svc.ExportAsync(ct);
            return Results.File(stream, "application/zip", fileName);
        });

        // Форму читаем сами из HttpRequest, а не принимаем IFormFile параметром: связывание параметра
        // читает тело ДО входа в обработчик, а поднять предел нужно ДО чтения. Архив восстановления —
        // единственное, чему нужен потолок в сотни мегабайт, и глобально задирать его нельзя
        // (issue #482): тогда любой пользователь заставил бы сервер выписать на диск сотни мегабайт
        // прежде, чем получить отказ.
        g.MapPost("/restore", async (HttpRequest request, BackupService svc, CancellationToken ct) =>
        {
            var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
                sizeFeature.MaxRequestBodySize = UploadLimits.BackupRequest;

            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Ожидается multipart/form-data" });

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null) return Results.BadRequest(new { error = "Файл не указан" });
            if (UploadLimits.Exceeded(file, UploadLimits.BackupArchive) is { } tooLarge) return tooLarge;

            try
            {
                using var stream = file.OpenReadStream();
                var report = await svc.ImportAsync(stream, ct);
                return Results.Ok(report);
            }
            catch (DomainException ex)
            {
                // Только НАШ отказ: «файл не резервная копия», «манифест не читается». Всё прочее
                // здесь не ловим намеренно — восстановление работает и с базой, и с хранилищем, и
                // сообщение их клиентов называет таблицу, ограничение и адрес. Общий обработчик
                // запишет такое в лог и ответит обобщённо (issue #691).
                return Results.BadRequest(new { error = ex.Message });
            }
        }).DisableAntiforgery();
    }
}
