using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Generation;

/// <summary>Плоская запись одного Typst-блока (вариант отображения типа) с провенансом.</summary>
/// <param name="TypeCode">Код типа — ключ диспетч-таблицы (issue #768). Тем же кодом штампуется
/// <c>_type.chain</c>, поэтому шаблон находит блок по метаполю объекта. Пустой код — не ошибка
/// данных, а тип, до которого не добрались: такой блок в таблицу не попадает (адресовать нечем),
/// но обычным вызовом по имени функции остаётся доступен.</param>
public sealed record TypstBlockRecord(
    string FnName, string Block, string Provenance, Guid TypeId, string TypeName, string VariantName,
    string TypeCode = "");

public enum TypstBlockDiagnosticSeverity { Warning, Error }

/// <summary>Диагностика сборки typeblocks.typ (цикл ссылок, дубликат имени функции).</summary>
public sealed record TypstBlockDiagnostic(
    TypstBlockDiagnosticSeverity Severity, string Code, string Message, IReadOnlyList<string> FnNames);

/// <summary>Один файл сборки блоков. <paramref name="Path"/> — относительно корня компиляции,
/// с прямыми слэшами (это же путь, который Typst печатает в диагностиках и который кладётся в ZIP).</summary>
public sealed record TypstBlockFile(string Path, string Content);

/// <summary>Карта строк: в каком файле и на каких строках лежит блок (для маппинга ошибок Typst
/// назад на тип/вариант). С расколом по файлам (issue #772) координата — пара «файл + строка»:
/// номера строк в разных модулях совпадают, и одной строки для поиска больше не хватает.</summary>
public sealed record TypstBlockSpan(
    string FnName, string Provenance, Guid TypeId, int StartLine, int EndLine, string File);

/// <param name="Files">Агрегатор и модули. Агрегатор — всегда первый и всегда присутствует, даже
/// когда блоков нет вовсе: шаблон импортирует его дословно (#353), и отсутствие файла было бы
/// ошибкой компиляции у КАЖДОГО документа.</param>
public sealed record TypstPreambleResult(
    IReadOnlyList<TypstBlockFile> Files,
    IReadOnlyList<TypstBlockSpan> Spans,
    IReadOnlyList<TypstBlockDiagnostic> Diagnostics)
{
    /// <summary>Содержимое агрегатора — для мест, которым нужен только он (harness, тесты).</summary>
    public string Entrypoint => Files[0].Content;
}

/// <summary>
/// Собирает блоки отображения составных типов (схема, свойство "typstRenders") — агрегатор
/// <c>typeblocks.typ</c> и по файлу-модулю на тип в подпапке <c>typeblocks/</c> (issue #772).
///
/// <para><b>Порядок внутри модуля КРИТИЧЕН</b> (issue #309): в Typst замыкание захватывает лексическую
/// область НА МЕСТЕ определения, поэтому если блок A вызывает блок B ТОГО ЖЕ типа, <c>#let B</c> обязан
/// стоять ВЫШЕ. Топосорт (Kahn, тай-брейк по исходному индексу → стабильно) остался, но стал
/// внутримодульным: рёбра МЕЖДУ типами порядком больше не разрешаются — их разводят импорты.</para>
///
/// <para><b>Циклы теперь двух разных природ.</b> Внутри модуля цикл неразрешим, как и был: Error
/// плюс best-effort порядок (Typst ленив и сам финальный арбитр). Между модулями статический импорт
/// по кругу — <c>error: cyclic import</c>, отказ компиляции ВСЕГО файла, то есть локальная поломка
/// стала бы глобальной. Поэтому рёбра, замыкающие межмодульный цикл, эмитятся отложенным импортом
/// (см. <see cref="LazyImport"/>) — они работают, а пользователь получает предупреждение.</para>
///
/// <para><b>Зачем раскол вообще</b> — не только ради адресных ошибок и дерева в просмотрщике.
/// Диспетч <c>render-by-type</c> (#768) обязан знать все типы, поэтому живёт в агрегаторе, а нужен
/// он внутри блоков; во flat-файле блок его позвать не мог в принципе (эмитится ниже). Второй файл
/// даёт отложенный импорт, а с ним и доступ блока к диспетчу.</para>
///
/// <para>Адаптер (<see cref="ExtractRenders"/>: тип → плоские записи) отделён от чистого ядра
/// (<see cref="BuildDetailed"/>: граф+сорт+эмиссия). Генерация, debug-бандл и просмотрщик зовут одно
/// ядро — единый источник правды порядка/номеров строк. Фаза 2 (проверка блоков) переиспользует ядро
/// с draft-overlay.</para>
/// </summary>
public static class TypstPreambleBuilder
{
    /// <summary>Типы → готовый набор файлов (генерация, debug-бандл, просмотрщик).</summary>
    public static IReadOnlyList<TypstBlockFile> Build(IEnumerable<DocumentType> compositeTypes)
        => BuildWithDiagnostics(compositeTypes).Files;

