using BHS.CRG.Infrastructure.Reconciliation;

namespace BHS.CRG.Tests.Reconciliation;

/// <summary>
/// Сведение ключа по алиасам (issue #446). Ошибка здесь либо не сводит позиции, которые человек уже
/// связал, либо вешает прогон на цикле.
/// </summary>
public class AliasResolverTests
{
    private static AliasResolver Of(params (string From, string To)[] pairs)
        => new(pairs.ToDictionary(p => p.From, p => p.To, StringComparer.Ordinal));

    [Fact]
    public void UnknownKey_StaysAsIs() => Assert.Equal("а", Of(("б", "в")).Resolve("а"));

    [Fact]
    public void DirectAlias_IsApplied() => Assert.Equal("канон", Of(("вариант", "канон")).Resolve("вариант"));

    /// <summary>
    /// Человек неизбежно заведёт А→Б, а потом Б→В. Остановившись на первом шаге, мы дали бы две
    /// позиции там, где он ожидает одну.
    /// </summary>
    [Fact]
    public void Chain_IsResolvedTransitively()
        => Assert.Equal("в", Of(("а", "б"), ("б", "в")).Resolve("а"));

    /// <summary>Цикл — ошибка данных, но прогон он вешать не должен.</summary>
    [Theory]
    [InlineData("а")]
    [InlineData("б")]
    public void Cycle_DoesNotHang(string start)
    {
        var resolved = Of(("а", "б"), ("б", "а")).Resolve(start);
        Assert.Contains(resolved, new[] { "а", "б" });
    }

    [Fact]
    public void SelfCycle_DoesNotHang() => Assert.Equal("а", Of(("а", "а")).Resolve("а"));

    [Fact]
    public void Empty_IsPassthrough() => Assert.Equal("что угодно", AliasResolver.Empty.Resolve("что угодно"));

    [Fact]
    public void RepeatedResolve_IsStable()
    {
        var r = Of(("а", "б"), ("б", "в"));
        Assert.Equal(r.Resolve("а"), r.Resolve("а"));
    }
}
