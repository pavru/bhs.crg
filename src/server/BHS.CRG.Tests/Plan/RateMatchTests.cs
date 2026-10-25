using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Matching;
using BHS.CRG.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Запомненное сопоставление шифра: ключ, единственность, единицы и коэффициент (change <c>plan-08-matching</c>,
/// задача 1.1, ТЗ PLAN-6.1).
/// </summary>
public class RateMatchKeyTests
{
    /// <summary>Регистр, пробелы и неразрывные пробелы шифра — не различие; изменения базы — тоже.</summary>
    [Fact]
    public void Ключ_приводит_шифр_и_берёт_базу_без_изменений()
    {
        var plain = RateMatchKey.Of("ГЭСНм08-02-399-01", "ФСНБ-2022 (с Изм. 1-18.1)")!;

        Assert.Equal(plain.Hash, RateMatchKey.Of(" гэснм 08-02-399-01 ", "ФСНБ-2022 (с Изм. 1-19)")!.Hash);
        Assert.Equal(plain.Hash, RateMatchKey.Of("ГЭСНм" + '\u00A0' + "08-02-399-01", "ФСНБ-2022")!.Hash);
        Assert.Equal("ФСНБ-2022", plain.NormativeBase);
        Assert.Equal("ГЭСН-2020, ФЕР-2020", RateMatchKey.BaseOf(RateMatchFlow.SampleBase));

        // Другая база — другой ключ: единицы сборников могут различаться.
        Assert.NotEqual(plain.Hash, RateMatchKey.Of("ГЭСНм08-02-399-01", RateMatchFlow.SampleBase)!.Hash);
        Assert.NotEqual(plain.Hash, RateMatchKey.Of("ГЭСНм08-02-399-01", null)!.Hash);
        Assert.Null(RateMatchKey.Of("  ", "ФСНБ-2022"));
    }

    [Theory]
    [InlineData("100 м", "м", "100")]
    [InlineData("100м", "м", "100")]
    [InlineData("1000 м3", "м3", "1000")]
    [InlineData("шт", "шт", "1")]
    [InlineData("0,1 т", "т", "0.1")]
    [InlineData("100 м", "шт", null)]
    [InlineData("компл", "шт", null)]
    [InlineData(null, "м", null)]
    public void Коэффициент_предлагается_из_единиц(string? source, string unit, string? factor) =>
        Assert.Equal(factor is null ? null : decimal.Parse(factor, System.Globalization.CultureInfo.InvariantCulture),
            RateMatching.SuggestFactor(source, unit));

    /// <summary>Сторож из Risks design: «100 м → м» — сто, а не единица.</summary>
    [Fact]
    public void Сто_метров_в_метры_это_сто() => Assert.Equal(100m, RateMatching.SuggestFactor("100 м", "м"));

    [Fact]
    public void Единица_расценки_сравнивается_без_регистра_и_пробелов()
    {
        Assert.True(RateMatching.SameUnit("100 м", "100М"));
        Assert.False(RateMatching.SameUnit("100 м", "м"));
        Assert.False(RateMatching.SameUnit("шт", null));
    }
}

/// <summary>Единственность ключа — правило таблицы, а не обещание кода (задача 1.1).</summary>
[Collection(PlanOnlyCollection.Name)]
public class RateMatchUniquenessTests(PlanOnlyHost host)
{
    [Fact]
    public async Task Второй_ключ_с_тем_же_отпечатком_отвергнут_единственным_индексом()
    {
        var c = await EstimateSeed.ClassifierAsync(host);
        var code = RateMatchFlow.UniqueCode();
        var value = new RateMatchValue(c.WorkType, c.Unit, 100m, "100 м");

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
            db.RateMatches.Add(RateMatch.Create(RateMatchKey.Of(code, "ФСНБ-2022")!, value, null, null));
            await db.SaveChangesAsync();
        }

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
            // Тот же шифр, написанный иначе, и та же база с другими изменениями — тот же отпечаток.
            db.RateMatches.Add(RateMatch.Create(
                RateMatchKey.Of(" " + code.ToLowerInvariant(), "ФСНБ-2022 (с Изм. 1-18.1)")!, value, null, null));
            var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("ux_rate_matches_key", refused.InnerException!.Message);
        }
    }
}
