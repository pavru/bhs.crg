using BHS.CRG.Application.Generation;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Имя файла модуля блоков (issue #772). Код типа — доменное значение, вводимое свободно, а имя файла
/// обязано пережить файловую систему; здесь проверяется именно эта развязка, а не валидация кода.
/// </summary>
public class TypeBlockSlugTests
{
    [Theory]
    [InlineData("АОСР", "АОСР")]                       // кириллица — законное имя файла
    [InlineData("PROJECT_DOC_PDF", "PROJECT_DOC_PDF")]
    [InlineData("АОСР-1", "АОСР-1")]                   // дефис допустим и в Typst, и в ФС
    [InlineData("А/Б", "А_Б")]
    [InlineData("А:Б*В?", "А_Б_В")]                     // хвостовые подчёркивания срезаются
    [InlineData("..", "type")]                          // после чистки точек не остаётся ничего
    [InlineData("  ", "type")]
    [InlineData("", "type")]
    [InlineData("Акт черновой", "Акт_черновой")]
    public void Sanitize_ProducesUsableFileName(string code, string expected)
        => Assert.Equal(expected, TypeBlockSlug.Sanitize(code));

    /// <summary>Файл «NUL.typ» на Windows не создать — имена DOS-устройств живы в Win32 до сих пор.</summary>
    [Theory]
    [InlineData("NUL")]
    [InlineData("con")]
    [InlineData("COM1")]
    public void Sanitize_EscapesReservedWindowsNames(string code)
        => Assert.Equal("_" + code, TypeBlockSlug.Sanitize(code));

    /// <summary>
    /// Коды уникальны РЕГИСТРОЗАВИСИМО, а «Акт.typ» и «акт.typ» на Windows — один файл: без развязки
    /// один тип молча съел бы блоки другого, причём только на части платформ.
    /// </summary>
    [Fact]
    public void AssignUnique_SeparatesNamesCollidingOnlyByCase()
    {
        var slugs = TypeBlockSlug.AssignUnique(new[] { (1, "Акт"), (2, "акт"), (3, "АКТ") });
        Assert.Equal(3, slugs.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("Акт", slugs[1]);   // первый получает чистое имя, порядок задаёт вызывающий
    }

    /// <summary>Столкнуться могут и разные коды — после санитизации: «А/Б» и «А:Б» дают одно имя.</summary>
    [Fact]
    public void AssignUnique_SeparatesNamesCollidingAfterSanitize()
    {
        var slugs = TypeBlockSlug.AssignUnique(new[] { (1, "А/Б"), (2, "А:Б") });
        Assert.Equal("А_Б", slugs[1]);
        Assert.Equal("А_Б-2", slugs[2]);
    }

    [Fact]
    public void AssignUnique_IsStableForSameInputOrder()
    {
        var a = TypeBlockSlug.AssignUnique(new[] { (1, "Тип"), (2, "Тип") });
        var b = TypeBlockSlug.AssignUnique(new[] { (1, "Тип"), (2, "Тип") });
        Assert.Equal(a[1], b[1]);
        Assert.Equal(a[2], b[2]);
    }

    /// <summary>Длинный код не должен упереть путь в лимит ФС — обрезаем, уникальность сохраняем.</summary>
    [Fact]
    public void Sanitize_TruncatesVeryLongCodes()
    {
        var slug = TypeBlockSlug.Sanitize(new string('Я', 300));
        Assert.True(slug.Length <= 60);
    }
}
