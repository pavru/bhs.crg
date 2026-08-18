using BHS.CRG.Application.Generation;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Маска для переписчика текстов (issue #773). Главное её свойство — длина: замены применяются к
/// оригиналу по позициям, найденным в маске, и сдвиг хотя бы на символ испортил бы чужой код.
/// </summary>
public class TypstTextMaskTests
{
    [Theory]
    [InlineData("простой текст")]
    [InlineData("{ // комментарий\n it.x }")]
    [InlineData("{ /* блочный\n многострочный */ it.x }")]
    [InlineData("{ \"строка с (скобками)\" }")]
    [InlineData("{ `raw` и ```многострочный\n raw``` }")]
    [InlineData("{ \"экранированная \\\" кавычка\" }")]
    [InlineData("{ \"незакрытая строка")]
    [InlineData("{ `незакрытый raw")]
    public void Mask_PreservesLength(string text)
    {
        Assert.Equal(text.Length, TypstTextMask.Mask(text).Length);
        Assert.Equal(text.Length, TypstTextMask.Mask(text, TypstTextMask.Keep.StringsOnly).Length);
    }

    /// <summary>Переводы строк остаются на местах: по ним считаются номера строк в диагностиках.</summary>
    [Fact]
    public void Mask_KeepsNewlines()
    {
        const string src = "{ /* один\n два */\n it.x }";
        Assert.Equal(src.Count(c => c == '\n'), TypstTextMask.Mask(src).Count(c => c == '\n'));
    }

    [Fact]
    public void Mask_HidesCallsInsideComments()
    {
        var masked = TypstTextMask.Mask("{ // org-full(it)\n org-full(it) }");
        Assert.Single(FindAll(masked, "org-full"));
    }

    /// <summary>Имя в тексте документа — не вызов: переписывать его нельзя, значит и видеть не нужно.</summary>
    [Fact]
    public void Mask_HidesTextInsideStrings_ByDefault()
        => Assert.Empty(FindAll(TypstTextMask.Mask("{ \"см. org-full\" }"), "org-full"));

    /// <summary>А путям, наоборот, нужны именно строки — там они и живут.</summary>
    [Fact]
    public void Mask_KeepsStringContent_WhenAsked()
        => Assert.Single(FindAll(TypstTextMask.Mask("{ import \"userlib.typ\": dig }", TypstTextMask.Keep.StringsOnly),
            "userlib.typ"));

    /// <summary>Raw-блок гасится всегда: там имя функции — часть примера в документации, а не вызов.</summary>
    [Fact]
    public void Mask_HidesRawBlocks_EvenWhenStringsKept()
        => Assert.Empty(FindAll(TypstTextMask.Mask("{ ```\n org-full(it)\n``` }", TypstTextMask.Keep.StringsOnly), "org-full"));

    /// <summary>«//» внутри строки не начинает комментарий — иначе остаток строки пропал бы из вида
    /// вместе с настоящими вызовами после неё.</summary>
    [Fact]
    public void Mask_DoesNotTreatSlashesInsideStringAsComment()
    {
        var masked = TypstTextMask.Mask("{ link(\"https://x.ru\") + org-full(it) }");
        Assert.Single(FindAll(masked, "org-full"));
    }

    /// <summary>
    /// В markup-режиме кавычка — обычный символ, и «\"» её просто печатает. Приняв это за начало
    /// строкового литерала, маска гасила весь остаток файла: на живом шаблоне АОСР так пропали 13
    /// настоящих вызовов из 27 — переписчик бы их не тронул, и шаблон после миграции звал бы имена,
    /// которых нет. Отсюда правило: экранированная кавычка литерала не открывает.
    /// </summary>
    [Fact]
    public void Mask_EscapedQuoteInMarkup_DoesNotOpenString()
    {
        var masked = TypstTextMask.Mask("#align(center)[\\\"Текст\\\"]\n#signatory-full(x)");
        Assert.Single(FindAll(masked, "signatory-full"));
    }

    /// <summary>Кавычки вокруг текста в markup — тоже не литерал: код после них обязан остаться
    /// видимым.</summary>
    [Fact]
    public void Mask_QuotesAroundMarkupText_DoNotHideFollowingCode()
    {
        var masked = TypstTextMask.Mask("Он сказал \"привет\" всем.\n#org-full(it)");
        Assert.Single(FindAll(masked, "org-full"));
    }

    /// <summary>А в аргументах вызова кавычка — настоящий литерал: имя внутри неё не вызов.</summary>
    [Fact]
    public void Mask_StringInsideCallArguments_IsStillHidden()
        => Assert.Empty(FindAll(TypstTextMask.Mask("#link(\"см. org-full\")"), "org-full"));

    [Fact]
    public void Mask_ImportPath_IsRecognizedAsString()
        => Assert.Single(FindAll(TypstTextMask.Mask("#import \"userlib.typ\": dig", TypstTextMask.Keep.StringsOnly),
            "userlib.typ"));

    private static List<int> FindAll(string haystack, string needle)
    {
        var res = new List<int>();
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
            res.Add(i);
        return res;
    }
}
