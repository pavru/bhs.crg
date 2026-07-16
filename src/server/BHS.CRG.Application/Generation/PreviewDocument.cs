using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Schema;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Templates;
using MediatR;

namespace BHS.CRG.Application.Generation;

/// <summary>Результат предпросмотра: либо PDF-байты, либо причина (нет шаблона / ошибка резолва/Typst).</summary>
public sealed record PreviewDocumentResult
{
    public byte[]? Pdf { get; init; }
    public bool NoTemplate { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<ResolutionDiagnostic>? Diagnostics { get; init; }

    public static PreviewDocumentResult Ok(byte[] pdf) => new() { Pdf = pdf };
    public static PreviewDocumentResult NoTpl() => new() { NoTemplate = true };
    public static PreviewDocumentResult Fail(string error, IReadOnlyList<ResolutionDiagnostic>? diags = null)
        => new() { Error = error, Diagnostics = diags };
}

/// <summary>
/// Живой предпросмотр документа (issue #193): рендерит ДЕФОЛТНЫЙ шаблон на ПЕРЕДАННЫХ (возможно
/// несохранённых) реквизитах в PDF. Read-only by contract: НЕ персистит файл, НЕ меняет статус,
/// НЕ пишет метаданные, НЕ шлёт уведомления — эфемерный рендер для панели предпросмотра.
/// </summary>
public sealed record PreviewDocumentQuery(Guid InstanceId, JsonDocument Requisites)
    : IRequest<PreviewDocumentResult>;

public class PreviewDocumentHandler(
    IRepository<DomainObject> instanceRepo,
    IRepository<Template> templateRepo,
    IRepository<DocumentType> docTypeRepo,
    IRepository<TypstUserLib> userLibRepo,
    IEntityResolver entityResolver,
    IDataSetResolver dataSetResolver,
    IQualityLinkResolver qualityLinkResolver,
    ITemplateAssetResolver templateAssetResolver,
    IDocumentGeneratorFactory generatorFactory
) : IRequestHandler<PreviewDocumentQuery, PreviewDocumentResult>
{
    private static List<Guid> ParseGuidList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<Guid>>(json) ?? []; } catch { return []; }
    }

    public async Task<PreviewDocumentResult> Handle(PreviewDocumentQuery q, CancellationToken ct)
    {
        var instance = await instanceRepo.GetByIdAsync(q.InstanceId, ct);
        if (instance is null) return PreviewDocumentResult.Fail("Документ не найден.");

        // Шаблон предпросмотра = как пользователь бы СГЕНЕРИРОВАЛ (issue #193 follow-up):
        // выбранный набор (ПЕРВЫЙ выбранный в порядке TemplateIds, активный) → одиночный выбор
        // (TemplateId) → дефолтный → первый активный. Зеркалит выбор в GenerateDocumentHandler.
        var candidates = (await templateRepo.FindAsync(t => t.DocumentTypeId == instance.CompositeTypeId, ct)).ToList();
        Template? bySelected = null;
        foreach (var id in ParseGuidList(instance.TemplateIds))
        {
            bySelected = candidates.FirstOrDefault(t => t.Id == id && t.IsActive);
            if (bySelected is not null) break;
        }
        var template = bySelected
            ?? (instance.TemplateId.HasValue ? candidates.FirstOrDefault(t => t.Id == instance.TemplateId.Value) : null)
            ?? candidates.FirstOrDefault(t => t.IsDefault && t.IsActive)
            ?? candidates.FirstOrDefault(t => t.IsActive);
        if (template is null) return PreviewDocumentResult.NoTpl();

        // Проекция генерации с ПОДМЕНЁННЫМИ реквизитами (несохранённые правки формы), без записи в БД.
        var view = DocumentView.From(instance) with { Requisites = q.Requisites };

        try
        {
            // ── Пайплайн контекста — ЗЕРКАЛИТ GenerateDocumentHandler (менять синхронно; там нет тестов). ──
            var allDocTypes = await docTypeRepo.GetAllAsync(ct);
            var diagnostics = new List<ResolutionDiagnostic>();
            var context = await entityResolver.ResolveAsync(view, ct);
            await dataSetResolver.InjectAsync(context, view, diagnostics, ct);
            await entityResolver.ApplyDefaultsAsync(context, view, ct);
            await entityResolver.ResolveEnumLabelsAsync(context, view, ct);
            await qualityLinkResolver.InjectAsync(context, view, ct);
            await entityResolver.ResolveContextRefsAsync(context, view.DocumentSetId, ct);
            ResolutionScanner.ScanLeftoverRefs(context, diagnostics);
            if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                return PreviewDocumentResult.Fail("Не все ссылки разрешены — предпросмотр недоступен.", diagnostics);

            var preamble = TypstPreambleBuilder.Build(allDocTypes);
            var lib = (await userLibRepo.GetAllAsync(ct)).FirstOrDefault();
            var userLib = lib is not null && !string.IsNullOrWhiteSpace(lib.Content) ? lib.Content : null;

            context.Set("params", TemplateParams.Effective(template.Parameters,
                TemplateParams.OverridesForTemplate(instance.TemplateParams, template.Id)));
            var assets = await templateAssetResolver.ResolveAsync(template.Id, instance.CompositeTypeId, ct);

            var generator = generatorFactory.Create(OutputFormat.Pdf);
            var request = new GenerationRequest(template.Content, OutputFormat.Pdf, context,
                TypeBlocksContent: string.IsNullOrEmpty(preamble) ? null : preamble,
                UserLibContent: userLib,
                ImageOptions: SchemaImageOptions.Collect(allDocTypes),
                TemplateAssets: assets);
            var bytes = await generator.GenerateAsync(request, ct);
            return PreviewDocumentResult.Ok(bytes);
        }
        catch (ResolutionValidationException ex)
        {
            return PreviewDocumentResult.Fail("Не все ссылки разрешены — предпросмотр недоступен.", ex.Diagnostics);
        }
        catch (Exception ex)
        {
            return PreviewDocumentResult.Fail(ex.Message);
        }
    }
}
