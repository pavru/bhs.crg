using BHS.CRG.Application.Generation;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Срезание типового префикса при переходе на `Код.Имя` (issue #773). Правило узкое намеренно:
/// оно должно молчать всюду, где могло бы отрезать смысл, — потому что переименование необратимо
/// переписывает пользовательские тексты.
/// </summary>
public class TypeBlockShortNameTests
{
    private static readonly string[] NoReserved = [];

    [Fact]
    public void CommonPrefix_IsStripped_WhenTypeHasSeveralBlocks()
    {
        var map = TypeBlockShortName.Shorten(["org-full-info", "org-inn-kpp", "org-codes"], NoReserved);
        Assert.Equal("full-info", map["org-full-info"]);
        Assert.Equal("inn-kpp", map["org-inn-kpp"]);
        Assert.Equal("codes", map["org-codes"]);
    }

    /// <summary>
    /// Главное условие правила. У типа с ОДНИМ блоком общий префикс тривиален — им окажется первое
    /// слово какого угодно имени, и срезание отрежет смысл: `unit-typst → typst`,
    /// `actual-draw-num-name → draw-num-name`. На живых данных это ровно все сомнительные случаи.
    /// </summary>
    [Theory]
    [InlineData("unit-typst")]
    [InlineData("actual-draw-num-name")]
    [InlineData("sro-org-full-info")]
    [InlineData("name-family-initials")]
    public void SingleBlockType_IsLeftAlone(string only)
        => Assert.Empty(TypeBlockShortName.Shorten([only], NoReserved));

    [Fact]
    public void NoCommonPrefix_NothingRenamed()
        => Assert.Empty(TypeBlockShortName.Shorten(["fbTypstUnit", "unit-typst"], NoReserved));

    [Fact]
    public void NamesWithoutSegments_AreLeftAlone()
        => Assert.Empty(TypeBlockShortName.Shorten(["full", "short"], NoReserved));

    /// <summary>Имя библиотеки перекрыло бы блок внутри тела (библиотека импортируется туда), и отказ
    /// был бы совершенно немым — берётся чужая функция с тем же именем.</summary>
    [Fact]
    public void CollisionWithReservedName_CancelsRenamingOfWholeType()
        => Assert.Empty(TypeBlockShortName.Shorten(["org-columns", "org-rows"], ["columns", "rows"]));

    [Fact]
    public void CollisionWithDispatchName_CancelsRenaming()
        => Assert.Empty(TypeBlockShortName.Shorten(
            ["x-render-by-type", "x-other"], [TypstPreambleBuilder.DispatchFnName]));

    /// <summary>Срезание сталкивает два имени между собой — не переименовываем ничего: иначе один
    /// блок молча перекрыл бы другой.</summary>
    [Fact]
    public void CollisionBetweenShortenedNames_CancelsRenaming()
        => Assert.Empty(TypeBlockShortName.Shorten(["a-x", "a-x-y", "a-x"], NoReserved));

    /// <summary>Короткое имя, столкнувшееся с ДРУГИМ существующим блоком того же типа, тоже отменяет
    /// переименование: после срезания `p-full` стал бы `full`, а `full` уже занят.</summary>
    [Fact]
    public void ShortenedNameEqualToAnotherExistingBlock_CancelsRenaming()
        => Assert.Empty(TypeBlockShortName.Shorten(["p-full", "p-short", "full"], NoReserved));

    /// <summary>Результат обязан оставаться именем Typst: `2-x` дал бы `x`, но `x-2` — цифру в начале
    /// после среза не даст, а вот `n-2fold` даст. Такое переименование отменяется целиком.</summary>
    [Fact]
    public void ShortenedNameThatIsNotIdentifier_CancelsRenaming()
        => Assert.Empty(TypeBlockShortName.Shorten(["n-2fold", "n-other"], NoReserved));

    /// <summary>Живые данные: 12 срезаний у 5 типов, остальные типы не тронуты.</summary>
    [Fact]
    public void LiveShape_RenamesOnlyTheFiveMultiBlockTypes()
    {
        Assert.Equal(2, TypeBlockShortName.Shorten(["addr-full", "addr-contacts"], NoReserved).Count);
        Assert.Equal(2, TypeBlockShortName.Shorten(["signatory-full", "signatory-short"], NoReserved).Count);
        Assert.Equal(2, TypeBlockShortName.Shorten(["project-one-line", "project-type-code-part"], NoReserved).Count);
        Assert.Equal(3, TypeBlockShortName.Shorten(
            ["person-title-name-org", "person-add-infos", "person-title-name-short-org"], NoReserved).Count);
        Assert.Empty(TypeBlockShortName.Shorten(["gps-full"], NoReserved));
        Assert.Empty(TypeBlockShortName.Shorten(["object-name-address"], NoReserved));
    }
}
