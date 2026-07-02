using BHS.CRG.Application.DataSets;

namespace BHS.CRG.Api.Endpoints.DataSets;

public static class DataSetEndpoints
{
    public static void MapDataSetEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/datasets").RequireAuthorization();

        // ── Файлы ──────────────────────────────────────────────────────────────

        g.MapGet("/files", async (string? scope, Guid? scopeId, IDataSetService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListFilesAsync(scope, scopeId, ct)));

        g.MapGet("/available", async (Guid setId, IDataSetService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAvailableFilesAsync(setId, ct)));

        g.MapPost("/files", async (HttpRequest request, IDataSetService svc, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Ожидается multipart/form-data" });

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file == null) return Results.BadRequest(new { error = "Файл не указан" });

            var input = new UploadFileInput(
                await ReadBytesAsync(file, ct), file.FileName, file.ContentType,
                form["name"].FirstOrDefault(), form["scope"].ToString(), form["scopeId"].ToString());

            return Results.Ok(await svc.UploadFileAsync(input, ct));
        }).DisableAntiforgery();

        g.MapPut("/files/{id:guid}", async (Guid id, HttpRequest request, IDataSetService svc, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Ожидается multipart/form-data" });

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file == null) return Results.BadRequest(new { error = "Файл не указан" });

            var input = new ReplaceFileInput(
                await ReadBytesAsync(file, ct), file.FileName, file.ContentType, form["name"].FirstOrDefault());

            var result = await svc.ReplaceFileAsync(id, input, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).DisableAntiforgery();

        g.MapGet("/files/{id:guid}/download", async (Guid id, IDataSetService svc, CancellationToken ct) =>
        {
            var dl = await svc.DownloadFileAsync(id, ct);
            return dl is null ? Results.NotFound() : Results.File(dl.Stream, dl.ContentType, dl.FileName);
        });

        g.MapDelete("/files/{id:guid}", async (Guid id, IDataSetService svc, CancellationToken ct) =>
            await svc.DeleteFileAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        // ── Источники ──────────────────────────────────────────────────────────

        g.MapGet("/files/{fileId:guid}/sources", async (Guid fileId, IDataSetService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListSourcesAsync(fileId, ct)));

        g.MapGet("/sources/{sourceId:guid}/preview", async (
            Guid sourceId, int maxRows, IDataSetService svc, CancellationToken ct) =>
        {
            var preview = await svc.PreviewSourceAsync(sourceId, maxRows, ct);
            return preview is null ? Results.NotFound() : Results.Ok(preview);
        });

        g.MapPost("/sources/{sourceId:guid}/auto-map", async (
            Guid sourceId, AutoMapRequest req, IDataSetService svc, CancellationToken ct) =>
        {
            var fields = req.Fields.Select(f => new FieldInfo(f.Key, f.Title)).ToList();
            var mapping = await svc.AutoMapAsync(sourceId, fields, ct);
            return mapping is null ? Results.NotFound() : Results.Ok(new { mapping });
        });

        g.MapGet("/files/{fileId:guid}/zip-xml-entries", async (Guid fileId, IDataSetService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListZipXmlEntriesAsync(fileId, ct)));

        // Предпросмотр XPath/JSONPath-выражения в builder'е — без сохранения источника.
        g.MapPost("/files/{fileId:guid}/expression-preview", async (
            Guid fileId, ExpressionPreviewRequest req, IDataSetService svc, CancellationToken ct) =>
        {
            try { return Results.Ok(await svc.PreviewExpressionAsync(fileId, req.RowSelector, req.Expr, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Ручное создание/редактирование/удаление источника — единственный способ для XML
        // (авто-детект по top-level элементам не используется, см. XmlDataSetParser) и
        // дополнительный способ для JSON (в дополнение к авто-детекту top-level узлов).
        g.MapPost("/files/{fileId:guid}/sources", async (
            Guid fileId, SourceRequest req, IDataSetService svc, CancellationToken ct) =>
        {
            try
            {
                var input = new CreateSourceInput(req.Name, req.SheetOrPath, req.ColumnExpressions);
                return Results.Ok(await svc.CreateSourceAsync(fileId, input, ct));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        g.MapPut("/sources/{sourceId:guid}", async (
            Guid sourceId, SourceRequest req, IDataSetService svc, CancellationToken ct) =>
        {
            try
            {
                var input = new UpdateSourceInput(req.Name, req.SheetOrPath, req.ColumnExpressions);
                var result = await svc.UpdateSourceAsync(sourceId, input, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        g.MapDelete("/sources/{sourceId:guid}", async (Guid sourceId, IDataSetService svc, CancellationToken ct) =>
        {
            try { return await svc.DeleteSourceAsync(sourceId, ct) ? Results.NoContent() : Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.Conflict(ex.Message); }
        });

        // Копия источника — доступна для источников любого формата (не только с ручным builder'ом).
        g.MapPost("/sources/{sourceId:guid}/duplicate", async (Guid sourceId, IDataSetService svc, CancellationToken ct) =>
        {
            var result = await svc.DuplicateSourceAsync(sourceId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        // PDF — Extraction через распознавание (vision-LLM), не XPath/JSONPath-builder.
        g.MapPost("/files/{fileId:guid}/pdf-sources", async (
            Guid fileId, PdfSourceRequest req, IDataSetService svc, CancellationToken ct) =>
        {
            try { return Results.Ok(await svc.CreatePdfSourceAsync(fileId, new CreatePdfSourceInput(req.Name, req.Tags), ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        });

        g.MapPost("/sources/{sourceId:guid}/recognize", async (Guid sourceId, IDataSetService svc, CancellationToken ct) =>
        {
            try
            {
                var result = await svc.RecognizePdfSourceAsync(sourceId, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Обработка (Filter/Transformation/Sort) — лёгкая правка, не трогает файл/кэш схемы.
        g.MapPut("/sources/{sourceId:guid}/processing", async (
            Guid sourceId, ProcessingRequest req, IDataSetService svc, CancellationToken ct) =>
        {
            try
            {
                var input = new SetSourceProcessingInput(req.RowFilter, req.ComputedColumns, req.SortSpec);
                var result = await svc.SetSourceProcessingAsync(sourceId, input, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Применить шаблон (Extraction, если задана в шаблоне, + Filter/Transformation/Sort) —
        // copy-on-apply, единожды; Extraction триггерит пере-парсинг файла.
        g.MapPost("/sources/{sourceId:guid}/apply-template/{templateId:guid}", async (
            Guid sourceId, Guid templateId, IDataSetService svc, CancellationToken ct) =>
        {
            try
            {
                var result = await svc.ApplyProcessingTemplateAsync(sourceId, templateId, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        });

        // ── Шаблоны обработки (переиспользуемые рецепты Extraction + Filter/Transformation/Sort) ─────────

        g.MapGet("/processing-templates", async (IDataSetService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListProcessingTemplatesAsync(ct)));

        g.MapPost("/processing-templates", async (ProcessingTemplateRequest req, IDataSetService svc, CancellationToken ct) =>
        {
            var input = new CreateProcessingTemplateInput(
                req.Name, req.SheetOrPath, req.ColumnExpressions, req.RowFilter, req.ComputedColumns, req.SortSpec);
            return Results.Ok(await svc.CreateProcessingTemplateAsync(input, ct));
        });

        g.MapPut("/processing-templates/{id:guid}", async (
            Guid id, ProcessingTemplateRequest req, IDataSetService svc, CancellationToken ct) =>
        {
            var input = new UpdateProcessingTemplateInput(
                req.Name, req.SheetOrPath, req.ColumnExpressions, req.RowFilter, req.ComputedColumns, req.SortSpec);
            var result = await svc.UpdateProcessingTemplateAsync(id, input, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        g.MapDelete("/processing-templates/{id:guid}", async (Guid id, IDataSetService svc, CancellationToken ct) =>
            await svc.DeleteProcessingTemplateAsync(id, ct) ? Results.NoContent() : Results.NotFound());
    }

    private static async Task<byte[]> ReadBytesAsync(IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private record AutoMapRequest(AutoMapFieldDto[] Fields);
    private record AutoMapFieldDto(string Key, string Title);
    private record SourceRequest(string Name, string SheetOrPath, ColumnExprDto[]? ColumnExpressions);
    private record ProcessingRequest(object? RowFilter, object? ComputedColumns, object? SortSpec);
    private record ProcessingTemplateRequest(
        string Name, string? SheetOrPath, ColumnExprDto[]? ColumnExpressions,
        object? RowFilter, object? ComputedColumns, object? SortSpec);
    private record ExpressionPreviewRequest(string RowSelector, string? Expr);
    private record PdfSourceRequest(string Name, string[]? Tags);
}
