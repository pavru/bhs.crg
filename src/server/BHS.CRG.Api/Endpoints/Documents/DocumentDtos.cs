using System.Text.Json;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;

namespace BHS.CRG.Api.Endpoints.Documents;

// DTO-проекции единого DomainObject в стабильный JSON-контракт клиента (issue #84). Отдельные DTO
// нужны и потому, что документные getter'ы DomainObject (Status/SortOrder/…) бросают для общих данных
// (нет фасеты) — сериализовать доменный объект напрямую нельзя.

/// <summary>Документ комплекта — форма клиентского DocumentInstance.</summary>
public record InstanceDto(
    Guid Id, Guid DocumentSetId, Guid DocumentTypeId, string? Name,
    Guid? TemplateId, string? TemplateIds, JsonDocument Requisites, JsonDocument PluginData,
    string? TemplateParams, DocumentStatus Status, IReadOnlyList<GeneratedFileDto> GeneratedFiles,
    int SortOrder, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static InstanceDto From(DomainObject o) => new(
        o.Id, o.ScopeId ?? Guid.Empty, o.CompositeTypeId, o.DisplayName,
        o.TemplateId, o.TemplateIds, o.Data, o.PluginData, o.TemplateParams, o.Status,
        o.GeneratedFiles.Select(GeneratedFileDto.From).ToList(),
        o.SortOrder, o.CreatedAt, o.UpdatedAt);
}

public record GeneratedFileDto(Guid Id, Guid DocumentInstanceId, OutputFormat Format, string BlobPath, Guid? TemplateId)
{
    public static GeneratedFileDto From(GeneratedFile f) => new(f.Id, f.ObjectId, f.Format, f.BlobPath, f.TemplateId);
}

/// <summary>Комплект с документами — форма клиентского DocumentSet.</summary>
public record DocumentSetDto(
    Guid Id, string Name, Guid SectionId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<InstanceDto> Instances)
{
    public static DocumentSetDto From(DocumentSet set, IReadOnlyList<DomainObject> documents) => new(
        set.Id, set.Name, set.SectionId, set.CreatedAt, set.UpdatedAt,
        documents.OrderBy(d => d.SortOrder).Select(InstanceDto.From).ToList());
}

/// <summary>Запись общих данных — форма клиентского CommonDataEntry.</summary>
public record CommonDataEntryDto(
    Guid Id, string DisplayName, string[] Aliases, Guid CompositeTypeId, JsonDocument Data,
    Domain.Catalog.CatalogScope Scope, Guid? ScopeId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static CommonDataEntryDto From(DomainObject o) => new(
        o.Id, o.DisplayName ?? "", o.Aliases.ToArray(), o.CompositeTypeId, o.Data,
        o.ScopeLevel, o.ScopeId, o.CreatedAt, o.UpdatedAt);
}
