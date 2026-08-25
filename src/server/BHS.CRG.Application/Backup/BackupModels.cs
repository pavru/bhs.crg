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
    IReadOnlyList<BackupTypstUserLibFile>? TypstUserLibFiles = null,
    BackupRecognitionProfile[]? RecognitionProfiles = null,
    BackupDataSetBindingTemplate[]? DataSetBindingTemplates = null,
    BackupReconciliationAlias[]? ReconciliationAliases = null,
    BackupDataSetProcessingTemplate[]? DataSetProcessingTemplates = null,
    BackupQualityDocument[]? QualityDocuments = null,
    // ── Проектные данные (issue #833) ────────────────────────────────────────────────────────
    // Тоже аддитивно и тоже без bump SchemaVersion: копия, снятая новой версией, читается старой
    // (лишние секции она проигнорирует), а старая копия читается новой — секций просто нет.
    // Присутствие этих секций и означает «копия полная»; IncludesProjectData говорит это прямо,
    // чтобы «полная копия системы, где нет ни одной стройки» не выглядела конфигурационной.
    bool? IncludesProjectData = null,
    BackupConstruction[]? Constructions = null,
    BackupSection[]? Sections = null,
    BackupDocumentSet[]? DocumentSets = null,
    BackupDocument[]? Documents = null,
    BackupDataSetFile[]? DataSetFiles = null,
    BackupDataSetSource[]? DataSetSources = null,
    BackupDataSetBinding[]? DataSetBindings = null,
    BackupReconciliationDefinition[]? Reconciliations = null,
    BackupMaterialQualityLink[]? MaterialQualityLinks = null,
    BackupDocumentSetPlan[]? DocumentSetPlans = null);

// ── Проектные данные (issue #833) ────────────────────────────────────────────────────────────

