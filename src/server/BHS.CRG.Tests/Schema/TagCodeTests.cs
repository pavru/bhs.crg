using BHS.CRG.Domain.Schema;

namespace BHS.CRG.Tests.Schema;

/// <summary>
/// Разбор записи тэга «код» / «код:параметр» (issue #583). Главное свойство — обратная
/// совместимость: запись без параметра обязана остаться собой, иначе перестали бы находиться поля
/// во ВСЕХ существующих схемах.
/// </summary>
public class TagCodeTests
{
    [Fact]
    public void Parse_PlainCode_HasNoOrder()
    {
        var tag = TagCode.Parse("identity");
        Assert.Equal("identity", tag.Code);
        Assert.Null(tag.Order);
    }

    [Fact]
    public void Parse_CodeWithOrder()
    {
        var tag = TagCode.Parse("identity:2");
        Assert.Equal("identity", tag.Code);
        Assert.Equal(2, tag.Order);
    }

    /// <summary>Точка — часть кода тэга, и разделителем параметра она не является.</summary>
    [Fact]
    public void Parse_DottedCode_StaysWhole()
        => Assert.Equal("material.qualityDocLink", TagCode.Parse("material.qualityDocLink").Code);

    [Fact]
    public void Parse_TrimsSpacesAroundBothParts()
    {
        var tag = TagCode.Parse(" identity : 3 ");
        Assert.Equal("identity", tag.Code);
        Assert.Equal(3, tag.Order);
    }

    /// <summary>
    /// Опечатка в номере не должна молча отключать поле от сопоставления: тэг продолжает работать,
    /// просто без номера.
    /// </summary>
    [Theory]
    [InlineData("identity:")]
    [InlineData("identity:первый")]
    [InlineData("identity:-1")]
    [InlineData("identity:1.5")]
    public void Parse_UnusableParameter_KeepsCodeWithoutOrder(string raw)
    {
        var tag = TagCode.Parse(raw);
        Assert.Equal("identity", tag.Code);
        Assert.Null(tag.Order);
    }

    [Fact]
    public void Parse_NullOrEmpty_IsEmptyCode()
    {
        Assert.Equal("", TagCode.Parse(null).Code);
        Assert.Equal("", TagCode.Parse("   ").Code);
    }

    /// <summary>Поле без номера идёт ПОСЛЕ нумерованных — на этом держится работа старых схем.</summary>
    [Fact]
    public void SortKey_UnnumberedGoesLast()
        => Assert.True(TagCode.Parse("identity:7").SortKey < TagCode.Parse("identity").SortKey);

    [Fact]
    public void CodeOf_StripsParameter()
        => Assert.Equal("identity", TagCode.CodeOf("identity:1"));
}
