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
        var g = app.MapGroup("/api/generate").RequireAuthorization();

        g.MapPost("/{instanceId:guid}", async (
            Guid instanceId, GenerateRequest req, IMediator m, ClaimsPrincipal user) =>
        {
            if (!string.Equals(req.Format, "pdf", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"Неизвестный формат генерации: «{req.Format}». Поддерживается только PDF." });
            var format = OutputFormat.Pdf;
            var generatedBy = user.FindFirst("displayName")?.Value;
            var userIdStr = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
            Guid? userId = Guid.TryParse(userIdStr, out var uid) ? uid : null;
            try
            {
                var files = await m.Send(new GenerateDocumentCommand(instanceId, format, generatedBy, userId));
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

        // Живой предпросмотр (issue #193): рендер ДЕФОЛТНОГО шаблона на переданных (несохранённых)
        // реквизитах в PDF. Эфемерно — ничего не персистит. PDF → 200 application/pdf;
        // нет шаблона → 200 {noTemplate:true}; ошибка резолва/Typst → 422 {error, diagnostics}.
        g.MapPost("/preview/{instanceId:guid}", async (
            Guid instanceId, System.Text.Json.JsonElement requisites, IMediator m, CancellationToken ct) =>
        {
            var raw = requisites.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "{}" : requisites.GetRawText();
            var result = await m.Send(new PreviewDocumentQuery(instanceId, System.Text.Json.JsonDocument.Parse(raw)), ct);
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
        g.MapGet("/validate/{instanceId:guid}", async (Guid instanceId, IMediator m) =>
        {
            var diagnostics = await m.Send(new ValidateInstanceResolutionQuery(instanceId));
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

        // Отладочный пакет: template.typ + data.json + typeblocks.typ + userlib.typ —
        // ровно те файлы, что генератор кладёт в tmpDir. Распаковал → typst compile template.typ.
        g.MapGet("/debug-bundle/{instanceId:guid}", async (Guid instanceId, IMediator m,
            BHS.CRG.Application.Common.IBlobStorage blob, CancellationToken ct) =>
        {
            var bundle = await m.Send(new GetGenerationDebugBundleQuery(instanceId), ct);
            if (bundle is null) return Results.NotFound();

            var typeBlocks = string.IsNullOrEmpty(bundle.TypeBlocks)
                ? "// no composite-type render functions defined"
                : bundle.TypeBlocks;
            var userLib = string.IsNullOrEmpty(bundle.UserLib)
                ? "// user typst library is empty"
                : bundle.UserLib;

            // Материализуем поля-изображения в assets/ во временный каталог, как при реальной генерации.
            var tmpDir = Path.Combine(Path.GetTempPath(), "dbgbundle-" + Guid.NewGuid());
            Directory.CreateDirectory(tmpDir);
            string dataJson;
            try
            {
                var prettyOpts = new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                };
                dataJson = BHS.CRG.Infrastructure.Generation.TypstImageMaterializer
                    .MaterializeJson(bundle.DataJson, tmpDir, "assets", prettyOpts, bundle.ImageOptions);

                // Вложения ({$type:"file"}) скачиваем в те же assets/ (att_N) — bundle воспроизводит ВХОД
                // для внешнего `typst compile`. blob-доступ на сервере есть; так внешний Typst найдёт файлы.
                var assetsDirDbg = Path.Combine(tmpDir, "assets");
                var attCountDbg = 0;
                var dataNode = System.Text.Json.Nodes.JsonNode.Parse(dataJson) ?? new System.Text.Json.Nodes.JsonObject();
                await new BHS.CRG.Infrastructure.Generation.TypstFileMaterializer(blob).MaterializeAsync(dataNode, (bytes, ext) =>
                {
                    Directory.CreateDirectory(assetsDirDbg);
                    var name = $"att_{attCountDbg++}.{ext}";
                    File.WriteAllBytes(Path.Combine(assetsDirDbg, name), bytes);
                    return $"assets/{name}";
                }, ct);
                dataJson = dataNode.ToJsonString(prettyOpts);

                // Ассеты шаблона (issue #62) — уже свёрнутые по приоритету резолвером; тот же
                // паттерн материализации, что и в TypstGenerator, только пишем сразу в assets/
                // отладочного пакета (картинки) и в отдельную fonts/ (шрифты для --font-path).
                foreach (var img in bundle.TemplateAssets.Images)
                {
                    try
                    {
                        await using var imgStream = await blob.DownloadAsync(img.BlobPath, ct);
                        using var imgMs = new MemoryStream();
                        await imgStream.CopyToAsync(imgMs, ct);
                        Directory.CreateDirectory(assetsDirDbg);
                        var ext = Path.GetExtension(img.FileName);
                        await File.WriteAllBytesAsync(Path.Combine(assetsDirDbg, $"{img.Name}{ext}"), imgMs.ToArray(), ct);
                    }
                    catch { /* пропускаем недоступный ассет — best effort, как и остальная материализация */ }
                }
                string? fontsDirDbg = null;
                if (bundle.TemplateAssets.Fonts.Count > 0)
                {
                    fontsDirDbg = Path.Combine(tmpDir, "fonts");
                    Directory.CreateDirectory(fontsDirDbg);
                    var fontIdx = 0;
                    foreach (var font in bundle.TemplateAssets.Fonts)
                    {
                        try
                        {
                            await using var fontStream = await blob.DownloadAsync(font.BlobPath, ct);
                            using var fontMs = new MemoryStream();
                            await fontStream.CopyToAsync(fontMs, ct);
                            var ext = Path.GetExtension(font.FileName);
                            await File.WriteAllBytesAsync(Path.Combine(fontsDirDbg, $"font_{fontIdx++}{ext}"), fontMs.ToArray(), ct);
                        }
                        catch { /* пропускаем недоступный шрифт — best effort */ }
                    }
                }

                using var ms = new MemoryStream();
                using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
                {
                    await WriteEntry(zip, "template.typ", bundle.TemplateContent);
                    await WriteEntry(zip, "data.json", dataJson);
                    await WriteEntry(zip, "typeblocks.typ", typeBlocks);
                    await WriteEntry(zip, "userlib.typ", userLib);
                    if (fontsDirDbg is not null)
                        await WriteEntry(zip, "README.txt",
                            "У шаблона есть шрифтовые ассеты — компилируйте с --font-path:\r\n\r\n" +
                            "  typst compile template.typ output.pdf --font-path fonts\r\n");

                    var assetsDir = Path.Combine(tmpDir, "assets");
                    if (Directory.Exists(assetsDir))
                        foreach (var file in Directory.GetFiles(assetsDir))
                        {
                            var entry = zip.CreateEntry($"assets/{Path.GetFileName(file)}",
                                System.IO.Compression.CompressionLevel.Optimal);
                            await using var es = entry.Open();
                            await using var fs = File.OpenRead(file);
                            await fs.CopyToAsync(es, ct);
                        }
                    if (fontsDirDbg is not null)
                        foreach (var file in Directory.GetFiles(fontsDirDbg))
                        {
                            var entry = zip.CreateEntry($"fonts/{Path.GetFileName(file)}",
                                System.IO.Compression.CompressionLevel.Optimal);
                            await using var es = entry.Open();
                            await using var fs = File.OpenRead(file);
                            await fs.CopyToAsync(es, ct);
                        }
                }
                return Results.File(ms.ToArray(), "application/zip", $"typst-debug-{instanceId}.zip");
            }
            finally
            {
                try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort */ }
            }
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

    static object ToDto(ResolutionDiagnostic d) => new
    {
        severity = d.Severity.ToString().ToLowerInvariant(),
        path = d.Path,
        message = d.Message,
    };

    static async Task WriteEntry(System.IO.Compression.ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
        await using var s = entry.Open();
        await using var w = new StreamWriter(s, new System.Text.UTF8Encoding(false));
        await w.WriteAsync(content);
    }

    record GenerateRequest(string Format);
    record PluginSearchRequest(string EntityType, string Query);
    record PluginFetchRequest(string EntityType, string ExternalId);
}
