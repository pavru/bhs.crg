namespace BHS.CRG.Tests.Common;

/// <summary>
/// Общие помощники переписей: снятие комментариев и обращение к таблице в тексте SQL. Ошибка здесь —
/// дыра или ложная тревога сразу во всех сторожах, что их зовут.
/// </summary>
public class SourceTreeTests
{
    [Theory]
    [InlineData("var a = 1; // db.Set<X>()", "var a = 1; ")]
    [InlineData("/// <c>db.Set<X>()</c>", "")]
    [InlineData("var a = /* db.Set<X>() */ 1;", "var a =  1;")]
    [InlineData("var u = \"https://host/\"; db.Set<X>();", "var u = \"https://host/\"; db.Set<X>();")]
    [InlineData("var u = @\"C:\\a\"\"//b\"; db.Set<X>();", "var u = @\"C:\\a\"\"//b\"; db.Set<X>();")]
    [InlineData("var q = '\"'; db.Set<X>(); // хвост", "var q = '\"'; db.Set<X>(); ")]
    [InlineData("var e = \"\"; db.Set<X>(); // хвост", "var e = \"\"; db.Set<X>(); ")]
    [InlineData("var s = \"a\\\"//b\"; x();", "var s = \"a\\\"//b\"; x();")]
    public void Комментарий_снимается_строка_остаётся(string line, string expected) =>
        Assert.Equal(expected, SourceTree.WithoutComments(line));

    [Fact]
    public void Номера_строк_сохраняются_и_сырая_строка_цела()
    {
        var text = "a();\n/* один\nдва */ b();\nvar s = \"\"\"\n// не комментарий\n\"\"\";\nc(); // хвост\n";
        var stripped = SourceTree.WithoutComments(text);

        Assert.Equal(text.Count(ch => ch == '\n'), stripped.Count(ch => ch == '\n'));
        Assert.Contains("// не комментарий", stripped);
        Assert.Equal(3, SourceTree.LineOf(stripped, stripped.IndexOf("b()", StringComparison.Ordinal)));
        Assert.DoesNotContain("хвост", stripped);
    }

    [Theory]
    [InlineData("SELECT * FROM work.reports")]
    [InlineData("update WORK.REPORTS set x = 1")]
    [InlineData("SELECT id FROM \\\"work\\\".\\\"reports\\\"")]
    [InlineData("SELECT id FROM \"\"work\"\".\"\"reports\"\"")]
    [InlineData("SELECT id FROM \"work\".\"reports\"")]
    [InlineData("TRUNCATE ONLY work.reports")]
    public void Таблица_со_схемой_находится_в_любых_кавычках(string sql) =>
        Assert.Matches(SourceTree.SqlTouch("work", schemaOptional: false, ["reports"]), sql);

    [Theory]
    [InlineData("from reports in db.Set<Plan>()")]
    [InlineData("group r by r.Site into reports")]
    [InlineData("SELECT * FROM work.reports_archive")]
    [InlineData("SELECT * FROM plan.reports")]
    public void Без_схемы_и_чужая_таблица_не_совпадают(string text) =>
        Assert.DoesNotMatch(SourceTree.SqlTouch("work", schemaOptional: false, ["reports"]), text);

    [Fact]
    public void Необязательная_схема_допускает_голое_имя() =>
        Assert.Matches(SourceTree.SqlTouch("public", schemaOptional: true, ["domain_objects"]), "DELETE FROM domain_objects");
}
