using System.Text.Json;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Recognition;
using MediatR;

namespace BHS.CRG.Api.Endpoints.QualityDocs;

public static class QualityDocEndpoints
{
    public static void MapQualityDocEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/quality-docs").RequireAuthorization();

        // ── Библиотека ──────────────────────────────────────────────────────────
        g.MapGet("/", async (string? scope, Guid? scopeId, string? search, IMediator m) =>
        {
            CatalogScope? s = scope is not null && Enum.TryParse<CatalogScope>(scope, true, out var v) ? v : null;
            var items = await m.Send(new ListQualityDocumentsQuery(s, scopeId, search));
            return Results.Ok(items.Select(ToDto));
        });

        g.MapGet("/{id:guid}", async (Guid id, IMediator m) =>
        {
            var doc = await m.Send(new GetQualityDocumentQuery(id));
            return doc is null ? Results.NotFound() : Results.Ok(ToDto(doc));
        });

        g.MapPost("/", async (CreateReq req, IMediator m) =>
        {
            var doc = await m.Send(new CreateQualityDocumentCommand(
                req.DocumentTypeId, req.DisplayName, ToDoc(req.Requisites),
                ParseScope(req.Scope), req.ScopeId, ParseSource(req.Source),
                req.ScanBlobPath, req.ScanFileName, req.ScanMimeType));
            return Results.Ok(ToDto(doc));
        });

        g.MapPut("/{id:guid}", async (Guid id, UpdateReq req, IMediator m)
            => Results.Ok(ToDto(await m.Send(new UpdateQualityDocumentCommand(id, req.DocumentTypeId, req.DisplayName, ToDoc(req.Requisites))))));

        g.MapPut("/{id:guid}/scan", async (Guid id, ScanReq req, IMediator m)
            => Results.Ok(ToDto(await m.Send(new SetQualityDocScanCommand(id, req.ScanBlobPath, req.ScanFileName, req.ScanMimeType)))));

        g.MapDelete("/{id:guid}", async (Guid id, IMediator m) =>
        {
            await m.Send(new DeleteQualityDocumentCommand(id));
            return Results.NoContent();
        });

