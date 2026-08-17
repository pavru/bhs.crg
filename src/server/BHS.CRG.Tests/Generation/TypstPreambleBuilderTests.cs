using System.Text.Json;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;

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

    // ── Диспетч-таблица и render-by-type (issue #768) ────────────────────────

    private static TypstBlockRecord C(string fn, string code, string variant = "осн", string block = "{ it.x }") =>
        new(fn, block, $"prov:{fn}", Guid.NewGuid(), "T", variant, code);

    /// <summary>
    /// Таблица держит сами функции значениями, а замыкание Typst захватывает область НА МЕСТЕ
    /// определения: стой она выше своих блоков — `unknown variable` на каждом. Та же причина, по
    /// которой блоки топологически сортируются (#309), поэтому проверяем не «таблица есть», а её
    /// место относительно последнего определения.
    /// </summary>
    [Fact]
    public void DispatchTable_ComesAfterAllBlockDefinitions()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { C("a", "КодA"), C("b", "КодB") });
        var table = res.Content.IndexOf("#let type-renders", StringComparison.Ordinal);
        Assert.True(table > Idx(res.Content, "a"));
        Assert.True(table > Idx(res.Content, "b"));
        Assert.True(res.Content.IndexOf("#let render-by-type", StringComparison.Ordinal) > table);
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
            res.Content);
    }

    /// <summary>
    /// Варианты — массив пар, а не словарь: повторяющийся ключ словаря Typst это ОШИБКА КОМПИЛЯЦИИ
    /// (`duplicate key`), то есть два одинаково названных варианта у одного типа уронили бы весь
    /// typeblocks.typ, а с ним генерацию всех документов. Имя варианта пишет админ, ограничений на
    /// него нет — значит форма таблицы обязана переживать совпадение.
    /// </summary>
    [Fact]
    public void DuplicateVariantNames_DoNotCollapseAndDoNotBreakTheFile()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[]
        {
            C("first", "Тип", "одно"),
            C("second", "Тип", "одно"),
        });
        Assert.Contains("(name: \"одно\", fn: first), (name: \"одно\", fn: second)", res.Content);
    }

    /// <summary>Пустой код адресовать нечем: в таблицу такой блок не попадает, но определение остаётся —
    /// шаблон, зовущий функцию по имени, продолжает работать.</summary>
    [Fact]
    public void BlockOfTypeWithoutCode_IsSkippedInTable_ButStillDefined()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { C("named", ""), C("coded", "Код") });
        Assert.Contains("#let named(it)", res.Content);
        Assert.DoesNotContain("fn: named", res.Content);
        Assert.Contains("fn: coded", res.Content);
    }

    /// <summary>Без единого кодированного блока таблица обязана быть пустым СЛОВАРЁМ `(:)`, а не `()`:
    /// `()` — пустой массив, и `code in type-renders` на нём ищет элемент, а не ключ.</summary>
    [Fact]
    public void EmptyTable_IsEmptyDictionaryNotArray()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { R("a", "{ it.x }") });
        Assert.Contains("#let type-renders = (:)", res.Content);
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
        var res = TypstPreambleBuilder.BuildDetailed(new[] { C(reserved, "Код") });
        var d = Assert.Single(res.Diagnostics);
        Assert.Equal("reserved-fn", d.Code);
        Assert.Equal(TypstBlockDiagnosticSeverity.Error, d.Severity);
    }

    /// <summary>Кавычка в имени варианта не должна рвать строковый литерал таблицы.</summary>
    [Fact]
    public void VariantName_WithQuote_IsEscaped()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { C("f", "Код", """с "кавычкой" """) });
        Assert.Contains("""(name: "с \"кавычкой\" ", fn: f)""", res.Content);
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
        Assert.True(Idx(res.Content, "helper") < Idx(res.Content, "main"));   // порядок ОПРЕДЕЛЕНИЙ
        Assert.Contains("(name: \"Основной\", fn: main), (name: \"Вспомогательный\", fn: helper)",
            res.Content);                                                     // порядок ВАРИАНТОВ
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
        Assert.Contains("#let union-types = (\"Строка\", \"Другой\", )", res.Content);
    }

    [Fact]
    public void WithoutUnionCodes_SetIsEmpty_SoNothingUnwrapsByShape()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { C("f", "Код") });
        Assert.Contains("#let union-types = ()", res.Content);
    }

    /// <summary>
    /// Сборка не зависит от порядка, в котором типы пришли из репозитория (issue #770). Тот отдаёт их
    /// без ORDER BY, и PostgreSQL после UPDATE любой строки возвращает набор иначе — файл менялся
    /// перестановкой независимых блоков. Работе это не мешало, но ломало сравнение файла с самим собой
    /// и обещание экрана «показываю то, что уходит в Typst»: экран и генерация делают РАЗНЫЕ запросы.
    /// </summary>
    [Fact]
    public void Build_IsIndependentOfRepositoryOrder()
    {
        var a = Type("КодA", "a-block");
        var b = Type("КодB", "b-block");
        Assert.Equal(
            TypstPreambleBuilder.Build(new[] { a, b }),
            TypstPreambleBuilder.Build(new[] { b, a }));
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

    /// <summary>Line-map указывает на блоки, а не на диспетч-часть: она идёт после, номера строк
    /// блоков не смещаются, и ошибка Typst по-прежнему маппится на свой тип.</summary>
    [Fact]
    public void DispatchSection_DoesNotShiftBlockLineMap()
    {
        var res = TypstPreambleBuilder.BuildDetailed(new[] { C("f", "Код") });
        var span = Assert.Single(res.Spans);
        var lines = res.Content.Split('\n');
        Assert.StartsWith("#let f(", lines[span.StartLine - 1]);
    }
}
