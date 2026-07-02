namespace BHS.CRG.Application.DataSets;

// ── Output DTOs (JSON shapes consumed by the SPA) ───────────────────────────────

public record DataSetSourceDto(
    Guid Id, Guid FileId, string Name, string SheetOrPath, string? ColumnExpressions,
    string CachedSchema, int CachedRowCount,
    object? RowFilter, object? ComputedColumns, object? SortSpec);

public record DataSetFileDto(
    Guid Id, string Name, string Format, string Scope, Guid? ScopeId,
    IReadOnlyList<DataSetSourceDto> Sources, DateTimeOffset CreatedAt);

public record BindingFileDto(Guid Id, string Name, string Format, string Scope, Guid? ScopeId);

public record BindingSourceDto(
    Guid Id, string Name, string SheetOrPath, string CachedSchema, int CachedRowCount, BindingFileDto? File);

/// <summary>Привязка — только Mapping. Filter/Transformation/Sort живут на DataSetSource.</summary>
public record DataSetBindingDto(
    Guid Id, Guid InstanceId, Guid SourceId, string? TargetFieldKey,
    Dictionary<string, string> Mapping, BindingSourceDto? Source);

/// <summary>Шаблон маппинга (для типа документа). Filter/Transformation/Sort — см. DataSetProcessingTemplateDto.</summary>
public record DataSetBindingTemplateDto(
    Guid Id, Guid DocumentTypeId, string Name, string? TargetFieldKey,
    Dictionary<string, string> ColumnMappings,
    int SortOrder, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Переиспользуемый рецепт обработки (Filter/Transformation/Sort) — не привязан к типу документа.</summary>
public record DataSetProcessingTemplateDto(
    Guid Id, string Name, object? RowFilter, object? ComputedColumns, object? SortSpec,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record BindingPreviewDto(
    Guid BindingId, string SourceName, string FileName, string Mode,
    string? TargetFieldKey, int TotalRows, object Data, string? Error);

public record SourcePreviewDto(
    IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows, int TotalRows);

/// <summary>
/// Предпросмотр XPath/JSONPath-выражения (row-selector или колонки) в builder'е — без сохранения
/// источника. rowSelector — куда встать (context); expr — что вычислить относительно найденных
/// узлов (null — предпросмотр самого rowSelector: сколько узлов найдено + их авто-колонки).
/// </summary>
public record ExpressionPreviewDto(int MatchCount, IReadOnlyList<string> Samples);

/// <summary>Original blob stream + metadata for file download.</summary>
public record FileDownloadDto(Stream Stream, string ContentType, string FileName);

// ── Input DTOs (assembled by the HTTP layer, free of ASP.NET types) ─────────────

public record UploadFileInput(
    byte[] Bytes, string FileName, string? ContentType, string? Name, string Scope, string? ScopeId);

public record ReplaceFileInput(byte[] Bytes, string FileName, string? ContentType, string? Name);

/// <summary>Явная относительная колонка XML-источника: имя + XPath-выражение относительно строки.</summary>
public record ColumnExprDto(string Name, string Expr);

public record CreateSourceInput(string Name, string SheetOrPath, IReadOnlyList<ColumnExprDto>? ColumnExpressions);

public record UpdateSourceInput(string Name, string SheetOrPath, IReadOnlyList<ColumnExprDto>? ColumnExpressions);

/// <summary>Лёгкая правка обработки источника — не трогает файл/кэш схемы (в отличие от Update/CreateSourceInput).</summary>
public record SetSourceProcessingInput(object? RowFilter, object? ComputedColumns, object? SortSpec);

public record CreateProcessingTemplateInput(string Name, object? RowFilter, object? ComputedColumns, object? SortSpec);

public record UpdateProcessingTemplateInput(string Name, object? RowFilter, object? ComputedColumns, object? SortSpec);

public record CreateBindingInput(
    Guid InstanceId, Guid SourceId, string? TargetFieldKey, Dictionary<string, string>? Mapping);

public record UpdateBindingInput(string? TargetFieldKey, Dictionary<string, string>? Mapping);

public record CreateTemplateInput(
    string Name, string? TargetFieldKey, Dictionary<string, string>? ColumnMappings);

public record UpdateTemplateInput(
    string Name, string? TargetFieldKey, Dictionary<string, string>? ColumnMappings, int? SortOrder);
