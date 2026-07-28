using BHS.CRG.Application.Templates;

namespace BHS.CRG.Tests.Templates;

/// <summary>
/// Разбор дерева библиотеки (issue #473). Ловит два режима отказа, созданных самим разрезанием одного
/// файла на много, — оба МОЛЧАЛИВЫЕ: компилятор их ошибками не считает.
/// </summary>
public class UserLibAnalysisTests
{
    private static UserLibFile F(string path, string content) => new(path, content);

    [Fact]
    public void ReachesFilesThroughEntrypoint()
    {
        var files = new[] { F("gost/f3.typ", "#let place-f3() = []") };
        var reachable = UserLibAnalysis.ReachableFrom("#import \"userlib/gost/f3.typ\": *", files);
        Assert.Equal(["gost/f3.typ"], reachable);
    }

    /// <summary>Цепочка: точка входа → файл → его относительный импорт. Так устроена реальная библиотека.</summary>
    [Fact]
    public void ReachesTransitively_ThroughRelativeImports()
    {
        var files = new[]
        {
            F("gost/forms/f3.typ", "#import \"../../util/text.typ\": capitalize-first\n#let place-f3() = []"),
            F("util/text.typ", "#let capitalize-first(s) = s"),
        };
        var reachable = UserLibAnalysis.ReachableFrom("#import \"userlib/gost/forms/f3.typ\": *", files);
        Assert.Equal(["gost/forms/f3.typ", "util/text.typ"], reachable.OrderBy(x => x));
    }

    /// <summary>
    /// Проверено на Typst 0.15.1: при двух <c>import: *</c> с одинаковым именем побеждает последний,
    /// БЕЗ предупреждения. В одном файле дубль видно глазом, в двадцати — нет.
    /// </summary>
    [Fact]
    public void DuplicateNames_AcrossFiles_AreReported()
    {
        var files = new[] { F("a.typ", "#let f() = []"), F("b.typ", "#let f() = []") };
        var entry = "#import \"userlib/a.typ\": *\n#import \"userlib/b.typ\": *";

        var duplicates = UserLibAnalysis.Warnings(entry, files).Where(w => w.Message.Contains("объявлено ещё")).ToList();

        Assert.Equal(2, duplicates.Count);   // сказать надо на обеих строках — иначе ищи вторую сам
        Assert.All(duplicates, d => Assert.Contains("«f»", d.Message));
    }

    /// <summary>Неподключённый файл ни с кем не конфликтует — про него уже сказано отдельно.</summary>
    [Fact]
    public void DuplicateWithUnreachableFile_IsNotReportedAsDuplicate()
    {
        var files = new[] { F("a.typ", "#let f() = []"), F("orphan.typ", "#let f() = []") };
        var warnings = UserLibAnalysis.Warnings("#import \"userlib/a.typ\": *", files);
        Assert.DoesNotContain(warnings, w => w.Message.Contains("объявлено ещё"));
    }

    [Fact]
    public void ConformingTree_IsSilent()
    {
        var files = new[] { F("gost/f3.typ", "#let place-f3() = []") };
        Assert.Empty(UserLibAnalysis.Warnings("#import \"userlib/gost/f3.typ\": *", files));
    }

    /// <summary>Взаимный импорт не должен зациклить обход.</summary>
    [Fact]
    public void ImportCycle_DoesNotHang()
    {
        var files = new[]
        {
            F("a.typ", "#import \"b.typ\": *"),
            F("b.typ", "#import \"a.typ\": *"),
        };
        var reachable = UserLibAnalysis.ReachableFrom("#import \"userlib/a.typ\": *", files);
        Assert.Equal(["a.typ", "b.typ"], reachable.OrderBy(x => x));
    }

    /// <summary>Вложенные объявления наружу не экспортируются — считать их дублями нельзя.</summary>
    [Fact]
    public void OnlyTopLevelDeclarations_Count()
    {
        var names = UserLibAnalysis.TopLevelNames("#let outer() = {\n  let inner = 1\n  inner\n}");
        Assert.Equal(["outer"], names);
    }

