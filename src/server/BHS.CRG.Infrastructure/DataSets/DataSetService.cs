using BHS.CRG.Application.DataSets;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Фасад <see cref="IDataSetService"/>: единый публичный контракт для Application/Api-границы,
/// делегирующий каждый вызов в специализированный под-сервис по агрегату (Files/Sources/Bindings/
/// шаблоны обработки/шаблоны привязок/PDF-распознавание). Декомпозиция описана в архитектурном
/// отчёте («Предложение 3»): реальная граница — по поведению/зависимостям, а не по имени сущности.
/// Application видит по-прежнему один <see cref="IDataSetService"/>, внутренняя структура — детали Infrastructure.
/// </summary>
public class DataSetService(
    DataSetFileService files,
    DataSetSourceService sources,
    DataSetBindingService bindings,
    DataSetProcessingTemplateService processingTemplates,
    DataSetBindingTemplateService bindingTemplates,
    DataSetPdfRecognitionService pdfRecognition
) : IDataSetService
{
    // ── Files ─────────────────────────────────────────────────────────────────
    public Task<IReadOnlyList<DataSetFileDto>> ListFilesAsync(string? scope, Guid? scopeId, CancellationToken ct) =>
        files.ListFilesAsync(scope, scopeId, ct);
    public Task<IReadOnlyList<DataSetFileDto>> ListAvailableFilesAsync(Guid setId, CancellationToken ct) =>
        files.ListAvailableFilesAsync(setId, ct);
    public Task<DataSetFileDto> UploadFileAsync(UploadFileInput input, CancellationToken ct) =>
        files.UploadFileAsync(input, ct);
    public Task<DataSetFileDto?> ReplaceFileAsync(Guid id, ReplaceFileInput input, CancellationToken ct) =>
        files.ReplaceFileAsync(id, input, ct);
    public Task<FileDownloadDto?> DownloadFileAsync(Guid id, CancellationToken ct) =>
        files.DownloadFileAsync(id, ct);
    public Task<bool> DeleteFileAsync(Guid id, CancellationToken ct) =>
        files.DeleteFileAsync(id, ct);

    // ── Sources ───────────────────────────────────────────────────────────────
    public Task<IReadOnlyList<DataSetSourceDto>> ListSourcesAsync(Guid fileId, CancellationToken ct) =>
        sources.ListSourcesAsync(fileId, ct);
    public Task<SourcePreviewDto?> PreviewSourceAsync(Guid sourceId, int maxRows, CancellationToken ct) =>
        sources.PreviewSourceAsync(sourceId, maxRows, ct);
    public Task<SourceExportDto?> ExportSourceAsync(Guid sourceId, string? format, CancellationToken ct) =>
        sources.ExportSourceAsync(sourceId, format, ct);
    public Task<Dictionary<string, string>?> AutoMapAsync(Guid sourceId, IReadOnlyList<FieldInfo> fields, CancellationToken ct) =>
        sources.AutoMapAsync(sourceId, fields, ct);
    public Task<DataSetSourceDto> CreateSourceAsync(Guid fileId, CreateSourceInput input, CancellationToken ct) =>
        sources.CreateSourceAsync(fileId, input, ct);
    public Task<DataSetSourceDto?> UpdateSourceAsync(Guid sourceId, UpdateSourceInput input, CancellationToken ct) =>
        sources.UpdateSourceAsync(sourceId, input, ct);
    public Task<bool> DeleteSourceAsync(Guid sourceId, CancellationToken ct) =>
        sources.DeleteSourceAsync(sourceId, ct);
    public Task<DataSetSourceDto?> DuplicateSourceAsync(Guid sourceId, CancellationToken ct) =>
        sources.DuplicateSourceAsync(sourceId, ct);
    public Task<IReadOnlyList<string>> ListZipXmlEntriesAsync(Guid fileId, CancellationToken ct) =>
        sources.ListZipXmlEntriesAsync(fileId, ct);
    public Task<ExpressionPreviewDto> PreviewExpressionAsync(Guid fileId, string rowSelector, string? expr, CancellationToken ct) =>
        sources.PreviewExpressionAsync(fileId, rowSelector, expr, ct);
    public Task<DataSetSourceDto?> SetSourceProcessingAsync(Guid sourceId, SetSourceProcessingInput input, CancellationToken ct) =>
        sources.SetSourceProcessingAsync(sourceId, input, ct);
    public Task<DataSetSourceDto?> ApplyProcessingTemplateAsync(Guid sourceId, Guid templateId, CancellationToken ct) =>
        sources.ApplyProcessingTemplateAsync(sourceId, templateId, ct);

    // ── PDF-распознавание ───────────────────────────────────────────────────────
    public Task<DataSetSourceDto> CreatePdfSourceAsync(Guid fileId, CreatePdfSourceInput input, CancellationToken ct) =>
        pdfRecognition.CreatePdfSourceAsync(fileId, input, ct);
    public Task<DataSetSourceDto?> RecognizePdfSourceAsync(Guid sourceId, bool confirm, CancellationToken ct) =>
        pdfRecognition.RecognizePdfSourceAsync(sourceId, confirm, ct);
    public Task<RecognizePlan?> PlanRecognitionAsync(Guid sourceId, bool confirm, CancellationToken ct) =>
        pdfRecognition.PlanRecognitionAsync(sourceId, confirm, ct);
    public Task<GostGroupingDto?> GetPagesAsync(Guid sourceId, CancellationToken ct) =>
        pdfRecognition.GetPagesAsync(sourceId, ct);
    public Task<byte[]?> GetPageThumbnailAsync(Guid sourceId, int pageIndex, CancellationToken ct, int dpi = 96) =>
        pdfRecognition.GetPageThumbnailAsync(sourceId, pageIndex, ct, dpi);
    public Task<GostGroupingDto?> ApplyGroupingAsync(Guid sourceId, ApplyGroupingInput input, CancellationToken ct) =>
        pdfRecognition.ApplyGroupingAsync(sourceId, input, ct);
    public Task<GostGroupingDto?> SetDocumentTagsAsync(Guid documentsSourceId, int firstPageIndex, IReadOnlyList<string> tags, CancellationToken ct) =>
        pdfRecognition.SetDocumentTagsAsync(documentsSourceId, firstPageIndex, tags, ct);
    public Task<DataSetSourceDto?> RecognizeDocumentTableAsync(Guid documentsSourceId, int firstPageIndex, CancellationToken ct) =>
        pdfRecognition.RecognizeDocumentTableAsync(documentsSourceId, firstPageIndex, ct);

    // ── Processing templates ────────────────────────────────────────────────────
    public Task<IReadOnlyList<DataSetProcessingTemplateDto>> ListProcessingTemplatesAsync(CancellationToken ct) =>
        processingTemplates.ListAsync(ct);
    public Task<DataSetProcessingTemplateDto> CreateProcessingTemplateAsync(CreateProcessingTemplateInput input, CancellationToken ct) =>
        processingTemplates.CreateAsync(input, ct);
    public Task<DataSetProcessingTemplateDto?> UpdateProcessingTemplateAsync(Guid id, UpdateProcessingTemplateInput input, CancellationToken ct) =>
        processingTemplates.UpdateAsync(id, input, ct);
    public Task<bool> DeleteProcessingTemplateAsync(Guid id, CancellationToken ct) =>
        processingTemplates.DeleteAsync(id, ct);

    // ── Bindings ──────────────────────────────────────────────────────────────
    public Task<IReadOnlyList<DataSetBindingDto>> ListBindingsAsync(Guid? instanceId, Guid? commonDataEntryId, CancellationToken ct) =>
        bindings.ListBindingsAsync(instanceId, commonDataEntryId, ct);
    public Task<DataSetBindingDto?> CreateBindingAsync(CreateBindingInput input, CancellationToken ct) =>
        bindings.CreateBindingAsync(input, ct);
    public Task<DataSetBindingDto?> UpdateBindingAsync(Guid id, UpdateBindingInput input, CancellationToken ct) =>
        bindings.UpdateBindingAsync(id, input, ct);
    public Task<bool> DeleteBindingAsync(Guid id, CancellationToken ct) =>
        bindings.DeleteBindingAsync(id, ct);
    public Task<IReadOnlyList<BindingPreviewDto>> PreviewBindingsAsync(Guid? instanceId, Guid? commonDataEntryId, CancellationToken ct) =>
        bindings.PreviewBindingsAsync(instanceId, commonDataEntryId, ct);

    // ── Binding templates ───────────────────────────────────────────────────────
    public Task<IReadOnlyList<DataSetBindingTemplateDto>> ListTemplatesAsync(Guid docTypeId, CancellationToken ct) =>
        bindingTemplates.ListAsync(docTypeId, ct);
    public Task<DataSetBindingTemplateDto> CreateTemplateAsync(Guid docTypeId, CreateTemplateInput input, CancellationToken ct) =>
        bindingTemplates.CreateAsync(docTypeId, input, ct);
    public Task<DataSetBindingTemplateDto?> UpdateTemplateAsync(Guid docTypeId, Guid id, UpdateTemplateInput input, CancellationToken ct) =>
        bindingTemplates.UpdateAsync(docTypeId, id, input, ct);
    public Task<bool> DeleteTemplateAsync(Guid docTypeId, Guid id, CancellationToken ct) =>
        bindingTemplates.DeleteAsync(docTypeId, id, ct);
}