/// <summary>
/// Стройка. Носитель области <c>Construction</c>: без неё общие данные и документы качества этого
/// уровня восстановиться не могут — ровно те предупреждения «относятся к стройкам, которых нет»,
/// с которых issue и начался.
/// </summary>
/// <remarks>
/// <paramref name="CreatedByUserId" /> переносится как есть, хотя учётных записей в копии нет:
/// поле фиксирует историю, а не право доступа, и подменять его на «кого-нибудь существующего»
/// значило бы соврать о том, кто завёл стройку. На целевой системе такой пользователь может не
/// найтись — интерфейс показывает имя только там, где оно есть.
/// </remarks>
public record BackupConstruction(
    Guid Id, string Name, Guid CreatedByUserId, Guid? ProfileObjectId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record BackupSection(
    Guid Id, Guid ConstructionId, string Name, Guid? ProfileObjectId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record BackupDocumentSet(
    Guid Id, Guid SectionId, string Name, Guid? ProfileObjectId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// Строка плана комплекта (issue #796). Секция аддитивная, как и остальные проектные: копия,
/// снятая новой версией, читается старой — план она просто не увидит.
///
/// Планы уровней выше в копию не попадают, потому что их не существует: раздел и стройка
/// консолидируют комплекты на лету. Восстановился бы такой «план» — и разошёлся бы с суммой
/// нижележащих при первой же правке.
/// </summary>
public record BackupDocumentSetPlan(
    Guid Id, Guid DocumentSetId, Guid DocumentTypeId, int PlannedCount,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// Документ комплекта: объект Document-типа вместе с документной фасетой (статус, порядок,
/// выбранные шаблоны, кэш плагинов) и выпущенными файлами.
///
/// Фасета едет ВНУТРИ записи, а не отдельной секцией: она и есть то, чем документ отличается от
/// записи общих данных, и разъехавшись с объектом хотя бы в одной копии, превратила бы документ в
/// общие данные — молча.
/// </summary>
public record BackupDocument(
    Guid Id, Guid SetId, Guid CompositeTypeId, string? DisplayName, JsonElement Data,
    string[] Aliases, string Status, int SortOrder,
    Guid? TemplateId, string? TemplateIds, string? TemplateParams, JsonElement PluginData,
    BackupGeneratedFile[] GeneratedFiles,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Выпущенный файл документа: запись плюс сам блоб (он уезжает в архив как скан).</summary>
public record BackupGeneratedFile(
    Guid Id, string Format, string BlobPath, Guid? TemplateId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// Набор данных: сам файл-сырьё (блоб) вместе с настройками разбора. Системные наборы
/// (<c>Format = System</c>) блоба не имеют — их сырьё это данные самой системы.
/// </summary>
public record BackupDataSetFile(
    Guid Id, string Name, string Format, string BlobPath, string Scope, Guid? ScopeId,
    string? PreprocessingProfile, string? Grouping, string? InvoiceRawData, string? RecognitionProfiles,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// Источник — разбор одного листа/пути внутри набора, вместе с КЭШЕМ данных.
///
/// Кэш переносится не ради скорости: восстановление не запускает разборщики и не распознаёт сканы
/// заново, поэтому источник без кэша приехал бы пустым — файл в хранилище есть, строк ноль. Он же
/// и делает полную копию тяжёлой.
/// </summary>
public record BackupDataSetSource(
    Guid Id, Guid FileId, string Name, string SheetOrPath, string? ColumnExpressions,
    string CachedSchema, int CachedRowCount, string? CachedData, string? Tags,
    string? RowFilter, string? ComputedColumns, string? SortSpec, string? StaleReason,
    Guid? MaterializeTypeId, string? MaterializeMapping, string? MaterializeDiscriminator,
    string? MaterializeByIdColumn,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Привязка источника к конкретному объекту — документу комплекта или записи общих данных.</summary>
public record BackupDataSetBinding(
    Guid Id, Guid OwnerId, Guid SourceId, string? TargetFieldKey, string Mapping,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// Определение сверки. До issue #833 в копию не шло с ясной причиной: спека адресует источники по
/// идентификатору, а идентификатор рождается при загрузке файла — на целевой системе такого
/// источника не было бы никогда. Возражение снимается ровно тем, что источники теперь едут в той
/// же копии и с теми же идентификаторами; поэтому определения идут ТОЛЬКО с проектными данными.
/// </summary>
public record BackupReconciliationDefinition(
    Guid Id, string Name, string Scope, Guid? ScopeId, JsonElement Spec,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// Связка «материал ↔ документ качества». Прежде не переносилась, потому что адресует материалы
/// комплектов, которых в копии нет; с проектными данными комплекты есть — связка едет с ними.
/// </summary>
public record BackupMaterialQualityLink(
    Guid Id, string Scope, Guid? ScopeId, string MaterialKey, string? MaterialLabel,
    Guid QualityDocumentId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// Переиспользуемый рецепт обработки источника (issue #687). Внешних ключей не имеет вовсе — внутри
/// только имя и правила, адресующие колонки ПО ИМЕНАМ. Подсистема наборов данных исключена из копии
/// (#403) как носитель проектного сырья и крупных блобов; рецепт не то и не другое.
/// </summary>
public record BackupDataSetProcessingTemplate(
    Guid Id, string Name, string? SheetOrPath, string? ColumnExpressions,
    string? RowFilter, string? ComputedColumns, string? SortSpec,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// Документ качества из общей библиотеки вместе со сканом (issue #687). Переносится ВСЯ библиотека,
/// включая документы уровня комплекта: библиотека наполняется годами и распознаётся вручную, и
/// потерять её дороже, чем нести лишние мегабайты.
/// </summary>
/// <remarks>
/// Связки с материалами (<c>MaterialQualityLink</c>) в копию не идут: они адресуют материалы
/// комплектов, которых в копии нет. Библиотека восстанавливается непривязанной — это осознанно.
/// </remarks>
public record BackupQualityDocument(
    Guid Id, Guid DocumentTypeId, string DisplayName, JsonElement Requisites,
    string Scope, Guid? ScopeId, string Source, string? SourceUrl,
    string? ScanBlobPath, string? ScanFileName, string? ScanMimeType,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// Шаблон стандартного маппинга колонок для типа документа. Проектных зависимостей не имеет —
/// висит только на типе документа, а типы в копии есть; пишется под ролью Admin, то есть это
/// настройка системы, ради переноса которой резервная копия и существует.
/// </summary>
public record BackupDataSetBindingTemplate(
    Guid Id, Guid DocumentTypeId, string Name, string? TargetFieldKey, string ColumnMappings,
    int SortOrder, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// Утверждение «эти два наименования обозначают одно и то же». Знание человека: пересчитать его
/// нельзя, только надумать заново.
/// </summary>
public record BackupReconciliationAlias(
    Guid Id, string AliasKey, string AliasLabel, string CanonicalKey, string CanonicalLabel,
    string Status, string? Note, string? ProposedBy, string? ConfirmedBy,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

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

// Файл дерева библиотеки (issue #473). Добавлено АДДИТИВНО, без подъёма версии схемы: иначе
// копии предыдущей версии перестали бы восстанавливаться (конвенция #403). Старый бэкап просто
// не несёт этой секции — дерево останется пустым, а точка входа восстановится как раньше.
public record BackupTypstUserLibFile(
    Guid Id, string Path, string Content, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

// Профиль распознавания (issue #406) — параметры к хардкод-промптам. Конфигурация, влияющая на
// извлекаемые данные, поэтому в бэкапе. Встроенные несут Code (ключ ре-сидинга) и IsModified:
// восстановленный правленый профиль не должен быть затёрт сидингом на целевой системе.
public record BackupRecognitionProfile(
    Guid Id, string Name, string? Code, string Kind,
    JsonElement Fields, JsonElement? Shape, bool IsBuiltIn, bool IsModified,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    JsonElement? RowColumns = null, string? BuiltInHash = null);

/// <summary>
/// Сколько весит копия, снятая прямо сейчас, и с чем этот вес сравнивать (issue #711).
///
/// <paramref name="MissingBlobCount" /> — файлы, которых в хранилище уже нет: экспорт их пропускает
/// с предупреждением, и в копию они не попадут. Число здесь не ради веса, а потому, что оно
/// означает битые ссылки, о которых иначе узнать неоткуда.
/// </summary>
public record BackupSizeEstimate(
    /// <summary>Состав, для которого посчитан вес: <c>Configuration</c> или <c>Full</c>.</summary>
    string Scope,
    BackupSizeVariant Variant,
    long LimitBytes);

/// <summary>
/// Вес копии одного состава (issue #833: составов стало два).
///
/// <paramref name="MissingBlobCount" /> - файлы, которых в хранилище уже нет: экспорт их пропускает
/// с предупреждением, и в копию они не попадут. Число здесь не ради веса, а потому, что оно
/// означает битые ссылки, о которых иначе узнать неоткуда.
/// </summary>
public record BackupSizeVariant(
    long TotalBytes,
    long ManifestBytes,
    long BlobBytes,
    int BlobCount,
    int MissingBlobCount);

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
    int TypstUserLibFilesRestored = 0,
    int RecognitionProfilesCreated = 0,
    int RecognitionProfilesUpdated = 0,
    int DataSetBindingTemplatesCreated = 0,
    int DataSetBindingTemplatesUpdated = 0,
    int ReconciliationAliasesCreated = 0,
    int ReconciliationAliasesUpdated = 0,
    int DataSetProcessingTemplatesCreated = 0,
    int DataSetProcessingTemplatesUpdated = 0,
    int QualityDocumentsCreated = 0,
    int QualityDocumentsUpdated = 0,
    /// <summary>
    /// Проектные секции (issue #833) — списком, а не парой полей на каждую. Отчёт и так
    /// перечисляет два десятка чисел; следующая секция копии не должна означать правку в
    /// четырёх местах ради ещё одной пары.
    /// </summary>
    IReadOnlyList<RestoreSectionStat>? ProjectSections = null);

/// <summary>Одна секция отчёта о восстановлении: сколько добавлено и сколько обновлено.</summary>
public record RestoreSectionStat(string Label, int Created, int Updated);
