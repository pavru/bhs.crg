using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Common;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Порядок смены настройки с обработчиком модуля (E2, issue #1100, spec v3.3 п. 1.9): пока модуль пишет,
/// ядро держит замок записи в учёт — закрытие периода ждёт; выравнивание идёт, когда замков ядра уже нет;
/// отказ модуля — отказ всей смены. Числа книги — в <c>InvoicePaymentTests.Tolerance.cs</c>; здесь —
/// служба ядра с обработчиком-зондом вместо модуля.
/// </summary>
[Collection("Integration")]
public class ModuleSettingWriterTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private const string Key = "costs.allocation.tolerance";

    public override async Task DisposeAsync()
    {
        using var scope = host.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAppSettingsStore>();
        await store.SetAsync(Key, null);
        await store.SetAsync(CostsSettings.ShippingDayCounts.Key, null);
    }

    [Fact]
    public async Task Модуль_пишет_под_замком_ядра_а_выравнивает_без_него()
    {
        var probe = new Probe(host.Services, refuse: false);
        await SaveAsync([probe], new Dictionary<string, string?> { [Key] = "0.30" });

        Assert.Equal((true, false), (probe.HeldOnApply, probe.HeldOnAlign));
        // «Было» — умолчание (null), «станет» — хранимый вид; выравнивание — к записанному.
        Assert.Equal((null, "0.30"), (probe.Applied!.Before, probe.Applied.After));
        Assert.Equal(["0.30"], probe.Aligned);
        Assert.Equal("0.30", await StoredAsync());
    }

    [Fact]
    public async Task Отказ_модуля_отказ_всей_смены()
    {
        var probe = new Probe(host.Services, refuse: true);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SaveAsync([probe], new Dictionary<string, string?> { [Key] = "0.30" }));

        Assert.Equal("модуль отказал", refused.Message);
        Assert.Null(await StoredAsync());
        // Отказал ли модуль до своей фиксации или после — ядру не узнать: выравнивается к прежнему. Модулю, не
        // записавшему ничего, выравнивание ничего и не сделает.
        Assert.Equal(new string?[] { null }, probe.Aligned);
    }

    /// <summary>
    /// Выравнивание берёт значение, действующее СЕЙЧАС, а не запомненное сменой: замок ключа к нему уже снят, и
    /// следующая смена могла пройти — опоздавшее выравнивание вернуло бы книги к устаревшему. Здесь «следующая
    /// смена» — запись в хранилище с чужого соединения, пока модуль работает; сам модуль после неё отказывает.
    /// </summary>
    [Fact]
    public async Task Выравнивание_берёт_действующее_значение_а_не_запомненное()
    {
        var probe = new Probe(host.Services, refuse: true) { Overtake = "0.70" };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SaveAsync([probe], new Dictionary<string, string?> { [Key] = "0.30" }));

        Assert.Equal(["0.70"], probe.Aligned);
    }

    /// <summary>
    /// Смена сорвалась ПОСЛЕ того, как модуль зафиксировал своё (отказал обработчик второй настройки): ядро
    /// не пишет ничего, а модуль выравнивается к прежнему значению — и уже без замков ядра: транзакция ядра
    /// при срыве не зафиксирована, и её замок жив, пока её не откатили.
    /// </summary>
    [Fact]
    public async Task Срыв_после_фиксации_модуля_выравнивает_к_прежнему_без_замков_ядра()
    {
        var applied = new Probe(host.Services, refuse: false);
        var refusing = new Probe(host.Services, refuse: true) { Watched = CostsSettings.ShippingDayCounts };

        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveAsync([applied, refusing],
            new Dictionary<string, string?> { [Key] = "0.30", [CostsSettings.ShippingDayCounts.Key] = "false" }));

        Assert.Equal((true, false), (applied.HeldOnApply, applied.HeldOnAlign));
        Assert.Equal(new string?[] { null }, applied.Aligned);
        Assert.Equal(new string?[] { null }, refusing.Aligned);
        Assert.Null(await StoredAsync());
    }

    /// <summary>
    /// Сторож при старте: обработчик стережёт объявленную настройку включённого модуля, тем же объектом, и
    /// он у неё один. Иначе смену провели бы мимо него — или дважды.
    /// </summary>
    [Fact]
    public void Сторож_обработчиков_отказывает_повтору_и_необъявленной_настройке()
    {
        using var scope = host.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ModuleSettingCatalog>();
        var registry = scope.ServiceProvider.GetRequiredService<ModuleRegistry>();
        var real = scope.ServiceProvider.GetServices<IModuleSettingChange>().ToList();

        Assert.Equal([Key], real.Select(h => h.Setting.Key));
        ModuleSettingWriter.Validate(real, catalog, registry);

        var twice = Assert.Throws<InvalidOperationException>(() =>
            ModuleSettingWriter.Validate([.. real, new Probe(host.Services, false)], catalog, registry));
        Assert.Contains("обработчиков смены несколько", twice.Message);

        var twin = new Probe(host.Services, false) { Watched = CostsSettings.AllocationTolerance with { DefaultValue = 50m } };
        var stray = Assert.Throws<InvalidOperationException>(() => ModuleSettingWriter.Validate([twin], catalog, registry));
        Assert.Contains("не объявляет ни один включённый модуль", stray.Message);
    }

    private async Task SaveAsync(IModuleSettingChange[] probes, Dictionary<string, string?> values)
    {
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        var writer = new ModuleSettingWriter(probes, services.GetRequiredService<AppDbContext>(),
            services.GetRequiredService<IAppSettingsStore>(), services.GetRequiredService<IActivityLog>(),
            services.GetRequiredService<IModulePeriods>(), NullLogger<ModuleSettingWriter>.Instance);
        await writer.SaveAsync(services.GetRequiredService<ModuleRegistry>().Find("costs")!, values, CancellationToken.None);
    }

    private async Task<string?> StoredAsync()
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAppSettingsStore>().GetAsync(Key);
    }

    /// <summary>
    /// Зонд вместо модуля: спрашивает с ЧУЖОГО соединения, держит ли кто-нибудь разделяемый замок записи в
    /// учёт, — так его видит закрытие периода.
    /// </summary>
    private sealed class Probe(IServiceProvider services, bool refuse) : IModuleSettingChange
    {
        public ModuleSetting Watched { get; init; } = CostsSettings.AllocationTolerance;
        public ModuleSetting Setting => Watched;
        public ModuleSettingChange? Applied { get; private set; }
        public bool? HeldOnApply { get; private set; }
        public bool? HeldOnAlign { get; private set; }
        public List<string?> Aligned { get; } = [];

        public Task<ModuleSettingImpact> PreviewAsync(string? before, string? after, CancellationToken ct = default) =>
            throw new NotSupportedException();

        /// <summary>Что записать в хранилище с чужого соединения, пока модуль работает, — «следующая смена».</summary>
        public string? Overtake { get; init; }

        public async Task ApplyAsync(ModuleSettingChange change, CancellationToken ct = default)
        {
            (Applied, HeldOnApply) = (change, await HeldAsync());
            if (Overtake is not null)
            {
                using var scope = services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IAppSettingsStore>().SetAsync(Setting.Key, Overtake);
            }
            if (refuse) throw new InvalidOperationException("модуль отказал");
        }

        public async Task AlignAsync(string? current, CancellationToken ct = default)
        {
            HeldOnAlign = await HeldAsync();
            Aligned.Add(current);
        }

        private async Task<bool> HeldAsync()
        {
            using var scope = services.CreateScope();
            const long key = AdvisoryLockKeys.PeriodWrite;
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.SqlQuery<bool>($"""
                SELECT EXISTS (SELECT 1 FROM pg_locks
                    WHERE locktype = 'advisory' AND granted AND mode = 'ShareLock'
                      AND objsubid = 1 AND classid::bigint = {key >> 32} AND objid::bigint = {key & 0xFFFFFFFF}) AS "Value"
                """).SingleAsync();
        }
    }
}
