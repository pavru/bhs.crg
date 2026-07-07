using System.Security.Claims;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Jobs;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Jobs;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Documents;

public static class DocumentSetEndpoints
{
    public static void MapDocumentSetEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Constructions ──────────────────────────────────────────────────────
        var c = app.MapGroup("/api/constructions").RequireAuthorization();

        c.MapGet("/", async (IMediator m, ClaimsPrincipal user) =>
        {
            var userId = GetUserId(user);
            return Results.Ok(await m.Send(new ListConstructionsQuery(userId)));
        });

        c.MapGet("/{id:guid}", async (Guid id, IMediator m) =>
        {
            var construction = await m.Send(new GetConstructionQuery(id));
            return construction is null ? Results.NotFound() : Results.Ok(construction);
        });

        c.MapPost("/", async (CreateConstructionRequest req, IMediator m, ClaimsPrincipal user) =>
        {
            var userId = GetUserId(user);
            return Results.Ok(await m.Send(new CreateConstructionCommand(req.Name, userId)));
        });

        c.MapPut("/{id:guid}", async (Guid id, RenameRequest req, IMediator m)
            => Results.Ok(await m.Send(new RenameConstructionCommand(id, req.Name))));

        c.MapDelete("/{id:guid}", async (Guid id, IMediator m) =>
        {
            await m.Send(new DeleteConstructionCommand(id));
            return Results.NoContent();
        });

        // ── Sections ───────────────────────────────────────────────────────────
        c.MapPost("/{constructionId:guid}/sections", async (Guid constructionId, CreateSectionRequest req, IMediator m)
            => Results.Ok(await m.Send(new CreateSectionCommand(constructionId, req.Name))));

        var s = app.MapGroup("/api/sections").RequireAuthorization();

        s.MapPut("/{id:guid}", async (Guid id, RenameRequest req, IMediator m)
            => Results.Ok(await m.Send(new RenameSectionCommand(id, req.Name))));

        s.MapDelete("/{id:guid}", async (Guid id, IMediator m) =>
        {
            await m.Send(new DeleteSectionCommand(id));
            return Results.NoContent();
        });

        // ── DocumentSets ───────────────────────────────────────────────────────
        s.MapPost("/{sectionId:guid}/sets", async (Guid sectionId, CreateSetRequest req, IMediator m)
            => Results.Ok(await m.Send(new CreateDocumentSetCommand(sectionId, req.Name))));

        var g = app.MapGroup("/api/document-sets").RequireAuthorization();

        // Поиск документов по всем комплектам (имя документа/типа + текст реквизитов). ?q= обязателен,
        // ?constructionId= — необязательный фильтр по стройке.
        g.MapGet("/search", async (string? q, Guid? constructionId, IDocumentSearch search, CancellationToken ct)
            => Results.Ok(await search.SearchAsync(q ?? "", constructionId, ct)));

        g.MapGet("/{id:guid}", async (Guid id, IMediator m) =>
        {
            var set = await m.Send(new GetDocumentSetQuery(id));
            return set is null ? Results.NotFound() : Results.Ok(set);
        });

        g.MapPut("/{id:guid}", async (Guid id, RenameRequest req, IMediator m)
            => Results.Ok(await m.Send(new RenameDocumentSetCommand(id, req.Name))));

        g.MapDelete("/{id:guid}", async (Guid id, IMediator m) =>
        {
            await m.Send(new DeleteDocumentSetCommand(id));
            return Results.NoContent();
        });

        // ── DocumentInstances ──────────────────────────────────────────────────
        g.MapGet("/{id:guid}/available-instances", async (Guid id, IMediator m)
            => Results.Ok(await m.Send(new ListAvailableInstancesQuery(id))));

        g.MapPost("/{setId:guid}/documents", async (Guid setId, AddDocumentRequest req, IMediator m)
            => Results.Ok(await m.Send(new AddDocumentToSetCommand(setId, req.DocumentTypeId))));

        g.MapGet("/{setId:guid}/documents/{id:guid}", async (Guid id, IMediator m) =>
        {
            var inst = await m.Send(new GetDocumentInstanceQuery(id));
            return inst is null ? Results.NotFound() : Results.Ok(inst);
        });

        g.MapPut("/{setId:guid}/documents/{id:guid}/name",
            async (Guid id, RenameDocumentInstanceRequest req, IMediator m)
                => Results.Ok(await m.Send(new RenameDocumentInstanceCommand(id, req.Name))));

        g.MapPut("/{setId:guid}/documents/{id:guid}/requisites",
            async (Guid id, JsonElement body, IMediator m)
                => Results.Ok(await m.Send(new UpdateRequisitesCommand(
                    id, JsonDocument.Parse(body.GetRawText())))));

        g.MapPut("/{setId:guid}/documents/{id:guid}/plugin-data",
            async (Guid id, JsonElement body, IMediator m)
                => Results.Ok(await m.Send(new UpdatePluginDataCommand(
                    id, JsonDocument.Parse(body.GetRawText())))));

