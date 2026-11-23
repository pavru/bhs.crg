using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Objects;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Printing;

namespace BHS.CRG.Api.Modules.Ports;

public sealed class ModulePrintCatalogPort(IDomainObjectRepository objects, IModuleUser user) : IModulePrintCatalog
{
    public async Task<JsonElement> ResolveAsync(JsonElement data, CancellationToken ct)
    {
        var canRead = await user.HasAsync(CorePermissions.CatalogRead, ct);
        return await ResolveNodeAsync(data, canRead, [], 0, ct);
    }

    private async Task<JsonElement> ResolveNodeAsync(JsonElement node, bool canRead,
        Dictionary<Guid, JsonElement> entries, int referenceDepth, CancellationToken ct)
    {
        if (CatalogRefs.EntryId(node) is { } entryId)
        {
            if (!canRead || referenceDepth >= BaseRefReader.MaxDepth)
                return JsonSerializer.SerializeToElement<object?>(null);

            if (!entries.TryGetValue(entryId, out var data))
            {
                var chain = await objects.CardChainAsync(entryId, [], ct);
                // Составной реквизит читает общие данные. Фасета документа из такой ссылки недоступна,
                // в том числе через основу записи. Слияние и границы цепочки те же, что у каталога.
                var visible = chain?.Links.TakeWhile(link => !link.IsDocument).Reverse().ToArray() ?? [];
                data = visible.Length == 0 ? JsonSerializer.SerializeToElement<object?>(null)
                    : visible.Aggregate(default(JsonElement), (merged, link) => BaseRefReader.MergeObjects(merged, link.Data));
                entries.Add(entryId, data);
            }

            return await ResolveNodeAsync(data, canRead, entries, referenceDepth + 1, ct);
        }

        if (node.ValueKind == JsonValueKind.Object)
        {
            var fields = new Dictionary<string, JsonElement>();
            foreach (var field in node.EnumerateObject())
                fields[field.Name] = await ResolveNodeAsync(field.Value, canRead, entries, referenceDepth, ct);
            return JsonSerializer.SerializeToElement(fields);
        }

        if (node.ValueKind == JsonValueKind.Array)
        {
            var items = new List<JsonElement>();
            foreach (var item in node.EnumerateArray())
                items.Add(await ResolveNodeAsync(item, canRead, entries, referenceDepth, ct));
            return JsonSerializer.SerializeToElement(items);
        }

        return node;
    }
}
