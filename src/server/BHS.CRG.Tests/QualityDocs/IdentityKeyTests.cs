using BHS.CRG.Application.QualityDocs;

namespace BHS.CRG.Tests.QualityDocs;

/// <summary>
/// Составной ключ идентичности (issue #582). Ключ строится ОДИНАКОВО при создании связки и при
/// сопоставлении на генерации, поэтому проверяем именно свойства ключа, а не путь вызова.
/// </summary>
public class IdentityKeyTests
{
    [Fact]
    public void From_JoinsAllValuesNormalized()
        => Assert.Equal("провод ввг 3х2.5 | ab-12", IdentityKey.From([" Провод  ВВГ 3х2.5 ", "AB-12"]));

    /// <summary>
    /// Пустое поле даёт пустой СЛОТ, а не пропускается: позиция компонента обязана быть постоянной,
    /// иначе материал без артикула и материал без наименования дали бы неразличимые ключи из одного
    /// значения — ровно та подмена, из-за которой сертификат уезжал не тому товару.
    /// </summary>
    [Fact]
    public void From_EmptyValueKeepsItsSlot()
    {
        Assert.Equal("трубка | ", IdentityKey.From(["Трубка", null]));
        Assert.Equal(" | трубка", IdentityKey.From([null, "Трубка"]));
        Assert.NotEqual(IdentityKey.From(["Трубка", null]), IdentityKey.From([null, "Трубка"]));
    }

    [Fact]
    public void From_ByFieldKeys_TakesValuesInGivenOrder()
    {
        var row = new Dictionary<string, string?> { ["Наименование"] = "Трубка", ["Артикул"] = "T-1" };
        Assert.Equal("t-1 | трубка",
            IdentityKey.From(["Артикул", "Наименование"], k => row.GetValueOrDefault(k)));
    }

    /// <summary>Одна и та же позиция, записанная по-разному, — разные ключи. Это не дефект, а цена
    /// строгости: качество данных остаётся на пользователе (issue #582).</summary>
    [Fact]
    public void From_DiffersWhenAnyComponentDiffers()
        => Assert.NotEqual(IdentityKey.From(["Трубка", "T-1"]), IdentityKey.From(["Трубка", "T-2"]));

    [Fact]
    public void IsEmpty_TrueWhenNothingToMatch()
    {
        Assert.True(IdentityKey.IsEmpty(null));
        Assert.True(IdentityKey.IsEmpty(IdentityKey.From([null, "", "   "])));
        Assert.False(IdentityKey.IsEmpty(IdentityKey.From([null, "Трубка"])));
    }
}
