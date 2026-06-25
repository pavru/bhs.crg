using System.Text.Json;

namespace BHS.CRG.Plugins;

public interface IDataSourcePlugin
{
    string Id { get; }
    string DisplayName { get; }
    EntitySchema[] ProvidedSchemas { get; }

    Task<SearchResult> SearchAsync(string entityType, string query, CancellationToken ct = default);
    Task<JsonDocument> FetchAsync(string entityType, string externalId, CancellationToken ct = default);
}

public record EntitySchema(string EntityType, string DisplayName, JsonDocument FieldsSchema);

public record SearchResult(IReadOnlyList<SearchResultItem> Items);

public record SearchResultItem(string ExternalId, string DisplayName, JsonDocument Preview);
