using BHS.CRG.Application.Generation;

namespace BHS.CRG.Tests.Generation;

/// <summary>Системная Typst-библиотека (issue #344/#353): содержимое + стандартные импорты при создании.</summary>
public class SystemTypstLibTests
{
    [Fact]
    public void Content_HasInstanceOf()
    {
        Assert.Contains("#let instance-of(", SystemTypstLib.Content);
    }

    [Fact]
    public void EnsureImports_PrependsSystemlibAndTypeblocks()
    {
        var content = "= Заголовок\n";
        var result = SystemTypstLib.EnsureImports(content);
        Assert.StartsWith("#import \"systemlib.typ\": *\n#import \"typeblocks.typ\": *\n", result);
        Assert.EndsWith(content, result);
    }

    [Fact]
    public void EnsureImports_Idempotent_WhenSystemlibAlreadyImported()
    {
        var content = "#import \"systemlib.typ\": *\n#import \"typeblocks.typ\": *\n= Тело\n";
        Assert.Equal(content, SystemTypstLib.EnsureImports(content)); // не дублируем
    }

    [Fact]
    public void EnsureImports_EmptyContent_GetsImports()
    {
        Assert.Contains("#import \"systemlib.typ\": *", SystemTypstLib.EnsureImports(""));
    }
}
