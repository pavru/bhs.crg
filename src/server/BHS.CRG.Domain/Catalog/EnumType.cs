using System.Text.Json;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Catalog;

/// <summary>
/// Переиспользуемый список вариантов для полей типа "enum" (issue #59) — используется в схемах
/// DocumentType через type="enum" + typeId (вместо инлайн options: string[] в самом поле).
/// Значения — пары код+имя: код хранится в реквизитах, имя резолвится при отображении/генерации,
/// чтобы переименование варианта в реестре не портило уже сохранённые документы.
/// </summary>
public class EnumType : Entity
{
    public string Name { get; private set; } = default!;

    /// <summary>Уникальный код: status, unit, …</summary>
    public string Code { get; private set; } = default!;

    public string? Description { get; private set; }

    /// <summary>JSON-массив [{code, label}] — варианты перечисления.</summary>
    public JsonDocument Values { get; private set; } = default!;

    /// <summary>Произвольная группа для отображения на странице типов (null — без группы).</summary>
    public string? Group { get; private set; }

    private EnumType() { }

    public static EnumType Create(string name, string code, string? description, JsonDocument values)
        => new() { Name = name, Code = code, Description = description, Values = values };

    public void Update(string name, string code, string? description, JsonDocument values)
    {
        Name = name; Code = code; Description = description; Values = values;
        TouchUpdatedAt();
    }

    public void SetGroup(string? group) { Group = string.IsNullOrWhiteSpace(group) ? null : group.Trim(); TouchUpdatedAt(); }

    public static EnumType Restore(
        Guid id, string name, string code, string? description, JsonDocument values,
        DateTimeOffset createdAt, DateTimeOffset updatedAt, string? group = null)
        => new()
        {
            Id = id, Name = name, Code = code, Description = description, Values = values,
            CreatedAt = createdAt, UpdatedAt = updatedAt, Group = group,
        };
}
