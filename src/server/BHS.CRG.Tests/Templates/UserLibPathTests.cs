using BHS.CRG.Application.Templates;

namespace BHS.CRG.Tests.Templates;

/// <summary>
/// Проверка путей файлов библиотеки (issue #473). Путь приходит от пользователя и превращается в путь
/// на диске, поэтому цена ошибки — запись за пределы папки генерации, а не кривое имя.
/// </summary>
public class UserLibPathTests
{
    private static string? ErrorFor(string? raw)
    {
        UserLibPath.TryNormalize(raw, out _, out var error);
        return error;
    }

    private static string Ok(string raw)
    {
        Assert.True(UserLibPath.TryNormalize(raw, out var normalized, out var error), error);
        return normalized;
    }

    [Theory]
    [InlineData("f3.typ")]
    [InlineData("gost/forms/f3.typ")]
    [InlineData("util/текст.typ")]              // кириллица в именах — обычное дело у этого пользователя
    [InlineData("gost/forms/f-3_v2.typ")]
    public void ValidPaths_Pass(string path) => Assert.Equal(path, Ok(path));

    [Fact]
    public void Backslashes_AreNormalized() => Assert.Equal("gost/forms/f3.typ", Ok(@"gost\forms\f3.typ"));

    /// <summary>Главное, ради чего вся проверка: выход за пределы дерева.</summary>
    [Theory]
    [InlineData("../userlib.typ")]
    [InlineData("gost/../../data.json")]
    [InlineData("/etc/passwd")]
    [InlineData("gost/./f3.typ")]
    public void EscapeAttempts_AreRejected(string path) => Assert.NotNull(ErrorFor(path));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gost//f3.typ")]
    [InlineData("gost/")]
    public void EmptyOrMalformed_AreRejected(string path) => Assert.NotNull(ErrorFor(path));

    /// <summary>Библиотека — это Typst-код; всё прочее в дереве только сбивало бы с толку.</summary>
    [Theory]
    [InlineData("gost/f3.txt")]
    [InlineData("gost/f3")]
    [InlineData("gost/.typ")]
    public void NonTypstFiles_AreRejected(string path) => Assert.NotNull(ErrorFor(path));

    /// <summary>Файл «CON.typ» на Windows не создастся — и упало бы это уже во время генерации.</summary>
    [Theory]
    [InlineData("CON.typ")]
    [InlineData("gost/nul.typ")]
    [InlineData("COM1.typ")]
    public void WindowsReservedNames_AreRejected(string path) => Assert.NotNull(ErrorFor(path));

    [Theory]
    [InlineData("go:st/f3.typ")]
    [InlineData("gost/f?3.typ")]
    [InlineData("gost/f|3.typ")]
    public void ForbiddenCharacters_AreRejected(string path) => Assert.NotNull(ErrorFor(path));

    [Fact]
    public void TooDeep_IsRejected()
        => Assert.NotNull(ErrorFor(string.Join('/', Enumerable.Repeat("a", 11)) + ".typ"));

    [Fact]
    public void TooLong_IsRejected() => Assert.NotNull(ErrorFor(new string('a', 250) + ".typ"));

    /// <summary>
    /// Регистр значим: Linux в контейнере различает «Gost/» и «gost/», и импорт, написанный по одному
    /// написанию, сломался бы только в продакшене.
    /// </summary>
    [Fact]
    public void CaseIsSignificant()
    {
        Assert.False(UserLibPath.AreEqual("Gost/f3.typ", "gost/f3.typ"));
        Assert.True(UserLibPath.DiffersOnlyByCase("Gost/f3.typ", "gost/f3.typ"));
    }

    /// <summary>
    /// Точка входа адресуется той же строкой, поэтому файл дерева с таким путём становится с ней
    /// неразличим (issue #510): его ошибки садились бы на строку точки входа и помечались бы
    /// «входит в сборку» даже будучи неподключёнными. Регистр не спасает — на Windows это один файл.
    ///
    /// Отдельно от <see cref="UserLibPath.TryNormalize"/> (issue #512): структурно путь законен, и
    /// старая запись могла приехать из бэкапа — отвергать её на каждом сохранении значило бы сделать
    /// библиотеку несохраняемой целиком. Запрет применяется к НОВЫМ путям, в эндпоинте.
    /// </summary>
    [Theory]
    [InlineData("userlib.typ", true)]
    [InlineData("UserLib.typ", true)]
    [InlineData("gost/userlib.typ", false)]   // во вложенной папке путь другой — конфликта нет
    public void TakesEntrypointName_IsCaseInsensitiveAndRootOnly(string path, bool expected)
        => Assert.Equal(expected, UserLibPath.TakesEntrypointName(path));

    [Fact]
    public void PathOfTheEntrypoint_IsStructurallyValid() => Assert.Null(ErrorFor("userlib.typ"));

    [Fact]
    public void IsInFolder_MatchesOnlyWholeSegments()
    {
        Assert.True(UserLibPath.IsInFolder("gost/forms/f3.typ", "gost"));
        Assert.True(UserLibPath.IsInFolder("gost/forms/f3.typ", "gost/forms"));
        Assert.False(UserLibPath.IsInFolder("gostx/f3.typ", "gost"));
    }
}
