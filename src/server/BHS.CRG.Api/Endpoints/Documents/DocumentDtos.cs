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

/// <summary>Комплект в дереве стройки — без документов, но со счётчиком (issue #84: документы —
/// DomainObject по расположению, здесь нужен только COUNT для навигации/каскадов).</summary>
public record DocumentSetSummaryDto(
    Guid Id, string Name, Guid SectionId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int DocumentCount)
{
    public static DocumentSetSummaryDto From(DocumentSet ds, IReadOnlyDictionary<Guid, int> counts) => new(
        ds.Id, ds.Name, ds.SectionId, ds.CreatedAt, ds.UpdatedAt,
        counts.TryGetValue(ds.Id, out var n) ? n : 0);
}

/// <summary>Раздел в дереве стройки.</summary>
public record SectionSummaryDto(
    Guid Id, string Name, Guid ConstructionId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<DocumentSetSummaryDto> DocumentSets)
{
    public static SectionSummaryDto From(Section s, IReadOnlyDictionary<Guid, int> counts) => new(
        s.Id, s.Name, s.ConstructionId, s.CreatedAt, s.UpdatedAt,
        s.DocumentSets.Select(ds => DocumentSetSummaryDto.From(ds, counts)).ToList());
}

/// <summary>Стройка с деревом разделов/комплектов и счётчиками документов.</summary>
public record ConstructionDto(
    Guid Id, string Name, Guid CreatedByUserId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<SectionSummaryDto> Sections)
{
    public static ConstructionDto From(Construction c, IReadOnlyDictionary<Guid, int> counts) => new(
        c.Id, c.Name, c.CreatedByUserId, c.CreatedAt, c.UpdatedAt,
        c.Sections.Select(s => SectionSummaryDto.From(s, counts)).ToList());
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
