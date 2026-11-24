using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BHS.CRG.Api.Modules.Ports;

public sealed class ModuleAcceptanceFieldsPort(
    IRepository<DocumentType> types, IDocumentTypeCapabilities capabilities,
    IRepository<PrimitiveType> primitives, IRepository<EnumType> enums, AppDbContext db)
    : IModuleAcceptanceFields
{
    public async Task<ModuleAcceptanceSchema> ReadAsync(string module, string typeCode, CancellationToken ct)
    {
        var type = await FindAsync(module, typeCode, ct);
        if (type is null) return new ModuleSchema(new AcceptanceFields([]));
        var keys = CustomerFields(type).Select(field => field.GetProperty("key").GetString()).ToHashSet();
        return new ModuleSchema(new AcceptanceFields(
            [.. DocumentTypeSchemaReader.OwnFields(type.Schema).Where(field => keys.Contains(field.Key))]));
    }

    public async Task<JsonElement?> ChoicesAsync(string module, string typeCode, Guid siteId, Guid targetTypeId,
        bool acceptUnionVariants, bool archived, string? search, int limit, CancellationToken ct)
    {
        var formType = await FindAsync(module, typeCode, ct);
        if (formType is null) return null;
        var byId = (await types.GetAllAsync(ct)).ToDictionary(type => type.Id);
        if (!byId.TryGetValue(targetTypeId, out var target) || !ReferenceTargets(formType, byId).Contains(targetTypeId))
            return null;

        var roots = new HashSet<Guid> { targetTypeId };
        if (acceptUnionVariants && SchemaTags.TypeHasTag(target, byId, FunctionalTag.TypeUnion))
            roots.UnionWith(DocumentTypeSchemaReader.EffectiveFields(targetTypeId, byId)
                .Where(field => !field.Computed && field.Type is "complex" or "doc-ref")
                .Select(field => field.TypeId).OfType<Guid>());

        var allowed = byId.Values.Where(type => InFamily(type, roots, byId)).Select(type => type.Id).ToArray();
        var q = search?.Trim().ToLowerInvariant();
        var rows = await db.DomainObjects.AsNoTracking()
            .Where(row => row.Facet == null && allowed.Contains(row.CompositeTypeId)
                && (row.ArchivedAt != null) == archived
                && (row.ScopeLevel == CatalogScope.System
                    || row.ScopeLevel == CatalogScope.Construction && row.ScopeId == siteId)
                && (string.IsNullOrEmpty(q) || row.DisplayName != null && row.DisplayName.ToLower().Contains(q)))
            .OrderBy(row => row.ScopeLevel).ThenBy(row => row.DisplayName).ThenBy(row => row.Id)
            .Select(row => new { row.Id, row.CompositeTypeId, DisplayName = row.DisplayName ?? "", Scope = row.ScopeLevel,
                Archived = row.ArchivedAt != null })
            .Take(limit + 1).ToListAsync(ct);
        var lineage = new Dictionary<Guid, DocumentType>();
        foreach (var row in rows.Take(limit))
            for (var type = byId.GetValueOrDefault(row.CompositeTypeId); type is not null && lineage.TryAdd(type.Id, type);)
                type = type.ParentId is { } parent ? byId.GetValueOrDefault(parent) : null;
        return JsonSerializer.SerializeToElement(new
        {
            items = rows.Take(limit), more = rows.Count > limit,
            types = lineage.Values.Select(type => new { type.Id, type.ParentId }),
        }, WebJson);
    }

    private static bool InFamily(DocumentType type, HashSet<Guid> roots, IReadOnlyDictionary<Guid, DocumentType> byId)
    {
        var seen = new HashSet<Guid>();
        for (DocumentType? item = type; item is not null && seen.Add(item.Id);)
        {
            if (roots.Contains(item.Id)) return true;
            item = item.ParentId is { } parent ? byId.GetValueOrDefault(parent) : null;
        }
        return false;
    }

    private static HashSet<Guid> ReferenceTargets(DocumentType formType, IReadOnlyDictionary<Guid, DocumentType> byId)
    {
        var targets = new HashSet<Guid>();
        var seen = new HashSet<Guid> { formType.Id };
        var pending = new Queue<Guid>();
        var customerKeys = CustomerFields(formType).Select(raw => Text(raw, "key")).ToHashSet();
        Follow(DocumentTypeSchemaReader.OwnFields(formType.Schema).Where(field => customerKeys.Contains(field.Key)));
        while (pending.TryDequeue(out var type))
            Follow(DocumentTypeSchemaReader.EffectiveFields(type, byId));
        return targets;

        void Follow(IEnumerable<SchemaFieldInfo> fields)
        {
            foreach (var field in fields.Where(field => !field.Computed && !field.Locked))
            {
                if (field.TypeId is not { } id || field.Type is not ("complex" or "array" or "doc-ref" or "doc-array"))
                    continue;
                targets.Add(id);
                if ((field.Type is "complex" or "array") && seen.Add(id)) pending.Enqueue(id);
            }
        }
    }

    public async Task<JsonElement?> FormAsync(string module, string typeCode, CancellationToken ct)
    {
        var type = await FindAsync(module, typeCode, ct);
        if (type is null) return null;

        var fields = CustomerFields(type);
        var referenced = new Dictionary<Guid, DocumentType>();
        var primitiveIds = new HashSet<Guid>();
        var enumIds = new HashSet<Guid>();
        var pending = new Queue<Guid>();
        var seen = new HashSet<Guid> { type.Id };
        Follow(fields);
        while (pending.TryDequeue(out var id))
        {
            var child = await types.GetByIdAsync(id, ct);
            if (child is null) continue;
            referenced.Add(id, child);
            if (child.ParentId is { } parentId && seen.Add(parentId)) pending.Enqueue(parentId);
            Follow(FieldsOf(child));
        }

        return JsonSerializer.SerializeToElement(new
        {
            fields, types = referenced.Values.Select(t => new { t.Id, t.Name, t.Code, t.Kind, t.ParentId, Schema = FormSchema(t) }),
            primitiveTypes = primitiveIds.Count == 0 ? [] : await primitives.FindAsync(t => primitiveIds.Contains(t.Id), ct),
            enumTypes = enumIds.Count == 0 ? [] : await enums.FindAsync(t => enumIds.Contains(t.Id), ct),
        }, WebJson);

        void Follow(IEnumerable<JsonElement> schema)
        {
            foreach (var field in schema)
            {
                if (!field.TryGetProperty("typeId", out var target) || target.ValueKind != JsonValueKind.String
                    || !target.TryGetGuid(out var targetId)) continue;
                switch (Text(field, "type"))
                {
                    case "primitive": primitiveIds.Add(targetId); break;
                    case "enum": enumIds.Add(targetId); break;
                    case "complex" or "array" or "doc-ref" or "doc-array":
                        if (seen.Add(targetId)) pending.Enqueue(targetId);
                        break;
                }
            }
        }
    }

    private async Task<DocumentType?> FindAsync(string module, string typeCode, CancellationToken ct)
    {
        var found = await types.FindAsync(type => type.Code == typeCode && type.Module == module, ct);
        var type = found.SingleOrDefault();
        return type is not null && capabilities.SupportsAcceptance(type) ? type : null;
    }

    private static JsonElement[] CustomerFields(DocumentType type) => [.. FieldsOf(type).Where(field =>
        Text(field, SchemaFieldOrigin.Property) != SchemaFieldOrigin.Module
        && !(field.TryGetProperty("computed", out var computed) && computed.ValueKind == JsonValueKind.True))];

    private static IEnumerable<JsonElement> FieldsOf(DocumentType type) =>
        type.Schema.RootElement is { ValueKind: JsonValueKind.Object } root
        && root.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array
            ? fields.EnumerateArray().Where(field =>
                field.ValueKind == JsonValueKind.Object && !string.IsNullOrEmpty(Text(field, "key")))
            : [];

    private static string? Text(JsonElement field, string name) =>
        field.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Dictionary<string, JsonElement> FormSchema(DocumentType type) =>
        type.Schema.RootElement is { ValueKind: JsonValueKind.Object } root
            ? root.EnumerateObject().Where(p =>
                p.Name is "fields" or "groups" or "ungroupedOrder" or "excludedFields" or "fieldOverrides" or "tags")
                .ToDictionary(p => p.Name, p => p.Value)
            : [];

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class ModuleSchema(AcceptanceFields fields) : ModuleAcceptanceSchema
    {
        public override bool HasFields => fields.HasFields;
        public override IReadOnlyList<ModuleAcceptanceField> Missing(JsonElement data) =>
            [.. fields.Missing(data).Select(f => new ModuleAcceptanceField(f.Key, f.Title))];
    }
}
