using System.Text.Json;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Schema;
using BHS.CRG.Infrastructure.Generation;

namespace BHS.CRG.Tests.Generation;

/// <summary>Расчётные поля (issue #368): топосорт зависимостей, циклы, ошибки — без движка (fake eval),
/// плюс дымовой тест реального Jint-вычислителя.</summary>
public class ComputedFieldResolverTests
{
    // Fake-вычислитель: сопоставляет строку выражения с лямбдой над переменными (Jint не задействован).
    private sealed class FakeEvaluator(Dictionary<string, Func<IReadOnlyDictionary<string, object?>, object?>> map)
        : IExpressionEvaluator
    {
        public object? Evaluate(string expression, IReadOnlyDictionary<string, object?> variables)
            => map[expression](variables);
    }

    private static SchemaFieldInfo Computed(string key, string expr, string type = "number")
        => new(key, type, null, Computed: true, Expression: expr);

    private static double D(object? v) => Convert.ToDouble(v);

    [Fact]
    public void ResolveRoot_EvaluatesInDependencyOrder()
    {
        var ctx = new GenerationContext();
        ctx.Set("x", JsonSerializer.SerializeToElement(10));
        ctx.Set("y", JsonSerializer.SerializeToElement(5));

        // withTax зависит от computed «sum» → sum обязан вычислиться первым (иначе get("sum") == null).
        var fields = new List<SchemaFieldInfo>
        {
            Computed("withTax", "get(\"sum\") * 1.2"),
            Computed("sum", "get(\"x\") + get(\"y\")"),
        };
        var eval = new FakeEvaluator(new()
        {
            ["get(\"x\") + get(\"y\")"] = v => D(v["x"]) + D(v["y"]),
            ["get(\"sum\") * 1.2"] = v => D(v["sum"]) * 1.2,
        });
        var diags = new List<ResolutionDiagnostic>();

        ComputedFieldResolver.ResolveRoot(ctx, fields, eval, diags);

        Assert.Empty(diags);
        Assert.Equal(15.0, D(ctx.Data["sum"]));
        Assert.Equal(18.0, D(ctx.Data["withTax"]));
    }

    [Fact]
    public void ResolveRoot_Cycle_EmitsErrorAndDoesNotEvaluate()
    {
        var ctx = new GenerationContext();
        var fields = new List<SchemaFieldInfo>
        {
            Computed("a", "get(\"b\")"),
            Computed("b", "get(\"a\")"),
        };
        var eval = new FakeEvaluator(new()
        {
            ["get(\"b\")"] = v => v["b"],
            ["get(\"a\")"] = v => v["a"],
        });
        var diags = new List<ResolutionDiagnostic>();

        ComputedFieldResolver.ResolveRoot(ctx, fields, eval, diags);

        Assert.Equal(2, diags.Count);
        Assert.All(diags, d => Assert.Equal("computed-cycle", d.Code));
        Assert.All(diags, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.False(ctx.Data.ContainsKey("a")); // циклические не вычисляются
        Assert.False(ctx.Data.ContainsKey("b"));
    }

    [Fact]
    public void ResolveRoot_EvalError_EmitsWarningAndSetsNull()
    {
        var ctx = new GenerationContext();
        var fields = new List<SchemaFieldInfo> { Computed("c", "boom") };
        var eval = new FakeEvaluator(new()
        {
            ["boom"] = _ => throw new InvalidOperationException("bad expr"),
        });
        var diags = new List<ResolutionDiagnostic>();

        ComputedFieldResolver.ResolveRoot(ctx, fields, eval, diags);

        var d = Assert.Single(diags);
        Assert.Equal("computed-error", d.Code);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.True(ctx.Data.ContainsKey("c"));
        Assert.Null(ctx.Data["c"]);
    }

    [Theory]
    [InlineData("get(\"a\") + get('b') + get(\"a\")", "a,b")]
    [InlineData("1 + 2", "")]
    public void ReferencedKeys_ExtractsDistinctGetTargets(string expr, string expectedCsv)
    {
        var keys = ComputedFieldResolver.ReferencedKeys(expr);
        Assert.Equal(expectedCsv, string.Join(",", keys));
    }

    // ── Реальный Jint-вычислитель ────────────────────────────────────────────

    [Fact]
    public void Jint_Evaluates_Arithmetic_And_Get()
    {
        var eval = new JintExpressionEvaluator();
        var vars = new Dictionary<string, object?> { ["a"] = 2.0, ["b"] = 3.0 };
        Assert.Equal(5.0, Convert.ToDouble(eval.Evaluate("get('a') + get('b')", vars)));
        Assert.Equal("2шт", eval.Evaluate("get('a') + 'шт'", vars)); // JS: 2 + строка = конкатенация
        Assert.Null(eval.Evaluate("get('missing')", vars));          // нет переменной → null
    }
}
