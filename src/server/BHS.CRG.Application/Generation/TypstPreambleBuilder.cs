using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Generation;

/// <summary>Плоская запись одного Typst-блока (вариант отображения типа) с провенансом.</summary>
public sealed record TypstBlockRecord(string FnName, string Block, string Provenance, Guid TypeId, string TypeName, string VariantName);

public enum TypstBlockDiagnosticSeverity { Warning, Error }

/// <summary>Диагностика сборки typeblocks.typ (цикл ссылок, дубликат имени функции).</summary>
public sealed record TypstBlockDiagnostic(
    TypstBlockDiagnosticSeverity Severity, string Code, string Message, IReadOnlyList<string> FnNames);

/// <summary>Карта строк: где в итоговом typeblocks.typ лежит блок (для маппинга ошибок Typst назад на тип/вариант).</summary>
public sealed record TypstBlockSpan(string FnName, string Provenance, Guid TypeId, int StartLine, int EndLine);

public sealed record TypstPreambleResult(
    string Content, IReadOnlyList<TypstBlockSpan> Spans, IReadOnlyList<TypstBlockDiagnostic> Diagnostics);

/// <summary>
/// Собирает typeblocks.typ — функции рендеринга составных типов (схема, свойство "typstRenders").
///
/// Порядок КРИТИЧЕН (issue #309): в Typst замыкание захватывает лексическую область НА МЕСТЕ
/// определения, поэтому если блок A вызывает функцию B, `#let B` обязан стоять ВЫШЕ, иначе
/// `unknown variable`. Блоки топологически сортируются по зависимостям (Kahn, тай-брейк по исходному
/// индексу → стабильно и обратно совместимо); межтиповые циклы неразрешимы во flat-`#let` и
/// выдаются диагностикой (best-effort порядок, без pre-throw — Typst ленив и сам финальный арбитр).
///
/// Адаптер (<see cref="ExtractRenders"/>: тип → плоские записи) отделён от чистого ядра
/// (<see cref="BuildDetailed"/>: граф+сорт+эмиссия). Генерация и debug-бандл зовут одно ядро —
/// единый источник правды порядка/номеров строк. Фаза 2 (проверка блоков) переиспользует ядро с
/// draft-overlay.
/// </summary>
public static class TypstPreambleBuilder
{
    /// <summary>Обратно совместимая точка: типы → готовый текст typeblocks.typ (генерация, debug-бандл).</summary>
    public static string Build(IEnumerable<DocumentType> compositeTypes)
        => BuildDetailed(compositeTypes.SelectMany(ExtractRenders)).Content;

    /// <summary>Адаптер: схема типа → плоские записи блоков (с провенансом). Пустые/битые — пропускаются.</summary>
    public static IEnumerable<TypstBlockRecord> ExtractRenders(DocumentType type)
    {
        if (type.Schema.RootElement.TryGetProperty("typstRenders", out var renders))
            foreach (var r in ExtractRenders(renders, type.Id, type.Name, type.Code))
                yield return r;
    }

    /// <summary>Адаптер поверх сырого массива typstRenders (для draft-overlay проверки, issue #309 фаза 2):
    /// тот же JSON-shape, что в схеме, но приходит НЕсохранённым черновиком из UI.</summary>
    public static IEnumerable<TypstBlockRecord> ExtractRenders(JsonElement rendersArray, Guid typeId, string typeName, string code)
    {
        if (rendersArray.ValueKind != JsonValueKind.Array) yield break;

        foreach (var render in rendersArray.EnumerateArray())
        {
            if (render.ValueKind != JsonValueKind.Object) continue;
            var fnName = render.TryGetProperty("fnName", out var fn) ? fn.GetString() : null;
            var block = render.TryGetProperty("block", out var bl) ? bl.GetString() : null;
            if (string.IsNullOrWhiteSpace(fnName) || string.IsNullOrWhiteSpace(block)) continue;
            var variant = render.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
            var fnTrim = fnName.Trim();
            yield return new TypstBlockRecord(fnTrim, block, Provenance(typeName, code, variant, fnTrim),
                typeId, typeName, variant);
        }
    }

