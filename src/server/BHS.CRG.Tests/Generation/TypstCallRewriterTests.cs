using BHS.CRG.Application.Generation;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Переписывание текстов под адресацию `Код.Имя` (issue #773). Проверяем не только «переписал», но и
/// «не тронул»: переписчик правит пользовательские шаблоны, и лишняя замена здесь дороже пропущенной.
/// </summary>
public class TypstCallRewriterTests
{
    private static readonly Dictionary<string, BlockRef> Map = new()
    {
        ["org-full-info"] = new("Организация", "org-full-info", "full-info"),
        ["addr-full"] = new("Адрес", "addr-full", "full"),
        ["gps-full"] = new("ГеографическиеКоординаты", "gps-full", "gps-full"),   // не переименован
    };

    private static RewriteResult Run(string text, string? own = null)
        => TypstCallRewriter.Rewrite(text, Map, own);

    /// <summary>Текст БЛОКА: он переехал в подпапку, поэтому здесь чинятся и пути.</summary>
    private static RewriteResult RunBlock(string text, string own = "Организация")
        => TypstCallRewriter.Rewrite(text, Map, own, fixPaths: true);

    [Fact]
    public void CallInTemplate_GetsTypePrefixAndNewName()
    {
        var r = Run("#org-full-info(it.Подрядчик)");
        Assert.Equal("#Организация.full-info(it.Подрядчик)", r.Text);
        Assert.Equal(1, r.Calls);
    }

    /// <summary>Тип, у которого имена не срезали, всё равно адресуется через код — иначе шаблон
    /// продолжал бы звать имя, которого в его области больше нет.</summary>
    [Fact]
    public void CallOfNotRenamedBlock_StillGetsPrefix()
        => Assert.Equal("#ГеографическиеКоординаты.gps-full(x)", Run("#gps-full(x)").Text);

    /// <summary>Внутри блока СВОЕГО типа префикс не нужен — модуль общий, — но имя новое.</summary>
    [Fact]
    public void CallOfOwnTypeBlock_KeepsBareName()
        => Assert.Equal("{ full-info(it) }", Run("{ org-full-info(it) }", own: "Организация").Text);

    [Fact]
    public void CallOfForeignBlock_InsideBlock_GetsPrefix()
        => Assert.Equal("{ Адрес.full(it) }", Run("{ addr-full(it) }", own: "Организация").Text);

    [Fact]
    public void CallWithSpaceBeforeParen_IsRewritten()
        => Assert.Equal("#Адрес.full (x)", Run("#addr-full (x)").Text);

    // ── Чего переписчик не трогает ───────────────────────────────────────────

    [Fact]
    public void MentionInsideCommentOrString_IsUntouched()
    {
        const string src = "{ // org-full-info(it)\n \"текст про org-full-info\" }";
        var r = Run(src);
        Assert.Equal(src, r.Text);
        Assert.Equal(0, r.Calls);
    }

    [Fact]
    public void MentionInsideRawBlock_IsUntouched()
    {
        const string src = "{ ```typst\n#org-full-info(it)\n``` }";
        Assert.Equal(src, Run(src).Text);
    }

    /// <summary>Уже переписанный текст второй раз не трогается: имя после точки — не вызов. Иначе
    /// повторный или продолженный после сбоя прогон дал бы `Код.Код.имя`.</summary>
    [Fact]
    public void AlreadyRewritten_IsNotRewrittenAgain()
    {
        const string src = "#Организация.full-info(x)";
        Assert.Equal(src, Run(src).Text);
    }

    /// <summary>Имя как часть другого идентификатора — не вызов.</summary>
    [Fact]
    public void LongerIdentifierContainingTheName_IsUntouched()
        => Assert.Equal("#my-addr-full-x(y)", Run("#my-addr-full-x(y)").Text);

    /// <summary>Передача функции значением: переписать её МОЖНО было бы, но правило «отказ вместо
    /// догадки» требует сообщить, а не угадывать — такие места человек смотрит сам.</summary>
    [Fact]
    public void MentionNotInCallPosition_IsReportedNotRewritten()
    {
        var r = Run("#let f = addr-full");
        Assert.Equal("#let f = addr-full", r.Text);
        Assert.Contains("addr-full", r.Ambiguous);
    }

    // ── Пути (хвост #772) ────────────────────────────────────────────────────

    [Fact]
    public void RelativePath_InBlock_BecomesRooted()
    {
        var r = RunBlock("{ import \"userlib.typ\": dig }");
        Assert.Equal("{ import \"/userlib.typ\": dig }", r.Text);
        Assert.Equal(1, r.Paths);
    }

    /// <summary>А в ШАБЛОНЕ пути не трогаем: он как лежал в корне компиляции, так и лежит, и
    /// «userlib.typ» там резолвится. Правка ради правки завела бы новую версию шаблона.</summary>
    [Fact]
    public void RelativePath_InTemplate_IsUntouched()
    {
        const string src = "#import \"userlib.typ\": dig";
        var r = Run(src);
        Assert.Equal(src, r.Text);
        Assert.Equal(0, r.Paths);
    }

    /// <summary>Путь в ПРОЗЕ документа — не путь к файлу сборки: правка испортила бы видимый текст.</summary>
    [Fact]
    public void PathLikeTextInMarkup_IsUntouched()
    {
        const string src = "Приложить \"смета.pdf\" к акту.";
        Assert.Equal(src, RunBlock(src).Text);
    }

    [Theory]
    [InlineData("{ import \"/userlib.typ\": dig }")]        // уже от корня
    [InlineData("{ import \"../userlib.typ\": dig }")]      // тоже находится
    [InlineData("{ link(\"https://gost.ru/a.pdf\") }")]     // сетевой адрес
    public void PathsThatWork_AreUntouched(string src)
        => Assert.Equal(src, RunBlock(src).Text);

    /// <summary>Вызов и путь на одной строке правятся вместе, и позиции не съезжают.</summary>
    [Fact]
    public void CallAndPathOnSameLine_BothRewritten()
    {
        var r = RunBlock("{ import \"userlib.typ\": dig; addr-full(it) }");
        Assert.Equal("{ import \"/userlib.typ\": dig; Адрес.full(it) }", r.Text);
        Assert.Equal(1, r.Calls);
        Assert.Equal(1, r.Paths);
    }

    /// <summary>Несколько замен в одной строке: применяются с конца, иначе каждая следующая
    /// сдвигалась бы на длину предыдущей.</summary>
    [Fact]
    public void SeveralCallsOnOneLine_AllRewrittenCorrectly()
        => Assert.Equal("#Адрес.full(a) + #Организация.full-info(b) + #Адрес.full(c)",
            Run("#addr-full(a) + #org-full-info(b) + #addr-full(c)").Text);

    [Fact]
    public void NothingToDo_ReturnsOriginalInstance()
    {
        const string src = "= Заголовок\n\nОбычный текст без вызовов.";
        var r = Run(src);
        Assert.Equal(src, r.Text);
        Assert.Equal(0, r.Calls + r.Paths);
    }
}
