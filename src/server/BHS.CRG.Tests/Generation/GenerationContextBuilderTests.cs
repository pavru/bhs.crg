using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Сборка контекста — одна на генерацию, предпросмотр, проверку и пакет (change
/// core-generation-input-one-path, D1): порядок семи шагов и то, в какую корзину попадает диагностика.
/// Корзина решает, что остановит предпросмотр, — поэтому распределение проверяется отдельно.
/// </summary>
public class GenerationContextBuilderTests
{
    private sealed class Recorder : IEntityResolver, IDataSetResolver, IQualityLinkResolver
    {
        public List<string> Steps { get; } = [];
        public object? Leftover { get; init; }

        public Task<GenerationContext> ResolveAsync(DocumentView instance, bool keepRefProvenance = false, CancellationToken ct = default)
        {
            Steps.Add("resolve");
            var ctx = new GenerationContext();
            if (Leftover is not null) ctx.Set("Ссылка", Leftover);
            return Task.FromResult(ctx);
        }

        public Task InjectAsync(GenerationContext ctx, DocumentView instance, DataAccess access,
            List<ResolutionDiagnostic>? diagnostics = null, CancellationToken ct = default)
        {
            Steps.Add("datasets");
            diagnostics!.Add(new ResolutionDiagnostic(DiagnosticSeverity.Warning, "Набор", "набор", "dataset"));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, object?>> ResolveOwnerBindingsAsync(Guid ownerId, Guid typeId,
            CatalogScope scopeLevel, Guid? scopeId, IReadOnlySet<Guid> standing, DataAccess access,
            List<ResolutionDiagnostic>? diagnostics = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task ApplyDefaultsAsync(GenerationContext ctx, DocumentView instance, CancellationToken ct = default)
        { Steps.Add("defaults"); return Task.CompletedTask; }

        public Task ResolveEnumLabelsAsync(GenerationContext ctx, DocumentView instance, CancellationToken ct = default)
        { Steps.Add("enum-labels"); return Task.CompletedTask; }

        public Task InjectAsync(GenerationContext ctx, DocumentView instance, CancellationToken ct = default)
        { Steps.Add("quality"); return Task.CompletedTask; }

        public Task ResolveContextRefsAsync(GenerationContext ctx, Guid documentSetId, CancellationToken ct = default)
        { Steps.Add("context-refs"); return Task.CompletedTask; }

        public Task ResolveComputedFieldsAsync(GenerationContext ctx, DocumentView instance,
            List<ResolutionDiagnostic> diagnostics, CancellationToken ct = default)
        {
            Steps.Add("computed");
            diagnostics.Add(new ResolutionDiagnostic(DiagnosticSeverity.Error, "Итог", "цикл", "computed-cycle"));
            return Task.CompletedTask;
        }
    }

    private static (DocumentView View, SchemaCatalog Schemas) Doc()
    {
        var type = DocumentType.Create("Акт", "Акт", DocumentTypeKind.Document, null, JsonDocument.Parse("""
            { "fields": [
                { "key": "Номер", "type": "string", "required": true },
                { "key": "Количество", "type": "number" } ] }
            """), TypeOwner.Core, TypeVisibility.Shared, false);
        var view = new DocumentView(Guid.NewGuid(), Guid.NewGuid(), type.Id,
            JsonDocument.Parse("{}"), JsonDocument.Parse("{}"));
        return (view, new SchemaCatalog(new Dictionary<Guid, DocumentType> { [type.Id] = type },
            new Dictionary<Guid, PrimitiveType>()));
    }

    private static GenerationContextBuilder Builder(Recorder r) => new(null!, null!, r, r, r);

    [Fact]
    public async Task Steps_RunInGenerationOrder()
    {
        var r = new Recorder();
        var (view, schemas) = Doc();

        await Builder(r).BuildAsync(view, TestAccess.All, schemas, CancellationToken.None);

        Assert.Equal(["resolve", "datasets", "defaults", "enum-labels", "quality", "context-refs", "computed"], r.Steps);
    }

    /// <summary>
    /// Шаги разрешения и висящие ссылки — в <c>Resolution</c>, полнота и типы значений — в
    /// <c>Checks</c>. Предпросмотр смотрит только первую: незаполненное поле его не гасит.
    /// </summary>
    [Fact]
    public async Task Diagnostics_GoToTheirBuckets()
    {
        var r = new Recorder
        {
            Leftover = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["$ref"] = Guid.NewGuid().ToString() }),
        };
        var (view, schemas) = Doc();

        var built = await Builder(r).BuildAsync(view, TestAccess.All, schemas, CancellationToken.None);

        Assert.Equal(["dataset", "computed-cycle"], built.Resolution.Take(2).Select(d => d.Code));
        Assert.Contains(built.Resolution.Skip(2), d => d.Path == "Ссылка" && d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(["missing-required"], built.Checks.Select(d => d.Code));
        Assert.Equal([.. built.Resolution, .. built.Checks], built.All);
    }
}
