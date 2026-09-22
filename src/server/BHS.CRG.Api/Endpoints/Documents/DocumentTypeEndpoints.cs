using BHS.CRG.Api.Auth;
using BHS.CRG.Modules;
using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Documents;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Documents;

public static class DocumentTypeEndpoints
{
    public static void MapDocumentTypeEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/document-types").RequireAuthorization(AppPolicies.Permission(CorePermissions.TypesRead));
        var admin = app.MapGroup("/api/document-types").RequireAuthorization(AppPolicies.Permission(CorePermissions.TypesEdit));

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

        admin.MapPost("/", async (CreateTypeRequest req, ModuleRegistry modules, IMediator m) =>
        {
            var kind = req.Kind switch
            {
                "Composite" => DocumentTypeKind.Composite,
                _           => DocumentTypeKind.Document,
            };
            if (!TryOwner(req.Module, modules, out var owner, out var refusal)) return refusal;
            try
            {
                return Results.Ok(await m.Send(new CreateDocumentTypeCommand(
                    req.Name, req.Code, kind, req.ParentId, JsonDocument.Parse(req.Schema),
                    owner, req.IsAbstract)));
            }
            catch (ConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        // Передача типа другому владельцу (ТЗ CORE-30). Владельца существующим типам расставила
        // миграция по явному списку, а список — по смыслу: справочник, заведённый человеком, мог
        // оказаться не у того владельца, и без этого адреса чинить это было бы нечем.
        admin.MapPut("/{id:guid}/module", async (Guid id, SetModuleRequest req, ModuleRegistry modules, IMediator m) =>
        {
            if (!TryOwner(req.Module, modules, out var owner, out var refusal)) return refusal;
            try { return Results.Ok(await m.Send(new SetDocumentTypeOwnerCommand(id, owner))); }
            catch (ConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (NotFoundException) { return Results.NotFound(); }
        });

        admin.MapPut("/{id:guid}", async (Guid id, UpdateTypeRequest req, IMediator m) =>
        {
            try { return Results.Ok(await m.Send(new UpdateDocumentTypeCommand(id, req.Name, req.Code, req.ParentId))); }
            catch (ConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        admin.MapPut("/{id:guid}/schema", async (Guid id, UpdateSchemaRequest req, IMediator m) =>
        {
            try { return Results.Ok(await m.Send(new UpdateDocumentTypeSchemaCommand(id, JsonDocument.Parse(req.Schema)))); }
            catch (ConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        // Последствия правки полей-идентификаторов (issue #584): изменится ли составной ключ и
        // сколько связок «материал → документ качества» это осиротит. Не мутирует — предпросчёт по
        // черновику схемы, тот же, что уйдёт в PUT /schema.
        admin.MapPost("/{id:guid}/identity-impact", async (Guid id, UpdateSchemaRequest req, IMediator m)
            => Results.Ok(await m.Send(new IdentityImpactQuery(id, JsonDocument.Parse(req.Schema)))));

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
            catch (NotFoundException) { return Results.NotFound(); }
        });

        // Применение исправлений аудита (issue #350) — мутирует реквизиты инстансов, атомарно.
        admin.MapPost("/{id:guid}/audit/apply", async (Guid id, ApplyAuditFixesRequest req, IMediator m)
            => Results.Ok(await m.Send(new ApplyAuditFixesCommand(req.Fixes))));

        // Миграция ключа поля в данных инстансов при переименовании ключа в схеме (issue #357).
        admin.MapPost("/{id:guid}/migrate-field-key", async (Guid id, MigrateFieldKeyRequest req, IMediator m) =>
        {
            var r = await m.Send(new MigrateFieldKeyCommand(id, req.OldKey, req.NewKey));
            // «migrated» — прежнее имя поля (число инстансов): клиент читает его с issue #357, и
            // ломать ответ ради стройности незачем. Привязки и шаблоны добавлены рядом (issue #737).
            return Results.Ok(new { migrated = r.Instances, bindings = r.Bindings, templates = r.Templates });
        });

        // Использование типа (issue #275) — проактивный показ «чем занят тип» до попытки удаления.
        admin.MapGet("/{id:guid}/usage", async (Guid id, IMediator m) =>
        {
            try { return Results.Ok(await m.Send(new GetDocumentTypeUsageQuery(id))); }
            catch (NotFoundException) { return Results.NotFound(); }
        });

        admin.MapDelete("/{id:guid}", async (Guid id, IMediator m) =>
        {
            try { await m.Send(new DeleteDocumentTypeCommand(id)); return Results.NoContent(); }
            catch (ConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
    }

    /// <summary>
    /// Владельцем можно назвать ядро или ВКЛЮЧЁННЫЙ модуль. Выключенный отвергается нарочно: тип,
    /// отданный тому, кого на этом экземпляре нет, исчез бы из редактора тем же действием, каким
    /// его отдавали, — и вернуть его было бы нечем, потому что адрес владельца тоже в редакторе.
    ///
    /// ⚠️ Возвращается НЕ пришедшая строка, а объявленный код: сверка идёт без учёта регистра и
    /// краёв, и «ID» или « core » её проходят — а дальше сохранились бы как есть. Дальше их никто
    /// так не сравнивает: и клиент, и правило опоры сверяют коды строго, поэтому тип с владельцем
    /// «ID» пропал бы из редактора при включённом модуле, а правило ядра сочло бы его чужим. Найдено
    /// ревью PR #1002.
    ///
    /// Отказ называет, что можно: список допустимых кодов короткий, и человеку он полезнее, чем
    /// слово «недопустимо».
    /// </summary>
    private static bool TryOwner(string? module, ModuleRegistry modules,
        out string owner, out IResult refusal)
    {
        var allowed = new List<string> { TypeOwner.Core };
        allowed.AddRange(modules.Enabled.Select(m => m.Code));

        var match = string.IsNullOrWhiteSpace(module)
            ? null
            : allowed.FirstOrDefault(c => string.Equals(c, module.Trim(), StringComparison.OrdinalIgnoreCase));

        owner = match ?? string.Empty;
        refusal = Results.BadRequest(new
        {
            error = string.IsNullOrWhiteSpace(module)
                ? "У типа обязан быть владелец: " + string.Join(", ", allowed.Select(c => $"«{c}»")) + "."
                : $"Владелец «{module}» не подходит: на этом экземпляре доступны " +
                  string.Join(", ", allowed.Select(c => $"«{c}»")) + ".",
        });
        return match is not null;
    }

    record CreateTypeRequest(string Name, string Code, string Kind, Guid? ParentId, string Schema, string Module, bool IsAbstract = false);
    record SetModuleRequest(string Module);
    record UpdateTypeRequest(string Name, string Code, Guid? ParentId);
    record UpdateSchemaRequest(string Schema);
    record SetAbstractRequest(bool IsAbstract);
    record SetAllowsProxyRequest(bool AllowsProxy);
    record SetGroupRequest(string? Group);
    record ApplyAuditFixesRequest(IReadOnlyList<AuditFix> Fixes);
    record MigrateFieldKeyRequest(string OldKey, string NewKey);
}
