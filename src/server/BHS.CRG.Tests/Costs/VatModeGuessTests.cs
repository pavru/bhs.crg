using BHS.CRG.Modules.Costs.Data;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Догадка о режиме НДС по арифметике бумаги (сторож С10, issue #1500). Числа выдуманные; пример владельца
/// — строки 1000 + 500, НДС 22 % = 330, к оплате 1830.
/// </summary>
public class VatModeGuessTests
{
    private const decimal Tolerance = 1m;

    private static VatMode? Guess(decimal? headerVat, decimal total, params (decimal Paper, decimal? Rate)[] lines) =>
        VatModeGuess.Of([.. lines.Select(l => new VatGuessLine(l.Paper, l.Rate))], headerVat, total, Tolerance);

    [Fact]
    public void Суммы_сходятся_с_суммой_к_оплате_в_том_числе() =>
        Assert.Equal(VatMode.Included, Guess(270.49m, 1500m, (1000m, 22m), (500m, 22m)));

    [Fact]
    public void Расхождение_ровно_на_НДС_шапки_сверху() =>
        Assert.Equal(VatMode.OnTop, Guess(330m, 1830m, (1000m, null), (500m, null)));

    /// <summary>НДС шапки не прочитан — хватает ставок строк: тем же правилом, что досчёт строки.</summary>
    [Fact]
    public void Расхождение_ровно_на_НДС_по_ставкам_строк_сверху() =>
        Assert.Equal(VatMode.OnTop, Guess(null, 1830m, (1000m, 22m), (500m, 22m)));

    /// <summary>Бумага считает НДС от итога, строки — построчно: копейки разницы — в допуске.</summary>
    [Fact]
    public void Копейки_от_итога_в_допуске() =>
        Assert.Equal(VatMode.OnTop, Guess(null, 406.67m, (333.33m, 22m)));

    /// <summary>
    /// Ставка не у каждой строки — по ставкам не угадывается: НДС части строк совпал бы с расхождением
    /// случайно. Шапка без НДС — тоже догадки нет.
    /// </summary>
    [Fact]
    public void Ставка_не_у_каждой_строки_догадки_нет() =>
        Assert.Null(Guess(null, 1720m, (1000m, 22m), (500m, null)));

    [Fact]
    public void Расхождение_не_равно_НДС_догадки_нет() =>
        Assert.Null(Guess(330m, 1700m, (1000m, 22m), (500m, 22m)));

    /// <summary>
    /// Скидка случайно равна НДС: догадка «сверху» выйдет — и это лишь подсказка; переключить счёт она не
    /// может (готовность только называет её, проверено интеграционно).
    /// </summary>
    [Fact]
    public void Совпадение_случайно_тоже_лишь_догадка() =>
        Assert.Equal(VatMode.OnTop, Guess(330m, 1830m, (1830m - 330m, 22m)));

    [Fact]
    public void Строк_нет_догадки_нет() => Assert.Null(Guess(330m, 1830m));

    /// <summary>НДС меньше допуска (счёт на копейки) — сошлось бы и то и другое; сошедшийся счёт — «в том числе».</summary>
    [Fact]
    public void В_том_числе_первым() =>
        Assert.Equal(VatMode.Included, Guess(0.5m, 3m, (2.5m, 20m)));
}
