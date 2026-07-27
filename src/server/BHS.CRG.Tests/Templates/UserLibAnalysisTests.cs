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
    /// Самый неприятный отказ: файл валиден, просто никем не импортирован — функции молча не появятся
    /// в шаблонах, и ошибки не будет.
    /// </summary>
    [Fact]
    public void UnreachableFile_IsReported()
    {
        var warnings = UserLibAnalysis.Warnings("// пусто", [F("util/text.typ", "#let x() = []")]);
        var w = Assert.Single(warnings);
        Assert.Equal("util/text.typ", w.Path);
        Assert.Contains("не подключён", w.Message);
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

    [Fact]
    public void PackageImports_AreIgnored()
        => Assert.Empty(UserLibAnalysis.ReachableFrom("#import \"@preview/cetz:0.3.1\": *", []));

    [Fact]
    public void ReExportLine_PointsAtTheFileFromEntrypoint()
        => Assert.Equal("#import \"userlib/gost/f3.typ\": *", UserLibAnalysis.ReExportLine("gost/f3.typ"));
}
