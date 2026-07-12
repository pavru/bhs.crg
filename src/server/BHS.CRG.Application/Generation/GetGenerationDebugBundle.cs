using System.Text.Encodings.Web;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Templates;
using MediatR;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Файлы, которые генератор кладёт во временный каталог Typst.
/// Выгружаются «как есть» для отладки шаблона во внешнем инструменте:
/// распаковал → <c>typst compile template.typ</c> (для шаблонов со шрифтовыми ассетами —
/// <c>typst compile template.typ output.pdf --font-path fonts</c>, см. README.txt в пакете).
/// </summary>
public record GenerationDebugBundle(
    string TemplateContent,
    string DataJson,
    string TypeBlocks,
    string UserLib,
    IReadOnlyDictionary<string, BHS.CRG.Application.Schema.ImageRenderOptions> ImageOptions,
    ResolvedTemplateAssets TemplateAssets);

public record GetGenerationDebugBundleQuery(Guid InstanceId) : IRequest<GenerationDebugBundle?>;

public class GetGenerationDebugBundleHandler(
    IRepository<DomainObject> instanceRepo,
    IRepository<Template> templateRepo,
    IRepository<DocumentType> docTypeRepo,
    IRepository<TypstUserLib> userLibRepo,
    IEntityResolver entityResolver,
    IDataSetResolver dataSetResolver,
    IQualityLinkResolver qualityLinkResolver,
    ITemplateAssetResolver templateAssetResolver
) : IRequestHandler<GetGenerationDebugBundleQuery, GenerationDebugBundle?>
{
    // Indented + нескрытая кириллица — для удобства чтения при отладке.
    // Для Typst json("data.json") отступы и экранирование значения не имеют:
    // распарсенные значения идентичны тем, что получает шаблон при генерации.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<GenerationDebugBundle?> Handle(GetGenerationDebugBundleQuery q, CancellationToken ct)
    {
        var instance = await instanceRepo.GetByIdAsync(q.InstanceId, ct);
        if (instance is null) return null;

        // Тот же выбор шаблона, что и в GenerateDocumentHandler.
        var candidates = await templateRepo.FindAsync(t => t.DocumentTypeId == instance.CompositeTypeId, ct);
        Template? template = null;
        if (instance.TemplateId.HasValue)
            template = await templateRepo.GetByIdAsync(instance.TemplateId.Value, ct);
        template ??= candidates.FirstOrDefault(t => t.IsDefault && t.IsActive)
            ?? candidates.FirstOrDefault(t => t.IsActive)
            ?? throw new InvalidOperationException($"No active template for DocumentType {instance.CompositeTypeId}");

        var allDocTypes = await docTypeRepo.GetAllAsync(ct);
        var view = DocumentView.From(instance);
        var context = await entityResolver.ResolveAsync(view, ct);
        await dataSetResolver.InjectAsync(context, view, null, ct);
        await entityResolver.ApplyDefaultsAsync(context, view, ct);
        await entityResolver.ResolveEnumLabelsAsync(context, view, ct);
        await qualityLinkResolver.InjectAsync(context, view, ct);
        // Тот же второй проход, что и при генерации — разрешаем $ref, добавленные наборами данных.
        await entityResolver.ResolveContextRefsAsync(context, view.DocumentSetId, ct);
        context.Set("params", TemplateParams.Effective(template.Parameters,
            TemplateParams.OverridesForTemplate(instance.TemplateParams, template.Id)));

        var dataJson = JsonSerializer.Serialize(context.Data, JsonOpts);
        var typeBlocks = TypstPreambleBuilder.Build(allDocTypes);

        var allLibs = await userLibRepo.GetAllAsync(ct);
        var userLib = allLibs.FirstOrDefault()?.Content ?? "";

        var imageOptions = BHS.CRG.Application.Schema.SchemaImageOptions.Collect(allDocTypes);
        var templateAssets = await templateAssetResolver.ResolveAsync(template.Id, instance.CompositeTypeId, ct);
        return new GenerationDebugBundle(template.Content, dataJson, typeBlocks, userLib, imageOptions, templateAssets);
    }
}
