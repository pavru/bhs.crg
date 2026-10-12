using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Валюта учёта и документы модуля (issue #1248): кто держит валюту и что с ней делает
/// восстановление копии.
///
/// <para>На поддельном модуле — по той же причине, что и остальной класс: здесь можно и выключить
/// модуль, и убрать его документы, не трогая настоящих счетов соседних тестов.</para>
/// </summary>
public partial class ModuleDataBackupTests
{
    private const string Currency = AppSettingKeys.AccountingCurrency;

    /// <summary>
    /// Сторож главного довода против порта: ВЫКЛЮЧЕННЫЙ модуль со счетами валюту держит. Его служб в
    /// контейнере нет, и спросить его самого было бы не у кого — ответом стала бы тишина.
    /// </summary>
    [Fact]
    public async Task Выключенный_модуль_с_документами_держит_валюту()
    {
        await SeedInvoiceAsync(await SeedCoreObjectAsync());
        await SeedInvoiceAsync(await SeedCoreObjectAsync(), "Счёт №2");

        var holder = Assert.Single(await HoldersAsync(new ModuleRegistry([], [new ProbeModule()])));

        Assert.False(holder.Enabled);
        Assert.Equal(new CurrencyHeldCount("счетов", 2), Assert.Single(holder.Counts));
        var reason = AccountingCurrencyLockText.Reason([holder]);
        Assert.Contains("в данных выключенного модуля «Поддельный модуль копии» — счетов: 2", reason);
    }

    [Fact]
    public async Task Без_документов_валюту_не_держит_никто()
    {
        Assert.Empty(await HoldersAsync(Registry()));

        // Схемы нет вовсе — модуль на экземпляре не включали ни разу. Это «документов нет», а не отказ.
        await DropSchemaAsync();
        Assert.Empty(await HoldersAsync(Registry()));
    }

    /// <summary>
    /// Копия с другой валютой поверх живых документов валюту НЕ меняет и говорит об этом:
    /// восстановление ничего не удаляет, и местные суммы получили бы чужой знак.
    /// </summary>
    [Fact]
    public async Task Копия_не_меняет_валюту_поверх_местных_документов()
    {
        await SeedInvoiceAsync(await SeedCoreObjectAsync());
        await SetCurrencyAsync("KZT");
        var (archive, _) = await ExportAsync(BackupScope.Full);
        await SetCurrencyAsync(null);

        var report = await ImportAsync(archive);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        Assert.Null(await StoredCurrencyAsync());
        var warning = Assert.Single(report.Warnings, w => w.Contains("Валюта учёта", StringComparison.Ordinal));
        Assert.Contains("Казахстанский тенге (KZT)", warning);
        Assert.Contains("Российский рубль (RUB)", warning);
        Assert.Contains("счетов: 1", warning);
        Assert.Empty(await CurrencyChangesAsync());
    }

    [Fact]
    public async Task Без_местных_документов_валюта_приходит_из_копии_и_пишется_в_журнал()
    {
        await SeedInvoiceAsync(await SeedCoreObjectAsync());
        await SetCurrencyAsync("KZT");
        var (archive, _) = await ExportAsync(BackupScope.Full);
        await ExecuteAsync($"DELETE FROM {ModuleSchemaName}.invoices");
        await SetCurrencyAsync(null);

        var report = await ImportAsync(archive);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        Assert.Equal("KZT", await StoredCurrencyAsync());
        var written = Assert.Single(await CurrencyChangesAsync());
        Assert.Equal("Российский рубль (RUB)", written.Before);
        Assert.Equal("Казахстанский тенге (KZT) (восстановление копии)", written.After);
    }

    /// <summary>
    /// Копия без ключа — это рубль, а не «о валюте ничего не сказано»: рубль не хранят, пока не
    /// сменили. Её документы под выбранным здесь тенге получили бы чужой знак.
    /// </summary>
    [Fact]
    public async Task Копия_без_ключа_с_документами_возвращает_рубль()
    {
        await SeedInvoiceAsync(await SeedCoreObjectAsync());
        var (archive, manifest) = await ExportAsync(BackupScope.Full);
        Assert.DoesNotContain(manifest.AppSettings ?? [], s => s.Key == Currency);
        await ExecuteAsync($"DELETE FROM {ModuleSchemaName}.invoices");
        await SetCurrencyAsync("KZT");

        var report = await ImportAsync(archive);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        Assert.Equal("RUB", await StoredCurrencyAsync());
        Assert.Equal("Казахстанский тенге (KZT)", Assert.Single(await CurrencyChangesAsync()).Before);
    }

    /// <summary>Копия без ключа и без документов выбор, сделанный здесь, не трогает: защищать нечего.</summary>
    [Fact]
    public async Task Копия_без_ключа_и_без_документов_валюту_не_трогает()
    {
        var (archive, _) = await ExportAsync(BackupScope.Full);
        await SetCurrencyAsync("KZT");

        var report = await ImportAsync(archive);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        Assert.Equal("KZT", await StoredCurrencyAsync());
        Assert.Empty(await CurrencyChangesAsync());
    }

    /// <summary>
    /// Строка не из списка в копии не записывается: клиент отдаёт код прямо в форматтер сумм, и тот
    /// на строке не по форме роняет каждый экран.
    /// </summary>
    [Fact]
    public void Каталог_принимает_валюту_только_из_списка()
    {
        var catalog = fixture.Services.GetRequiredService<IAppSettingCatalog>();

        Assert.True(catalog.Accepts(Currency, "CNY"));
        Assert.False(catalog.Accepts(Currency, "cny"));
        Assert.False(catalog.Accepts(Currency, "рубль"));
        Assert.False(catalog.Accepts(Currency, ""));
    }

    private async Task<IReadOnlyList<CurrencyHolder>> HoldersAsync(ModuleRegistry registry)
    {
        using var scope = fixture.Services.CreateScope();
        return await new AccountingCurrencyLock(scope.ServiceProvider.GetRequiredService<AppDbContext>(), registry)
            .HoldersAsync();
    }

    private async Task SetCurrencyAsync(string? value)
    {
        using var scope = fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAppSettingsStore>().SetAsync(Currency, value);
    }

    private async Task<string?> StoredCurrencyAsync()
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAppSettingsStore>().GetAsync(Currency);
    }

    private async Task<IReadOnlyList<BHS.CRG.Domain.Activity.ActivityRecord>> CurrencyChangesAsync()
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IActivityLog>()
            .ReadAsync(0, 50, ActivityVisibility.Whole, ActivityActions.AccountingCurrencyChanged.Code);
    }
}
