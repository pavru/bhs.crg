using System.Text.Json;
using System.Text.RegularExpressions;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Резолвер расчётных полей (issue #368). Фаза 1 — верхний уровень (<see cref="ResolveRoot"/>); фаза 2
/// (#370) — рекурсивный обход дерева контекста (<see cref="ResolveTree"/>) по образцу
/// <see cref="TypeStamper"/>: расчётные поля вычисляются в КАЖДОМ объекте (inline complex, элементы
/// array/doc-array, вложенные) в его собственном скоупе (siblings объекта). Тип объекта = сырой
/// <c>_typeId</c> (факт. тип ссылки) ⟶ объявленный тип поля. Скоуп строго локальный — межобъектных
/// ссылок/циклов нет, топосорт per-object (Kahn, как #309). Цикл → Error, ошибка/нет sibling → null+Warning.
/// Значения живут в контексте, в реквизиты не пишутся; <c>_typeId</c> сохраняется для TypeStamper.
/// </summary>
public static class ComputedFieldResolver
{
    // Ссылки на поля в выражении: get("ключ") / get('ключ').
    private static readonly Regex GetRef = new(@"get\(\s*[""']([^""']+)[""']\s*\)", RegexOptions.Compiled);

    /// <summary>
    /// Фаза 2 (#370): вычисляет расчётные поля по всему дереву — верхний уровень + вложенные составные.
    /// </summary>
    public static void ResolveTree(
        GenerationContext ctx, Guid documentTypeId,
        IReadOnlyDictionary<Guid, DocumentType> byId,
        IExpressionEvaluator evaluator, List<ResolutionDiagnostic> diagnostics)
    {
        var fields = DocumentTypeSchemaReader.EffectiveFields(documentTypeId, byId).ToDictionary(f => f.Key);

        // 1) Верхний уровень (ctx.Data).
        ResolveRoot(ctx, fields.Values.ToList(), evaluator, diagnostics);

        // 2) Рекурсия в составные подполя (значения — JsonElement; расчётные корня уже стали CLR и не объекты).
        foreach (var key in ctx.Data.Keys.ToList())
        {
            if (ctx.Data[key] is not JsonElement je) continue;
            var childType = CompositeTypeOf(key, fields);
            ctx.Set(key, ProcessObject(je, childType, byId, evaluator, diagnostics, key));
        }
    }

    /// <summary>Фаза 1: расчётные поля верхнего уровня; результат инжектится в <paramref name="ctx"/>.</summary>
    public static void ResolveRoot(
        GenerationContext ctx, IReadOnlyList<SchemaFieldInfo> effectiveFields,
        IExpressionEvaluator evaluator, List<ResolutionDiagnostic> diagnostics)
    {
        var computed = effectiveFields
            .Where(f => f.Computed && !string.IsNullOrWhiteSpace(f.Expression))
            .ToList();
        if (computed.Count == 0) return;

        var scope = BuildVars(ctx);
        var evaluated = RunComputedInScope(computed, scope, evaluator, diagnostics, path: "");
        foreach (var key in evaluated)
            ctx.Set(key, scope.GetValueOrDefault(key));
    }

    // Рекурсивная пересборка значения с вычислением computed на каждом уровне-объекте (по образцу TypeStamper.Process).
    private static JsonElement ProcessObject(
        JsonElement v, Guid? declaredTypeId,
        IReadOnlyDictionary<Guid, DocumentType> byId,
        IExpressionEvaluator evaluator, List<ResolutionDiagnostic> diagnostics, string path)
    {
        if (v.ValueKind == JsonValueKind.Array)
            return JsonSerializer.SerializeToElement(
                v.EnumerateArray().Select((it, i) => ProcessObject(it, declaredTypeId, byId, evaluator, diagnostics, $"{path}[{i}]")).ToList());
        if (v.ValueKind != JsonValueKind.Object) return v;

        Guid? actualFromRef = null;
        foreach (var p in v.EnumerateObject())
        {
            if (p.Name == "$ref") return v; // неразрешённая ссылка — не данные
            if (p.Name == TypeStamper.TypeIdKey && Guid.TryParse(p.Value.GetString(), out var tid)) actualFromRef = tid;
        }

        var typeId = actualFromRef ?? declaredTypeId;
        var fields = typeId is { } t
            ? DocumentTypeSchemaReader.EffectiveFields(t, byId).ToDictionary(f => f.Key)
            : null;

        // Пересобираем объект, рекурсируя в подполя (все ключи сохраняем, включая _typeId для TypeStamper).
        var dict = new Dictionary<string, JsonElement>();
        foreach (var p in v.EnumerateObject())
        {
            var childDeclared = fields is not null ? CompositeTypeOf(p.Name, fields) : null;
            dict[p.Name] = ProcessObject(p.Value, childDeclared, byId, evaluator, diagnostics, $"{path}.{p.Name}");
        }

        // Вычисляем computed-поля ЭТОГО типа против siblings объекта.
        if (fields is not null)
        {
            var computed = fields.Values.Where(f => f.Computed && !string.IsNullOrWhiteSpace(f.Expression)).ToList();
            if (computed.Count > 0)
            {
                var scope = dict.ToDictionary(kv => kv.Key, kv => ToClr(kv.Value), StringComparer.OrdinalIgnoreCase);
                var evaluated = RunComputedInScope(computed, scope, evaluator, diagnostics, path);
                foreach (var key in evaluated)
                    dict[key] = JsonSerializer.SerializeToElement(scope.GetValueOrDefault(key));
            }
        }
        return JsonSerializer.SerializeToElement(dict);
    }

    // Топосорт + вычисление computed-полей одного объекта; результаты пишутся в scope (для зависимых
    // computed). Возвращает вычисленные ключи (в порядке; циклические исключены — их не пишем в вывод).
    private static List<string> RunComputedInScope(
        List<SchemaFieldInfo> computed, Dictionary<string, object?> scope,
        IExpressionEvaluator evaluator, List<ResolutionDiagnostic> diagnostics, string path)
    {
        var byKey = computed.ToDictionary(f => f.Key);
        var computedKeys = byKey.Keys.ToHashSet();
        var deps = computed.ToDictionary(
            f => f.Key,
            f => ReferencedKeys(f.Expression!).Where(computedKeys.Contains).ToHashSet());

        var (order, cyclic) = TopoSort(computed.Select(f => f.Key).ToList(), deps);

        foreach (var key in cyclic)
            diagnostics.Add(new ResolutionDiagnostic(DiagnosticSeverity.Error, PathOf(path, key),
                $"Циклическая зависимость расчётного поля «{Title(byKey[key])}».", "computed-cycle"));

        foreach (var key in order)
        {
            var f = byKey[key];
            try
            {
                scope[key] = evaluator.Evaluate(f.Expression!, scope);
            }
            catch (Exception ex)
            {
                diagnostics.Add(new ResolutionDiagnostic(DiagnosticSeverity.Warning, PathOf(path, key),
                    $"Ошибка расчётного поля «{Title(f)}»: {ex.Message}", "computed-error"));
                scope[key] = null;
            }
        }
        return order; // только вычисленные (не циклические) — их пишем в вывод
    }

    /// <summary>Объявленный composite-тип поля (complex/doc-ref/array/doc-array), иначе null.</summary>
    private static Guid? CompositeTypeOf(string key, IReadOnlyDictionary<string, SchemaFieldInfo> fields)
        => fields.TryGetValue(key, out var f) && f.TypeId is { } tid
           && (DocumentTypeSchemaReader.IsSingleComposite(f.Type) || DocumentTypeSchemaReader.IsMultiValued(f.Type))
            ? tid : null;

    /// <summary>Ключи полей, на которые ссылается выражение через get("…").</summary>
    public static IReadOnlyList<string> ReferencedKeys(string expression)
        => GetRef.Matches(expression).Select(m => m.Groups[1].Value).Distinct().ToList();

    // Kahn: порядок вычисления (зависимости раньше зависимых) + ключи в циклах (не вычисляются). Стабильно.
    private static (List<string> Order, List<string> Cyclic) TopoSort(
        List<string> nodes, Dictionary<string, HashSet<string>> deps)
    {
        var indeg = nodes.ToDictionary(n => n, n => deps[n].Count);
        var dependents = nodes.ToDictionary(n => n, _ => new List<string>());
        foreach (var n in nodes)
            foreach (var d in deps[n])
                dependents[d].Add(n);

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

    // JsonElement → CLR-примитив для движка (число → double; строка/bool как есть; объект/массив → сырой
    // JSON-текст). Не-JSON значения (напр. резолвнутый enum-label — строка) отдаём как есть.
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

    private static string PathOf(string path, string key) => string.IsNullOrEmpty(path) ? key : $"{path}.{key}";
    private static string Title(SchemaFieldInfo f) => f.Title ?? f.Key;
}
