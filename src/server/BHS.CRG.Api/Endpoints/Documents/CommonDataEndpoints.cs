using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Documents;

public static class CommonDataEndpoints
{
    public static void MapCommonDataEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/common-data").RequireAuthorization();

        // List — optional filters: scope, scopeId, typeId
        g.MapGet("/", async (string? scope, Guid? scopeId, Guid? typeId, IMediator m) =>
        {
            CatalogScope? parsedScope = scope switch
            {
                "Set"          => CatalogScope.Set,
                "Section"      => CatalogScope.Section,
                "Construction" => CatalogScope.Construction,
                "System"       => CatalogScope.System,
                _              => null,
            };
            return Results.Ok((await m.Send(new ListCommonDataEntriesQuery(parsedScope, scopeId, typeId)))
                .Select(CommonDataEntryDto.From));
        });

        // Resolve all relevant entries for a document set (full hierarchy)
        g.MapGet("/for-set/{setId:guid}", async (Guid setId, Guid? typeId, IMediator m) =>
        {
            try
            {
                return Results.Ok(await m.Send(new ResolveCommonDataForSetQuery(setId, typeId)));
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(ex.Message); }
        });

        // Resolve entries visible from ANY scope level, walking the parent chain (issue #82).
        g.MapGet("/for-scope", async (string scope, Guid? scopeId, Guid? typeId, IMediator m) =>
        {
            CatalogScope? parsed = scope switch
            {
                "Set"          => CatalogScope.Set,
                "Section"      => CatalogScope.Section,
                "Construction" => CatalogScope.Construction,
                "System"       => CatalogScope.System,
                _              => null,
            };
            if (parsed is null) return Results.BadRequest($"Unknown scope '{scope}'.");
            return Results.Ok(await m.Send(new ResolveCommonDataForScopeQuery(parsed.Value, scopeId, typeId)));
        });

        g.MapGet("/{id:guid}", async (Guid id, IMediator m) =>
        {
            var all = await m.Send(new ListCommonDataEntriesQuery());
            var entry = all.FirstOrDefault(e => e.Id == id);
            return entry is null ? Results.NotFound() : Results.Ok(CommonDataEntryDto.From(entry));
        });

        // Проверка связок (issue #99): сверка снимка $ref-ссылок со свежим резолвом источника.
        g.MapGet("/{id:guid}/binding-check", async (Guid id, IMediator m) =>
        {
            try { return Results.Ok(await m.Send(new CheckCommonDataBindingsQuery(id))); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        g.MapPost("/", async (CreateRequest req, IMediator m) =>
        {
            var scope = req.Scope switch
            {
                "Section"      => CatalogScope.Section,
                "Construction" => CatalogScope.Construction,
                "System"       => CatalogScope.System,
                _              => CatalogScope.Set,
            };
            return Results.Ok(CommonDataEntryDto.From(await m.Send(new CreateCommonDataEntryCommand(
                req.DisplayName, req.CompositeTypeId,
                JsonDocument.Parse(req.Data), scope, req.ScopeId, req.Aliases))));
        });

        g.MapPut("/{id:guid}", async (Guid id, UpdateRequest req, IMediator m) =>
            Results.Ok(CommonDataEntryDto.From(await m.Send(new UpdateCommonDataEntryCommand(
                id, req.DisplayName, JsonDocument.Parse(req.Data), req.Aliases)))));

        g.MapDelete("/{id:guid}", async (Guid id, IMediator m) =>
        {
            try { await m.Send(new DeleteCommonDataEntryCommand(id)); return Results.NoContent(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
    }

    record CreateRequest(string DisplayName, Guid CompositeTypeId, string Data, string Scope, Guid? ScopeId, string[]? Aliases);
    record UpdateRequest(string DisplayName, string Data, string[]? Aliases);
}
