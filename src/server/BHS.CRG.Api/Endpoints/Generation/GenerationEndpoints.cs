using BHS.CRG.Api.Auth;
using BHS.CRG.Modules;
using System.Security.Claims;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Templates;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Generation;

public static class GenerationEndpoints
{
    public static void MapGenerationEndpoints(this IEndpointRouteBuilder app)
    {
        // ⚠️ Ворота на праве МОДУЛЯ, а адрес регистрируется ОБЩИМ кодом запуска (переезд кода в модуль
        // отложен, см. IdModule). Сегодня это безопасно: в сборке модуль один, он же умолчание, и
        // конфигурации с выключенным «id» не существует — пустой Modules__Enabled подставляет умолчание,
        // а любое другое значение роняет запуск. Проверено живым приложением 21.09.2026.
        //
        // Ловушка не исчезла, а отложена: как появится второй модуль и сборка, где «id» можно выключить,
        // права выключенного модуля перестанут попадать в справочник (AUTH-19) — и эти ворота начнут
        // отвечать 500 при сборке политики вместо отказа. Лечится переездом адресов в модуль, а не
        // смягчением проверки: проверка здесь права.
        var g = app.MapGroup("/api/generate").RequireAuthorization(AppPolicies.Permission("id.document.generate"));

        g.MapPost("/{instanceId:guid}", async (
            Guid instanceId, GenerateRequest req, IMediator m, ClaimsPrincipal user,
            DataAccessResolver access, CancellationToken ct) =>
        {
            if (!string.Equals(req.Format, "pdf", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"Неизвестный формат генерации: «{req.Format}». Поддерживается только PDF." });
            var format = OutputFormat.Pdf;
            var generatedBy = user.FindFirst("displayName")?.Value;
            var userIdStr = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
            Guid? userId = Guid.TryParse(userIdStr, out var uid) ? uid : null;
            try
            {
                // ⚠️ БЕЗ токена запроса — как было до issue #965. Обработчик уже перевёл документ в
                // «Генерируется», и его же catch пишет «Ошибка» тем же токеном: с отменённым токеном
                // это сохранение не проходит, и документ остаётся в «Генерируется» навсегда, без
                // уведомления. Клиент, закрывший вкладку, не должен оставлять такой след (нашло
                // ревью PR #1057). Параметр доступа берётся ДО отправки — там токен ещё нужен.
                var files = await m.Send(new GenerateDocumentCommand(
                    instanceId, format, await access.ForAsync(user, ct), generatedBy, userId));
                return Results.Ok(files.Select(f => new { f.Id, f.BlobPath, Format = f.Format.ToString(), f.TemplateId }));
            }
            catch (ResolutionValidationException ex)
            {
                // Ошибки разрешения ссылок — генерация прервана, отдаём диагностику (422).
                return Results.UnprocessableEntity(new
                {
                    error = "Генерация прервана: ошибки разрешения ссылок",
                    diagnostics = ex.Diagnostics.Select(ToDto),
                });
            }
        });

        // Живой предпросмотр (issue #193): рендер шаблона генерации (TemplateChoice) на переданных (несохранённых)
        // реквизитах в PDF. Эфемерно — ничего не персистит. PDF → 200 application/pdf;
        // нет шаблона → 200 {noTemplate:true}; ошибка резолва/Typst → 422 {error, diagnostics}.
        g.MapPost("/preview/{instanceId:guid}", async (
            Guid instanceId, System.Text.Json.JsonElement requisites, IMediator m,
            ClaimsPrincipal user, DataAccessResolver access, CancellationToken ct) =>
        {
            var raw = requisites.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "{}" : requisites.GetRawText();
            var result = await m.Send(new PreviewDocumentQuery(
                instanceId, System.Text.Json.JsonDocument.Parse(raw), await access.ForAsync(user, ct)), ct);
            if (result.Pdf is not null)
                return Results.File(result.Pdf, "application/pdf");
            if (result.NoTemplate)
                return Results.Ok(new { noTemplate = true });
            return Results.UnprocessableEntity(new
            {
                error = result.Error ?? "Не удалось построить предпросмотр",
                diagnostics = result.Diagnostics?.Select(ToDto),
            });
        });

        // Проверка разрешения ссылок «по требованию» — возвращает все проблемы (warning/error).
        g.MapGet("/validate/{instanceId:guid}", async (Guid instanceId, IMediator m,
            ClaimsPrincipal user, DataAccessResolver access, CancellationToken ct) =>
        {
            var diagnostics = await m.Send(
                new ValidateInstanceResolutionQuery(instanceId, await access.ForAsync(user, ct)), ct);
            return Results.Ok(diagnostics.Select(ToDto));
        });

        g.MapGet("/download/{instanceId:guid}/{format}", async (
            Guid instanceId, string format, IMediator m, IBlobStorage blob,
            IRepository<DocumentType> docTypes, IRepository<Template> templates, CancellationToken ct) =>
        {
            if (!string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"Неизвестный формат: «{format}». Поддерживается только PDF." });

            var inst = await m.Send(new GetDocumentInstanceQuery(instanceId), ct);
            if (inst is null) return Results.NotFound();

            var generatedFile = inst.GeneratedFiles.FirstOrDefault(f => f.Format == OutputFormat.Pdf);
            if (generatedFile is null) return Results.NotFound();

            var name = await BuildDownloadNameAsync(inst, generatedFile.TemplateId, docTypes, templates, ct);
            var stream = await blob.DownloadAsync(generatedFile.BlobPath, ct);
            return Results.File(stream, "application/pdf", name);
        });

