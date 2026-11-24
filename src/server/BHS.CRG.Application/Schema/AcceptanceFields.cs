using System.Text.Json;

namespace BHS.CRG.Application.Schema;

/// <summary>Пустые поля с отметкой для приёмки, в порядке их объявления в схеме.</summary>
public sealed class AcceptanceFields(IReadOnlyList<SchemaFieldInfo> fields)
{
    private readonly AcceptanceField[] _fields = fields.Where(f => f.ForAcceptance && !f.Computed)
        .Select(f => new AcceptanceField(f.Key, string.IsNullOrWhiteSpace(f.Title) ? f.Key : f.Title))
        .ToArray();

    public bool HasFields => _fields.Length > 0;

    public IReadOnlyList<AcceptanceField> Missing(JsonElement data) =>
        [.. _fields.Where(f => RequiredFields.IsEmpty(
            data.ValueKind == JsonValueKind.Object && data.TryGetProperty(f.Key, out var value) ? value : null))];
}

public sealed record AcceptanceField(string Key, string Title);
