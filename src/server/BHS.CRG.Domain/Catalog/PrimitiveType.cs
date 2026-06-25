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

    private PrimitiveType() { }

    public static PrimitiveType Create(string name, string code, string baseType,
        string? description, JsonDocument constraints)
        => new()
        {
            Name = name, Code = code, BaseType = baseType,
            Description = description, Constraints = constraints,
        };

    public void Update(string name, string code, string? description, JsonDocument constraints)
    {
        Name = name; Code = code; Description = description; Constraints = constraints;
        TouchUpdatedAt();
    }

    public static PrimitiveType Restore(
        Guid id, string name, string code, string baseType, string? description,
        JsonDocument constraints, DateTimeOffset createdAt, DateTimeOffset updatedAt)
        => new()
        {
            Id = id, Name = name, Code = code, BaseType = baseType,
            Description = description, Constraints = constraints,
            CreatedAt = createdAt, UpdatedAt = updatedAt,
        };
}
