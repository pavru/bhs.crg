using BHS.CRG.Application.DataSets;
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
    // Блоки типов (issue #772): агрегатор + модули по своим путям. Одной строкой их больше нет —
    // без модулей внешняя компиляция упала бы на первом же импорте агрегатора.
    IReadOnlyList<TypstBlockFile> TypeBlocks,
    string UserLib,
    ResolvedTemplateAssets TemplateAssets,
    // Дерево библиотеки (issue #473). Без него бандл ломается на первом же вложенном импорте —
    // точка входа реэкспортирует файлы, которых в пакете бы не оказалось.
    IReadOnlyList<UserLibFile> UserLibFiles);

public record GetGenerationDebugBundleQuery(Guid InstanceId, DataAccess Access) : IRequest<GenerationDebugBundle?>;

public class GetGenerationDebugBundleHandler(
    IRepository<DomainObject> instanceRepo,
    IRepository<Template> templateRepo,
    IGenerationContextBuilder contextBuilder,
    IGenerationRequestBuilder requestBuilder
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
            ?? throw new ConflictException($"No active template for DocumentType {instance.CompositeTypeId}");

        // Контекст и запрос — тем же кодом, что у генерации (D1, D2). Пакет не останавливается ни на
        // одной корзине: отлаживают как раз документы, которые генерация не выпустит.
        var schemas = await contextBuilder.LoadSchemasAsync(ct);
        var built = await contextBuilder.BuildAsync(DocumentView.From(instance), q.Access, schemas, ct);
        var requests = await requestBuilder.PrepareAsync(
            built.Context, instance.CompositeTypeId, instance.TemplateParams, schemas, ct);
        var request = await requests.ForTemplateAsync(template, OutputFormat.Pdf, ct);

        return new GenerationDebugBundle(request.TemplateContent,
            JsonSerializer.Serialize(request.Context.Data, JsonOpts), request.TypeBlocksFiles ?? [],
            request.UserLibContent ?? "", request.TemplateAssets ?? ResolvedTemplateAssets.Empty,
            request.UserLibFiles ?? []);
    }
}