    /// <summary>То же, но с диагностиками сборки — для мест, которые обязаны их показать
    /// (просмотрщик #770). Генерация их намеренно игнорирует: Typst ленив и сам финальный арбитр,
    /// а обрывать выпуск документа из-за предупреждения о чужом блоке было бы хуже поломки.</summary>
    public static TypstPreambleResult BuildWithDiagnostics(IEnumerable<DocumentType> compositeTypes)
    {
        // Порядок типов задаём САМИ, а не берём как пришло: репозиторий отдаёт их без ORDER BY, и
        // PostgreSQL после UPDATE любой строки возвращает набор в другом физическом порядке. Файл от
        // этого менялся между вызовами — не по содержанию, а перестановкой независимых блоков и строк
        // диспетч-таблицы. Работать это не мешало (топосорт держит зависимости, поиск в таблице по
        // ключу), но ломало две вещи: сравнение файла с самим собой (шумные диффы на пустом месте) и
        // обещание экрана «показываю ровно то, что уходит в Typst» — экран и генерация делают РАЗНЫЕ
        // запросы к репозиторию, и совпадение было делом случая. Ключ сортировки — Code: он уникален
        // (реестр типов) и стабилен, в отличие от порядка вставки.
        var types = compositeTypes.OrderBy(t => t.Code, StringComparer.Ordinal).ToList();
        return BuildDetailed(types.SelectMany(ExtractRenders), UnionCodes(types));
    }