    /// <summary>
    /// Пока строку писало приложение, она всегда была канонической. Теперь импорты ведёт пользователь
    /// (#492), и «./userlib/…» — обычная запись. Без нормализации подключённый файл объявлялся бы
    /// неподключённым, а попытка починить это вторым импортом дала бы предупреждение о дубликате.
    /// </summary>
    [Fact]
    public void EntrypointImportWithDotSlash_IsReachable()
    {
        var files = new[] { F("gost/f3.typ", "#let place-f3() = []") };
        var entry = "#import \"./userlib/gost/f3.typ\": *";
        Assert.Equal(["gost/f3.typ"], UserLibAnalysis.ReachableFrom(entry, files));
        Assert.Empty(UserLibAnalysis.Warnings(entry, files));
    }

    /// <summary>
    /// Импорты ведёт пользователь (#492), и закомментировать строку — обычное действие. Считая её
    /// живой, мы держали бы файл «подключённым» и тащили его в проверку одноимённых объявлений (#498).
    /// </summary>
    [Fact]
    public void CommentedOutImport_IsNotAReference()
    {
        var files = new[] { F("gost/f3.typ", "#let place-f3() = []") };
        Assert.Empty(UserLibAnalysis.ReachableFrom("// #import \"userlib/gost/f3.typ\": *", files));
        Assert.Empty(UserLibAnalysis.ReachableFrom("/* #import \"userlib/gost/f3.typ\": * */", files));
    }

    /// <summary>
    /// Закомментированное объявление не объявляет ничего (#500). Иначе перенос функции в другой файл
    /// с закомментированным оригиналом — обычный приём — давал бы ложное «объявлено ещё в».
    /// </summary>
    [Fact]
    public void CommentedOutDeclaration_IsNotADuplicate()
    {
        var files = new[]
        {
            F("util/case.typ", "#let shout(s) = upper(s)"),
            F("util/text.typ", """
                /*
                #let shout(s) = upper(s)
                */
                """),
        };
        var entry = """
            #import "userlib/util/case.typ": *
            #import "userlib/util/text.typ": *
            """;
        Assert.Empty(UserLibAnalysis.Warnings(entry, files));
    }

    /// <summary>
    /// Строковые литералы переживают снятие комментариев (#501). Иначе «/*» внутри строки открывал бы
    /// мнимый блок и уносил импорты ниже: файлы числились бы неподключёнными, и их одноимённые
    /// объявления — которые Typst молча разрешает в пользу последнего импорта — переставали бы
    /// показываться.
    /// </summary>
    [Fact]
    public void SlashStarInsideString_DoesNotSwallowImportBelow()
    {
        var files = new[] { F("a.typ", "#let f() = []") };
        var entry = """
            #let fence = "/*"
            #import "userlib/a.typ": *
            /* настоящий комментарий */
            """;
        Assert.Equal(["a.typ"], UserLibAnalysis.ReachableFrom(entry, files));
    }

    [Fact]
    public void DoubleSlashInsideString_DoesNotSwallowRestOfLine()
    {
        var names = UserLibAnalysis.TopLevelNames("#let site = \"https://typst.app\"\n#let after() = []");
        Assert.Equal(["site", "after"], names);
    }

    /// <summary>Непарная кавычка в разметке не должна проглотить полфайла до следующей кавычки.</summary>
    [Fact]
    public void UnbalancedQuote_DoesNotSwallowImportBelow()
    {
        var files = new[] { F("a.typ", "#let f() = []") };
        var entry = "#let note = [Кабель \"ВВГнг проложен]\n#import \"userlib/a.typ\": *";
        Assert.Equal(["a.typ"], UserLibAnalysis.ReachableFrom(entry, files));
    }

    /// <summary>
    /// Блочные комментарии Typst вложенные, и закомментировать область, где комментарий уже есть, —
    /// обычное действие редактора. Нежадное регулярное выражение закрывало блок на первом внутреннем
    /// «*/», оставляя импорт живым: файл числился подключённым, а его имена шли в проверку
    /// дубликатов — то самое, от чего избавлялись в #498 (issue #504).
    /// </summary>
    [Fact]
    public void NestedBlockComment_ClosesAtItsOwnEnd()
    {
        var files = new[] { F("a.typ", "#let f() = []") };
        Assert.Empty(UserLibAnalysis.ReachableFrom(
            "/* черновик /* внутри */ #import \"userlib/a.typ\": * */", files));
        Assert.Equal(["a.typ"], UserLibAnalysis.ReachableFrom(
            "/* /* */ */\n#import \"userlib/a.typ\": *", files));
    }

