using BHS.CRG.Application.Generation;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Топосорт и диагностики сборки typeblocks.typ (issue #309): порядок определений по зависимостям
/// (замыкание Typst захватывает область на месте определения), стабильность, циклы, дубликаты,
/// провенанс и line-map.
/// </summary>
public class TypstPreambleBuilderTests
{
    private static TypstBlockRecord R(string fn, string block) =>
        new(fn, block, $"prov:{fn}", Guid.NewGuid(), "T", fn);

    private static int Idx(string content, string fn) => content.IndexOf($"#let {fn}(", StringComparison.Ordinal);

    [Fact]
    public void Dependency_IsEmittedBeforeDependent()
    {
        // a вызывает b → #let b обязан идти выше #let a, хотя a передан первым.
        var res = TypstPreambleBuilder.BuildDetailed(new[] { R("a", "{ b(it) }"), R("b", "{ it.x }") });
        Assert.True(Idx(res.Content, "b") < Idx(res.Content, "a"));
        Assert.Empty(res.Diagnostics);
    }

    [Fact]
    public void IndependentBlocks_KeepOriginalOrder()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { R("first", "{ it.x }"), R("second", "{ it.y }") });
        Assert.True(Idx(res.Content, "first") < Idx(res.Content, "second"));
        Assert.Empty(res.Diagnostics);
    }

    [Fact]
    public void SelfRecursion_IsNotACycle()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { R("f", "{ if it.n > 0 { f(it) } }") });
        Assert.Empty(res.Diagnostics);
    }

    [Fact]
    public void MutualReference_ReportsCycle_ButStillEmitsBoth()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { R("a", "{ b(it) }"), R("b", "{ a(it) }") });
        Assert.Contains(res.Diagnostics, d => d.Code == "cycle" && d.Severity == TypstBlockDiagnosticSeverity.Error);
        Assert.True(Idx(res.Content, "a") >= 0 && Idx(res.Content, "b") >= 0);
    }

    [Fact]
    public void DuplicateFnName_IsReported()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { R("dup", "{ it.x }"), R("dup", "{ it.y }") });
        Assert.Contains(res.Diagnostics, d => d.Code == "duplicate-fn");
    }

    [Fact]
    public void ReferenceInsideComment_DoesNotCreateEdge()
    {
        // Упоминание b() только в комментарии не должно двигать порядок (нет реальной зависимости).
        var res = TypstPreambleBuilder.BuildDetailed(new[] { R("a", "{ // uses b(it)\n it.x }"), R("b", "{ it.y }") });
        Assert.True(Idx(res.Content, "a") < Idx(res.Content, "b"));
        Assert.Empty(res.Diagnostics);
    }

    [Fact]
    public void Emits_ProvenanceComment_AndLineMap()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { R("f", "{ it.x }") });
        Assert.Contains("// prov:f", res.Content);
        var span = Assert.Single(res.Spans);
        Assert.Equal("f", span.FnName);
        var lines = res.Content.Split('\n');
        Assert.StartsWith("#let f(", lines[span.StartLine - 1]);
    }

    [Fact]
    public void LineMap_TracksMultiLineBlocks()
    {
        // Комментарий = строка 1; `#let f(it) = {\n it.x \n}` (2 перевода строки) = строки 2..4.
        var res = TypstPreambleBuilder.BuildDetailed(new[] { R("f", "{\n it.x \n}") });
        var span = Assert.Single(res.Spans);
        Assert.Equal(2, span.StartLine);
        Assert.Equal(4, span.EndLine);
    }

    [Fact]
    public void Chain_OrdersTransitively()
    {
        // c→b→a: итог должен идти a, b, c (каждая зависимость выше зависимого), хотя дан обратный порядок.
        var res = TypstPreambleBuilder.BuildDetailed(new[] { R("c", "{ b(it) }"), R("b", "{ a(it) }"), R("a", "{ it.x }") });
        Assert.True(Idx(res.Content, "a") < Idx(res.Content, "b"));
        Assert.True(Idx(res.Content, "b") < Idx(res.Content, "c"));
        Assert.Empty(res.Diagnostics);
    }
}
