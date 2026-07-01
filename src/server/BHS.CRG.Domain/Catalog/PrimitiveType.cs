using System.Text.Json;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Catalog;

/// <summary>
/// Пользовательский тип поля — стандартный примитив (string/number/date)
/// с набором ограничений (паттерн, мин/макс, целое число и т.д.).
/// Используется в схемах DocumentType через type="primitive" + typeId.
/// </summary>
public class PrimitiveType : Entity
{
    public string Name { get; private set; } = default!;

    /// <summary>Уникальный код: email, phone, inn, …</summary>
    public string Code { get; private set; } = default!;

    /// <summary>"string" | "number" | "date"</summary>
    public string BaseType { get; private set; } = default!;

    public string? Description { get; private set; }

    /// <summary>
    /// JSON с ограничениями.
    /// string: { pattern?, patternMessage?, minLength?, maxLength? }
    /// number: { min?, max?, integer? }
    /// date:   { minDate?, maxDate? }
    /// </summary>
    public JsonDocument Constraints { get; private set; } = default!;

    /// <summary>
    /// Коды функциональных тэгов, применимых к полям этого типа (см. реестр FunctionalTag).
    /// Определяет, какие тэги показываются в редакторе схемы для поля type="primitive".
    /// </summary>
    public List<string> AllowedTags { get; private set; } = [];

    /// <summary>Произвольная группа для отображения на странице типов (null — без группы).</summary>
    public string? Group { get; private set; }

    private PrimitiveType() { }

    public static PrimitiveType Create(string name, string code, string baseType,
        string? description, JsonDocument constraints, IEnumerable<string>? allowedTags = null)
        => new()
        {
            Name = name, Code = code, BaseType = baseType,
            Description = description, Constraints = constraints,
            AllowedTags = allowedTags?.ToList() ?? [],
        };

    public void Update(string name, string code, string? description, JsonDocument constraints,
        IEnumerable<string>? allowedTags = null)
    {
        Name = name; Code = code; Description = description; Constraints = constraints;
        AllowedTags = allowedTags?.ToList() ?? [];
        TouchUpdatedAt();
    }

    public void SetGroup(string? group) { Group = string.IsNullOrWhiteSpace(group) ? null : group.Trim(); TouchUpdatedAt(); }

    public static PrimitiveType Restore(
        Guid id, string name, string code, string baseType, string? description,
        JsonDocument constraints, DateTimeOffset createdAt, DateTimeOffset updatedAt,
        IEnumerable<string>? allowedTags = null, string? group = null)
        => new()
        {
            Id = id, Name = name, Code = code, BaseType = baseType,
            Description = description, Constraints = constraints,
            CreatedAt = createdAt, UpdatedAt = updatedAt,
            AllowedTags = allowedTags?.ToList() ?? [],
            Group = group,
        };
}
