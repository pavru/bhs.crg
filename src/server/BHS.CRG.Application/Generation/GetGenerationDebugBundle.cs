using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Templates;
using MediatR;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Отладочный пакет — ZIP каталога компиляции, который записывает <c>TypstCompileDirectory</c> (ТЗ
/// CORE-21, CORE-22). Каталог тот же, что у генерации, записанный тем же кодом: распаковал →
/// <c>typst compile template.typ</c>; ключи компиляции и причины, по которым генерация этого документа
/// откажет, называет <c>README.txt</c> в пакете (когда есть что сказать).
/// </summary>
public record GenerationDebugBundle(byte[] Zip);

public record GetGenerationDebugBundleQuery(Guid InstanceId, DataAccess Access) : IRequest<GenerationDebugBundle?>;

public class GetGenerationDebugBundleHandler(
    IRepository<DomainObject> instanceRepo,
    IRepository<Template> templateRepo,
    IGenerationContextBuilder contextBuilder,
    IGenerationRequestBuilder requestBuilder,
    IDocumentGeneratorFactory generatorFactory
) : IRequestHandler<GetGenerationDebugBundleQuery, GenerationDebugBundle?>
{
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
            ?? throw new ConflictException($"No active template for DocumentType {instance.CompositeTypeId}");

        // Контекст и запрос — тем же кодом, что у генерации (D1, D2). Пакет не останавливается ни на
        // одной корзине: отлаживают как раз документы, которые генерация не выпустит, — а её ошибки
        // README называет (D5).
        var schemas = await contextBuilder.LoadSchemasAsync(ct);
        var built = await contextBuilder.BuildAsync(DocumentView.From(instance), q.Access, schemas, ct);
        var requests = await requestBuilder.PrepareAsync(
            built.Context, instance.CompositeTypeId, instance.TemplateParams, schemas, ct);
        var request = await requests.ForTemplateAsync(template, OutputFormat.Pdf, ct);

        var zip = await generatorFactory.Create(OutputFormat.Pdf).DebugBundleAsync(request, new BundleNotes(built.All), ct);
        return new GenerationDebugBundle(zip);
    }
}