        // ── Распознавание скана (vision-LLM) ─────────────────────────────────────
        // PromptKind — необязательный выбор промпта: "titleblock" — под штамп чертежа/документа
        // по ГОСТ Р 21.101-2020 (см. PDF-наборы данных), иначе — общий (сертификат/декларация).
        g.MapPost("/recognize", async (RecognizeReq req, IMediator m, System.Security.Claims.ClaimsPrincipal user) =>
        {
            var fields = (req.Fields ?? []).Select(f => new RecognitionField(f.Path, f.Title, f.Type, f.Options)).ToList();
            var uidStr = user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value;
            Guid? userId = Guid.TryParse(uidStr, out var uid) ? uid : null;
            Func<IReadOnlyList<RecognitionField>, string>? promptBuilder = req.PromptKind switch
            {
                "titleblock" => RecognitionShared.BuildTitleBlockPrompt,
                _ => null,
            };
            try
            {
                var res = await m.Send(new RecognizeDocumentCommand(
                    req.BlobPath, req.MimeType, fields, userId, Notify: !(req.Silent ?? false), promptBuilder));
                return Results.Ok(new { values = res.Values, pageCount = res.PageCount });
            }
            catch (RecognitionLimitException ex)
            {
                return Results.Json(new { error = ex.Message, limit = true, retryAfter = ex.RetryAfterSeconds }, statusCode: 429);
            }
            catch (RecognitionUnavailableException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 503);
            }
        });

        // ── Связи материал → документ ───────────────────────────────────────────
        g.MapGet("/links", async (string scope, Guid? scopeId, IMediator m) =>
        {
            var links = await m.Send(new ListMaterialLinksQuery(ParseScope(scope), scopeId));
            return Results.Ok(links.Select(l => new { l.Id, Scope = l.Scope.ToString(), l.ScopeId, l.MaterialKey, l.QualityDocumentId }));
        });

        g.MapPost("/links", async (SetLinksReq req, IMediator m) =>
        {
            var n = await m.Send(new SetMaterialLinksCommand(ParseScope(req.Scope), req.ScopeId, req.MaterialKeys, req.QualityDocumentId));
            return Results.Ok(new { linked = n });
        });

        g.MapDelete("/links/{id:guid}", async (Guid id, IMediator m) =>
        {
            await m.Send(new RemoveMaterialLinkCommand(id));
            return Results.NoContent();
        });

        // ── Веб-поиск документов (ФГИС → производитель → веб) ─────────────────────
        g.MapPost("/search", async (SearchReq req, IMediator m) =>
        {
            try { return Results.Ok(await m.Send(new SearchQualityDocsQuery(req.Query))); }
            catch (SearchUnavailableException ex) { return Results.Json(new { error = ex.Message }, statusCode: 503); }
        });

        g.MapPost("/import-url", async (ImportUrlReq req, IMediator m) =>
        {
            try
            {
                var doc = await m.Send(new ImportQualityDocFromUrlCommand(req.Url, req.Title, req.DocumentTypeId, ParseScope(req.Scope), req.ScopeId));
                return Results.Ok(ToDto(doc));
            }
            catch (SearchUnavailableException ex) { return Results.Json(new { error = ex.Message }, statusCode: 503); }
        });

        // ── Предложение связей по сходству наименований ──────────────────────────
        g.MapPost("/suggest", async (SuggestReq req, IMediator m) =>
        {
            var mats = (req.Materials ?? []).Select(x => new SuggestMaterial(x.Key, x.Name)).ToList();
            var s = await m.Send(new SuggestLinksQuery(req.SetId, mats));
            return Results.Ok(s);
        });
    }

    private static CatalogScope ParseScope(string? s)
        => s is not null && Enum.TryParse<CatalogScope>(s, true, out var v) ? v : CatalogScope.System;

    private static QualityDocSource ParseSource(string? s)
        => s is not null && Enum.TryParse<QualityDocSource>(s, true, out var v) ? v : QualityDocSource.Manual;

    private static JsonDocument ToDoc(JsonElement el) => JsonDocument.Parse(el.GetRawText());

    private static object ToDto(QualityDocument d) => new
    {
        d.Id,
        d.DocumentTypeId,
        d.DisplayName,
        Requisites = d.Requisites.RootElement,
        d.ScanBlobPath,
        d.ScanFileName,
        d.ScanMimeType,
        Source = d.Source.ToString(),
        Scope = d.Scope.ToString(),
        d.ScopeId,
        d.CreatedAt,
        d.UpdatedAt,
    };

    private record CreateReq(Guid DocumentTypeId, string DisplayName, JsonElement Requisites,
        string Scope, Guid? ScopeId, string? Source, string? ScanBlobPath, string? ScanFileName, string? ScanMimeType);
    private record UpdateReq(Guid DocumentTypeId, string DisplayName, JsonElement Requisites);
    private record ScanReq(string? ScanBlobPath, string? ScanFileName, string? ScanMimeType);
    private record SetLinksReq(string Scope, Guid? ScopeId, string[] MaterialKeys, Guid QualityDocumentId);
    private record RecognizeReq(string BlobPath, string MimeType, RecognizeFieldReq[]? Fields, bool? Silent, string? PromptKind);
    private record RecognizeFieldReq(string Path, string Title, string Type, string[]? Options);
    private record SuggestReq(Guid SetId, SuggestMaterialReq[]? Materials);
    private record SuggestMaterialReq(string Key, string Name);
    private record SearchReq(string Query);
    private record ImportUrlReq(string Url, string Title, Guid DocumentTypeId, string Scope, Guid? ScopeId);
}
