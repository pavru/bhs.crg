using System.Text.Json;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Сборка блоков типов: раскладка по файлам (issue #772), топосорт внутри модуля и диагностики
/// (issue #309), диспетч-часть в агрегаторе (issue #768).
///
/// Порядок ОПРЕДЕЛЕНИЙ значим только внутри модуля — замыкание Typst захватывает область на месте
/// определения; между модулями зависимости разводят импорты, поэтому взаимный вызов двух ТИПОВ
/// перестал быть ошибкой порядка и стал петлёй импортов (её сборка разрывает отложенным импортом).
/// </summary>
public class TypstPreambleBuilderTests
{
    // Один и тот же тип для всех записей без явного кода: без этого каждая запись уезжала бы в
    // собственный модуль, и тесты порядка проверяли бы порядок файлов, а не строк.
    private static readonly Guid OneType = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static TypstBlockRecord R(string fn, string block) =>
        new(fn, block, $"prov:{fn}", OneType, "T", fn);

    private static readonly Dictionary<string, Guid> IdsByCode = new();
    private static Guid IdFor(string code)
    {
        lock (IdsByCode)
        {
            if (!IdsByCode.TryGetValue(code, out var g)) IdsByCode[code] = g = Guid.NewGuid();
            return g;
        }
    }

    /// <summary>Запись типа с кодом. Один код = один тип (и, значит, один модуль) — как в системе.</summary>
    private static TypstBlockRecord C(string fn, string code, string variant = "осн", string block = "{ it.x }") =>
        new(fn, block, $"prov:{fn}", IdFor(code), code, variant, code);

    private static string Entry(TypstPreambleResult res) => res.Entrypoint;

    private static string Module(TypstPreambleResult res, string slug) =>
        res.Files.Single(f => f.Path == $"typeblocks/{slug}.typ").Content;

    /// <summary>Модуль единственного типа — когда слаг для теста не важен.</summary>
    private static string OnlyModule(TypstPreambleResult res) =>
        res.Files.Single(f => f.Path != "typeblocks.typ").Content;

    private static int Idx(string content, string fn) => content.IndexOf($"#let {fn}(", StringComparison.Ordinal);

    // ── Раскладка по файлам (issue #772) ─────────────────────────────────────

    [Fact]
    public void Entrypoint_IsFirst_AndImportsEveryModule()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { C("a", "КодA"), C("b", "КодB") });

        Assert.Equal("typeblocks.typ", res.Files[0].Path);
        Assert.Contains("#import \"typeblocks/КодA.typ\": *", Entry(res));
        Assert.Contains("#import \"typeblocks/КодB.typ\": *", Entry(res));
        Assert.Equal(3, res.Files.Count);
    }

    /// <summary>Реэкспорт wildcard'ом, а не алиасом: имена блоков остаются глобальными, поэтому
    /// шаблоны и тексты блоков после раскола не переписываются (адресация `Код.Имя` — это #773).</summary>
    [Fact]
    public void Entrypoint_ReexportsFlatNames_NoAliasing()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { C("org-full", "Организация") });
        Assert.Contains(": *", Entry(res));
        Assert.DoesNotContain(" as Организация", Entry(res));
    }

    /// <summary>Агрегатор существует ВСЕГДА: шаблон импортирует его дословно (#353), и отсутствие
    /// файла было бы ошибкой компиляции у каждого документа, а не «пустой библиотекой».</summary>
    [Fact]
    public void NoBlocksAtAll_StillProducesEntrypoint()
    {
        var res = TypstPreambleBuilder.BuildDetailed(Array.Empty<TypstBlockRecord>());
        var only = Assert.Single(res.Files);
        Assert.Equal("typeblocks.typ", only.Path);
        Assert.Contains("#let render-by-type", only.Content);
    }

    /// <summary>Диспетч #768 живёт в агрегаторе (только он знает все типы), а нужен внутри блоков.
    /// Статический импорт агрегатора из модуля — `cyclic import`, поэтому в шапке модуля стоит
    /// переходник с импортом в ТЕЛЕ функции. Во flat-файле такой возможности не было вовсе.</summary>
    [Fact]
    public void EveryModule_GetsLazyDispatchShim()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { C("a", "КодA"), C("b", "КодB") });
        foreach (var slug in new[] { "КодA", "КодB" })
            Assert.Contains(
                "#let render-by-type(..args) = { import \"../typeblocks.typ\": render-by-type as _fn; _fn(..args) }",
                Module(res, slug));
    }

    [Fact]
    public void CrossTypeCall_BecomesStaticImport_NotOrdering()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[]
        {
            C("caller", "Вызывающий", "осн", "{ callee(it) }"),
            C("callee", "Вызываемый"),
        });
        Assert.Contains("#import \"Вызываемый.typ\": *", Module(res, "Вызывающий"));
        Assert.DoesNotContain("#import", Module(res, "Вызываемый").Replace("import \"../typeblocks.typ\"", ""));
        Assert.Empty(res.Diagnostics);
    }

    /// <summary>
    /// Взаимная ссылка между ТИПАМИ: статические импорты по кругу Typst запрещает целиком
    /// (`error: cyclic import`), и один такой вызов обрушил бы генерацию ВСЕХ документов — хуже, чем
    /// было во flat-файле, где ломался только сам вызов. Поэтому ребро цикла эмитится отложенным
    /// импортом: связь работает, диагностика предупреждающая.
    /// </summary>
    [Fact]
    public void CrossTypeCycle_UsesLazyImport_AndWarns()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[]
        {
            C("a", "ТипA", "осн", "{ b(it) }"),
            C("b", "ТипB", "осн", "{ a(it) }"),
        });

        Assert.Contains("#let b(..args) = { import \"ТипB.typ\": b as _fn; _fn(..args) }", Module(res, "ТипA"));
        Assert.Contains("#let a(..args) = { import \"ТипA.typ\": a as _fn; _fn(..args) }", Module(res, "ТипB"));
        Assert.DoesNotContain("#import \"ТипB.typ\"", Module(res, "ТипA"));

        var d = Assert.Single(res.Diagnostics);
        Assert.Equal("cycle-cross-type", d.Code);
        Assert.Equal(TypstBlockDiagnosticSeverity.Warning, d.Severity);
    }

    // ── Пути внутри блока после переезда в подпапку (issue #772) ─────────────

    /// <summary>
    /// Блоки лежат в подпапке, и точка отсчёта относительных путей внутри блока сместилась:
    /// `import "userlib.typ"` ищется как `typeblocks/userlib.typ`. Сказать об этом обязана сборка —
    /// импорт в теле функции ленив, проверка блоков только парсит и до него не доходит, так что
    /// иначе поломка была бы тихой и вылезла бы на генерации каждого документа.
    /// </summary>
    [Fact]
    public void RelativePathInsideBlock_IsWarned_WithRootedSuggestion()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[]
        {
            R("f", "{ import \"userlib.typ\": dig\n dig(it, \"x\") }"),
        });
        var d = Assert.Single(res.Diagnostics, x => x.Code == "relative-path");
        Assert.Equal(TypstBlockDiagnosticSeverity.Warning, d.Severity);
        Assert.Contains("«/userlib.typ»", d.Message);
    }

    [Theory]
    [InlineData("{ image(\"assets/logo.png\") }")]        // картинка ассета
    [InlineData("{ let d = json(\"data.json\") }")]       // данные
    public void OtherRelativeFileReferences_AreWarnedToo(string block)
        => Assert.Contains(TypstPreambleBuilder.BuildDetailed(new[] { R("f", block) }).Diagnostics,
            d => d.Code == "relative-path");

    /// <summary>Уже правильные записи предупреждения не получают — иначе оно превратилось бы в шум,
    /// который перестают читать.</summary>
    [Theory]
    [InlineData("{ import \"/userlib.typ\": dig }")]      // от корня проекта Typst — так и надо
    [InlineData("{ import \"../userlib.typ\": dig }")]    // тоже находится
    [InlineData("{ [https://example.com/a.pdf] }")]       // не путь к файлу
    [InlineData("{ // import \"userlib.typ\"\n it.x }")]  // упоминание в комментарии
    public void CorrectOrIrrelevantPaths_AreNotWarned(string block)
        => Assert.DoesNotContain(TypstPreambleBuilder.BuildDetailed(new[] { R("f", block) }).Diagnostics,
            d => d.Code == "relative-path");

    // ── Порядок определений внутри модуля (issue #309) ───────────────────────

    [Fact]
    public void Dependency_IsEmittedBeforeDependent()
    {
        // a вызывает b → #let b обязан идти выше #let a, хотя a передан первым.
        var res = TypstPreambleBuilder.BuildDetailed(new[] { R("a", "{ b(it) }"), R("b", "{ it.x }") });
        var m = OnlyModule(res);
        Assert.True(Idx(m, "b") < Idx(m, "a"));
        Assert.Empty(res.Diagnostics);
    }

    [Fact]
    public void IndependentBlocks_KeepOriginalOrder()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { R("first", "{ it.x }"), R("second", "{ it.y }") });
        var m = OnlyModule(res);
        Assert.True(Idx(m, "first") < Idx(m, "second"));
        Assert.Empty(res.Diagnostics);
    }

    [Fact]
    public void SelfRecursion_IsNotACycle()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { R("f", "{ if it.n > 0 { f(it) } }") });
        Assert.Empty(res.Diagnostics);
    }

    /// <summary>Цикл ВНУТРИ типа импортом не развести — блоки в одной области. Остаётся Error и
    /// best-effort порядок, как было во flat-файле (Typst ленив и сам финальный арбитр).</summary>
    [Fact]
    public void MutualReference_WithinType_ReportsCycle_ButStillEmitsBoth()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { R("a", "{ b(it) }"), R("b", "{ a(it) }") });
        Assert.Contains(res.Diagnostics, d => d.Code == "cycle" && d.Severity == TypstBlockDiagnosticSeverity.Error);
        var m = OnlyModule(res);
        Assert.True(Idx(m, "a") >= 0 && Idx(m, "b") >= 0);
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
        var m = OnlyModule(res);
        Assert.True(Idx(m, "a") < Idx(m, "b"));
        Assert.Empty(res.Diagnostics);
    }

    [Fact]
    public void Emits_ProvenanceComment_AndLineMap()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { R("f", "{ it.x }") });
        var m = OnlyModule(res);
        Assert.Contains("// prov:f", m);
        var span = Assert.Single(res.Spans);
        Assert.Equal("f", span.FnName);
        Assert.Equal("typeblocks/T.typ", span.File);
        Assert.StartsWith("#let f(", m.Split('\n')[span.StartLine - 1]);
    }

    [Fact]
    public void LineMap_TracksMultiLineBlocks()
    {
        // `#let f(it) = {\n it.x \n}` — два перевода строки внутри, значит span покрывает три строки.
        var res = TypstPreambleBuilder.BuildDetailed(new[] { R("f", "{\n it.x \n}") });
        var span = Assert.Single(res.Spans);
        var lines = OnlyModule(res).Split('\n');
        Assert.StartsWith("#let f(", lines[span.StartLine - 1]);
        Assert.Equal(2, span.EndLine - span.StartLine);
        Assert.Equal("}", lines[span.EndLine - 1]);
    }

    [Fact]
    public void Chain_OrdersTransitively()
    {
        // c→b→a: итог должен идти a, b, c (каждая зависимость выше зависимого), хотя дан обратный порядок.
        var res = TypstPreambleBuilder.BuildDetailed(new[] { R("c", "{ b(it) }"), R("b", "{ a(it) }"), R("a", "{ it.x }") });
        var m = OnlyModule(res);
        Assert.True(Idx(m, "a") < Idx(m, "b"));
        Assert.True(Idx(m, "b") < Idx(m, "c"));
        Assert.Empty(res.Diagnostics);
    }

    // ── Имена файлов модулей ─────────────────────────────────────────────────

    /// <summary>Коды типов уникальны регистрозависимо, а на Windows «Акт.typ» и «акт.typ» — один
    /// файл: без регистронезависимой развязки один тип молча съел бы блоки другого, причём только
    /// на части платформ.</summary>
    [Fact]
    public void SlugsCollidingOnlyByCase_GetDistinctFiles()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { C("f1", "Акт"), C("f2", "акт") });
        var paths = res.Files.Select(f => f.Path).Where(p => p != "typeblocks.typ").ToList();
        Assert.Equal(2, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void CodeWithPathCharacters_IsSanitizedIntoFileName()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { C("f", "А/Б:В") });
        var path = res.Files.Single(f => f.Path != "typeblocks.typ").Path;
        Assert.Equal("typeblocks/А_Б_В.typ", path);
        Assert.Contains($"#import \"{path}\": *", Entry(res));
    }

    /// <summary>Тип без кода не теряет блоки: слаг берётся из имени. Адресовать его в диспетч-таблице
    /// нечем (см. отдельный тест), но определения остаются доступны по имени функции.</summary>
    [Fact]
    public void TypeWithoutCode_StillGetsModule_NamedAfterType()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { R("named", "{ it.x }") });
        Assert.Contains(res.Files, f => f.Path == "typeblocks/T.typ");
    }

    // ── Диспетч-таблица и render-by-type (issue #768) ────────────────────────

    /// <summary>
    /// Таблица держит сами функции значениями, поэтому стоит ПОСЛЕ импортов модулей: до импорта имена
    /// ещё не связаны. Проверяем не «таблица есть», а её место относительно импортов.
    /// </summary>
    [Fact]
    public void DispatchTable_ComesAfterModuleImports()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { C("a", "КодA"), C("b", "КодB") });
        var e = Entry(res);
        var table = e.IndexOf("#let type-renders", StringComparison.Ordinal);
        Assert.True(table > e.IndexOf("#import \"typeblocks/КодA.typ\"", StringComparison.Ordinal));
        Assert.True(table > e.IndexOf("#import \"typeblocks/КодB.typ\"", StringComparison.Ordinal));
        Assert.True(e.IndexOf("#let render-by-type", StringComparison.Ordinal) > table);
    }

    [Fact]
    public void DispatchTable_KeyedByTypeCode_WithAllVariantsInOrder()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[]
        {
            C("full", "Организация", "Полная"),
            C("short", "Организация", "Краткая"),
        });
        Assert.Contains("\"Организация\": ((name: \"Полная\", fn: full), (name: \"Краткая\", fn: short), ),",
            Entry(res));
    }

    /// <summary>
    /// Варианты — массив пар, а не словарь: повторяющийся ключ словаря Typst это ОШИБКА КОМПИЛЯЦИИ
    /// (`duplicate key`), то есть два одинаково названных варианта у одного типа уронили бы весь
    /// файл, а с ним генерацию всех документов. Имя варианта пишет админ, ограничений на него нет —
    /// значит форма таблицы обязана переживать совпадение.
    /// </summary>
    [Fact]
    public void DuplicateVariantNames_DoNotCollapseAndDoNotBreakTheFile()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[]
        {
            C("first", "Тип", "одно"),
            C("second", "Тип", "одно"),
        });
        Assert.Contains("(name: \"одно\", fn: first), (name: \"одно\", fn: second)", Entry(res));
    }

    /// <summary>Пустой код адресовать нечем: в таблицу такой блок не попадает, но определение остаётся —
    /// шаблон, зовущий функцию по имени, продолжает работать.</summary>
    [Fact]
    public void BlockOfTypeWithoutCode_IsSkippedInTable_ButStillDefined()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { R("named", "{ it.x }"), C("coded", "Код") });
        Assert.Contains("#let named(it)", Module(res, "T"));
        Assert.DoesNotContain("fn: named", Entry(res));
        Assert.Contains("fn: coded", Entry(res));
    }

    /// <summary>Без единого кодированного блока таблица обязана быть пустым СЛОВАРЁМ `(:)`, а не `()`:
    /// `()` — пустой массив, и `code in type-renders` на нём ищет элемент, а не ключ.</summary>
    [Fact]
    public void EmptyTable_IsEmptyDictionaryNotArray()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { R("a", "{ it.x }") });
        Assert.Contains("#let type-renders = (:)", Entry(res));
    }

    /// <summary>Пользовательский блок с зарезервированным именем молча перекрыл бы хелпер (повторный
    /// `#let` в Typst не ошибка — побеждает последний), и шаблоны получили бы вместо диспетчера чужую
    /// функцию.</summary>
    [Theory]
    [InlineData("render-by-type")]
    [InlineData("type-renders")]
    [InlineData("union-types")]
    public void ReservedName_IsDiagnosed(string reserved)
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { C(reserved, "Код" + reserved) });
        var d = Assert.Single(res.Diagnostics);
        Assert.Equal("reserved-fn", d.Code);
        Assert.Equal(TypstBlockDiagnosticSeverity.Error, d.Severity);
    }

    /// <summary>Кавычка в имени варианта не должна рвать строковый литерал таблицы.</summary>
    [Fact]
    public void VariantName_WithQuote_IsEscaped()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { C("f", "Код", """с "кавычкой" """) });
        Assert.Contains("""(name: "с \"кавычкой\" ", fn: f)""", Entry(res));
    }

    /// <summary>
    /// Порядок вариантов в таблице — порядок ОБЪЯВЛЕНИЯ, а не эмиссии. Топосорт двигает блоки по
    /// зависимостям, и на живых типах это уже перемешало варианты: «Организация» отдавала первым
    /// «ИНН/КПП», хотя в схеме первым стоит «Наименование + коды». «Первый вариант» — тот, что
    /// человек видит первым в редакторе; иначе вывод шаблона менялся бы от правки ЧУЖОГО блока.
    /// </summary>
    [Fact]
    public void VariantOrder_FollowsDeclaration_NotTopologicalOrder()
    {
        // Первый по объявлению вариант зависит от второго → топосорт поставит его определение НИЖЕ.
        var res = TypstPreambleBuilder.BuildDetailed(new[]
        {
            C("main", "Тип", "Основной", "{ helper(it) }"),
            C("helper", "Тип", "Вспомогательный"),
        });
        var m = Module(res, "Тип");
        Assert.True(Idx(m, "helper") < Idx(m, "main"));                          // порядок ОПРЕДЕЛЕНИЙ
        Assert.Contains("(name: \"Основной\", fn: main), (name: \"Вспомогательный\", fn: helper)",
            Entry(res));                                                         // порядок ВАРИАНТОВ
    }

    /// <summary>
    /// Коды union-типов эмитируются отдельным набором: по форме объекта union-строку от обычного
    /// объекта не отличить (у обеих стоит `_type`, а «одно заполненное составное поле» бывает у
    /// чего угодно — незаполненные ключи в документ не пишутся). Без набора хелпер разворачивал бы
    /// любой такой объект и показывал вложенное значение вместо пометки «нет блока для типа».
    /// </summary>
    [Fact]
    public void UnionCodes_AreEmittedAsSeparateSet()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { C("f", "Строка") }, new[] { "Строка", "Другой" });
        Assert.Contains("#let union-types = (\"Строка\", \"Другой\", )", Entry(res));
    }

    [Fact]
    public void WithoutUnionCodes_SetIsEmpty_SoNothingUnwrapsByShape()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { C("f", "Код") });
        Assert.Contains("#let union-types = ()", Entry(res));
    }

    /// <summary>
    /// Сборка не зависит от порядка, в котором типы пришли из репозитория (issue #770). Тот отдаёт их
    /// без ORDER BY, и PostgreSQL после UPDATE любой строки возвращает набор иначе. Работе это не
    /// мешало, но ломало сравнение файла с самим собой и обещание экрана «показываю то, что уходит в
    /// Typst»: экран и генерация делают РАЗНЫЕ запросы. С расколом (#772) цена ошибки выше — от
    /// порядка зависят ещё и имена файлов при столкновении слагов.
    /// </summary>
    [Fact]
    public void Build_IsIndependentOfRepositoryOrder()
    {
        var a = Type("КодA", "a-block");
        var b = Type("КодB", "b-block");
        var first = TypstPreambleBuilder.Build(new[] { a, b });
        var second = TypstPreambleBuilder.Build(new[] { b, a });

        Assert.Equal(first.Select(f => f.Path), second.Select(f => f.Path));
        Assert.Equal(first.Select(f => f.Content), second.Select(f => f.Content));
    }

    private static DocumentType Type(string code, string fn)
    {
        var t = (DocumentType)Activator.CreateInstance(typeof(DocumentType), nonPublic: true)!;
        typeof(DocumentType).GetProperty(nameof(DocumentType.Code))!.SetValue(t, code);
        typeof(DocumentType).GetProperty(nameof(DocumentType.Name))!.SetValue(t, code);
        typeof(DocumentType).GetProperty(nameof(DocumentType.Schema))!.SetValue(t, JsonDocument.Parse(
            $$"""{"typstRenders":[{"name":"осн","fnName":"{{fn}}","block":"{ it.x }"}]}"""));
        return t;
    }
}
