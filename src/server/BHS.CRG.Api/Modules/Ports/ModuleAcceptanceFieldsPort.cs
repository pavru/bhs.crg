using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules.Ports;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BHS.CRG.Api.Modules.Ports;

public sealed class ModuleAcceptanceFieldsPort(
    IRepository<DocumentType> types, IDocumentTypeCapabilities capabilities,
    IRepository<PrimitiveType> primitives, IRepository<EnumType> enums)
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