    /// <summary>
    /// Коды типов, у которых заполняется ровно один вариант (тэг <c>type.union</c>, issue #320).
    ///
    /// <para>Нужны хелперу, чтобы отличить СТРОКУ union-массива от обычного объекта (issue #768).
    /// По форме их не различить: у строки union тоже стоит <c>_type</c> (её штампует
    /// <see cref="TypeStamper"/>), а «ровно одно заполненное составное поле» — обычное дело, ведь
    /// незаполненные ключи в документ не пишутся. Развернув такой объект «на всякий случай», хелпер
    /// показал бы вложенное значение вместо пометки «нет блока для типа» — то есть ровно то молчание,
    /// от которого пометка и заводилась.</para>
    ///
    /// <para>Тэг наследуется, поэтому спрашиваем его у каждого типа с проходом вверх по цепочке, а не
    /// ищем объявление: потомок union'а — тоже union.</para>
    /// </summary>
    private static IReadOnlyCollection<string> UnionCodes(IReadOnlyList<DocumentType> types)
    {
        var byId = types.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        return types
            .Where(t => !string.IsNullOrWhiteSpace(t.Code)
                        && SchemaTags.TypeHasTag(t, byId, FunctionalTag.TypeUnion))
            .Select(t => t.Code)
            .Distinct()
            .ToList();
    }

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
                typeId, typeName, variant, code ?? "");
        }
    }

    /// <summary>Провенанс-строка над блоком (одна строка — без переводов строк, чтобы не сбить line-map).</summary>
    private static string Provenance(string typeName, string code, string variant, string fnName)
        => $"[type: {San(typeName)} ({San(code)})] variant: {San(variant)} -> {fnName}";

    /// <summary>Чистое ядро: граф зависимостей → топосорт → эмиссия с провенансом + line-map + диагностики.</summary>
    public static TypstPreambleResult BuildDetailed(
        IEnumerable<TypstBlockRecord> records, IReadOnlyCollection<string>? unionCodes = null)
    {
        var list = records.ToList();
        var diagnostics = new List<TypstBlockDiagnostic>();
        var n = list.Count;

        // Дубликаты имени — теперь В ПРЕДЕЛАХ ТИПА (issue #773). До префиксной адресации имена были
        // глобальными и совпадение у разных типов ломало сборку; теперь каждый тип — свой модуль со
        // своей областью, и `Адрес.full` рядом с `Подписант.full` совершенно законны. А вот два
        // одноимённых блока ОДНОГО типа по-прежнему перекрывают друг друга в Typst (побеждает
        // последний), и адресовать второй нечем.
        foreach (var g in list.GroupBy(r => (r.TypeId, r.FnName)).Where(g => g.Count() > 1))
            diagnostics.Add(new(TypstBlockDiagnosticSeverity.Error, "duplicate-fn",
                $"Имя функции «{g.Key.FnName}» задано более чем в одном варианте этого типа: "
                + string.Join("; ", g.Select(r => r.Provenance)),
                new[] { g.Key.FnName }));

        foreach (var r in list)
            foreach (var path in RelativePathsIn(r.Block))
                diagnostics.Add(new(TypstBlockDiagnosticSeverity.Warning, "relative-path",
                    $"Путь «{path}» отсчитывается от файла блока, а блоки лежат в подпапке "
                    + $"{TypeBlockSlug.FolderName}/ — из неё он не найдётся. Укажите его от корня "
                    + $"проекта Typst: «/{path}». Блок: {r.Provenance}",
                    new[] { r.FnName }));

        // deps[i] = индексы блоков, которые блок i вызывает. Со сменой адресации (issue #773) вызов
        // выглядит по-разному в зависимости от того, СВОЙ блок или чужой: свой — просто по имени
        // (общая область модуля), чужой — через префикс типа. Искать надо обе формы, иначе рёбра
        // потеряются и порядок внутри модуля станет случайным.
        var deps = new List<HashSet<int>>(n);
        for (int i = 0; i < n; i++)
        {
            var masked = TypstTextMask.Mask(list[i].Block, TypstTextMask.Keep.CodeOnly);
            var set = new HashSet<int>();
            for (int j = 0; j < n; j++)
            {
                if (j == i) continue;   // саморекурсию Typst допускает — не ребро
                var callee = list[j];
                var pattern = callee.TypeId == list[i].TypeId
                    ? CallOfOwn(callee.FnName)
                    : CallOfOther(callee.TypeCode, callee.FnName);
                if (pattern is not null && Regex.IsMatch(masked, pattern)) set.Add(j);
            }
            deps.Add(set);
        }

        // ── Модули: один тип — один файл ──────────────────────────────────────────────────────
        // Порядок модулей задаём САМИ (код → имя → id), а не берём как пришло: от него зависит
        // порядок импортов в агрегаторе и — что важнее — кому из столкнувшихся слагов достанется
        // суффикс. Порядок записей репозитория для этого негоден, он меняется после любого UPDATE.
        var modules = new List<ModuleBuild>();
        var moduleOf = new int[n];
        var byTypeId = new Dictionary<Guid, int>();
        foreach (var (r, i) in list.Select((r, i) => (r, i))
                     .OrderBy(x => x.r.TypeCode, StringComparer.Ordinal)
                     .ThenBy(x => x.r.TypeName, StringComparer.Ordinal)
                     .ThenBy(x => x.r.TypeId))
        {
            if (!byTypeId.TryGetValue(r.TypeId, out var m))
            {
                m = modules.Count;
                byTypeId[r.TypeId] = m;
                modules.Add(new ModuleBuild(r.TypeId, r.TypeName, r.TypeCode));
            }
            modules[m].Indices.Add(i);   // OrderBy стабилен → внутри типа сохраняется порядок схемы
            moduleOf[i] = m;
        }

        var slugs = TypeBlockSlug.AssignUnique(modules.Select(m =>
            (m.TypeId, string.IsNullOrWhiteSpace(m.TypeCode) ? m.TypeName : m.TypeCode)));
        foreach (var m in modules) m.Slug = slugs[m.TypeId];

        // Межмодульные рёбра: modDeps[a] — модули, чьи блоки зовут блоки модуля a.
        var modDeps = modules.Select(_ => new HashSet<int>()).ToList();
        for (int i = 0; i < n; i++)
            foreach (var d in deps[i])
                if (moduleOf[d] != moduleOf[i]) modDeps[moduleOf[i]].Add(moduleOf[d]);

        // Круговой статический импорт Typst запрещает целиком (`error: cyclic import`): один взаимный
        // вызов между типами обрушил бы сборку, а с ней генерацию ВСЕХ документов. Поэтому ребро,
        // замыкающее цикл, не эмитируется вовсе — файлы собираются, падает только сам вызов, и то
        // при рендере. Это осознанный размен: локальная поломка вместо глобальной.
        //
        // До префиксной адресации (#772) такое ребро разрывалось отложенным импортом и продолжало
        // работать. С алиасами приём неприменим: обращение `Код.имя(…)` требует, чтобы `Код` был
        // модулем в момент вызова, а подменить его словарём переходников нельзя — Typst запрещает
        // вызывать ключ словаря как функцию напрямую (проверено: «cannot directly call dictionary
        // keys as functions»). Отложить же сам импорт модуля в значение язык не позволяет.
        var lazyEdges = new HashSet<(int From, int To)>();
        foreach (var comp in FindCycles(modDeps, new bool[modules.Count]))
        {
            // Рвём ОДНО ребро — этого достаточно, чтобы круг перестал быть кругом. Отбрасывать все
            // рёбра сразу значило бы сломать и те вызовы, которые могли бы работать: в паре A↔B
            // отказали бы обе стороны вместо одной.
            var inComp = comp.ToHashSet();
            var from = comp[^1];
            var to = modDeps[from].First(inComp.Contains);
            lazyEdges.Add((from, to));
            var names = comp.SelectMany(m => modules[m].Indices).Select(i => list[i].FnName).ToList();
            diagnostics.Add(new(TypstBlockDiagnosticSeverity.Error, "cycle-cross-type",
                "Типы ссылаются друг на друга блоками: "
                + string.Join(" → ", comp.Select(m => modules[m].Title())) + " → " + modules[comp[0]].Title()
                + $". Typst запрещает круговой импорт, поэтому связь «{modules[from].Title()} → "
                + $"{modules[to].Title()}» не подключена и этот вызов упадёт при генерации. Разорвите"
                + " цикл: вынесите общую часть в Typst-библиотеку либо принимайте готовое значение"
                + " параметром.",
                names));
        }

        // ── Эмиссия модулей ───────────────────────────────────────────────────────────────────
        var files = new List<TypstBlockFile>(modules.Count + 1);
        var spans = new List<TypstBlockSpan>(n);
        foreach (var (m, mi) in modules.Select((m, i) => (m, i)))
        {
            var path = TypeBlockSlug.PathFor(m.Slug);
            var sb = new StringBuilder();
            int line = 1;
            void Emit(string text) { sb.Append(text).Append('\n'); line++; }

            // Полный код и имя типа — здесь: имя файла это лишь слаг, и без строки провенанса по
            // адресу ошибки нельзя было бы узнать тип, если слаг разошёлся с кодом.
            Emit($"// Блоки отображения типа «{San(m.TypeName)}» (код: {San(m.TypeCode)}).");
            Emit("// Файл собран автоматически (issue #772) — правки будут затёрты при генерации.");

            // Тот же guard, что в агрегаторе: `#import … as 2Тип` не парсится и уронил бы сборку
            // целиком — ради одного чужого типа с неудачным кодом.
            var statics = modDeps[mi].Where(d => !lazyEdges.Contains((mi, d))
                                                 && IsTypstIdentifier(modules[d].TypeCode))
                .OrderBy(d => modules[d].Slug, StringComparer.Ordinal).ToList();
            if (statics.Count > 0)
            {
                Emit("// Блоки других типов, вызываемые отсюда (обращение — Код.имя):");
                // Соседний модуль — по имени без пути: оба лежат в одной папке. Алиас = код типа
                // (#773): имена блоков больше не глобальны, поэтому импортировать «: *» нельзя —
                // одноимённые блоки разных типов перекрыли бы друг друга молча.
                foreach (var d in statics)
                    Emit($"#import \"{modules[d].Slug}.typ\" as {modules[d].TypeCode}");
            }

            foreach (var d in modDeps[mi].Where(d => lazyEdges.Contains((mi, d)))
                         .OrderBy(d => modules[d].Slug, StringComparer.Ordinal))
                Emit($"// Взаимная ссылка с типом «{San(modules[d].TypeName)}» — импорт НЕ эмитируется,"
                     + " иначе cyclic import обрушил бы сборку целиком. Вызов упадёт при рендере.");

            // Доступ блока к диспетчу (#768). Статический импорт агрегатора здесь дал бы
            // `cyclic import` — агрегатор импортирует этот модуль. Импорт в ТЕЛЕ функции петли не
            // создаёт: он исполняется при первом вызове, когда агрегатор уже вычислен и закеширован.
            // Во flat-файле такой возможности не было вовсе — хелпер эмитился ниже блоков.
            Emit("// Диспетчеризация по типу (#768) — импорт отложенный, статический дал бы cyclic import:");
            Emit(LazyImport(DispatchFnName, $"../{TypeBlockSlug.EntrypointName}"));

            foreach (var idx in TopoSortWithin(m.Indices, deps, list, diagnostics))
            {
                var r = list[idx];
                sb.Append("// ").Append(r.Provenance).Append('\n');
                line++;
                // Явный '\n' (не Environment.NewLine) — чтобы номера строк совпадали с тем, что
                // видит Typst, кросс-платформенно.
                var def = $"#let {r.FnName}(it) = {r.Block}";
                int defLines = 1 + def.Count(c => c == '\n');
                spans.Add(new(r.FnName, r.Provenance, r.TypeId, line, line + defLines - 1, path));
                sb.Append(def).Append('\n');
                line += defLines;
            }

            files.Add(new(path, sb.ToString()));
        }

        // ── Агрегатор ─────────────────────────────────────────────────────────────────────────
        var agg = new StringBuilder();
        agg.Append($"// Блоки отображения типов: по файлу на тип в {TypeBlockSlug.FolderName}/ (issue #772).\n");
        agg.Append("// Точка входа: шаблон импортирует ЭТОТ файл и обращается к блокам по коду типа —\n");
        agg.Append("// #Организация.полный(...). Имена блоков уникальны внутри типа (issue #773).\n");
        foreach (var m in modules)
        {
            // Тип, чей код нельзя записать как имя Typst, не импортируется вовсе: строка `as <код>`
            // не скомпилировалась бы, и вместе с ней рухнула бы вся сборка — из-за одного типа.
            // Его блоки остаются в своём файле (видны в просмотрщике и в бандле), но не адресуемы.
            if (!IsTypstIdentifier(m.TypeCode))
            {
                diagnostics.Add(new(TypstBlockDiagnosticSeverity.Error, "code-not-identifier",
                    $"Код типа «{San(m.TypeCode)}» ({San(m.TypeName)}) нельзя использовать как имя в Typst"
                    + " — блоки этого типа недоступны шаблонам. Код должен начинаться с буквы или «_»"
                    + " и состоять из букв, цифр, «_» и «-».",
                    m.Indices.Select(i => list[i].FnName).ToList()));
                continue;
            }
            agg.Append($"#import \"{TypeBlockSlug.PathFor(m.Slug)}\" as {m.TypeCode}\n");
        }
        // Таблица — в ИСХОДНОМ порядке записей, а не в порядке эмиссии: топосорт переставляет блоки
        // по зависимостям, и на живых типах это уже перемешало варианты внутри типа («Организация»
        // отдавала ИНН/КПП там, где в схеме первым стоит «Наименование + коды»). А «первый вариант»
        // — то, что человек видит первым в редакторе схемы; связывать его с порядком компиляции
        // значило бы менять вывод шаблона от правки чужого блока.
        AppendDispatch(agg, list, unionCodes ?? Array.Empty<string>(), diagnostics);
        files.Insert(0, new(TypeBlockSlug.EntrypointName, agg.ToString()));

        return new(files, spans, diagnostics);
    }

    /// <summary>Данные модуля во время сборки (тип → файл).</summary>
    private sealed class ModuleBuild(Guid typeId, string typeName, string typeCode)
    {
        public Guid TypeId { get; } = typeId;
        public string TypeName { get; } = typeName;
        public string TypeCode { get; } = typeCode;
        public string Slug { get; set; } = "";
        public List<int> Indices { get; } = new();

        public string Title() => string.IsNullOrWhiteSpace(TypeCode) ? TypeName : $"{TypeName} ({TypeCode})";
    }

    /// <summary>Одна строка без переводов строк — провенанс не должен сбивать line-map.</summary>
    private static string San(string? s) => (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();

    /// <summary>
    /// Отложенный импорт: имя связывается функцией-переходником, и сам <c>import</c> исполняется
    /// только при вызове. Так разрывается любая петля импортов — Typst видит статический граф без
    /// обратного ребра. Цена — порядка сотой доли миллисекунды на вызов.
    /// </summary>
    private static string LazyImport(string fnName, string modulePath) =>
        $"#let {fnName}(..args) = {{ import \"{modulePath}\": {fnName} as _fn; _fn(..args) }}";

    /// <summary>Имена функций модуля <paramref name="target"/>, которые вызывают блоки модуля
    /// <paramref name="source"/> (для точечного отложенного импорта — wildcard в теле функции
    /// привязок наружу не даёт).</summary>
    private static IEnumerable<string> CalledFrom(
        List<int> source, List<int> target, List<HashSet<int>> deps, List<TypstBlockRecord> list)
    {
        var t = target.ToHashSet();
        return source.SelectMany(i => deps[i]).Where(t.Contains).Distinct()
            .Select(i => list[i].FnName).OrderBy(x => x, StringComparer.Ordinal);
    }

    /// <summary>
    /// Топосорт блоков ОДНОГО модуля (Kahn с тай-брейком по исходному индексу: зависимости раньше
    /// зависимых, независимые — в порядке схемы). Рёбра к чужим типам игнорируются: их разводит
    /// импорт, а не порядок строк. Цикл внутри модуля неразрешим (все блоки в одной области) —
    /// Error и best-effort порядок, как было во flat-файле.
    /// </summary>
    private static List<int> TopoSortWithin(
        List<int> indices, List<HashSet<int>> deps, List<TypstBlockRecord> list,
        List<TypstBlockDiagnostic> diagnostics)
    {
        var inModule = indices.ToHashSet();
        var local = new Dictionary<int, HashSet<int>>();
        foreach (var i in indices) local[i] = deps[i].Where(inModule.Contains).ToHashSet();

        var remaining = indices.ToDictionary(i => i, i => local[i].Count);
        var dependents = indices.ToDictionary(i => i, _ => new List<int>());
        foreach (var i in indices) foreach (var d in local[i]) dependents[d].Add(i);

        var ready = new SortedSet<int>(indices.Where(i => remaining[i] == 0));
        var order = new List<int>(indices.Count);
        var done = new HashSet<int>();
        while (ready.Count > 0)
        {
            var i = ready.Min; ready.Remove(i);
            order.Add(i); done.Add(i);
            foreach (var dep in dependents[i])
                if (!done.Contains(dep) && --remaining[dep] == 0) ready.Add(dep);
        }

        if (order.Count < indices.Count)
        {
            // Индексы блоков плотные только глобально, поэтому цикл ищем по локальной карте.
            var pos = indices.Select((i, k) => (i, k)).ToDictionary(x => x.i, x => x.k);
            var compact = indices.Select(i => local[i].Select(d => pos[d]).ToHashSet()).ToList();
            var emitted = indices.Select(done.Contains).ToArray();
            foreach (var cycle in FindCycles(compact, emitted))
                diagnostics.Add(new(TypstBlockDiagnosticSeverity.Error, "cycle",
                    "Взаимные ссылки между блоками одного типа — Typst не может их упорядочить: "
                    + string.Join(" → ", cycle.Select(k => list[indices[k]].FnName))
                    + " → " + list[indices[cycle[0]]].FnName,
                    cycle.Select(k => list[indices[k]].FnName).ToList()));
            order.AddRange(indices.Where(i => !done.Contains(i)));
        }
        return order;
    }

    /// <summary>Имена, которые занимает диспетч-часть; столкновение с блоком пользователя диагностируем.</summary>
    public const string DispatchTableName = "type-renders";
    public const string DispatchFnName = "render-by-type";
    public const string UnionSetName = "union-types";

    /// <summary>
    /// Диспетч-таблица «код типа → варианты» и хелпер <c>render-by-type</c> (issue #768).
    ///
    /// <para>Ставится ПОСЛЕ всех <c>#let</c>: таблица держит сами функции значениями, а замыкание в
    /// Typst захватывает область на месте определения — до своего блока имя ещё не связано. Порядок
    /// блоков между собой уже разрешён топосортом, поэтому «в конец» здесь достаточно.</para>
    ///
    /// <para>Варианты — МАССИВ пар, а не словарь «имя → функция», хотя словарь читался бы короче.
    /// Имя варианта задаёт админ в UI и ничем не ограничено, а <b>повторяющийся ключ словаря Typst —
    /// ошибка компиляции</b> (проверено: <c>error: duplicate key</c>), то есть два одинаково названных
    /// варианта у одного типа уронили бы весь <c>typeblocks.typ</c>, а с ним генерацию ВСЕХ документов.
    /// Массив такой возможности не даёт вовсе. Порядок в нём явный — на «первый по порядку» можно
    /// опереться, не полагаясь на порядок ключей словаря.</para>
    /// </summary>
    private static void AppendDispatch(
        StringBuilder sb, IReadOnlyList<TypstBlockRecord> declared, IReadOnlyCollection<string> unionCodes,
        List<TypstBlockDiagnostic> diagnostics)
    {
        // ВАЖНО: `declared` — записи в порядке ОБЪЯВЛЕНИЯ, а не эмиссии (см. вызов). От этого зависит,
        // какой вариант достаётся `variant: auto`; передача сюда топологически отсортированного
        // списка тихо сменила бы вывод шаблонов (тест VariantOrder_FollowsDeclaration_...).
        //
        // Столкновение имён: пользовательский блок с таким именем перекрыл бы наш #let (в Typst
        // повторный #let не ошибка — молча побеждает последний), и шаблоны получили бы вместо
        // хелпера чужую функцию. Молчать нельзя, отменять эмиссию — тоже: сломается ровно то, что
        // человек написал сам.
        foreach (var reserved in new[] { DispatchTableName, DispatchFnName, UnionSetName })
            if (declared.Any(r => r.FnName == reserved))
                diagnostics.Add(new(TypstBlockDiagnosticSeverity.Error, "reserved-fn",
                    $"Имя «{reserved}» занято диспетчеризацией по типу (issue #768) — переименуйте функцию блока: "
                    + string.Join("; ", declared.Where(r => r.FnName == reserved).Select(r => r.Provenance)),
                    new[] { reserved }));

        // В таблицу идут только блоки типов с кодом — код и есть адрес в `_type.chain`. И только с
        // кодом-идентификатором: значение записи — `Код.имя`, а модуль типа с «неудобным» кодом не
        // импортирован (см. эмиссию агрегатора), так что запись о нём уронила бы весь файл.
        var byCode = declared.Where(r => IsTypstIdentifier(r.TypeCode))
            .GroupBy(r => r.TypeCode)
            .ToList();

        sb.Append('\n').Append("// ── Диспетчеризация по типу (issue #768) ──\n");
        sb.Append("// Таблица «код типа → варианты отображения». Ключ совпадает с кодом в data._type.chain.\n");
        sb.Append($"#let {DispatchTableName} = (");
        if (byCode.Count == 0)
        {
            // Пустой словарь в Typst — `(:)`; `()` был бы пустым МАССИВОМ, и `code in type-renders`
            // на нём работает иначе (ищет элемент, а не ключ).
            sb.Append(":)\n");
        }
        else
        {
            sb.Append('\n');
            foreach (var g in byCode)
            {
                sb.Append("  ").Append(Str(g.Key)).Append(": (");
                foreach (var r in g)
                    // Функция адресуется через алиас модуля (#773): в агрегаторе голого имени блока
                    // больше нет — модули импортируются `as <Код>`, а не «: *».
                    sb.Append("(name: ").Append(Str(r.VariantName))
                      .Append(", fn: ").Append(r.TypeCode).Append('.').Append(r.FnName).Append("), ");
                sb.Append("),\n");
            }
            sb.Append(")\n");
        }

        // Коды union-типов — по ним хелпер узнаёт СТРОКУ union-массива. Массив, а не словарь:
        // нужна только принадлежность, `in` работает для обоих, а массив не требует значений.
        sb.Append("// Коды типов «заполняется ровно один вариант» (#320) — их строки разворачиваются.\n");
        sb.Append($"#let {UnionSetName} = (");
        foreach (var code in unionCodes) sb.Append(Str(code)).Append(", ");
        sb.Append(")\n");

        sb.Append(DispatchHelper);
    }

    /// <summary>Строковый литерал Typst: экранируются кавычка и обратный слэш, переводы строк не
    /// проходят в однострочный литерал.</summary>
    private static string Str(string? s)
    {
        var v = (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", " ").Replace("\n", " ");
        return $"\"{v}\"";
    }

    /// <summary>
    /// Хелпер диспетчеризации. Поведение проверено на живом Typst до написания эмиссии:
    /// подтип берёт блок предка; без <c>variant</c> — первый по порядку; именованный вариант
    /// находится; union-строка разворачивается; всё непокрытое даёт ВИДИМУЮ заглушку, а не пустоту —
    /// молчащий рендер невозможно отличить от «объекта не было».
    /// </summary>
    private const string DispatchHelper = """

// Отобразить объект его собственным блоком: идём по data._type.chain от фактического типа вверх,
// берём первый тип, у которого блок есть (наследование — как у матчеров вариантов на сервере).
//   #render-by-type(строка)                     — первый вариант
//   #render-by-type(строка, variant: "Краткое") — именованный
#let render-by-type(obj, variant: auto) = {
  if type(obj) != dictionary {
    text(fill: red)[⚠ render-by-type: ожидался объект, получено #type(obj)]
  } else {
    let meta = obj.at("_type", default: none)
    let chain = if meta == none { () } else { meta.at("chain", default: ()) }
    // Ищем ПОДХОДЯЩИЙ вариант по всей цепочке, а не блок у первого попавшегося типа: имя варианта
    // («Полное», «ИНН/КПП») описывает СПОСОБ показа и живёт на уровне семейства типов. Подтип,
    // объявивший свой единственный вариант, не должен отнимать у шаблона право попросить вариант
    // предка — на живых данных ровно так и вышло: «Подрядчик» имеет тип «Организация в СРО» с одним
    // вариантом, а «ИНН/КПП» объявлен у «Организации» выше по цепочке.
    let pick = none
    for code in chain {
      if pick == none and code in type-renders {
        let candidates = type-renders.at(code)
        pick = if variant == auto { candidates.at(0) } else { candidates.find(v => v.name == variant) }
      }
    }
    // Строка union-массива — это {Вариант: значение}: разворачиваем единственный содержательный
    // ключ и диспетчим по значению, у которого свой _type (после резолва ссылки — фактический).
    // Признак — КОД ТИПА в union-types, а не форма объекта: у строки union тоже стоит _type, а
    // «ровно одно заполненное составное поле» бывает у чего угодно (незаполненные ключи в документ
    // не пишутся), и разворот по форме подменял бы пометку «нет блока» чужим содержимым.
    let unwrap = none
    if meta != none and meta.at("code", default: "") in union-types {
      let keys = obj.keys().filter(k => k != "_type")
      if keys.len() == 1 and type(obj.at(keys.at(0))) == dictionary { unwrap = obj.at(keys.at(0)) }
    }
    if pick != none {
      (pick.fn)(obj)
    } else if unwrap != none {
      // Разворот проверяется ПЕРЕД жалобой на вариант: у union-строки запрошенный вариант обычно
      // объявлен у типа ЗНАЧЕНИЯ, а не у самого union'а, и ранняя жалоба отменяла бы разворот,
      // обещанный в инструкции.
      render-by-type(unwrap, variant: variant)
    } else if variant != auto and chain.any(c => c in type-renders) {
      // Блоки у типа есть, а варианта с таким именем нет ни у кого в цепочке — это опечатка в
      // шаблоне, а не отсутствие оформления. Разные случаи — разные сообщения.
      text(fill: red)[⚠ нет варианта «#variant» ни у одного типа в цепочке]
    } else {
      let name = if meta == none { "без _type" } else { meta.at("name", default: "?") }
      text(fill: red)[⚠ нет Typst-блока для типа «#name»]
    }
  }
}
""";

    /// <summary>
    /// Пути к файлам в тексте блока, записанные ОТНОСИТЕЛЬНО (issue #772). Блоки переехали в
    /// подпапку, и точка отсчёта у таких путей сместилась на уровень вниз: <c>import "userlib.typ"</c>
    /// ищется как <c>typeblocks/userlib.typ</c> и не находится.
    ///
    /// <para>Сказать об этом обязана сборка, потому что больше некому: импорт внутри тела функции
    /// ленив, проверка блоков (#309, фаза 2) только парсит файлы и до него не доходит — она осталась
    /// бы зелёной, а падала бы генерация каждого документа с таким блоком.</para>
    ///
    /// <para>Ищем строковые литералы, похожие на путь к файлу (по расширению), кроме уже правильных:
    /// начинающихся с «/» (от корня проекта Typst), с «../» и сетевых. Правильный ответ —
    /// <c>"/userlib.typ"</c>: корень задаёт сама компиляция, и такой путь не зависит от того, где
    /// лежит файл блока.</para>
    ///
    /// <para>Severity — предупреждение, хотя генерация с таким путём падает. Признак эвристический
    /// (строка «похожа на путь»), а Error в этой сборке означает «файл не соберётся» и обязан быть
    /// точным: «data.json» в тексте документа — законный текст, и краснеть на него нельзя.</para>
    /// </summary>
    private static IEnumerable<string> RelativePathsIn(string block)
    {
        var cleaned = TypstTextMask.Mask(block, TypstTextMask.Keep.StringsOnly);
        foreach (Match m in FilePathLiteral.Matches(cleaned))
        {
            var path = m.Groups[1].Value;
            if (path.StartsWith('/') || path.StartsWith("../", StringComparison.Ordinal)
                || path.Contains("://", StringComparison.Ordinal)) continue;
            yield return path;
        }
    }

    private static readonly Regex FilePathLiteral = new(
        @"""([^""\n]+\.(?:typ|json|csv|toml|yaml|yml|xml|png|jpg|jpeg|gif|svg|pdf|bib))""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Годится ли код типа как имя в Typst (issue #773): им называется алиас модуля, и через него
    /// шаблон обращается к блокам. Правило языка: первый символ — буква (любого алфавита, кириллица
    /// в том числе) или «_», дальше буквы, цифры, «_» и «-». Проверено на живом CLI: код с цифры
    /// даёт «expected identifier», код с дефисом внутри легален.
    /// </summary>
    public static bool IsTypstIdentifier(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        if (!char.IsLetter(code[0]) && code[0] != '_') return false;
        if (code[^1] == '-') return false;                       // «АОСР-» обрывает выражение
        if (TypstKeywords.Contains(code)) return false;
        return code.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-');
    }

    /// <summary>Слова, которые Typst не отдаёт под имя: `as none` не парсится — проверено на CLI
    /// (`expected identifier, found none`). Список короткий и закрытый, поэтому здесь, а не в
    /// настройке.</summary>
    private static readonly HashSet<string> TypstKeywords = new(StringComparer.Ordinal)
    {
        "none", "auto", "true", "false", "let", "set", "show", "context", "if", "else", "for",
        "while", "break", "continue", "return", "import", "include", "as", "in", "and", "or", "not",
    };

    /// <summary>Вызов блока СВОЕГО типа: просто имя — оно в общей области модуля.</summary>
    private static string CallOfOwn(string fnName)
        => $@"(?<![\w\-.]){Regex.Escape(fnName)}\s*\(";

    /// <summary>
    /// Вызов блока ЧУЖОГО типа: <c>Код.имя(</c> — модуль импортируется под алиасом-кодом (#773).
    /// Тип без кода адресовать нечем: его блоки в чужих модулях недоступны, и ребра не будет.
    /// </summary>
    private static string? CallOfOther(string typeCode, string fnName)
        => string.IsNullOrWhiteSpace(typeCode)
            ? null
            : $@"(?<![\w\-.]){Regex.Escape(typeCode)}\s*\.\s*{Regex.Escape(fnName)}\s*\(";

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
