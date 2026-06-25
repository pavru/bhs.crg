using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Documents;
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
    IDocumentGeneratorFactory generatorFactory,
    IBlobStorage blobStorage,
    IMetadataExtractor metadataExtractor
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
            var context = await entityResolver.ResolveAsync(instance, ct);
            await dataSetResolver.InjectAsync(context, instance, ct);

            string? typeBlocksContent = null;
            string? userLibContent = null;
            if (cmd.Format == OutputFormat.Pdf)
            {
                var preamble = BuildTypstPreamble(allDocTypes);
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
                TypeBlocksContent: typeBlocksContent, UserLibContent: userLibContent);
            var bytes = await generator.GenerateAsync(request, ct);

            // ── Обратная запись метаданных в реквизиты ───────────────────────
            var docType = allDocTypes.FirstOrDefault(dt => dt.Id == instance.DocumentTypeId);
            if (docType is not null)
            {
                var taggedFields = DocumentMetaTagHelper.GetTaggedFields(docType, allDocTypes);
                if (taggedFields.Count > 0)
                {
                    var meta = metadataExtractor.Extract(bytes, cmd.Format, cmd.GeneratedBy);
                    var patchedRequisites = DocumentMetaTagHelper.PatchMetadata(instance.Requisites, taggedFields, meta);
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

            return generatedFile;
        }
        catch
        {
            instance.MarkFailed();
            instanceRepo.Update(instance);
            await instanceRepo.SaveChangesAsync(ct);
            throw;
        }
    }

    private static string BuildTypstPreamble(IEnumerable<DocumentType> compositeTypes)
    {
        var sb = new StringBuilder();
        foreach (var ct in compositeTypes)
        {
            if (!ct.Schema.RootElement.TryGetProperty("typstRenders", out var renders)) continue;
            if (renders.ValueKind != JsonValueKind.Array) continue;
            foreach (var render in renders.EnumerateArray())
            {
                var fnName = render.TryGetProperty("fnName", out var fn) ? fn.GetString() : null;
                var block = render.TryGetProperty("block", out var bl) ? bl.GetString() : null;
                if (string.IsNullOrWhiteSpace(fnName) || string.IsNullOrWhiteSpace(block)) continue;
                sb.AppendLine($"#let {fnName}(it) = {block}");
            }
        }
        return sb.ToString();
    }
}