    /// <summary>Переводы строк из комментариев сохраняются: «#let» считается только в начале строки.</summary>
    [Fact]
    public void DeclarationAfterMultilineComment_IsCounted()
    {
        var names = UserLibAnalysis.TopLevelNames("#let a() = []\n/* пояснение\n   в две строки */\n#let b() = []");
        Assert.Equal(["a", "b"], names);
    }

    /// <summary>
    /// Ведущий «/» Typst считает от корня проекта, а не от папки файла — компилятор зовётся с --root
    /// на временную папку, где дерево лежит в подпапке userlib/. Проверено на Typst 0.15.1: такой
    /// импорт из файла дерева компилируется. Разрешая его как относительный, мы приставляли папку
    /// файла («gost/userlib/util/text.typ») и ссылку молча не находили — файл числился
    /// неподключённым, а его одноимённые объявления выпадали из проверки (issue #505).
    /// </summary>
    [Fact]
    public void RootAbsoluteImport_FromTreeFile_IsResolved()
    {
        var files = new[]
        {
            F("gost/f3.typ", "#import \"/userlib/util/text.typ\": *"),
            F("util/text.typ", "#let shout(s) = upper(s)"),
        };
        var reachable = UserLibAnalysis.ReachableFrom("#import \"userlib/gost/f3.typ\": *", files);
        Assert.Equal(["gost/f3.typ", "util/text.typ"], reachable.OrderBy(x => x));
    }

    /// <summary>Путь от корня мимо userlib/ ведёт к служебным файлам генерации — они не наши.</summary>
    [Fact]
    public void RootAbsoluteImport_OutsideTree_IsNotOurFile()
    {
        var files = new[]
        {
            F("gost/f3.typ", "#import \"/typeblocks.typ\": *"),
            F("typeblocks.typ", "#let t() = []"),
        };
        var reachable = UserLibAnalysis.ReachableFrom("#import \"userlib/gost/f3.typ\": *", files);
        Assert.Equal(["gost/f3.typ"], reachable);
    }

    /// <summary>
    /// Объявления самой точки входа живут в той же области, что вытащенные из дерева, и шаблон
    /// получает их вместе. Дыра приходилась ровно на приём, ради которого дерево и заводилось: вынес
    /// функцию в файл, дописал импорт, а оригинальный «#let» убрать забыл — Typst молча берёт
    /// последнее связывание, шаблоны получают старую копию (issue #506).
    /// </summary>
    [Fact]
    public void DuplicateBetweenEntrypointAndTreeFile_IsReported()
    {
        var files = new[] { F("util/text.typ", "#let shout(s) = upper(s)") };
        var entry = "#import \"userlib/util/text.typ\": *\n#let shout(s) = upper(s)";

        var duplicates = UserLibAnalysis.Warnings(entry, files).ToList();

        Assert.Equal(2, duplicates.Count);
        Assert.Contains(duplicates, w => w.Path == UserLibAnalysis.EntrypointName);
        Assert.Contains(duplicates, w => w.Path == "util/text.typ");
        Assert.All(duplicates, d => Assert.Contains("«shout»", d.Message));
    }

    /// <summary>Перенос БЕЗ оставленного оригинала — обычный успешный случай, он молчит.</summary>
    [Fact]
    public void EntrypointWithoutOwnDeclarations_IsSilent()
    {
        var files = new[] { F("util/text.typ", "#let shout(s) = upper(s)") };
        Assert.Empty(UserLibAnalysis.Warnings("#import \"userlib/util/text.typ\": *", files));
    }

    /// <summary>
    /// <c>#include</c> — тоже ссылка на файл. Диалог удаления теперь единственная защита (#492), и на
    /// файле, на который ссылались только так, он говорил бы «ссылок нет» (issue #506).
    /// </summary>
    [Fact]
    public void IncludeCountsAsReference()
    {
        var files = new[] { F("a.typ", "#let f() = []") };
        Assert.Equal(["a.typ"], UserLibAnalysis.ReachableFrom("#include \"userlib/a.typ\"", files));
    }

    /// <summary>
    /// …но имён <c>#include</c> в область НЕ приносит: проверено на Typst 0.15.1 — вызов объявленной
    /// во включённом файле функции падает с «unknown variable». Считая его наравне с импортом, мы
    /// обещали бы «Typst молча возьмёт объявление из файла, импортированного последним» там, где
    /// ничего не перекрывается (issue #507).
    /// </summary>
    [Fact]
    public void IncludedFile_DoesNotShadowNames()
    {
        var files = new[] { F("frag.typ", "#let shout(s) = upper(s)") };
        var entry = "#include \"userlib/frag.typ\"\n#let shout(s) = upper(s)";

        Assert.Equal(["frag.typ"], UserLibAnalysis.ReachableFrom(entry, files));   // в сборку входит
        Assert.Empty(UserLibAnalysis.ImportedFrom(entry, files));                  // имён не приносит
        Assert.Empty(UserLibAnalysis.Warnings(entry, files));
    }

