using BHS.CRG.Application.Generation;

namespace BHS.CRG.Tests.Generation;

/// <summary>Системная Typst-библиотека (issue #344): авто-префикс импортов + офсет строк.</summary>
public class SystemTypstLibTests
{
    [Fact]
    public void Content_HasInstanceOf()
    {
        Assert.Contains("#let instance-of(", SystemTypstLib.Content);
    }

    [Fact]
    public void ComposeTemplate_PrependsSystemlibAndTypeblocksImports_ThenTemplateVerbatim()
    {
        var tpl = "#set page(width: 10cm)\n= Заголовок\n";
        var composed = SystemTypstLib.ComposeTemplate(tpl);
        Assert.StartsWith("#import \"systemlib.typ\": *\n#import \"typeblocks.typ\": *\n", composed);
        Assert.EndsWith(tpl, composed);                 // шаблон дословно в хвосте
    }

    [Fact]
    public void PreludeLineCount_MatchesPrependedLines()
    {
        // Офсет для сдвига номеров строк ошибок = число добавленных строк префикса.
        Assert.Equal(2, SystemTypstLib.PreludeLineCount);
        var composed = SystemTypstLib.ComposeTemplate("X");
        // Строка редактора N → строка composed (N + PreludeLineCount): "X" (ред. строка 1) на 3-й строке.
        Assert.Equal("X", composed.Split('\n')[SystemTypstLib.PreludeLineCount]);
    }
}
