using BHS.CRG.Application.DataSets;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Schema;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Common;
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
public sealed record PreviewDocumentQuery(Guid InstanceId, JsonDocument Requisites, DataAccess Access)
    : IRequest<PreviewDocumentResult>;

public class PreviewDocumentHandler(
    IRepository<DomainObject> instanceRepo,
    IRepository<Template> templateRepo,
    IGenerationContextBuilder contextBuilder,
    IGenerationRequestBuilder requestBuilder,
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
            // Контекст — тем же кодом, что у генерации (D1). Предпросмотр останавливается только на
            // корзине Resolution: полнота и типы значений его не гасят — человек смотрит документ, который
            // ещё заполняет.
            var schemas = await contextBuilder.LoadSchemasAsync(ct);
            var built = await contextBuilder.BuildAsync(view, q.Access, schemas, ct);
            if (built.Resolution.Any(d => d.Severity == DiagnosticSeverity.Error))
                return PreviewDocumentResult.Fail("Не все ссылки разрешены — предпросмотр недоступен.", built.Resolution);

            var requests = await requestBuilder.PrepareAsync(
                built.Context, instance.CompositeTypeId, instance.TemplateParams, schemas, ct);
            var request = await requests.ForTemplateAsync(template, OutputFormat.Pdf, ct);
            var generator = generatorFactory.Create(OutputFormat.Pdf);
            var bytes = await generator.GenerateAsync(request, ct);
            return PreviewDocumentResult.Ok(bytes);
        }
        catch (ResolutionValidationException ex)
        {
            return PreviewDocumentResult.Fail("Не все ссылки разрешены — предпросмотр недоступен.", ex.Diagnostics);
        }
        catch (DomainException ex)
        {
            // Только НАШ отказ: его текст написан для человека и уходит дословно — это тот же
            // порядок, что у ответа на запрос (ApiErrorMapping, issue #691). Так сюда приходит
            // разобранный отказ компилятора шаблона (issue #1047) — панели предпросмотра он нужен
            // не меньше, чем генерации: правят шаблон как раз в ней.
            //
            // ⚠️ Прежде здесь стоял catch(Exception) с `ex.Message` наружу, то есть предпросмотр
            // отдавал чужие сообщения дословно — и вывод компилятора с путями папки прогона, и
            // текст Npgsql со строкой подключения. Чужое исключение теперь проходит выше: конвейер
            // запишет его в журнал целиком и ответит идентификатором запроса.
            return PreviewDocumentResult.Fail(ex.Message);
        }
    }
}