    /// <summary>Провенанс-строка над блоком (одна строка — без переводов строк, чтобы не сбить line-map).</summary>
    private static string Provenance(string typeName, string code, string variant, string fnName)
    {
        static string San(string? s) => (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return $"[type: {San(typeName)} ({San(code)})] variant: {San(variant)} -> {fnName}";
    }

    /// <summary>Чистое ядро: граф зависимостей → топосорт → эмиссия с провенансом + line-map + диагностики.</summary>
    public static TypstPreambleResult BuildDetailed(IEnumerable<TypstBlockRecord> records)
    {
        var list = records.ToList();
        var diagnostics = new List<TypstBlockDiagnostic>();
        var n = list.Count;

        // Дубликаты fnName между типами: typeblocks глобален → одноимённые функции делают граф
        // неоднозначным и в самом Typst перекрывают друг друга (последняя побеждает).
        foreach (var g in list.GroupBy(r => r.FnName).Where(g => g.Count() > 1))
            diagnostics.Add(new(TypstBlockDiagnosticSeverity.Error, "duplicate-fn",
                $"Имя функции «{g.Key}» задано более чем в одном варианте: {string.Join("; ", g.Select(r => r.Provenance))}",
                new[] { g.Key }));

        var known = new HashSet<string>(list.Select(r => r.FnName));
        var nameToIndices = new Dictionary<string, List<int>>();
        for (int i = 0; i < n; i++)
        {
            if (!nameToIndices.TryGetValue(list[i].FnName, out var l)) { l = new(); nameToIndices[list[i].FnName] = l; }
            l.Add(i);
        }

        // deps[i] = индексы блоков, которые блок i вызывает (они должны идти ВЫШЕ i).
        var deps = new List<HashSet<int>>(n);
        for (int i = 0; i < n; i++)
        {
            var set = new HashSet<int>();
            foreach (var refName in FindReferencedFnNames(list[i].Block, known, list[i].FnName))
                if (nameToIndices.TryGetValue(refName, out var targets))
                    foreach (var t in targets) if (t != i) set.Add(t);
            deps.Add(set);
        }

        // Kahn с тай-брейком по исходному индексу: зависимости раньше зависимых, независимые — в
        // исходном порядке (двигается только реально неверно упорядоченное → обратная совместимость).
        var dependents = new List<List<int>>();
        for (int i = 0; i < n; i++) dependents.Add(new());
        for (int i = 0; i < n; i++) foreach (var d in deps[i]) dependents[d].Add(i);

        var remaining = deps.Select(d => d.Count).ToArray();
        var emitted = new bool[n];
        var ready = new SortedSet<int>();
        for (int i = 0; i < n; i++) if (remaining[i] == 0) ready.Add(i);

        var order = new List<int>(n);
        while (ready.Count > 0)
        {
            var i = ready.Min; ready.Remove(i);
            order.Add(i); emitted[i] = true;
            foreach (var dep in dependents[i])
                if (!emitted[dep] && --remaining[dep] == 0) ready.Add(dep);
        }

        // Оставшиеся — в циклах (или ниже по течению цикла). Best-effort: добавляем в исходном порядке.
        if (order.Count < n)
        {
            foreach (var cycle in FindCycles(deps, emitted))
                diagnostics.Add(new(TypstBlockDiagnosticSeverity.Error, "cycle",
                    $"Взаимные ссылки между блоками — Typst не может их упорядочить: "
                    + string.Join(" → ", cycle.Select(i => list[i].FnName)) + " → " + list[cycle[0]].FnName,
                    cycle.Select(i => list[i].FnName).ToList()));
            for (int i = 0; i < n; i++) if (!emitted[i]) { order.Add(i); emitted[i] = true; }
        }

        // Эмиссия + line-map. Явный '\n' (не Environment.NewLine) — чтобы номера строк совпадали
        // с тем, что видит Typst, кросс-платформенно.
        var sb = new StringBuilder();
        var spans = new List<TypstBlockSpan>(n);
        int line = 1;
        foreach (var idx in order)
        {
            var r = list[idx];
            sb.Append("// ").Append(r.Provenance).Append('\n');
            line++;
            var def = $"#let {r.FnName}(it) = {r.Block}";
            int defLines = 1 + def.Count(c => c == '\n');
            spans.Add(new(r.FnName, r.Provenance, r.TypeId, line, line + defLines - 1));
            sb.Append(def).Append('\n');
            line += defLines;
        }
        return new(sb.ToString(), spans, diagnostics);
    }

    /// <summary>Ссылки блока на ДРУГИЕ известные функции: скан вызова `name(` по границе идентификатора,
    /// с очисткой комментариев/строк (чтобы упоминание в комментарии не давало ложное ребро/цикл).</summary>
    private static IEnumerable<string> FindReferencedFnNames(string block, HashSet<string> known, string self)
    {
        var cleaned = StripCommentsAndStrings(block);
        foreach (var name in known)
        {
            if (name == self) continue; // саморекурсию Typst допускает — не ребро
            if (Regex.IsMatch(cleaned, $@"(?<![\w\-]){Regex.Escape(name)}\s*\("))
                yield return name;
        }
    }

    /// <summary>Одно-проходная очистка Typst line/block-комментариев и строк "…" (переводы строк
    /// сохраняются). Не полный парсинг — достаточно, чтобы убрать ложные упоминания имён функций.</summary>
    private static string StripCommentsAndStrings(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                if (i < s.Length) sb.Append('\n');
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) { if (s[i] == '\n') sb.Append('\n'); i++; }
                i++; // встанем на '/', внешний i++ пройдёт дальше
                continue;
            }
            if (c == '"')
            {
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length) { i++; }
                    else if (s[i] == '\n') sb.Append('\n');
                    i++;
                }
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Нетривиальные SCC (циклы) среди ещё не отсортированных узлов — Tarjan. Саморефы исключены,
    /// поэтому SCC размера &gt;1 = реальный цикл взаимных ссылок.</summary>
    private static List<List<int>> FindCycles(List<HashSet<int>> deps, bool[] emitted)
    {
        int n = deps.Count;
        var index = new int[n];
        var low = new int[n];
        var onStack = new bool[n];
        Array.Fill(index, -1);
        var stack = new Stack<int>();
        int counter = 0;
        var result = new List<List<int>>();

        void Strong(int v)
        {
            index[v] = low[v] = counter++;
            stack.Push(v); onStack[v] = true;
            foreach (var w in deps[v])
            {
                if (emitted[w]) continue; // уже упорядоченные — вне циклов
                if (index[w] == -1) { Strong(w); low[v] = Math.Min(low[v], low[w]); }
                else if (onStack[w]) low[v] = Math.Min(low[v], index[w]);
            }
            if (low[v] == index[v])
            {
                var comp = new List<int>();
                int w;
                do { w = stack.Pop(); onStack[w] = false; comp.Add(w); } while (w != v);
                if (comp.Count > 1) { comp.Reverse(); result.Add(comp); }
            }
        }

        for (int v = 0; v < n; v++)
            if (!emitted[v] && index[v] == -1) Strong(v);
        return result;
    }
}
