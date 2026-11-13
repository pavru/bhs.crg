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

        // Пакет отлаживает то, что выпустит генерация: первый из её шаблонов, общим правилом
        // (TemplateChoice, [ID-3]). Прежде пакет не читал выбранный набор и брал «первый активный» без
        // порядка — после восстановления копии он отдавал другой шаблон (issue #1401).
        var candidates = await templateRepo.FindAsync(t => t.DocumentTypeId == instance.CompositeTypeId, ct);
        var chosen = TemplateChoice.For(instance, candidates);
        if (chosen.Count == 0)
            throw new ConflictException($"No active template for DocumentType {instance.CompositeTypeId}");
        var template = chosen[0];

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
