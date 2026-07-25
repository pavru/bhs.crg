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
    BackupPrimitiveType[]? PrimitiveTypes = null,
    // Аддитивные (nullable, в конце) — не ломают чтение прежних v2-копий и не требуют bump схемы:
    // конфигурация, от которой зависит генерация, но которая раньше в бэкап не попадала (issue #403).
    BackupEnumType[]? EnumTypes = null,
    BackupTemplateAsset[]? TemplateAssets = null,
    BackupTypstUserLib? TypstUserLib = null,
    BackupRecognitionProfile[]? RecognitionProfiles = null);

public record BackupPrimitiveType(
    Guid Id, string Name, string Code, string BaseType, string? Description,
    JsonElement Constraints,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? Group = null);

public record BackupDocumentType(
    Guid Id, string Name, string Code, string Kind, Guid? ParentId, bool IsAbstract,
    JsonElement Schema, JsonElement PluginBindings,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? Group = null, bool AllowsProxy = false);

public record BackupTemplate(
    Guid Id, Guid DocumentTypeId, string Name, string Content, int Version,
    bool IsActive, bool IsDefault,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? Parameters = null, string? Comment = null);

public record BackupCatalogEntity(
    Guid Id, string EntityType, string DisplayName, JsonElement Data, Guid? OwnerId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record BackupCommonDataEntry(
    Guid Id, string DisplayName, Guid CompositeTypeId, JsonElement Data,
    string Scope, Guid? ScopeId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string[]? Aliases = null);

// Переиспользуемое перечисление (issue #59) — схемы типов ссылаются на него через typeId; без него
// генерация не резолвит код→имя enum-полей.
public record BackupEnumType(
    Guid Id, string Name, string Code, string? Description, JsonElement Values,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? Group = null);

// Ассет Typst-шаблона (issue #62) — графика/шрифт; сам файл лежит в blob-хранилище (BlobPath собирается
// в архив как остальные блобы).
public record BackupTemplateAsset(
    Guid Id, string Scope, Guid? ScopeId, string Kind,
    string Name, string FileName, string MimeType, string BlobPath, string? FontFamilyName,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

// Общая Typst-библиотека (userlib.typ) — синглтон, подмешивается при компиляции всех шаблонов.
public record BackupTypstUserLib(string Content, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

// Профиль распознавания (issue #406) — параметры к хардкод-промптам. Конфигурация, влияющая на
// извлекаемые данные, поэтому в бэкапе. Встроенные несут Code (ключ ре-сидинга) и IsModified:
// восстановленный правленый профиль не должен быть затёрт сидингом на целевой системе.
public record BackupRecognitionProfile(
    Guid Id, string Name, string? Code, string Kind,
    JsonElement Fields, JsonElement? Shape, bool IsBuiltIn, bool IsModified,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    JsonElement? RowColumns = null, string? BuiltInHash = null);

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
    int PrimitiveTypesUpdated = 0,
    int EnumTypesCreated = 0,
    int EnumTypesUpdated = 0,
    int TemplateAssetsCreated = 0,
    int TemplateAssetsUpdated = 0,
    bool TypstUserLibRestored = false,
    int RecognitionProfilesCreated = 0,
    int RecognitionProfilesUpdated = 0);
