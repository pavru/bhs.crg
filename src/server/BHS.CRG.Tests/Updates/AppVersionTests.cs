using BHS.CRG.Application.Updates;

namespace BHS.CRG.Tests.Updates;

/// <summary>
/// Разбор и сравнение версий (issue #813). Сравниваются две ФОРМЫ одного номера: версия сборки
/// («0.137.1+хеш») и тег выпуска («v0.137.1»); ошибка здесь означает либо вечное молчание о вышедших
/// версиях, либо предложение обновиться до той, что уже установлена.
/// </summary>
public class AppVersionTests
{
    [Theory]
    [InlineData("0.137.1", 0, 137, 1)]
    [InlineData("v0.137.1", 0, 137, 1)]                      // тег выпуска
    [InlineData("0.137.1+a1b2c3d", 0, 137, 1)]               // версия сборки с хешем коммита
    [InlineData("v0.137.1+a1b2c3d", 0, 137, 1)]              // обе обёртки разом
    [InlineData("  1.0.0  ", 1, 0, 0)]
    [InlineData("2.10.30-rc.1", 2, 10, 30)]                  // пре-релиз: номер берём, суффикс нет
    public void ParsesBothForms(string text, int major, int minor, int patch)
    {
        Assert.True(AppVersion.TryParse(text, out var v));
        Assert.Equal(new AppVersion(major, minor, patch), v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("0.137")]        // не трёхчленный
    [InlineData("0.137.1.2")]
    [InlineData("0.x.1")]
    [InlineData("-1.0.0")]
    public void RefusesGarbage_InsteadOfReturningZero(string? text)
    {
        // Тихий 0.0.0 не дал бы ложного уведомления — он дал бы ВЕЧНОЕ МОЛЧАНИЕ: ноль никогда не
        // больше установленной версии, и служба просто перестала бы работать, никого не потревожив.
        Assert.False(AppVersion.TryParse(text, out _));
    }

    [Theory]
    [InlineData("0.138.0", "0.137.1", true)]
    [InlineData("1.0.0", "0.999.999", true)]
    [InlineData("0.137.2", "0.137.1", true)]
    [InlineData("0.137.1", "0.137.1", false)]                 // та же версия — сообщать не о чем
    [InlineData("0.137.0", "0.137.1", false)]                 // выпуск старше установленной
    public void IsNewer_ComparesStrictly(string released, string installed, bool expected)
        => Assert.Equal(expected, AppVersion.IsNewer(released, installed));

    [Fact]
    public void IsNewer_ComparesTagAgainstBuildVersion()
    {
        // Ровно тот случай, ради которого разбор терпит обёртки: слева тег GitHub, справа то, что
        // отдаёт AssemblyInformationalVersion.
        Assert.True(AppVersion.IsNewer("v0.138.0", "0.137.1+21ed989"));
        Assert.False(AppVersion.IsNewer("v0.137.1", "0.137.1+21ed989"));
    }

    [Fact]
    public void IsNewer_OnDevBuildAheadOfRelease_StaysSilent()
    {
        // На машине разработчика версия поднимается в том же PR, а релиз выходит позже: установленная
        // оказывается СТАРШЕ выпущенной. Предлагать «обновиться» до 0.137.1 при собранной 0.138.0
        // нельзя, и отдельного случая для этого не нужно — строгое сравнение уже отвечает верно.
        Assert.False(AppVersion.IsNewer("v0.137.1", "0.138.0+local"));
    }

    [Fact]
    public void IsNewer_UnparsableSide_IsNotNewer()
    {
        Assert.False(AppVersion.IsNewer("latest", "0.137.1"));
        Assert.False(AppVersion.IsNewer("v0.138.0", "неизвестно"));
    }

    [Theory]
    [InlineData("0.137.1+a1b2c3d", "0.137.1", "a1b2c3d")]
    [InlineData("0.137.1", "0.137.1", "")]
    public void SplitsInformationalVersion(string informational, string version, string commit)
    {
        var (v, c) = AppVersion.SplitInformational(informational);
        Assert.Equal(version, v);
        Assert.Equal(commit, c);
    }

    [Theory]
    [InlineData("v0.138.0", "0.138.0")]
    [InlineData("0.138.0+a1b2c3d", "0.138.0")]
    [InlineData("0.138.0", "0.138.0")]
    public void Normalize_StripsWrappers(string text, string expected)
    {
        // Наружу номер уходит без «v»: иначе в подвале панели рядом стоят «v0.137.1» и «доступна
        // v0.138.0», где «v» означает в одном случае наше оформление, а в другом — форму тега.
        Assert.Equal(expected, AppVersion.Normalize(text));
    }

    [Fact]
    public void Normalize_KeepsUnparsableAsIs()
    {
        // Подменять непонятную строку пустотой хуже, чем показать как есть: пустота выглядит как
        // «данных нет», хотя данные пришли — просто в неизвестной форме.
        Assert.Equal("latest", AppVersion.Normalize("latest"));
        Assert.Null(AppVersion.Normalize(null));
    }

    [Fact]
    public void OrdersByMajorThenMinorThenPatch()
    {
        var sorted = new[]
        {
            new AppVersion(0, 137, 2), new AppVersion(1, 0, 0),
            new AppVersion(0, 138, 0), new AppVersion(0, 137, 10),
        };
        Array.Sort(sorted);
        Assert.Equal(
            [new AppVersion(0, 137, 2), new AppVersion(0, 137, 10), new AppVersion(0, 138, 0), new AppVersion(1, 0, 0)],
            sorted);
    }
}
