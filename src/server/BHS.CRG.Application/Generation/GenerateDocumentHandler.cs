using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Application.Schema;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Templates;
using MediatR;

namespace BHS.CRG.Application.Generation;

public class GenerateDocumentHandler(
    IRepository<DomainObject> instanceRepo,
    IRepository<GeneratedFile> fileRepo,
    IRepository<Template> templateRepo,
    IRepository<DocumentType> docTypeRepo,
    IRepository<TypstUserLib> userLibRepo,
    IEntityResolver entityResolver,
    IDataSetResolver dataSetResolver,
    IQualityLinkResolver qualityLinkResolver,
    ITemplateAssetResolver templateAssetResolver,
    IDocumentGeneratorFactory generatorFactory,
    IBlobStorage blobStorage,
    IMetadataExtractor metadataExtractor,
    INotificationService notifications
) : IRequestHandler<GenerateDocumentCommand, IReadOnlyList<GeneratedFile>>
{
    private static List<Guid> ParseGuidList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<Guid>>(json) ?? []; } catch { return []; }
    }

    public async Task<IReadOnlyList<GeneratedFile>> Handle(GenerateDocumentCommand cmd, CancellationToken ct)
    {
        var instance = await instanceRepo.GetByIdAsync(cmd.InstanceId, ct)
            ?? throw new KeyNotFoundException($"DocumentInstance {cmd.InstanceId} not found");

        instance.MarkGenerating();
        instanceRepo.Update(instance);
        await instanceRepo.SaveChangesAsync(ct);

        try
        {
            var candidates = (await templateRepo.FindAsync(t => t.DocumentTypeId == instance.CompositeTypeId, ct)).ToList();

            // Список шаблонов для генерации: выбранный НАБОР (мульти-шаблоны) или один эффективный
            // (явно выбранный → по умолчанию → первый активный) — как раньше при пустом наборе.
            var selectedIds = ParseGuidList(instance.TemplateIds);
            List<Template> templates;
            if (selectedIds.Count > 0)
            {
                templates = candidates.Where(t => selectedIds.Contains(t.Id) && t.IsActive).ToList();
                if (templates.Count == 0)
                    throw new InvalidOperationException("Ни один из выбранных шаблонов не активен.");
            }
            else
            {
                var single = (instance.TemplateId.HasValue ? candidates.FirstOrDefault(t => t.Id == instance.TemplateId.Value) : null)
                    ?? candidates.FirstOrDefault(t => t.IsDefault && t.IsActive)
                    ?? candidates.FirstOrDefault(t => t.IsActive)
                    ?? throw new InvalidOperationException($"No active template for DocumentType {instance.CompositeTypeId}");
                templates = [single];
            }

            var allDocTypes = await docTypeRepo.GetAllAsync(ct);
            var diagnostics = new List<ResolutionDiagnostic>();
            var view = DocumentView.From(instance);
            var context = await entityResolver.ResolveAsync(view, ct);
            await dataSetResolver.InjectAsync(context, view, diagnostics, ct);
            // Значения по умолчанию из схемы типа (issue #53) — для полей, оставшихся без значения
            // после реквизитов инстанса и биндингов (самый низкий приоритет).
            await entityResolver.ApplyDefaultsAsync(context, view, ct);
            // Enum-поля: код → отображаемое имя (issue #59) — иначе в PDF попадёт сырой код.
            await entityResolver.ResolveEnumLabelsAsync(context, view, ct);
            // Подмешиваем документы качества по идентичности материала (артикул/наименование).
            await qualityLinkResolver.InjectAsync(context, view, ct);
            // Наборы данных могли добавить ссылки на каталог ($ref) в составные поля —
            // разрешаем их вторым проходом (для уже разрешённых данных идемпотентно).
            await entityResolver.ResolveContextRefsAsync(context, view.DocumentSetId, ct);
            // Проверка разрешения ссылок перед генерацией: оставшиеся $ref — ошибки,
            // при их наличии прерываем генерацию с диагностикой.
            ResolutionScanner.ScanLeftoverRefs(context, diagnostics);
            // Полнота обязательных (issue #296, фаза 0b): проверяем ПОСЛЕ полного резолва (реквизиты +
            // привязки + база + дефолты) — обязательность = инвариант генерации, а не сохранения.
            ResolutionScanner.ScanMissingRequired(context,
                DocumentTypeSchemaReader.EffectiveFields(instance.CompositeTypeId, allDocTypes.ToDictionary(t => t.Id)), diagnostics);
            if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new ResolutionValidationException(diagnostics);

            string? typeBlocksContent = null;
            string? userLibContent = null;
            if (cmd.Format == OutputFormat.Pdf)
            {
                var preamble = TypstPreambleBuilder.Build(allDocTypes);
                if (!string.IsNullOrEmpty(preamble))
                    typeBlocksContent = preamble;

                var lib = (await userLibRepo.GetAllAsync(ct)).FirstOrDefault();
                if (lib is not null && !string.IsNullOrWhiteSpace(lib.Content))
                    userLibContent = lib.Content;
            }

            var ext = cmd.Format == OutputFormat.Pdf ? "pdf" : "docx";
            var contentType = cmd.Format == OutputFormat.Pdf
                ? "application/pdf"
                : "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
            var docType = allDocTypes.FirstOrDefault(dt => dt.Id == instance.CompositeTypeId);

            // По PDF на каждый выбранный шаблон. Контекст (реквизиты/наборы/каталог) общий — строится
            // один раз выше; на шаблон меняются только params, содержимое и настройки страницы.
            var generated = new List<GeneratedFile>();
            foreach (var template in templates)
            {
                context.Set("params", TemplateParams.Effective(template.Parameters,
                    TemplateParams.OverridesForTemplate(instance.TemplateParams, template.Id)));

                // Ассеты шаблона (issue #62) — только для PDF (как typeBlocks/userLib выше).
                var templateAssets = cmd.Format == OutputFormat.Pdf
                    ? await templateAssetResolver.ResolveAsync(template.Id, instance.CompositeTypeId, ct)
                    : null;

                var generator = generatorFactory.Create(cmd.Format);
                var request = new GenerationRequest(template.Content, cmd.Format, context,
                    TypeBlocksContent: typeBlocksContent, UserLibContent: userLibContent,
                    TemplateAssets: templateAssets);
                var bytes = await generator.GenerateAsync(request, ct);

                // Обратная запись метаданных — только с ПЕРВОГО файла (репрезентативно: число листов и т.п.).
                if (generated.Count == 0 && docType is not null)
                {
                    var taggedFields = SchemaTags.TaggedFields(docType, allDocTypes);
                    if (taggedFields.Count > 0)
                    {
                        var meta = metadataExtractor.Extract(bytes, isPdf: cmd.Format == OutputFormat.Pdf, cmd.GeneratedBy);
                        instance.SetData(SchemaTags.PatchMetadata(instance.Data, taggedFields, meta));
                    }
                }

                await using var ms = new MemoryStream(bytes);
                var blobPath = await blobStorage.UploadAsync($"{instance.Id}-{template.Id}.{ext}", ms, contentType, ct);
                var gf = instance.AddGeneratedFile(cmd.Format, blobPath, template.Id);
                await fileRepo.AddAsync(gf, ct);
                generated.Add(gf);
            }

            await instanceRepo.SaveChangesAsync(ct);

            var first = generated[0];
            await notifications.PublishAsync(NotificationSeverity.Info, "Документ сгенерирован",
                generated.Count == 1 ? $"«{instance.DisplayName}» — {cmd.Format}." : $"«{instance.DisplayName}» — сгенерировано файлов: {generated.Count}.",
                "Генерация", userId: cmd.UserId,
                linkUrl: $"/api/generate/download/{instance.Id}/{first.TemplateId}/{ext}",
                linkLabel: generated.Count == 1 ? $"Скачать {ext.ToUpperInvariant()}" : "Открыть",
                ct: ct);

            return generated;
        }
        catch (Exception ex)
        {
            instance.MarkFailed();
            instanceRepo.Update(instance);
            await instanceRepo.SaveChangesAsync(ct);
            await notifications.PublishAsync(NotificationSeverity.Error, "Ошибка генерации",
                $"«{instance.DisplayName}»: {ex.Message}", "Генерация", userId: cmd.UserId, ct: ct);
            throw;
        }
    }
}
