namespace BHS.CRG.Application.DataSets;

// ── Output DTOs (JSON shapes consumed by the SPA) ───────────────────────────────

public record DataSetSourceDto(
    Guid Id, Guid FileId, string Name, string SheetOrPath, string CachedSchema, int CachedRowCount);

public record DataSetFileDto(
    Guid Id, string Name, string Format, string Scope, Guid? ScopeId,
    IReadOnlyList<DataSetSourceDto> Sources, DateTimeOffset CreatedAt);

public record BindingFileDto(Guid Id, string Name, string Format, string Scope, Guid? ScopeId);

public record BindingSourceDto(
    Guid Id, string Name, string SheetOrPath, string CachedSchema, int CachedRowCount, BindingFileDto? File);

public record DataSetBindingDto(
    Guid Id, Guid InstanceId, Guid SourceId, string? TargetFieldKey,
    Dictionary<string, string> Mapping, object? RowFilter, object? ComputedColumns,
    BindingSourceDto? Source);

public record DataSetBindingTemplateDto(
    Guid Id, Guid DocumentTypeId, string Name, string? TargetFieldKey,
    Dictionary<string, string> ColumnMappings, object? RowFilter, object? ComputedColumns,
    int SortOrder, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record BindingPreviewDto(
    Guid BindingId, string SourceName, string FileName, string Mode,
    string? TargetFieldKey, int TotalRows, object Data, string? Error);

public record SourcePreviewDto(
    IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows, int TotalRows);

/// <summary>Original blob stream + metadata for file download.</summary>
public record FileDownloadDto(Stream Stream, string ContentType, string FileName);

// ── Input DTOs (assembled by the HTTP layer, free of ASP.NET types) ─────────────

public record UploadFileInput(
    byte[] Bytes, string FileName, string? ContentType, string? Name, string Scope, string? ScopeId);

public record ReplaceFileInput(byte[] Bytes, string FileName, string? ContentType, string? Name);

public record CreateBindingInput(
    Guid InstanceId, Guid SourceId, string? TargetFieldKey,
    Dictionary<string, string>? Mapping, object? RowFilter, object? ComputedColumns);

public record UpdateBindingInput(
    string? TargetFieldKey, Dictionary<string, string>? Mapping, object? RowFilter, object? ComputedColumns);

public record CreateTemplateInput(
    string Name, string? TargetFieldKey, Dictionary<string, string>? ColumnMappings,
    object? RowFilter, object? ComputedColumns);

public record UpdateTemplateInput(
    string Name, string? TargetFieldKey, Dictionary<string, string>? ColumnMappings,
    object? RowFilter, object? ComputedColumns, int? SortOrder);