    /// <summary>Импортированный «: *» файл, наоборот, имена приносит — и перекрытие остаётся видимым.</summary>
    [Fact]
    public void ImportedFile_StillShadowsNames()
    {
        var files = new[] { F("frag.typ", "#let shout(s) = upper(s)") };
        var entry = "#import \"userlib/frag.typ\": *\n#let shout(s) = upper(s)";
        Assert.NotEmpty(UserLibAnalysis.Warnings(entry, files));
    }

    /// <summary>
    /// Импорт с псевдонимом и выборочный импорт приносят только псевдоним и только названные имена —
    /// проверено на Typst 0.15.1: «#import "frag.typ" as t» рядом со своим «#let shout» собирается, а
    /// после «#import "frag.typ": pad» имя «shout» неизвестно. Считая их наравне с «: *», мы обещали
    /// бы перекрытие там, где его нет (issue #508); форма с псевдонимом естественнее всего выглядит
    /// как раз в точке входа, которую мы с #506 стали проверять.
    /// </summary>
    /// <summary>
    /// А вот псевдоним ВМЕСТЕ с «: *» экспортирует всё — проверено на Typst 0.15.1: «#import
    /// "frag.typ" as t: *» и следом вызов «shout» компилируется. Регулярное выражение, требовавшее
    /// «"путь": *» подряд, эту форму не ловило, и перекрытие имён проходило молча (issue #511).
    /// </summary>
    [Fact]
    public void AliasedWildcardImport_StillShadowsNames()
    {
        var files = new[] { F("frag.typ", "#let shout(s) = upper(s)") };
        var entry = "#import \"userlib/frag.typ\" as t: *\n#let shout(s) = lower(s)";
        Assert.Equal(["frag.typ"], UserLibAnalysis.ImportedFrom(entry, files));
        Assert.NotEmpty(UserLibAnalysis.Warnings(entry, files));
    }

    /// <summary>
    /// Файл, ссылающийся на артефакты генерации, проверке недоступен: настоящего typeblocks.typ у неё
    /// нет, а выборочный импорт из пустой заглушки даёт «unresolved import» — ложную ошибку на
    /// каждом сохранении (issue #511). Битая ссылка ВНУТРИ дерева наружной не считается: о ней
    /// проверка обязана сказать.
    /// </summary>
    [Theory]
    [InlineData("#import \"/typeblocks.typ\": place-table", true)]
    [InlineData("#import \"../../data.json\": *", true)]
    [InlineData("#import \"/userlib/util/text.typ\": *", false)]
    [InlineData("#import \"missing.typ\": *", false)]
    [InlineData("#import \"@preview/cetz:0.3.1\": *", false)]
    public void ReferencesOutsideTree_DetectsWhatCheckCannotProvide(string content, bool expected)
        => Assert.Equal(expected, UserLibAnalysis.ReferencesOutsideTree(F("gost/f3.typ", content)));

    [Theory]
    [InlineData("#import \"userlib/frag.typ\" as t")]
    [InlineData("#import \"userlib/frag.typ\": pad")]
    public void AliasedOrSelectiveImport_DoesNotShadowNames(string importLine)
    {
        var files = new[] { F("frag.typ", "#let shout(s) = upper(s)\n#let pad(s) = s") };
        var entry = importLine + "\n#let shout(s) = lower(s)";
        Assert.Empty(UserLibAnalysis.Warnings(entry, files));
    }

