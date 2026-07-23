using System.Text.Json;
using System.Text.RegularExpressions;
using BHS.CRG.Application.Schema;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Резолвер расчётных полей (issue #368, фаза 1 — root-level). Чистая Application-логика: извлекает
/// зависимости из выражений, топосортит (Kahn, как #309 typeblocks), вычисляет через
/// <see cref="IExpressionEvaluator"/> в порядке зависимостей и инжектит результат в контекст.
/// Циклы → диагностика-Error; ошибка выражения/отсутствующий sibling → null + Warning.
/// Значения расчётных полей живут в контексте генерации, в реквизиты (Data) не пишутся.
/// </summary>
public static class ComputedFieldResolver
{
    // Ссылки на поля в выражении: get("ключ") / get('ключ').
    private static readonly Regex GetRef = new(@"get\(\s*[""']([^""']+)[""']\s*\)", RegexOptions.Compiled);

    /// <summary>Вычисляет расчётные поля верхнего уровня и инжектит их в <paramref name="ctx"/>.</summary>
    public static void ResolveRoot(
        GenerationContext ctx,
        IReadOnlyList<SchemaFieldInfo> effectiveFields,
        IExpressionEvaluator evaluator,
        List<ResolutionDiagnostic> diagnostics)
    {
        var computed = effectiveFields
            .Where(f => f.Computed && !string.IsNullOrWhiteSpace(f.Expression))
            .ToList();
        if (computed.Count == 0) return;

        var byKey = computed.ToDictionary(f => f.Key);
        var computedKeys = byKey.Keys.ToHashSet();

        // Зависимости среди расчётных полей: ключи, на которые ссылается выражение и которые сами computed.
        var deps = computed.ToDictionary(
            f => f.Key,
            f => ReferencedKeys(f.Expression!).Where(computedKeys.Contains).ToHashSet());

        var (order, cyclic) = TopoSort(computed.Select(f => f.Key).ToList(), deps);

        foreach (var key in cyclic)
            diagnostics.Add(new ResolutionDiagnostic(DiagnosticSeverity.Error, key,
                $"Циклическая зависимость расчётного поля «{Title(byKey[key])}».", "computed-cycle"));

        // Вычисляем в топологическом порядке — уже посчитанные computed видны следующим через get().
        foreach (var key in order)
        {
            var f = byKey[key];
            var vars = BuildVars(ctx);
            try
            {
                ctx.Set(key, evaluator.Evaluate(f.Expression!, vars));
            }
            catch (Exception ex)
            {
                diagnostics.Add(new ResolutionDiagnostic(DiagnosticSeverity.Warning, key,
                    $"Ошибка расчётного поля «{Title(f)}»: {ex.Message}", "computed-error"));
                ctx.Set(key, null);
            }
        }
    }

    /// <summary>Ключи полей, на которые ссылается выражение через get("…").</summary>
    public static IReadOnlyList<string> ReferencedKeys(string expression)
        => GetRef.Matches(expression).Select(m => m.Groups[1].Value).Distinct().ToList();

    // Kahn: возвращает порядок вычисления (зависимости раньше зависимых) + ключи в циклах (не вычисляются).
    // Стабильность: исходный порядок полей сохраняется при равных зависимостях (для детерминизма).
    private static (List<string> Order, List<string> Cyclic) TopoSort(
        List<string> nodes, Dictionary<string, HashSet<string>> deps)
    {
        var indeg = nodes.ToDictionary(n => n, n => deps[n].Count);
        var dependents = nodes.ToDictionary(n => n, _ => new List<string>());
        foreach (var n in nodes)
            foreach (var d in deps[n])
                dependents[d].Add(n);

        // Очередь в исходном порядке узлов (стабильно).
        var ready = new List<string>(nodes.Where(n => indeg[n] == 0));
        var order = new List<string>();
        while (ready.Count > 0)
        {
            var n = ready[0];
            ready.RemoveAt(0);
            order.Add(n);
            foreach (var m in dependents[n])
                if (--indeg[m] == 0)
                    ready.Add(m);
        }
        var cyclic = nodes.Where(n => !order.Contains(n)).ToList();
        return (order, cyclic);
    }

    private static Dictionary<string, object?> BuildVars(GenerationContext ctx)
    {
        var vars = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in ctx.Data)
            vars[k] = ToClr(v);
        return vars;
    }

    // JsonElement → CLR-примитив для движка (число → double для арифметики; строка/bool как есть;
    // объект/массив → сырой JSON-текст, формула их обычно не использует). Не-JSON значения (напр.
    // резолвнутый enum-label — строка) отдаём как есть.
    private static object? ToClr(object? v) => v switch
    {
        JsonElement el => el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => el.GetRawText(),
        },
        _ => v,
    };

    private static string Title(SchemaFieldInfo f) => f.Title ?? f.Key;
}
