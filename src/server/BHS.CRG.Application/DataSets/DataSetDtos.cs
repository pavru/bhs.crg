namespace BHS.CRG.Application.DataSets;

// ── Output DTOs (JSON shapes consumed by the SPA) ───────────────────────────────

public record DataSetSourceDto(
    Guid Id, Guid FileId, string Name, string SheetOrPath, string? ColumnExpressions,
    string CachedSchema, int CachedRowCount,
    object? RowFilter, object? ComputedColumns, object? SortSpec,
    IReadOnlyList<string>? Tags, bool RecognitionStale = false,
    Guid? MaterializeTypeId = null, Dictionary<string, string>? MaterializeMapping = null);

/// <summary>Материализованный предпросмотр источника: строки, развёрнутые в объекты формы типа (issue #19).</summary>
public record MaterializePreviewDto(Guid? TypeId, int TotalRows, IReadOnlyList<Dictionary<string, object?>> Rows, string? Error);

public record DataSetFileDto(
    Guid Id, string Name, string Format, string Scope, Guid? ScopeId,
    IReadOnlyList<DataSetSourceDto> Sources, DateTimeOffset CreatedAt,
    string? PreprocessingProfile = null);

public record BindingFileDto(Guid Id, string Name, string Format, string Scope, Guid? ScopeId);

public record BindingSourceDto(
    Guid Id, string Name, string SheetOrPath, string CachedSchema, int CachedRowCount, BindingFileDto? File,
    Guid? MaterializeTypeId = null, Dictionary<string, string>? MaterializeMapping = null);

/// <summary>Привязка — только Mapping. Filter/Transformation/Sort живут на DataSetSource.
/// Владелец — ровно одно из InstanceId/CommonDataEntryId задано.</summary>
public record DataSetBindingDto(
    Guid Id, Guid? InstanceId, Guid? CommonDataEntryId, Guid SourceId, string? TargetFieldKey,
    Dictionary<string, string> Mapping, BindingSourceDto? Source);

