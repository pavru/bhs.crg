using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Configuration.ContractProbes;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Контракт отвечает в правах спрашивающего (ТЗ CORE-34; plan-22): без права — «скрыто правами» с кодом
/// права, системе — отказ с причиной, выключенный поставщик — «не подключено». На пробном контракте,
/// пройденном через порт, как его увидит потребитель.
/// </summary>
public class ModuleContractAnswerTests
{
    private static ContractAnswer<int>? Ask(string enabled, ModuleReader reader)
    {
        using var sp = Build(enabled, [typeof(IProbeVolumes)],
            Supplying((s, code) => s.AddModuleContract<IProbeVolumes, ProbeVolumes>(code)), Consuming());
        using var scope = sp.CreateScope();

        return scope.ServiceProvider.GetRequiredService<IModuleContracts>().Get<IProbeVolumes>() switch
        {
            ModuleContractAnswer<IProbeVolumes>.Connected on => on.Contract.Read(reader),
            _ => null,
        };
    }

    private static ModuleReader User(params string[] keys) => ModuleReader.Of(Guid.NewGuid(), "Иванов", keys);

    [Fact]
    public void Без_права_скрыто_правами_с_кодом_права()
    {
        var answer = Ask($"{Supplier},{Consumer}", User("consumer.request.read"));

        var hidden = Assert.IsType<ContractAnswer<int>.Hidden>(answer);
        Assert.Equal(ProbeVolumes.ReadPermission, hidden.Permission);
        Assert.Contains(ProbeVolumes.ReadPermission, hidden.Text);
    }

    [Theory]
    [InlineData(ProbeVolumes.ReadPermission)]
    [InlineData(ProbeVolumes.Narrow)]
    public void Любое_из_прав_ответа_открывает_данные(string permission)
    {
        var shown = Assert.IsType<ContractAnswer<int>.Shown>(Ask($"{Supplier},{Consumer}", User(permission)));

        Assert.Equal(42, shown.Value);
    }

    [Fact]
    public void Системе_отказ_с_причиной_а_не_данные_и_не_скрыто()
    {
        var answer = Ask($"{Supplier},{Consumer}", ModuleReader.OfSystem("ночной пересчёт сводки"));

        var refused = Assert.IsType<ContractAnswer<int>.SystemRefused>(answer);
        Assert.Equal("ночной пересчёт сводки", refused.Reason);
        Assert.Contains("ночной пересчёт сводки", refused.Text);
    }

    [Fact]
    public void Выключенный_поставщик_не_подключено()
    {
        using var sp = Build(Consumer, [typeof(IProbeVolumes)],
            Supplying((s, code) => s.AddModuleContract<IProbeVolumes, ProbeVolumes>(code)), Consuming());
        using var scope = sp.CreateScope();

        var answer = scope.ServiceProvider.GetRequiredService<IModuleContracts>().Get<IProbeVolumes>();

        var off = Assert.IsType<ModuleContractAnswer<IProbeVolumes>.NotConnected>(answer);
        Assert.Equal("Пробные объёмы", off.ContractTitle);
        Assert.Equal(SupplierTitle, off.ProviderTitle);
    }

    [Fact]
    public void Контракт_обязан_назвать_право_ответа()
    {
        Assert.Throws<ArgumentException>(() => ContractAnswer.Refusal<int>(User()));
    }
}
