using System.Text.Json;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Documents;

public enum DocumentTypeKind { Document, Composite }

public class DocumentType : Entity
{
    public string Name { get; private set; } = default!;
    public string Code { get; private set; } = default!;
    public DocumentTypeKind Kind { get; private set; }
    public Guid? ParentId { get; private set; }

    /// <summary>Абстрактный тип нельзя добавить в комплект напрямую — он используется как базовый.</summary>
    public bool IsAbstract { get; private set; }

    /// <summary>Разрешает роль/прокси (issue #89): объект этого типа может ссылаться (`_baseRef`) на
    /// ДРУГОЙ объект ТОГО ЖЕ типа как на реального носителя данных (делегирование, не наследование
    /// по типам). Opt-in — по умолчанию выключено.</summary>
    public bool AllowsProxy { get; private set; }

    /// <summary>Произвольная группа для отображения на странице типов (null — без группы).</summary>
    public string? Group { get; private set; }

    /// <summary>Схема: { fields, groups?, excludedFields?, fieldOverrides? }</summary>
    public JsonDocument Schema { get; private set; } = default!;

    public JsonDocument PluginBindings { get; private set; } = JsonDocument.Parse("[]");

    private DocumentType() { }

    public static DocumentType Create(
        string name, string code, DocumentTypeKind kind, Guid? parentId, JsonDocument schema, bool isAbstract = false)
        => new() { Name = name, Code = code, Kind = kind, ParentId = parentId, Schema = schema, IsAbstract = isAbstract };

    public static DocumentType Restore(
        Guid id, string name, string code, DocumentTypeKind kind, Guid? parentId,
        JsonDocument schema, JsonDocument pluginBindings, bool isAbstract,
        DateTimeOffset createdAt, DateTimeOffset updatedAt, string? group = null, bool allowsProxy = false)
        => new()
        {
            Id = id, Name = name, Code = code, Kind = kind, ParentId = parentId,
            Schema = schema, PluginBindings = pluginBindings, IsAbstract = isAbstract,
            CreatedAt = createdAt, UpdatedAt = updatedAt, Group = group, AllowsProxy = allowsProxy,
        };

    public void UpdateSchema(JsonDocument schema) { Schema = schema; TouchUpdatedAt(); }
    public void Rename(string name, string code) { Name = name; Code = code; TouchUpdatedAt(); }
    public void SetParent(Guid? parentId) { ParentId = parentId; TouchUpdatedAt(); }
    public void UpdatePluginBindings(JsonDocument bindings) { PluginBindings = bindings; TouchUpdatedAt(); }
    public void SetAbstract(bool isAbstract) { IsAbstract = isAbstract; TouchUpdatedAt(); }
    public void SetAllowsProxy(bool allowsProxy) { AllowsProxy = allowsProxy; TouchUpdatedAt(); }
    public void SetGroup(string? group) { Group = string.IsNullOrWhiteSpace(group) ? null : group.Trim(); TouchUpdatedAt(); }
}
