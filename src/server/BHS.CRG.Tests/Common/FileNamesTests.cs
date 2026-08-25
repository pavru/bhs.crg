using BHS.CRG.Application.Common;

namespace BHS.CRG.Tests.Common;

/// <summary>
/// Имя файла для скачивания собирается из пользовательских строк (имя документа, имя шаблона).
/// Разделители пути обязаны вычищаться НА ЛЮБОЙ платформе: на Linux обратный слэш — обычный
/// символ имени, и платформенный список запрещённых символов его не ловит (issue #854).
/// </summary>
public class FileNamesTests
{
    [Theory]
    [InlineData(@"отдел\акт.pdf")]
    [InlineData("отдел/акт.pdf")]
    [InlineData(@"..\..\секрет.pdf")]
    public void Sanitize_StripsSeparators_OnEveryPlatform(string name)
    {
        var sanitized = FileNames.Sanitize(name);

        Assert.DoesNotContain('\\', sanitized);
        Assert.DoesNotContain('/', sanitized);
        Assert.Equal(sanitized, Path.GetFileName(sanitized));
    }

    [Fact]
    public void Sanitize_KeepsUsableNameIntact()
        => Assert.Equal("Акт освидетельствования №12.pdf", FileNames.Sanitize("Акт освидетельствования №12.pdf"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_FallsBackWhenNothingUsableLeft(string? name)
        => Assert.Equal("файл", FileNames.Sanitize(name));
}
