using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Documents;

public static class DocumentTypeEndpoints
{
    public static void MapDocumentTypeEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/document-types").RequireAuthorization();
        var admin = app.MapGroup("/api/document-types").RequireAuthorization("Admin");

        g.MapGet("/", async (string? kind, IMediator m) =>
        {
            DocumentTypeKind? filter = kind switch
            {
                "Document"  => DocumentTypeKind.Document,
                "Composite" => DocumentTypeKind.Composite,
                _           => null,
            };
            return Results.Ok(await m.Send(new ListDocumentTypesQuery(filter)));
        });

        g.MapGet("/{id:guid}", async (Guid id, IMediator m) =>
        {
            var dt = await m.Send(new GetDocumentTypeQuery(id));
            return dt is null ? Results.NotFound() : Results.Ok(dt);
        });

        admin.MapPost("/", async (CreateTypeRequest req, IMediator m) =>
        {
            var kind = req.Kind switch
            {
                "Composite" => DocumentTypeKind.Composite,
                _           => DocumentTypeKind.Document,
            };
            try
            {
                return Results.Ok(await m.Send(new CreateDocumentTypeCommand(
                    req.Name, req.Code, kind, req.ParentId, JsonDocument.Parse(req.Schema), req.IsAbstract)));
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        admin.MapPut("/{id:guid}", async (Guid id, UpdateTypeRequest req, IMediator m) =>
        {
            try { return Results.Ok(await m.Send(new UpdateDocumentTypeCommand(id, req.Name, req.Code, req.ParentId))); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        admin.MapPut("/{id:guid}/schema", async (Guid id, UpdateSchemaRequest req, IMediator m) =>
        {
            try { return Results.Ok(await m.Send(new UpdateDocumentTypeSchemaCommand(id, JsonDocument.Parse(req.Schema)))); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        // Проверка сборки Typst-блоков (issue #309, фаза 2): глобально по всем типам, с draft-overlay
        // редактируемого типа (тело = его несохранённый массив typstRenders). Ловит циклы/дубликаты
        // (граф) + синтаксис (Typst CLI). Не мутирует — только диагностика.
        admin.MapPost("/{id:guid}/validate-typst-blocks", async (Guid id, JsonElement? draftRenders, IMediator m)
            => Results.Ok(await m.Send(new ValidateTypstBlocksQuery(id, draftRenders))));

        admin.MapPut("/{id:guid}/abstract", async (Guid id, SetAbstractRequest req, IMediator m)
            => Results.Ok(await m.Send(new SetDocumentTypeAbstractCommand(id, req.IsAbstract))));

        admin.MapPut("/{id:guid}/allows-proxy", async (Guid id, SetAllowsProxyRequest req, IMediator m)
            => Results.Ok(await m.Send(new SetDocumentTypeAllowsProxyCommand(id, req.AllowsProxy))));

        admin.MapPut("/{id:guid}/group", async (Guid id, SetGroupRequest req, IMediator m)
            => Results.Ok(await m.Send(new SetDocumentTypeGroupCommand(id, req.Group))));

        // Аудит типа (issue #348): расхождения данных существующих инстансов с текущей схемой (read-only).
        admin.MapGet("/{id:guid}/audit", async (Guid id, IMediator m) =>
        {
            try { return Results.Ok(await m.Send(new AuditDocumentTypeQuery(id))); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        // Применение исправлений аудита (issue #350) — мутирует реквизиты инстансов, атомарно.
        admin.MapPost("/{id:guid}/audit/apply", async (Guid id, ApplyAuditFixesRequest req, IMediator m)
            => Results.Ok(await m.Send(new ApplyAuditFixesCommand(req.Fixes))));

        // Миграция ключа поля в данных инстансов при переименовании ключа в схеме (issue #357).
        admin.MapPost("/{id:guid}/migrate-field-key", async (Guid id, MigrateFieldKeyRequest req, IMediator m)
            => Results.Ok(new { migrated = await m.Send(new MigrateFieldKeyCommand(id, req.OldKey, req.NewKey)) }));

        // Использование типа (issue #275) — проактивный показ «чем занят тип» до попытки удаления.
        admin.MapGet("/{id:guid}/usage", async (Guid id, IMediator m) =>
        {
            try { return Results.Ok(await m.Send(new GetDocumentTypeUsageQuery(id))); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        admin.MapDelete("/{id:guid}", async (Guid id, IMediator m) =>
        {
            try { await m.Send(new DeleteDocumentTypeCommand(id)); return Results.NoContent(); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
    }

    record CreateTypeRequest(string Name, string Code, string Kind, Guid? ParentId, string Schema, bool IsAbstract = false);
    record UpdateTypeRequest(string Name, string Code, Guid? ParentId);
    record UpdateSchemaRequest(string Schema);
    record SetAbstractRequest(bool IsAbstract);
    record SetAllowsProxyRequest(bool AllowsProxy);
    record SetGroupRequest(string? Group);
    record ApplyAuditFixesRequest(IReadOnlyList<AuditFix> Fixes);
    record MigrateFieldKeyRequest(string OldKey, string NewKey);
}
