using System.Text.Json;

namespace BHS.CRG.Application.Backup;

public record BackupManifest(
    int SchemaVersion,
    string AppVersion,
    DateTimeOffset CreatedAt,
    BackupDocumentType[] DocumentTypes,
    BackupTemplate[] Templates,
    BackupCatalogEntity[] CatalogEntities,
    BackupCommonDataEntry[] CommonDataEntries,
    BackupPrimitiveType[]? PrimitiveTypes = null);

public record BackupPrimitiveType(
    Guid Id, string Name, string Code, string BaseType, string? Description,
    JsonElement Constraints,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? Group = null);

public record BackupDocumentType(
    Guid Id, string Name, string Code, string Kind, Guid? ParentId, bool IsAbstract,
    JsonElement Schema, JsonElement PluginBindings,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? Group = null);

public record BackupTemplate(
    Guid Id, Guid DocumentTypeId, string Name, string Content, int Version,
    bool IsActive, bool IsDefault,
    string PageSize, string PageOrientation,
    int MarginTop, int MarginRight, int MarginBottom, int MarginLeft,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? Parameters = null);

public record BackupCatalogEntity(
    Guid Id, string EntityType, string DisplayName, JsonElement Data, Guid? OwnerId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record BackupCommonDataEntry(
    Guid Id, string DisplayName, Guid CompositeTypeId, JsonElement Data,
    string Scope, Guid? ScopeId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record RestoreReport(
    bool Success,
    string? ConversionNotice,
    IReadOnlyList<string> Warnings,
    int DocumentTypesCreated,
    int DocumentTypesUpdated,
    int TemplatesCreated,
    int TemplatesUpdated,
    int CatalogEntitiesCreated,
    int CatalogEntitiesUpdated,
    int CommonDataEntriesCreated,
    int CommonDataEntriesUpdated,
    int PrimitiveTypesCreated = 0,
    int PrimitiveTypesUpdated = 0);