        // Скачивание файла конкретного шаблона (мульти-шаблонная генерация — файлов может быть несколько).
        g.MapGet("/download/{instanceId:guid}/{templateId:guid}/{format}", async (
            Guid instanceId, Guid templateId, string format, IMediator m, IBlobStorage blob,
            IRepository<DocumentType> docTypes, IRepository<Template> templates, CancellationToken ct) =>
        {
            if (!string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"Неизвестный формат: «{format}». Поддерживается только PDF." });

            var inst = await m.Send(new GetDocumentInstanceQuery(instanceId), ct);
            if (inst is null) return Results.NotFound();

            var generatedFile = inst.GeneratedFiles.FirstOrDefault(f => f.Format == OutputFormat.Pdf && f.TemplateId == templateId);
            if (generatedFile is null) return Results.NotFound();

            var name = await BuildDownloadNameAsync(inst, templateId, docTypes, templates, ct);
            var stream = await blob.DownloadAsync(generatedFile.BlobPath, ct);
            return Results.File(stream, "application/pdf", name);
        });

        // Отладочный пакет: каталог компиляции генерации, записанный тем же кодом, что у неё
        // (TypstCompileDirectory), и README — ключи компиляции и причины, по которым генерация откажет.
        // Распаковал → typst compile template.typ. Собирает пакет обработчик; здесь — только отдача.
        g.MapGet("/debug-bundle/{instanceId:guid}", async (Guid instanceId, IMediator m,
            ClaimsPrincipal user, DataAccessResolver access, CancellationToken ct) =>
        {
            var bundle = await m.Send(
                new GetGenerationDebugBundleQuery(instanceId, await access.ForAsync(user, ct)), ct);
            return bundle is null
                ? Results.NotFound()
                : Results.File(bundle.Zip, "application/zip", $"typst-debug-{instanceId}.zip");
        });

        // Список плагинов
        g.MapGet("/plugins", (BHS.CRG.Infrastructure.Plugins.IPluginHost host)
            => Results.Ok(host.Plugins.Select(p => new { p.Id, p.DisplayName, p.ProvidedSchemas })));

        g.MapPost("/plugins/{pluginId}/search", async (
            string pluginId, PluginSearchRequest req,
            BHS.CRG.Infrastructure.Plugins.IPluginHost host) =>
        {
            var plugin = host.GetById(pluginId);
            if (plugin is null) return Results.NotFound();
            var result = await plugin.SearchAsync(req.EntityType, req.Query);
            return Results.Ok(result);
        });

        g.MapPost("/plugins/{pluginId}/fetch", async (
            string pluginId, PluginFetchRequest req,
            BHS.CRG.Infrastructure.Plugins.IPluginHost host) =>
        {
            var plugin = host.GetById(pluginId);
            if (plugin is null) return Results.NotFound();
            var data = await plugin.FetchAsync(req.EntityType, req.ExternalId);
            return Results.Ok(data);
        });
    }

    // Имя скачиваемого файла: «Имя документа - Имя шаблона.pdf» (спецсимволы → '_'). Имя документа —
    // из instance.Name, иначе имя типа; суффикс-шаблон — если файл сгенерирован конкретным шаблоном.
    static async Task<string> BuildDownloadNameAsync(DomainObject inst, Guid? templateId,
        IRepository<DocumentType> docTypes, IRepository<Template> templates, CancellationToken ct)
    {
        var docName = inst.DisplayName;
        if (string.IsNullOrWhiteSpace(docName))
            docName = (await docTypes.GetByIdAsync(inst.CompositeTypeId, ct))?.Name ?? "Документ";

        var name = FileNames.Sanitize(docName, "документ");
        if (templateId is { } tid)
        {
            var tpl = await templates.GetByIdAsync(tid, ct);
            if (tpl is not null && !string.IsNullOrWhiteSpace(tpl.Name))
                name += " - " + FileNames.Sanitize(tpl.Name, "шаблон");
        }
        return name + ".pdf";
    }

    /// <remarks>
    /// <c>code</c> обязателен (issue #759): по нему клиент отличает «цель удалена» от прочих
    /// претензий и решает, красить ли поле danger-плиткой. Без него
    /// <c>brokenRefPaths</c> отфильтровывал ВСЁ, и индикатор битых ссылок (#332) не загорался ни
    /// разу за всё время — ни на поле, ни бейджем раздела. Панель «Проверить ссылки» при этом
    /// работала: она показывает сообщения, а не коды, поэтому пропажа и не бросалась в глаза.
    /// </remarks>
    static object ToDto(ResolutionDiagnostic d) => new
    {
        severity = d.Severity.ToString().ToLowerInvariant(),
        path = d.Path,
        message = d.Message,
        code = d.Code,
    };

    record GenerateRequest(string Format);
    record PluginSearchRequest(string EntityType, string Query);
    record PluginFetchRequest(string EntityType, string ExternalId);
}
