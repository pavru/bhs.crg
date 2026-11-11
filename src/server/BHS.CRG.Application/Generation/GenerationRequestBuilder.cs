using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Templates;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Запрос к генератору по собранному контексту — один код на генерацию, предпросмотр и отладочный
/// пакет (change core-generation-input-one-path, D2). Прежде блоки типов, снимок библиотеки, ассеты и
/// <c>params</c> собирал каждый из трёх сам, и пакет делал это в другом порядке.
/// </summary>
public interface IGenerationRequestBuilder
{
    /// <summary>
    /// Штамп <c>_type</c> (один раз на контекст), блоки типов и снимок библиотеки — общие для всех
    /// шаблонов документа. Штамп ТЕРМИНАЛЕН: после проверок полноты, чтобы штамп непустых объектов не
    /// сбил проверку пустоты (issue #342).
    /// </summary>
    Task<GenerationRequests> PrepareAsync(
        GenerationContext context, Guid documentTypeId, string? instanceTemplateParams,
        SchemaCatalog schemas, CancellationToken ct);
}

/// <summary>Запросы одного контекста — по шаблону: меняются только <c>params</c>, содержимое и ассеты.</summary>
public sealed class GenerationRequests(
    GenerationContext context,
    Guid documentTypeId,
    string? instanceTemplateParams,
    IReadOnlyList<TypstBlockFile> typeBlocks,
    UserLibSnapshot userLib,
    ITemplateAssetResolver assetResolver)
{
    public async Task<GenerationRequest> ForTemplateAsync(Template template, OutputFormat format, CancellationToken ct)
    {
        // params — в СВОЕЙ копии контекста: общий контекст не меняется, и запрос, собранный раньше,
        // не получает params шаблона, собранного после него.
        var own = context.With("params", TemplateParams.Effective(template.Parameters,
            TemplateParams.OverridesForTemplate(instanceTemplateParams, template.Id)));
        // Ассеты шаблона (issue #62) — уже свёрнутые по приоритету Template > DocumentType > System.
        var assets = await assetResolver.ResolveAsync(template.Id, documentTypeId, ct);
        return new GenerationRequest(template.Content, format, own,
            TypeBlocksFiles: typeBlocks,
            UserLibContent: string.IsNullOrWhiteSpace(userLib.Entrypoint) ? null : userLib.Entrypoint,
            TemplateAssets: assets,
            UserLibFiles: userLib.Files.Count > 0 ? userLib.Files : null);
    }
}

public class GenerationRequestBuilder(IUserLibProvider userLib, ITemplateAssetResolver assetResolver)
    : IGenerationRequestBuilder
{
    public async Task<GenerationRequests> PrepareAsync(
        GenerationContext context, Guid documentTypeId, string? instanceTemplateParams,
        SchemaCatalog schemas, CancellationToken ct)
    {
        TypeStamper.Stamp(context, documentTypeId, schemas.DocumentTypes);
        var typeBlocks = TypstPreambleBuilder.Build(schemas.AllTypes);
        var lib = await userLib.GetAsync(ct);
        return new GenerationRequests(context, documentTypeId, instanceTemplateParams, typeBlocks, lib, assetResolver);
    }
}
