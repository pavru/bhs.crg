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
                .Select(CommonDataEntryDto.From)
                .Select(Elide));
        });

        // Resolve all relevant entries for a document set (full hierarchy)
        g.MapGet("/for-set/{setId:guid}", async (Guid setId, Guid? typeId, IMediator m) =>
        {
            try
            {
                return Results.Ok((await m.Send(new ResolveCommonDataForSetQuery(setId, typeId))).Select(Elide));
            }
            catch (NotFoundException ex) { return Results.NotFound(ex.Message); }
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
            return Results.Ok((await m.Send(new ResolveCommonDataForScopeQuery(parsed.Value, scopeId, typeId)))
                .Select(Elide));
        });

        // По идентификатору — ПОЛНАЯ запись, без отсечения: этот путь кормит редактор (issue #520).
        g.MapGet("/{id:guid}", async (Guid id, IMediator m) =>
        {
            var entry = await m.Send(new GetCommonDataEntryQuery(id));
            return entry is null ? Results.NotFound() : Results.Ok(CommonDataEntryDto.From(entry));
        });

        // Аудит записи общих данных (issue #644). Тот же AuditInstanceQuery, что и у документа: он
        // работает над DomainObject, а запись общих данных — такой же объект. Форма читает отсюда
        // расхождения значений с типом; до этого их не показывал никто — сканер выпуска записей
        // общих данных не касается вовсе.
        g.MapGet("/{id:guid}/audit", async (Guid id, IMediator m) =>
        {
            try { return Results.Ok(await m.Send(new AuditInstanceQuery(id))); }
            catch (NotFoundException) { return Results.NotFound(); }
        });

        // Парного `audit/apply` здесь НЕТ намеренно: форма записи (issue #644) только показывает
        // расхождения, а чинят их в аудите типа — тем же ApplyAuditFixesCommand, но через уже
        // существующий маршрут. Заводить второй пишущий маршрут, которого никто не зовёт, значит
        // держать непроверенную точку записи по общим данным уровня «Система».

        // Проверка связок (issue #99): сверка снимка $ref-ссылок со свежим резолвом источника.
        g.MapGet("/{id:guid}/binding-check", async (Guid id, IMediator m) =>
        {
            try { return Results.Ok(await m.Send(new CheckCommonDataBindingsQuery(id))); }
            catch (NotFoundException) { return Results.NotFound(); }
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
            catch (NotFoundException) { return Results.NotFound(); }
            catch (ConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
    }

    /// <summary>
    /// Списочный ответ без тяжёлой полезной нагрузки (issue #520). Вызывается ЯВНО в трёх списочных
    /// местах и никогда внутри <see cref="CommonDataEntryDto.From"/>: тот обслуживает и чтение одной
    /// записи, и ответы POST/PUT — отсечение там молча обрезало бы редактор и round-trip записи.
    /// </summary>
    private static CommonDataEntryDto Elide(CommonDataEntryDto dto) =>
        dto with { Data = HeavyLeafElision.WithoutHeavyLeaves(dto.Data) };

    private static CommonDataEntryWithScope Elide(CommonDataEntryWithScope entry) =>
        entry with { Data = HeavyLeafElision.WithoutHeavyLeaves(entry.Data) };

    record CreateRequest(string DisplayName, Guid CompositeTypeId, string Data, string Scope, Guid? ScopeId, string[]? Aliases);
    record UpdateRequest(string DisplayName, string Data, string[]? Aliases);
}