/// <summary>Шаблон маппинга (для типа документа). Filter/Transformation/Sort — см. DataSetProcessingTemplateDto.</summary>
public record DataSetBindingTemplateDto(
    Guid Id, Guid DocumentTypeId, string Name, string? TargetFieldKey,
    Dictionary<string, string> ColumnMappings,
    int SortOrder, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Переиспользуемый рецепт источника (Extraction + Filter/Transformation/Sort) — не привязан к типу документа.</summary>
public record DataSetProcessingTemplateDto(
    Guid Id, string Name, string? SheetOrPath, string? ColumnExpressions,
    object? RowFilter, object? ComputedColumns, object? SortSpec,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record BindingPreviewDto(
    Guid BindingId, string SourceName, string FileName, string Mode,
    string? TargetFieldKey, int TotalRows, object Data, string? Error);

public record SourcePreviewDto(
    IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows, int TotalRows);

/// <summary>Готовый файл выгрузки табличного источника (CSV/XLS/XLSX) — байты + имя + content-type.</summary>
public record SourceExportDto(byte[] Content, string FileName, string ContentType);

/// <summary>
/// Предпросмотр XPath/JSONPath-выражения (row-selector или колонки) в builder'е — без сохранения
/// источника. rowSelector — куда встать (context); expr — что вычислить относительно найденных
/// узлов (null — предпросмотр самого rowSelector: сколько узлов найдено + их авто-колонки).
/// </summary>
public record ExpressionPreviewDto(int MatchCount, IReadOnlyList<string> Samples);

/// <summary>Original blob stream + metadata for file download.</summary>
public record FileDownloadDto(Stream Stream, string ContentType, string FileName);

/// <summary>Вид группы в единой постраничной группировке ГОСТ-профиля (Document=0 — обязателен для
/// толерантной миграции старого формата, см. Infrastructure GostGroupingData).</summary>
public enum GostGroupKind
{
    Document = 0,
    Cover = 1,
    TitlePage = 2,
}

/// <summary>
/// Текущая группировка ВСЕХ страниц источника ГОСТ-профиля — для ручного редактора разбиения:
/// обложка/титул/документы как группы с <see cref="GostGroupKind"/>. PageCount — общее число
/// страниц исходного PDF (в т.ч. не вошедших ни в одну группу — допустимо, см. GetPagesAsync).
/// </summary>
public record GostGroupingDto(IReadOnlyList<GostGroupingGroupDto> Groups, bool ManuallyEdited, int PageCount);

/// <summary>Одна группа страниц. Для документа Code/Name как в реестре; для обложки/титула — null.
/// PageIndices — 0-based индексы исходного PDF. Tags — функциональные тэги документа (тип таблицы).</summary>
public record GostGroupingGroupDto(
    GostGroupKind Kind, string? Code, string? Name, IReadOnlyList<int> PageIndices,
    IReadOnlyList<string>? Tags = null);

// ── Input DTOs (assembled by the HTTP layer, free of ASP.NET types) ─────────────

public record UploadFileInput(
    byte[] Bytes, string FileName, string? ContentType, string? Name, string Scope, string? ScopeId);

public record ReplaceFileInput(byte[] Bytes, string FileName, string? ContentType, string? Name);

/// <summary>Явная относительная колонка XML-источника: имя + XPath-выражение относительно строки.</summary>
public record ColumnExprDto(string Name, string Expr);

public record CreateSourceInput(string Name, string SheetOrPath, IReadOnlyList<ColumnExprDto>? ColumnExpressions);

public record UpdateSourceInput(string Name, string SheetOrPath, IReadOnlyList<ColumnExprDto>? ColumnExpressions);

/// <summary>
/// Ручное создание PDF-источника: без SheetOrPath/ColumnExpressions (Extraction для PDF —
/// распознавание, а не XPath/JSONPath-builder, см. RecognizePdfSourceAsync). Tags — коды
/// функциональных тэгов (scope Dataset), напр. dataset.hasTitleBlock — применимы только к
/// профилю "gost-titleblock". Profile — "gost-titleblock" (по умолчанию, один источник,
/// реестр по страницам) или "invoice" (счёт на оплату — создаёт пару источников
/// шапка+товары, см. PdfProfiles в Infrastructure).
/// </summary>
public record CreatePdfSourceInput(string Name, IReadOnlyList<string>? Tags, string? Profile = null);

/// <summary>План распознавания источника: Background=true — операция долгая (GOST-набор), её ставят в
/// фоновую задачу; false — короткая (счёт/legacy), выполняется синхронно. Title — заголовок для
/// индикатора задач. null-результат метода = источник не найден. Кидает 409/400 при пред-валидации.</summary>
public record RecognizePlan(bool Background, string Title);

/// <summary>
/// Новая группировка ВСЕХ страниц — целиком заменяет предыдущую (ручную или автоматическую).
/// Группы всех видов (обложка/титул/документы). Пересекающиеся PageIndices между группами — ошибка
/// (400); страница может не входить ни в одну группу (тогда выпадает из реестров — допустимо).
/// </summary>
public record ApplyGroupingInput(IReadOnlyList<GostGroupingGroupDto> Groups);

/// <summary>Лёгкая правка обработки источника — не трогает файл/кэш схемы (в отличие от Update/CreateSourceInput).</summary>
public record SetSourceProcessingInput(object? RowFilter, object? ComputedColumns, object? SortSpec);

public record CreateProcessingTemplateInput(
    string Name, string? SheetOrPath, IReadOnlyList<ColumnExprDto>? ColumnExpressions,
    object? RowFilter, object? ComputedColumns, object? SortSpec);

public record UpdateProcessingTemplateInput(
    string Name, string? SheetOrPath, IReadOnlyList<ColumnExprDto>? ColumnExpressions,
    object? RowFilter, object? ComputedColumns, object? SortSpec);

/// <summary>Владелец — ровно одно из InstanceId/CommonDataEntryId задано.</summary>
public record CreateBindingInput(
    Guid? InstanceId, Guid? CommonDataEntryId, Guid SourceId, string? TargetFieldKey, Dictionary<string, string>? Mapping);

public record UpdateBindingInput(string? TargetFieldKey, Dictionary<string, string>? Mapping);

public record CreateTemplateInput(
    string Name, string? TargetFieldKey, Dictionary<string, string>? ColumnMappings);

public record UpdateTemplateInput(
    string Name, string? TargetFieldKey, Dictionary<string, string>? ColumnMappings, int? SortOrder);
