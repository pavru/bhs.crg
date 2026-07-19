using System.Security.Claims;
using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Jobs;
using BHS.CRG.Domain.Jobs;

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

        // Кандидаты на источник в сыром файле (листы/массивы/«весь файл») — без персиста, для
        // подсказок в диалоге создания источника. Пусто для XML (строится вручную builder'ом).
        g.MapGet("/files/{fileId:guid}/source-candidates", async (Guid fileId, IDataSetService svc, CancellationToken ct) =>
        {
            var candidates = await svc.DetectSourceCandidatesAsync(fileId, ct);
            return Results.Ok(candidates.Select(c => new
            {
                name = c.Name,
                sheetOrPath = c.SheetOrPath,
                columns = c.Columns.Select(col => col.Name).ToList(),
                rowCount = c.RowCount,
            }));
        });

        g.MapGet("/sources/{sourceId:guid}/preview", async (
            Guid sourceId, int maxRows, IDataSetService svc, CancellationToken ct) =>
        {
            var preview = await svc.PreviewSourceAsync(sourceId, maxRows, ct);
            return preview is null ? Results.NotFound() : Results.Ok(preview);
        });

        // Выгрузка ВСЕХ строк источника (после обработки) в CSV/XLS/XLSX. format=xlsx по умолчанию.
        g.MapGet("/sources/{sourceId:guid}/export", async (
            Guid sourceId, string? format, IDataSetService svc, CancellationToken ct) =>
        {
            var result = await svc.ExportSourceAsync(sourceId, format, ct);
            return result is null ? Results.NotFound() : Results.File(result.Content, result.ContentType, result.FileName);
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

        // Лёгкое переименование источника (issue #43) — только имя, без extraction/кэша; для любого
        // источника, включая PDF-проекции (у них полный PUT /sources/{id} неприменим).
        g.MapPut("/sources/{sourceId:guid}/name", async (
            Guid sourceId, RenameSourceRequest req, IDataSetService svc, CancellationToken ct) =>
        {
            try
            {
                var result = await svc.RenameSourceAsync(sourceId, req.Name, ct);
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

        // Материализация источника в тип (issue #19): typeId + маппинг колонок → поля типа. typeId=null снимает.
        g.MapPut("/sources/{sourceId:guid}/materialization", async (
            Guid sourceId, MaterializationRequest req, IDataSetService svc, CancellationToken ct) =>
        {
            var result = await svc.SetMaterializationAsync(sourceId, req.TypeId, req.Mapping, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        // Предпросмотр материализации: строки источника → объекты формы типа (без резолва каталога).
        // Live-превью материализации (issue #294): принимает текущие (несохранённые) typeId+mapping из диалога.
        g.MapPost("/sources/{sourceId:guid}/materialization/preview", async (
            Guid sourceId, MaterializePreviewRequest req, IDataSetService svc, CancellationToken ct) =>
        {
            var result = await svc.MaterializePreviewAsync(sourceId, req.MaxRows ?? 50, req.TypeId, req.Mapping, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        // PDF — Extraction через распознавание (vision-LLM), не XPath/JSONPath-builder.
        // Выбор профиля препроцессинга PDF-набора. ГОСТ (issue #38): ставит профиль на набор,
        // источников не создаёт (null → 204); «Счёт»: создаёт пару источников (200 + шапка).
        g.MapPost("/files/{fileId:guid}/pdf-sources", async (
            Guid fileId, PdfSourceRequest req, IDataSetService svc, CancellationToken ct) =>
        {
            try
            {
                var result = await svc.CreatePdfSourceAsync(fileId, new CreatePdfSourceInput(req.Name, req.Tags, req.Profile), ct);
                return result is null ? Results.NoContent() : Results.Ok(result);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        });

        // Распознавание PDF-набора (issue #38/#44, набор-centric) — по fileId, для ВСЕХ профилей (unifies
        // VERB вызова: ГОСТ и «Счёт» теперь оба входят через fileId, не только ГОСТ). confirm=true
        // подтверждает перезапись ручной правки разбиения (409 без него, только ГОСТ). Долгая операция
        // (ГОСТ, минуты) → фоновая задача, 202+jobId; короткая (Счёт, секунды) → синхронно, 200.
        g.MapPost("/files/{fileId:guid}/recognize", async (Guid fileId, bool? confirm, IDataSetService svc, IJobService jobs, ClaimsPrincipal user, CancellationToken ct) =>
        {
            try
            {
                if (await jobs.HasActiveForTargetAsync(UserId(user), fileId, ct))
                    return Results.Conflict(new { error = "По этому набору уже идёт распознавание." });
                var plan = await svc.PlanFileRecognitionAsync(fileId, confirm ?? false, ct);
                if (plan is null) return Results.NotFound();
                if (plan.Background)
                {
                    var jobId = await jobs.EnqueueAsync(JobKind.RecognizeGostSet, UserId(user), fileId, plan.Title, null, ct);
                    return Results.Accepted($"/api/jobs/active", new { jobId });
                }
                await svc.RecognizeFileAsync(fileId, confirm ?? false, ct);
                return Results.Ok();
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        // confirm=true — подтверждение перезаписи ручной корректировки разбиения (см.
        // ApplyGroupingAsync); без него, если источник уже правился вручную, — 409 Conflict.
        g.MapPost("/sources/{sourceId:guid}/recognize", async (Guid sourceId, bool? confirm, IDataSetService svc, IJobService jobs, ClaimsPrincipal user, CancellationToken ct) =>
        {
            try
            {
                // Защита от повторного запуска, пока по этому источнику уже идёт распознавание.
                if (await jobs.HasActiveForTargetAsync(UserId(user), sourceId, ct))
                    return Results.Conflict(new { error = "По этому источнику уже идёт распознавание." });
                // Пред-валидация синхронно (формат, 409 ручной правки). GOST-набор (минуты) → фоновая
                // задача, 202+jobId сразу (реквест не держится). Счёт/legacy (секунды) → синхронно.
                var plan = await svc.PlanRecognitionAsync(sourceId, confirm ?? false, ct);
                if (plan is null) return Results.NotFound();
                if (plan.Background)
                {
                    var jobId = await jobs.EnqueueAsync(JobKind.RecognizeGostSet, UserId(user), sourceId, plan.Title, null, ct);
                    return Results.Accepted($"/api/jobs/active", new { jobId });
                }
                var result = await svc.RecognizePdfSourceAsync(sourceId, confirm ?? false, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        // ── Редактор разбиения PDF — на уровне НАБОРА (issue #38, fileId) ─────

        g.MapGet("/files/{fileId:guid}/pages", async (Guid fileId, IDataSetService svc, CancellationToken ct) =>
        {
            try
            {
                var result = await svc.GetPagesAsync(fileId, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        g.MapGet("/files/{fileId:guid}/pages/{pageIndex:int}/thumbnail", async (Guid fileId, int pageIndex, int? dpi, IDataSetService svc, CancellationToken ct) =>
        {
            try
            {
                // dpi: 96 (миниатюра по умолчанию) … 200 (крупный просмотр листа глазами). Ограничиваем, чтобы
                // растр не разрастался до неподъёмного размера на больших форматах (А1/А0).
                var effectiveDpi = Math.Clamp(dpi ?? 96, 96, 200);
                var png = await svc.GetPageThumbnailAsync(fileId, pageIndex, ct, effectiveDpi);
                return png is null ? Results.NotFound() : Results.File(png, "image/png");
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        g.MapPut("/files/{fileId:guid}/grouping", async (Guid fileId, ApplyGroupingRequest req, IDataSetService svc, CancellationToken ct) =>
        {
            try
            {
                var input = new ApplyGroupingInput(req.Groups.Select(g => new GostGroupingGroupDto(g.Kind, g.Code, g.Name, g.PageIndices, g.Tags)).ToList());
                var result = await svc.ApplyGroupingAsync(fileId, input, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Лёгкая установка тэгов документа (тип таблицы) — без пересборки разбиения.
        g.MapPut("/files/{fileId:guid}/document-tags", async (
            Guid fileId, SetDocumentTagsRequest req, IDataSetService svc, CancellationToken ct) =>
        {
            try
            {
                var result = await svc.SetDocumentTagsAsync(fileId, req.FirstPageIndex, req.Tags ?? [], ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Распознать таблицу помеченного документа (спецификация/кабельный журнал) → отдельный табличный
        // источник. Vision-вызов (минуты на большом документе) → фоновая задача, 202+jobId сразу.
        g.MapPost("/files/{fileId:guid}/recognize-table", async (
            Guid fileId, RecognizeTableRequest req, IJobService jobs, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (await jobs.HasActiveForTargetAsync(UserId(user), fileId, ct))
                return Results.Conflict(new { error = "По этому набору уже идёт распознавание." });
            var payload = JsonSerializer.Serialize(new { firstPageIndex = req.FirstPageIndex });
            var jobId = await jobs.EnqueueAsync(JobKind.RecognizeTable, UserId(user), fileId, "Распознавание таблицы", payload, ct);
            return Results.Accepted($"/api/jobs/active", new { jobId });
        });

        // Точечное перераспознавание ОДНОГО документа набора (не всего альбома, P6) → фоновая задача.
        g.MapPost("/files/{fileId:guid}/recognize-document", async (
            Guid fileId, RecognizeTableRequest req, IJobService jobs, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (await jobs.HasActiveForTargetAsync(UserId(user), fileId, ct))
                return Results.Conflict(new { error = "По этому набору уже идёт распознавание." });
            var payload = JsonSerializer.Serialize(new { firstPageIndex = req.FirstPageIndex });
            var jobId = await jobs.EnqueueAsync(JobKind.RecognizeDocument, UserId(user), fileId, "Перераспознавание документа", payload, ct);
            return Results.Accepted($"/api/jobs/active", new { jobId });
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
    private record RenameSourceRequest(string Name);
    private record MaterializationRequest(Guid? TypeId, Dictionary<string, string>? Mapping);
    private record MaterializePreviewRequest(Guid? TypeId, Dictionary<string, string>? Mapping, int? MaxRows);
    private record ProcessingRequest(object? RowFilter, object? ComputedColumns, object? SortSpec);
    private record ProcessingTemplateRequest(
        string Name, string? SheetOrPath, ColumnExprDto[]? ColumnExpressions,
        object? RowFilter, object? ComputedColumns, object? SortSpec);
    private record ExpressionPreviewRequest(string RowSelector, string? Expr);
    private record PdfSourceRequest(string Name, string[]? Tags, string? Profile);
    private record ApplyGroupingRequest(ApplyGroupingGroupRequest[] Groups);
    private record ApplyGroupingGroupRequest(GostGroupKind Kind, string? Code, string? Name, int[] PageIndices, string[]? Tags);
    private record SetDocumentTagsRequest(int FirstPageIndex, string[]? Tags);
    private record RecognizeTableRequest(int FirstPageIndex);

    private static Guid UserId(ClaimsPrincipal user)
        => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub")!);
}
