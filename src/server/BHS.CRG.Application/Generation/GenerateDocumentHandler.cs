using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Domain.Templates;
using MediatR;

namespace BHS.CRG.Application.Generation;

public class GenerateDocumentHandler(
    IRepository<DocumentInstance> instanceRepo,
    IRepository<GeneratedFile> fileRepo,
    IRepository<Template> templateRepo,
    IRepository<DocumentType> docTypeRepo,
    IRepository<TypstUserLib> userLibRepo,
    IEntityResolver entityResolver,
    IDataSetResolver dataSetResolver,
    IQualityLinkResolver qualityLinkResolver,
    IDocumentGeneratorFactory generatorFactory,
    IBlobStorage blobStorage,
    IMetadataExtractor metadataExtractor,
    INotificationService notifications
) : IRequestHandler<GenerateDocumentCommand, GeneratedFile>
{
    public async Task<GeneratedFile> Handle(GenerateDocumentCommand cmd, CancellationToken ct)
    {
        var instance = await instanceRepo.GetByIdAsync(cmd.InstanceId, ct)
            ?? throw new KeyNotFoundException($"DocumentInstance {cmd.InstanceId} not found");

        instance.MarkGenerating();
        instanceRepo.Update(instance);
        await instanceRepo.SaveChangesAsync(ct);

        try
        {
            var candidates = await templateRepo.FindAsync(t => t.DocumentTypeId == instance.DocumentTypeId, ct);
            Template? template = null;
            if (instance.TemplateId.HasValue)
                template = await templateRepo.GetByIdAsync(instance.TemplateId.Value, ct);
            template ??= candidates.FirstOrDefault(t => t.IsDefault && t.IsActive)
                ?? candidates.FirstOrDefault(t => t.IsActive)
                ?? throw new InvalidOperationException($"No active template for DocumentType {instance.DocumentTypeId}");

            var allDocTypes = await docTypeRepo.GetAllAsync(ct);
            var diagnostics = new List<ResolutionDiagnostic>();
            var context = await entityResolver.ResolveAsync(instance, ct);
            await dataSetResolver.InjectAsync(context, instance, diagnostics, ct);
            // Подмешиваем документы качества по идентичности материала (артикул/наименование).
            await qualityLinkResolver.InjectAsync(context, instance, ct);
            // Наборы данных могли добавить ссылки на каталог ($ref) в составные поля —
            // разрешаем их вторым проходом (для уже разрешённых данных идемпотентно).
            await entityResolver.ResolveContextRefsAsync(context, instance.DocumentSetId, ct);
            // Проверка разрешения ссылок перед генерацией: оставшиеся $ref — ошибки,
            // при их наличии прерываем генерацию с диагностикой.
            ResolutionScanner.ScanLeftoverRefs(context, diagnostics);
            if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new ResolutionValidationException(diagnostics);

            string? typeBlocksContent = null;
            string? userLibContent = null;
            if (cmd.Format == OutputFormat.Pdf)
            {
                var preamble = TypstPreambleBuilder.Build(allDocTypes);
                if (!string.IsNullOrEmpty(preamble))
                    typeBlocksContent = preamble;

                var allLibs = await userLibRepo.GetAllAsync(ct);
                var lib = allLibs.FirstOrDefault();
                if (lib is not null && !string.IsNullOrWhiteSpace(lib.Content))
                    userLibContent = lib.Content;
            }

            var generator = generatorFactory.Create(cmd.Format);
            var request = new GenerationRequest(instance, template.Content, cmd.Format, context,
                template.PageSize, template.PageOrientation,
                template.MarginTop, template.MarginRight, template.MarginBottom, template.MarginLeft,
                TypeBlocksContent: typeBlocksContent, UserLibContent: userLibContent,
                ImageOptions: SchemaImageOptions.Collect(allDocTypes));
            var bytes = await generator.GenerateAsync(request, ct);

            // ── Обратная запись метаданных в реквизиты ───────────────────────
            var docType = allDocTypes.FirstOrDefault(dt => dt.Id == instance.DocumentTypeId);
            if (docType is not null)
            {
                var taggedFields = SchemaTags.TaggedFields(docType, allDocTypes);
                if (taggedFields.Count > 0)
                {
                    var meta = metadataExtractor.Extract(bytes, cmd.Format, cmd.GeneratedBy);
                    var patchedRequisites = SchemaTags.PatchMetadata(instance.Requisites, taggedFields, meta);
                    instance.UpdateRequisites(patchedRequisites);
                }
            }

            var ext = cmd.Format == OutputFormat.Pdf ? "pdf" : "docx";
            var contentType = cmd.Format == OutputFormat.Pdf
                ? "application/pdf"
                : "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

            await using var ms = new MemoryStream(bytes);
            var blobPath = await blobStorage.UploadAsync($"{instance.Id}.{ext}", ms, contentType, ct);

            var generatedFile = instance.AddGeneratedFile(cmd.Format, blobPath);
            await fileRepo.AddAsync(generatedFile, ct);
            await instanceRepo.SaveChangesAsync(ct);

            var ext2 = cmd.Format == OutputFormat.Pdf ? "pdf" : "docx";
            await notifications.PublishAsync(NotificationSeverity.Info, "Документ сгенерирован",
                $"«{instance.Name}» — {cmd.Format}.", "Генерация",
                userId: cmd.UserId,
                linkUrl: $"/api/generate/download/{instance.Id}/{ext2}",
                linkLabel: $"Скачать {ext2.ToUpperInvariant()}",
                ct: ct);

            return generatedFile;
        }
        catch (Exception ex)
        {
            instance.MarkFailed();
            instanceRepo.Update(instance);
            await instanceRepo.SaveChangesAsync(ct);
            await notifications.PublishAsync(NotificationSeverity.Error, "Ошибка генерации",
                $"«{instance.Name}»: {ex.Message}", "Генерация", userId: cmd.UserId, ct: ct);
            throw;
        }
    }
}
