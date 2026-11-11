using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Templates;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Запросы одного документа по шаблонам (change core-generation-input-one-path, D2): контекст общий,
/// <c>params</c> — у каждого свои.
/// </summary>
public class GenerationRequestBuilderTests
{
    private sealed class NoLib : IUserLibProvider
    {
        public Task<UserLibSnapshot> GetAsync(CancellationToken ct = default) => Task.FromResult(new UserLibSnapshot("", []));
    }

    private sealed class NoAssets : ITemplateAssetResolver
    {
        public Task<ResolvedTemplateAssets> ResolveAsync(Guid templateId, Guid documentTypeId, CancellationToken ct = default)
            => Task.FromResult(new ResolvedTemplateAssets([], []));
    }

    private static Template WithMode(string mode)
    {
        var t = Template.Create(Guid.NewGuid(), mode, "= " + mode);
        t.SetParameters($$"""[{"name":"mode","type":"string","default":"{{mode}}"}]""");
        return t;
    }

    private static string? Mode(GenerationRequest r)
        => ((Dictionary<string, object?>)r.Context.Data["params"]!)["mode"] as string;

    /// <summary>
    /// Запрос, собранный раньше, не получает params следующего шаблона: запросы держат контекст
    /// ссылкой, и собрать их все до компиляции первого — законный порядок.
    /// </summary>
    [Fact]
    public async Task EachRequest_KeepsItsOwnParams()
    {
        var context = new GenerationContext();
        var schemas = new SchemaCatalog(new Dictionary<Guid, DocumentType>(), new Dictionary<Guid, PrimitiveType>());
        var requests = await new GenerationRequestBuilder(new NoLib(), new NoAssets())
            .PrepareAsync(context, Guid.NewGuid(), null, schemas, default);

        var first = await requests.ForTemplateAsync(WithMode("A"), OutputFormat.Pdf, default);
        var second = await requests.ForTemplateAsync(WithMode("B"), OutputFormat.Pdf, default);

        Assert.Equal("A", Mode(first));
        Assert.Equal("B", Mode(second));
        Assert.False(context.Data.ContainsKey("params"));
    }
}