    /// <summary>
    /// Путь из диагностики Typst — в путь интерфейса. Неизвестный путь (файл пакета <c>@preview</c>)
    /// даёт null, и проверяющий по нему считает ошибку ВХОДЯЩЕЙ в сборку: обратное умолчание
    /// показывало бы сломанный пакет мягкой полосой «в сборку не входит» при Ok = true (issue #508).
    /// </summary>
    /// <summary>
    /// Пустой сырой литерал «``» закончен сам по себе (проверено на Typst 0.15.1: файл с ним
    /// компилируется). Ища ему пару, разбор находил её в следующем сыром блоке файла и съедал всё
    /// между ними — вместе с импортами: файл переставал числиться подключённым, его дубликаты
    /// пропадали из проверки, а диалог удаления говорил «ссылок нет» (issue #509).
    /// </summary>
    [Fact]
    public void EmptyRawLiteral_DoesNotSwallowImportBelow()
    {
        var files = new[] { F("util/text.typ", "#let shout(s) = upper(s)") };
        var entry = "#let t = ``\n#import \"userlib/util/text.typ\": *\n#let u = `x`";
        Assert.Equal(["util/text.typ"], UserLibAnalysis.ReachableFrom(entry, files));
    }

    /// <summary>Одиночная кавычка без пары — обычный символ, а не начало блока до конца файла.</summary>
    [Fact]
    public void UnpairedBacktick_DoesNotSwallowImportBelow()
    {
        var files = new[] { F("a.typ", "#let f() = []") };
        Assert.Equal(["a.typ"], UserLibAnalysis.ReachableFrom("#let tick = `\n#import \"userlib/a.typ\": *", files));
    }

    /// <summary>
    /// Диагностику зонда отбираем по ИМЕНИ и только среди путей, не приводимых к дереву: сравнение с
    /// путём временной папки зависело от канонизации хоста (короткие имена 8.3, «/private/var/…») и
    /// молча переставало совпадать. «userlib/check.typ» — законное имя файла дерева, оно приводится
    /// и под фильтр не попадает (issue #509).
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\ADMINI~1\AppData\Local\Temp\userlib-check-1\check.typ", true)]
    [InlineData("/private/var/folders/x/userlib-check-1/check.typ", true)]
    [InlineData("C:/tmp/userlib-check-1/userlib/gost/f3.typ", false)]
    // Одного имени мало (issue #510): «check.typ» вполне встречается внутри пакета @preview, а
    // именно туда Typst показывает ошибки типов — выбросив их, мы объявили бы сломанную библиотеку
    // собирающейся.
    [InlineData("C:/Users/x/AppData/Local/typst/packages/preview/cetz/0.3.1/src/check.typ", false)]
    public void IsProbePath_RequiresBothTheFileNameAndItsFolder(string path, bool expected)
        => Assert.Equal(expected, UserLibAnalysis.IsProbePath(path, "userlib-check-1"));

    [Fact]
    public void TreeFileNamedLikeProbe_IsStillOurFile()
        => Assert.Equal("check.typ", UserLibAnalysis.ToLibPath("C:/tmp/userlib-check-1/userlib/check.typ"));

    [Theory]
    [InlineData("C:/tmp/userlib-check-1/userlib/gost/f3.typ", "gost/f3.typ")]
    [InlineData(@"C:\tmp\userlib-check-1\userlib\gost\f3.typ", "gost/f3.typ")]
    [InlineData("C:/tmp/userlib-check-1/userlib.typ", "userlib.typ")]
    [InlineData("C:/Users/x/AppData/Local/typst/packages/preview/cetz/0.3.1/src/draw.typ", null)]
    public void ToLibPath_MapsTreePathsAndRejectsForeignOnes(string diagnostic, string? expected)
        => Assert.Equal(expected, UserLibAnalysis.ToLibPath(diagnostic));

    /// <summary>
    /// Внутри сырого блока не код: библиотека вправе показывать там синтаксис с непарным «/*». Без
    /// этого блок открывал бы мнимый комментарий и съедал остаток файла — тот же класс, что «/*» в
    /// строке (#501) и вложенные комментарии (#504) (issue #506).
    /// </summary>
    [Fact]
    public void RawBlock_IsNotParsedAsCode()
    {
        var files = new[] { F("a.typ", "#let f() = []") };
        var entry = "#let doc = ```typst\n/* пример комментария\n```\n#import \"userlib/a.typ\": *";
        Assert.Equal(["a.typ"], UserLibAnalysis.ReachableFrom(entry, files));

        Assert.Empty(UserLibAnalysis.ReachableFrom("#let doc = `#import \"userlib/a.typ\": *`", files));
        Assert.Equal(["a.typ"], UserLibAnalysis.ReachableFrom("#let tick = `\n#import \"userlib/a.typ\": *", files));
    }

    [Fact]
    public void PackageImports_AreIgnored()
        => Assert.Empty(UserLibAnalysis.ReachableFrom("#import \"@preview/cetz:0.3.1\": *", []));
}