        g.MapPut("/{setId:guid}/documents/{id:guid}/template",
            async (Guid id, SetTemplateRequest req, IMediator m)
                => Results.Ok(await m.Send(new SetDocumentTemplateCommand(id, req.TemplateId))));

        // Набор выбранных шаблонов для мульти-генерации (JSON-массив Guid в теле или null).
        g.MapPut("/{setId:guid}/documents/{id:guid}/templates",
            async (Guid id, JsonElement body, IMediator m)
                => Results.Ok(await m.Send(new SetDocumentTemplatesCommand(
                    id, body.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : body.GetRawText()))));

        // Переопределения значений параметров шаблона на документе (JSON-объект {имя:значение} или null).
        g.MapPut("/{setId:guid}/documents/{id:guid}/template-params",
            async (Guid id, JsonElement body, IMediator m)
                => Results.Ok(await m.Send(new SetDocumentTemplateParamsCommand(
                    id, body.ValueKind == JsonValueKind.Null ? null : body.GetRawText()))));

        g.MapDelete("/{setId:guid}/documents/{id:guid}", async (Guid id, IMediator m) =>
        {
            await m.Send(new DeleteDocumentInstanceCommand(id));
            return Results.NoContent();
        });

        // ── Сборка комплекта ───────────────────────────────────────────────────
        // Порядок документов в собранном файле (тела — массив id в нужном порядке).
        g.MapPut("/{setId:guid}/documents/order", async (Guid setId, Guid[] orderedIds, IMediator m)
            => Results.Ok(await m.Send(new ReorderDocumentInstancesCommand(setId, orderedIds))));

        // Запуск сборки всего комплекта (или подмножества) в один PDF — фоновая задача (генерация
        // недостающих + склейка могут занять десятки секунд). 202 + jobId, прогресс в индикаторе.
        g.MapPost("/{setId:guid}/assemble", async (
            Guid setId, AssembleSetRequest? req, IMediator m, IJobService jobs, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var set = await m.Send(new GetDocumentSetQuery(setId), ct);
            if (set is null) return Results.NotFound();
            var payload = req?.InstanceIds is { Length: > 0 } ids
                ? JsonSerializer.Serialize(new { instanceIds = ids })
                : null;
            var jobId = await jobs.EnqueueAsync(JobKind.AssembleDocumentSet, GetUserId(user), setId,
                $"Сборка комплекта «{set.Name}»", payload, ct);
            return Results.Accepted("/api/jobs/active", new { jobId });
        });

        // Отправка собранного комплекта подписчикам (с учётом наследования) — фоновая задача.
        g.MapPost("/{setId:guid}/email-to-subscribers", async (
            Guid setId, EmailToSubscribersRequest? req, IMediator m, IJobService jobs, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var set = await m.Send(new GetDocumentSetQuery(setId), ct);
            if (set is null) return Results.NotFound();
            var payload = JsonSerializer.Serialize(new { subject = req?.Subject, body = req?.Body });
            var jobId = await jobs.EnqueueAsync(JobKind.SendEmail, GetUserId(user), setId,
                $"Отправка комплекта «{set.Name}» подписчикам", payload, ct);
            return Results.Accepted("/api/jobs/active", new { jobId });
        }).RequireAuthorization("Admin");

        // Метаданные собранного комплекта (для показа кнопки скачивания) — 404, если ещё не собран.
        g.MapGet("/{setId:guid}/output", async (Guid setId, IRepository<DocumentSetOutput> outputRepo, CancellationToken ct) =>
        {
            var output = (await outputRepo.FindAsync(o => o.SetId == setId, ct)).FirstOrDefault();
            return output is null
                ? Results.NotFound()
                : Results.Ok(new { output.GeneratedAt, format = output.Format.ToString() });
        });

        // Скачивание собранного комплекта.
        g.MapGet("/{setId:guid}/output/download", async (
            Guid setId, IMediator m, IRepository<DocumentSetOutput> outputRepo, IBlobStorage blob, CancellationToken ct) =>
        {
            var output = (await outputRepo.FindAsync(o => o.SetId == setId, ct)).FirstOrDefault();
            if (output is null) return Results.NotFound();
            var set = await m.Send(new GetDocumentSetQuery(setId), ct);
            var stream = await blob.DownloadAsync(output.BlobPath, ct);
            return Results.File(stream, "application/pdf", $"{set?.Name ?? "Комплект"}.pdf");
        });
    }

    static Guid GetUserId(ClaimsPrincipal user)
        => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub")!);

    record CreateConstructionRequest(string Name);
    record CreateSectionRequest(string Name);
    record CreateSetRequest(string Name);
    record RenameRequest(string Name);
    record AddDocumentRequest(Guid DocumentTypeId);
    record RenameDocumentInstanceRequest(string? Name);
    record SetTemplateRequest(Guid? TemplateId);
    record AssembleSetRequest(Guid[]? InstanceIds);
    record EmailToSubscribersRequest(string? Subject, string? Body);
}
