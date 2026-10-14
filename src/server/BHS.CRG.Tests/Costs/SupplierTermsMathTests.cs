using BHS.CRG.Modules.Costs.Data;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Когда счёт получает отсрочку из условий поставщика (ТЗ COST-9.2; задача C4, issue #1080) — правило
/// без базы и хоста: все ветви разом.
/// </summary>
public class SupplierTermsMathTests
{
    private static readonly Guid First = Guid.NewGuid();
    private static readonly Guid Second = Guid.NewGuid();

    private static InvoiceColumns Columns(Guid? supplier, int? deferral) =>
        new(null, null, supplier, null, null, null, null, null, deferral, null);

    [Fact]
    public void Новый_счёт_с_поставщиком_без_отсрочки_получает_её() =>
        Assert.Equal(30, SupplierTerms.Substituted(Columns(First, null), (null, null, false), 30));

    [Fact]
    public void Отсрочка_из_бумаги_у_нового_счёта_остаётся() =>
        Assert.Null(SupplierTerms.Substituted(Columns(First, 45), (null, null, false), 30));

    [Fact]
    public void Поставщик_не_сменился_условия_не_подставляются() =>
        Assert.Null(SupplierTerms.Substituted(Columns(First, null), (First, null, false), 30));

    [Fact]
    public void Без_условий_у_нового_поставщика_подставлять_нечего() =>
        Assert.Null(SupplierTerms.Substituted(Columns(Second, 10), (First, 10, true), null));

    [Fact]
    public void Отсрочка_из_условий_прежнего_меняется_на_условия_нового() =>
        Assert.Equal(20, SupplierTerms.Substituted(Columns(Second, 10), (First, 10, true), 20));

    /// <summary>Вписанная рукой — даже равная условиям прежнего поставщика (ревью PR #1329).</summary>
    [Fact]
    public void Вписанная_отсрочка_при_смене_поставщика_остаётся() =>
        Assert.Null(SupplierTerms.Substituted(Columns(Second, 7), (First, 7, false), 20));

    [Fact]
    public void Отсрочку_правили_той_же_записью_условия_её_не_перекрывают() =>
        Assert.Null(SupplierTerms.Substituted(Columns(Second, 12), (First, null, false), 20));

    [Fact]
    public void Вписанная_равная_условиям_прежнего_тоже_остаётся() =>
        Assert.Null(SupplierTerms.Substituted(Columns(Second, 30), (First, 30, false), 45));

    [Fact]
    public void Поставщика_сняли_подставлять_некому() =>
        Assert.Null(SupplierTerms.Substituted(Columns(null, 10), (First, 10, true), null));
}
