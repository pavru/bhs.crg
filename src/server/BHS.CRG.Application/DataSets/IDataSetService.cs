namespace BHS.CRG.Application.DataSets;

/// <summary>
/// Application-level operations for data sets, bindings and binding templates.
/// HTTP endpoints stay thin and delegate here; all parsing/mapping/preview logic lives in the impl.
/// Throws <see cref="KeyNotFoundException"/> for missing entities and
/// <see cref="ArgumentException"/> for invalid input (mapped to 404 / 400 by the global handler).
/// </summary>
public interface IDataSetService
{
    // ── Files ───────────────────────────────────────────────────────────────────
    Task<IReadOnlyList<DataSetFileDto>> ListFilesAsync(string? scope, Guid? scopeId, CancellationToken ct);
    Task<IReadOnlyList<DataSetFileDto>> ListAvailableFilesAsync(Guid setId, CancellationToken ct);
    Task<DataSetFileDto> UploadFileAsync(UploadFileInput input, CancellationToken ct);
    Task<DataSetFileDto?> ReplaceFileAsync(Guid id, ReplaceFileInput input, CancellationToken ct);
    Task<FileDownloadDto?> DownloadFileAsync(Guid id, CancellationToken ct);
    Task<bool> DeleteFileAsync(Guid id, CancellationToken ct);

    // ── Sources ─────────────────────────────────────────────────────────────────
    Task<IReadOnlyList<DataSetSourceDto>> ListSourcesAsync(Guid fileId, CancellationToken ct);
    Task<SourcePreviewDto?> PreviewSourceAsync(Guid sourceId, int maxRows, CancellationToken ct);
    Task<Dictionary<string, string>?> AutoMapAsync(Guid sourceId, IReadOnlyList<FieldInfo> fields, CancellationToken ct);

    // ── Bindings ────────────────────────────────────────────────────────────────
    Task<IReadOnlyList<DataSetBindingDto>> ListBindingsAsync(Guid instanceId, CancellationToken ct);
    Task<DataSetBindingDto?> CreateBindingAsync(CreateBindingInput input, CancellationToken ct);
    Task<DataSetBindingDto?> UpdateBindingAsync(Guid id, UpdateBindingInput input, CancellationToken ct);
    Task<bool> DeleteBindingAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<BindingPreviewDto>> PreviewBindingsAsync(Guid instanceId, CancellationToken ct);

    // ── Binding templates ─────────────────────────────────────────────────────────
    Task<IReadOnlyList<DataSetBindingTemplateDto>> ListTemplatesAsync(Guid docTypeId, CancellationToken ct);
    Task<DataSetBindingTemplateDto> CreateTemplateAsync(Guid docTypeId, CreateTemplateInput input, CancellationToken ct);
    Task<DataSetBindingTemplateDto?> UpdateTemplateAsync(Guid docTypeId, Guid id, UpdateTemplateInput input, CancellationToken ct);
    Task<bool> DeleteTemplateAsync(Guid docTypeId, Guid id, CancellationToken ct);
}
