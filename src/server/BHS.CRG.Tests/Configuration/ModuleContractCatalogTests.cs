using BHS.CRG.Modules.Exchange;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Configuration.ContractProbes;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Межмодульный контракт: проверка при старте и ответ порта потребителю (ТЗ CORE-34, OVW-12; plan-22).
///
/// <para>На пробных модулях: настоящих контрактов в поставке пока нет, а сторож, который проверяется
/// только на настоящих, впервые сработал бы на первом из них — в чужом change.</para>
/// </summary>
public class ModuleContractCatalogTests
{
    // ── Отказ старта ─────────────────────────────────────────────────────────

    [Fact]
    public void Включённый_поставщик_без_реализации_останавливает_старт()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() =>
            Services($"{Supplier},{Consumer}", [typeof(IProbePlan)], Supplying(null), Consuming()));

        Assert.Contains("Пробный план", refusal.Message);
        Assert.Contains($"«{Supplier}»", refusal.Message);
        Assert.Contains("не зарегистрировал", refusal.Message);
    }

    [Fact]
    public void Два_поставщика_одного_контракта_останавливают_старт_и_названы_оба()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => Services($"{Supplier},{Consumer}",
            Supplying((s, code) => s.AddModuleContract<IProbePlan, ProbePlan>(code)),
            Consuming((s, code) => s.AddModuleContract<IProbePlan, ProbeOtherPlan>(code))));

        Assert.Contains("несколько реализаций", refusal.Message);
        Assert.Contains($"«{Supplier}» ({nameof(ProbePlan)})", refusal.Message);
        Assert.Contains($"«{Consumer}» ({nameof(ProbeOtherPlan)})", refusal.Message);
    }

    [Fact]
    public void Реализация_не_названным_поставщиком_останавливает_старт()
    {
        // Поставщик выключен, и чужая реализация заняла бы его место молча — потребитель получил бы
        // «подключено» от модуля, который за контракт не отвечает.
        var refusal = Assert.Throws<InvalidOperationException>(() => Services(Consumer,
            Supplying(null), Consuming((s, code) => s.AddModuleContract<IProbePlan, ProbeOtherPlan>(code))));

        Assert.Contains($"реализовал модуль «{Consumer}»", refusal.Message);
        Assert.Contains($"поставщик у контракта — «{Supplier}»", refusal.Message);
    }

    [Fact]
    public void Регистрация_от_имени_другого_модуля_останавливает_старт()
    {
        // Модуль назвал чужой код — сверка в цикле регистрации видит, чьими руками это сделано.
        var refusal = Assert.Throws<InvalidOperationException>(() => Services(Consumer,
            Supplying(null), Consuming((s, _) => s.AddModuleContract<IProbePlan, ProbeOtherPlan>(Supplier))));

        Assert.Contains($"Модуль «{Consumer}»", refusal.Message);
        Assert.Contains("от имени другого модуля", refusal.Message);
        Assert.Contains($"{nameof(IProbePlan)} — как «{Supplier}»", refusal.Message);
    }

    [Fact]
    public void Негодное_объявление_и_интерфейс_без_объявления_останавливают_старт()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => Services(Supplier, [typeof(IProbeUnnamed)],
            Supplying((s, code) =>
            {
                s.AddModuleContract<IProbeUnnamed, ProbeUnnamed>(code);
                s.AddModuleContract<IProbeNotContract, ProbePlan>(code);
            })));

        Assert.Contains("«Без поставщика»", refusal.Message);
        Assert.Contains("поставщик не назван", refusal.Message);
        Assert.Contains($"{nameof(IProbeNotContract)} зарегистрирован", refusal.Message);
    }

    [Fact]
    public void Два_вклада_одного_модуля_в_контракт_со_многими_останавливают_старт()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => Services(Supplier,
            Supplying((s, code) =>
            {
                s.AddModuleContract<IProbeContribution, SupplierContribution>(code);
                s.AddModuleContract<IProbeContribution, ConsumerContribution>(code);
            })));

        Assert.Contains("«Пробный вклад»", refusal.Message);
        Assert.Contains($"модуль «{Supplier}» дал несколько реализаций", refusal.Message);
    }

    // ── Ответ потребителю ────────────────────────────────────────────────────

    [Fact]
    public void Выключенный_поставщик_даёт_не_подключено_с_названием()
    {
        using var sp = Build(Consumer, [typeof(IProbePlan)],
            Supplying((s, code) => s.AddModuleContract<IProbePlan, ProbePlan>(code)), Consuming());
        using var scope = sp.CreateScope();

        var answer = scope.ServiceProvider.GetRequiredService<IModuleContracts>().Get<IProbePlan>();

        var off = Assert.IsType<ModuleContractAnswer<IProbePlan>.NotConnected>(answer);
        Assert.Equal(Supplier, off.Provider);
        Assert.Equal("Пробный план", off.ContractTitle);
        Assert.Equal($"не подключено: модуль «{SupplierTitle}»", off.Text);
    }

    [Fact]
    public void Включённый_поставщик_отвечает_своей_реализацией()
    {
        using var sp = Build($"{Supplier},{Consumer}",
            Supplying((s, code) => s.AddModuleContract<IProbePlan, ProbePlan>(code)), Consuming());
        using var scope = sp.CreateScope();

        var answer = scope.ServiceProvider.GetRequiredService<IModuleContracts>().Get<IProbePlan>();

        var on = Assert.IsType<ModuleContractAnswer<IProbePlan>.Connected>(answer);
        Assert.Equal("ответ поставщика", on.Contract.Ask());
    }

    [Fact]
    public void Прямой_запрос_интерфейса_контракта_из_контейнера_не_даёт_ничего()
    {
        // Реализация лежит по ключу: потребитель, попросивший интерфейс прямо, узнаёт об ошибке при
        // включённом поставщике, а не в день, когда поставщика выключат.
        using var sp = Build(Supplier, Supplying((s, code) => s.AddModuleContract<IProbePlan, ProbePlan>(code)));
        using var scope = sp.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<IProbePlan>());
        Assert.Empty(scope.ServiceProvider.GetServices<IProbePlan>());
    }

    [Fact]
    public void Контракт_со_многими_отдаёт_вклады_включённых_модулей()
    {
        var supplier = Supplying((s, code) => s.AddModuleContract<IProbeContribution, SupplierContribution>(code));
        var consumer = Consuming((s, code) => s.AddModuleContract<IProbeContribution, ConsumerContribution>(code));

        using (var both = Build($"{Consumer},{Supplier}", supplier, consumer))
        using (var scope = both.CreateScope())
        {
            var all = scope.ServiceProvider.GetRequiredService<IModuleContracts>().GetAll<IProbeContribution>();

            // Порядок состава поставки — как назван в Modules__Enabled.
            Assert.Equal([Consumer, Supplier], all.Select(c => c.Module));
            Assert.Equal([Consumer, Supplier], all.Select(c => c.Contract.From));
        }

        using (var one = Build(Supplier, supplier, consumer))
        using (var scope = one.CreateScope())
        {
            var only = Assert.Single(scope.ServiceProvider.GetRequiredService<IModuleContracts>()
                .GetAll<IProbeContribution>());
            Assert.Equal(Supplier, only.Module);
        }
    }

    [Fact]
    public void Контракт_со_многими_перечислен_каталогом()
    {
        using var sp = Build(Supplier, [typeof(IProbeContribution)], Supplying((s, code) => s.AddModuleContract<IProbePlan, ProbePlan>(code)));

        var many = sp.GetRequiredService<BHS.CRG.Api.Modules.ModuleContractCatalog>().ManyProviders;

        Assert.Equal([typeof(IProbeContribution)], many.Select(c => c.Interface));
    }

    [Fact]
    public void Не_тот_вопрос_порту_дефект_вызывающего()
    {
        using var sp = Build(Supplier, [typeof(IProbeContribution)], Supplying((s, code) => s.AddModuleContract<IProbePlan, ProbePlan>(code)));
        using var scope = sp.CreateScope();
        var contracts = scope.ServiceProvider.GetRequiredService<IModuleContracts>();

        Assert.Contains("GetAll", Assert.Throws<InvalidOperationException>(() => contracts.Get<IProbeContribution>()).Message);
        Assert.Contains("одного поставщика", Assert.Throws<InvalidOperationException>(() => contracts.GetAll<IProbePlan>()).Message);
        Assert.Contains("не межмодульный контракт",
            Assert.Throws<InvalidOperationException>(() => contracts.Get<IProbeNotContract>()).Message);
    }
}
