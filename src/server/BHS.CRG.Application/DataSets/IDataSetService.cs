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

    /// <summary>Ручное создание источника (для XML — единственный способ, авто-детект не используется).</summary>
    Task<DataSetSourceDto> CreateSourceAsync(Guid fileId, CreateSourceInput input, CancellationToken ct);
    Task<DataSetSourceDto?> UpdateSourceAsync(Guid sourceId, UpdateSourceInput input, CancellationToken ct);
    Task<bool> DeleteSourceAsync(Guid sourceId, CancellationToken ct);

    /// <summary>Копия источника (тот же locator/колонки/Filter/Transformation/Sort) на том же файле — доступно для любого формата.</summary>
    Task<DataSetSourceDto?> DuplicateSourceAsync(Guid sourceId, CancellationToken ct);

    /// <summary>
    /// Ручное создание PDF-источника (без Extraction через builder — см. RecognizePdfSourceAsync).
    /// </summary>
    Task<DataSetSourceDto> CreatePdfSourceAsync(Guid fileId, CreatePdfSourceInput input, CancellationToken ct);

    /// <summary>
    /// Распознаёт основную надпись каждой страницы PDF (по одной странице за вызов, через
    /// существующий IDocumentRecognizer) и кэширует результат на источнике (DataSetSource.CachedData).
    /// Дорогая/небыстрая операция — запускается явным действием пользователя, не при каждом
    /// preview/generation вызове.
    /// </summary>
    Task<DataSetSourceDto?> RecognizePdfSourceAsync(Guid sourceId, CancellationToken ct);

    /// <summary>Пути XML-записей внутри ZIP-файла — для выбора при ручном создании источника.</summary>
    Task<IReadOnlyList<string>> ListZipXmlEntriesAsync(Guid fileId, CancellationToken ct);

    /// <summary>Предпросмотр XPath/JSONPath-выражения в builder'е — без сохранения источника.</summary>
    Task<ExpressionPreviewDto> PreviewExpressionAsync(Guid fileId, string rowSelector, string? expr, CancellationToken ct);

    /// <summary>Обработка (Filter/Transformation/Sort) источника — лёгкая правка, файл не трогает.</summary>
    Task<DataSetSourceDto?> SetSourceProcessingAsync(Guid sourceId, SetSourceProcessingInput input, CancellationToken ct);

    /// <summary>
    /// Применить шаблон (Extraction, если задан в шаблоне, + Filter/Transformation/Sort) к
    /// источнику — copy-on-apply, единожды. Extraction в шаблоне триггерит пере-парсинг файла
    /// (как Update/CreateSourceInput), в отличие от SetSourceProcessingAsync.
    /// </summary>
    Task<DataSetSourceDto?> ApplyProcessingTemplateAsync(Guid sourceId, Guid templateId, CancellationToken ct);

    // ── Processing templates (переиспользуемые рецепты Extraction + Filter/Transformation/Sort) ────
    Task<IReadOnlyList<DataSetProcessingTemplateDto>> ListProcessingTemplatesAsync(CancellationToken ct);
    Task<DataSetProcessingTemplateDto> CreateProcessingTemplateAsync(CreateProcessingTemplateInput input, CancellationToken ct);
    Task<DataSetProcessingTemplateDto?> UpdateProcessingTemplateAsync(Guid id, UpdateProcessingTemplateInput input, CancellationToken ct);
    Task<bool> DeleteProcessingTemplateAsync(Guid id, CancellationToken ct);

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
