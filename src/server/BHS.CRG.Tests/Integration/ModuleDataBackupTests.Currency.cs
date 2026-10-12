using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Data;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

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

    /// <summary>
    /// Копия считается несущей документы, только если восстановление их ПОЛОЖИТ (ревью PR #1306):
    /// секцию выключенного модуля и секцию с чужим именем схемы оно пропускает. Иначе выбранная
    /// здесь валюта сбрасывалась бы к рублю из-за документов, которые так и не пришли.
    /// </summary>
    [Fact]
    public async Task Документы_копии_которые_не_восстановятся_валюту_не_сбрасывают()
    {
        await SeedInvoiceAsync(await SeedCoreObjectAsync());
        var (_, manifest) = await ExportAsync(BackupScope.Full);
        var data = manifest.ModuleData!;
        using var scope = fixture.Services.CreateScope();

        Assert.True(Guard(scope, Registry()).Carries(data));
        Assert.False(Guard(scope, new ModuleRegistry([], [new ProbeModule()])).Carries(data));
        Assert.False(Guard(scope, Registry()).Carries([.. data.Select(s => s with { Schema = "probe_old" })]));
    }

    /// <summary>
    /// Сторож гонки (ревью PR #1306): пока смена валюты сверяет запрет, в учётные таблицы писать
    /// нельзя. Без замка счёт, зафиксированный между сверкой и фиксацией смены, получил бы новый знак.
    /// </summary>
    [Fact]
    public async Task Пока_смена_сверяет_запрет_в_учётную_таблицу_не_записать()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guard = Guard(scope, Registry());

        // Без транзакции замок снялся бы сразу — это отказ, а не тихое «заперто».
        await Assert.ThrowsAsync<InvalidOperationException>(() => guard.TryLockAsync());

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            Assert.True(await guard.TryLockAsync());

            // Замок вставки — тот же, что берёт INSERT; ждать его конца не будем.
            var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                $"SET lock_timeout = '300ms'; LOCK TABLE {ModuleSchemaName}.invoices IN ROW EXCLUSIVE MODE"));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, refused.SqlState);
            // Читать при этом можно: экран счетов на время смены не встаёт.
            await ExecuteAsync($"SET lock_timeout = '300ms'; SELECT count(*) FROM {ModuleSchemaName}.invoices");
        }

        // Транзакция кончилась — запись снова идёт.
        await ExecuteAsync($"SET lock_timeout = '300ms'; LOCK TABLE {ModuleSchemaName}.invoices IN ROW EXCLUSIVE MODE");
    }

    /// <summary>
    /// Таблицы заняты чужой записью дольше срока — отказ словами, а не зависший запрос. И транзакция
    /// после отказа жива: восстановление копии на этом месте продолжается.
    /// </summary>
    [Fact]
    public async Task Занятые_учётные_таблицы_смена_не_ждёт_вечно()
    {
        await using var writer = new NpgsqlConnection(Connection);
        await writer.OpenAsync();
        await using var writing = await writer.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand(
            $"LOCK TABLE {ModuleSchemaName}.invoices IN ROW EXCLUSIVE MODE", writer, writing))
            await hold.ExecuteNonQueryAsync();

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();

        Assert.IsType<CurrencyChangeOutcome.Busy>(await Guard(scope, Registry()).TryChangeAsync("USD"));

        Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT 1 AS \"Value\"").SingleAsync());
        await tx.CommitAsync();
        Assert.Null(await StoredCurrencyAsync());
    }

    /// <summary>
    /// Две смены сразу не пишут обе (ревью PR #1306): замки таблиц между собой совместимы, строки
    /// настройки может ещё не быть — вторая ждёт очереди, а не читает «было рубль» рядом с первой.
    /// </summary>
    [Fact]
    public async Task Вторая_одновременная_смена_ждёт_первую_а_не_пишет_поверх()
    {
        using var first = fixture.Services.CreateScope();
        var firstDb = first.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var firstTx = await firstDb.Database.BeginTransactionAsync();
        Assert.IsType<CurrencyChangeOutcome.Changed>(await Guard(first, Registry()).TryChangeAsync("USD"));

        using (var second = fixture.Services.CreateScope())
        {
            var secondDb = second.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var secondTx = await secondDb.Database.BeginTransactionAsync();
            Assert.IsType<CurrencyChangeOutcome.Busy>(await Guard(second, Registry()).TryChangeAsync("KZT"));
        }

        await firstTx.CommitAsync();
        Assert.Equal("USD", await StoredCurrencyAsync());
        Assert.Single(await CurrencyChangesAsync());
    }

    /// <summary>
    /// Валюта, которой эта версия не знает, — не «ключа нет» (ревью PR #1306): приравняв её к рублю,
    /// восстановление сбросило бы местный выбор и положило бы документы копии под рубль.
    /// </summary>
    [Fact]
    public async Task Неизвестная_валюта_в_копии_местную_не_сбрасывает_и_названа()
    {
        await SeedInvoiceAsync(await SeedCoreObjectAsync());
        await SetCurrencyAsync("XTS");
        var (archive, _) = await ExportAsync(BackupScope.Full);
        await ExecuteAsync($"DELETE FROM {ModuleSchemaName}.invoices");
        await SetCurrencyAsync("KZT");

        var report = await ImportAsync(archive);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        Assert.Equal("KZT", await StoredCurrencyAsync());
        Assert.Empty(await CurrencyChangesAsync());
        var warning = Assert.Single(report.Warnings, w => w.Contains("Валюта учёта", StringComparison.Ordinal));
        Assert.Contains("«XTS»", warning);
        Assert.Contains("проверьте их суммы", warning);
    }

    /// <summary>Совет проверить суммы документов копии звучит, только когда документы из неё придут.</summary>
    [Fact]
    public async Task Копия_без_документов_совета_проверить_их_суммы_не_даёт()
    {
        await SetCurrencyAsync("KZT");
        var (archive, _) = await ExportAsync(BackupScope.Full);
        await SetCurrencyAsync(null);
        await SeedInvoiceAsync(await SeedCoreObjectAsync());

        var report = await ImportAsync(archive);

        var warning = Assert.Single(report.Warnings, w => w.Contains("Валюта учёта", StringComparison.Ordinal));
        Assert.Contains("НЕ изменена", warning);
        Assert.DoesNotContain("проверьте", warning);
    }

    /// <summary>Негодная местная строка при совпадении действующих валют убирается и восстановлением.</summary>
    [Fact]
    public async Task Восстановление_убирает_негодную_местную_строку_не_меняя_валюты()
    {
        await SeedInvoiceAsync(await SeedCoreObjectAsync());
        await SetCurrencyAsync("RUB");
        var (archive, _) = await ExportAsync(BackupScope.Full);
        await SetCurrencyAsync("рубли");

        var report = await ImportAsync(archive);

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        Assert.Equal("RUB", await StoredCurrencyAsync());
        Assert.Empty(await CurrencyChangesAsync());
    }

    private static AccountingCurrencyLock Guard(IServiceScope scope, ModuleRegistry registry) => new(
        scope.ServiceProvider.GetRequiredService<AppDbContext>(), registry,
        scope.ServiceProvider.GetRequiredService<IActivityLog>());

    /// <summary>
    /// Негодное объявление — отказ СТАРТА (ревью PR #1306), а не экрана настроек: имя таблицы уходит
    /// в запрос текстом.
    /// </summary>
    [Theory]
    [InlineData("Invoices", "строчных латинских букв")]
    [InlineData("invoices\"; drop", "строчных латинских букв")]
    [InlineData("", "строчных латинских букв")]
    public void Негодное_имя_учётной_таблицы_отказ_при_старте(string table, string reason)
    {
        var problem = Assert.Single(ModuleAccountingDocuments.Problems(
            [new ProbeModule(), new DeclaringModule([new(table, "счетов")])]));

        Assert.Contains("модуль «declaring»", problem);
        Assert.Contains(reason, problem);
        Assert.Throws<InvalidOperationException>(() => ModuleAccountingDocuments.Ensure(
            [new DeclaringModule([new(table, "счетов")])]));
    }

    [Fact]
    public void Учётные_таблицы_без_схемы_и_названные_дважды_отказ_при_старте()
    {
        Assert.Contains("схема базы — нет", Assert.Single(ModuleAccountingDocuments.Problems(
            [new DeclaringModule([new("invoices", "счетов")], schema: false)])));
        Assert.Contains("названа дважды", Assert.Single(ModuleAccountingDocuments.Problems(
            [new DeclaringModule([new("invoices", "счетов"), new("invoices", "счетов")])])));
        Assert.Empty(ModuleAccountingDocuments.Problems([new DeclaringModule([])]));
    }

    /// <summary>Модуль с произвольным объявлением учётных таблиц — только для проверки объявлений.</summary>
    private sealed class DeclaringModule(ModuleAccountingDocument[] documents, bool schema = true) : IAppModule
    {
        public string Code => "declaring";

        public string Title => "Объявляющий";

        public IReadOnlyList<AppPermission> Permissions => [];

        public IReadOnlyList<string> RoutePrefixes => [];

        public ModuleSchema? Schema => schema ? new("declaring", typeof(ProbeInvoiceContext)) : null;

        public IReadOnlyList<ModuleAccountingDocument> AccountingDocuments => documents;

        public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }

        public void MapEndpoints(IEndpointRouteBuilder endpoints) { }

        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }

    private async Task<IReadOnlyList<CurrencyHolder>> HoldersAsync(ModuleRegistry registry)
    {
        using var scope = fixture.Services.CreateScope();
        return await Guard(scope, registry).HoldersAsync();
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
