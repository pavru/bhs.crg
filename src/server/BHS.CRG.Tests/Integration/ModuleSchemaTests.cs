using System.IO.Compression;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Второй контекст базы живьём: схема модуля создаётся при старте, её история лежит отдельно от
/// истории ядра, дописываемые наборы модуля защищены от правки, а перепись схемы видит потерю строк
/// (задача A2a этапа 2, issue #1072, ТЗ CORE-4, CORE-29, CORE-31).
///
/// <para>Хост — <see cref="ModuleSchemaHost" />: модуль <c>costs</c> включён, база своя, и схема
/// модуля перед запуском СНОСИТСЯ, а в ней остаётся строка «прошлой версии». Без сноса проверка была
/// бы нарисованной: схема, созданная прошлым прогоном, остаётся навсегда, и тест «схема появилась при
/// старте» проходил бы даже с вырезанной миграцией модуля (проверено снятием вызова). Обратную половину
/// — что схема ВЫКЛЮЧЕННОГО модуля не создаётся — проверяет <see cref="DisabledModuleSchemaTests" />.
/// </para>
///
/// <para>⚠️ Таблиц у схемы <c>costs</c> пока нет — это решение A2a, а не заготовка (первая приезжает
/// с C1, issue #1076). Поэтому всё, что нельзя проверить на пустой схеме — правка дописываемого
/// набора, таблица в своей схеме, перепись по строкам, — проверяется на ПОДДЕЛЬНОМ контексте модуля:
/// он наследует тот же <see cref="ModuleDbContext" /> и живёт в своей схеме на той же базе. Механизм
/// при этом настоящий, поддельны только таблицы.</para>
/// </summary>
public class ModuleSchemaTests(ModuleSchemaHost host) : IClassFixture<ModuleSchemaHost>, IAsyncLifetime
{
    /// <summary>Схема поддельного контекста — своя, чтобы не мешать ни ядру, ни модулю.</summary>
    private const string ProbeSchema = "module_probe";

    private string Connection => host.Services.GetRequiredService<IConfiguration>()
        .GetConnectionString("Postgres")!;

    public async Task InitializeAsync()
    {
        _ = host.CreateClient();
        await DropProbeSchemaAsync();
    }

    public async Task DisposeAsync() => await DropProbeSchemaAsync();

    /// <summary>
    /// Схема модуля появляется при старте, и все её миграции применены.
    ///
    /// Это признак готовности A2a словами теста: модуль включается на чистой базе (её создаёт первый
    /// старт хоста) и на рабочей (второй старт ничего не применяет и не падает).
    /// </summary>
    [Fact]
    public async Task Схема_модуля_создаётся_при_старте()
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());

        await using var conn = new NpgsqlConnection(Connection);
        await conn.OpenAsync();

        Assert.True(await ScalarAsync<bool>(conn,
            "SELECT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = 'costs')"),
            "Схемы «costs» нет: миграция модуля при старте не выполнилась.");
    }

    /// <summary>
    /// Схема модуля названа в резервной копии, снятой ЖИВЫМ хостом (задача A2b, issue #1073).
    ///
    /// <para>На месте храповика, который стоял здесь до A2b. Тот требовал обратного — чтобы таблиц у
    /// контекста модуля не было вовсе, — потому что копия их не видела: она сущностная, построена на
    /// контексте ядра, и сторож против дрейфа (<see cref="BackupManifestCoverageTests" />) читает
    /// модель того же контекста. Первая таблица счетов (C1, issue #1076) оказалась бы вне ЛЮБОЙ копии
    /// при зелёном CI. Теперь копия схемы модулей видит, и храповик снят не молча, а заменён на
    /// проверку того, что он охранял.</para>
    ///
    /// <para>Состав сверяется с МОДЕЛЬЮ контекста, а не с числом: сегодня таблиц ноль, и равенство
    /// «ноль = ноль» ничего не стоит. С первой таблицей то же равенство начнёт требовать, чтобы она в
    /// копию попала. Круг «снял — восстановил» проверяют <see cref="ModuleDataBackupTests" /> — на
    /// поддельном модуле — и <see cref="InvoiceBackupRoundTripTests" /> — на настоящем счёте со
    /// строками и разноской.</para>
    /// </summary>
    [Fact]
    public async Task Схема_модуля_названа_в_резервной_копии()
    {
        using var scope = host.Services.CreateScope();

        var (zip, _) = await scope.ServiceProvider.GetRequiredService<BackupService>()
            .ExportAsync(BackupScope.Full);
        await using var handle = zip;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        await using var entry = archive.GetEntry("manifest.json")!.Open();
        var manifest = (await JsonSerializer.DeserializeAsync<BackupManifest>(entry))!;

        Assert.True(manifest.ModuleData is { Length: 1 },
            "Копия не назвала схему включённого модуля: его данные не попали бы ни в одну копию.");
        var section = manifest.ModuleData![0];
        Assert.Equal("costs", section.Module);
        Assert.Equal(CostsDbContext.SchemaName, section.Schema);

        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        Assert.Equal(
            db.Model.GetRelationalModel().Tables.Count(
                t => t.Schema == CostsDbContext.SchemaName),
            section.Tables.Length);
    }

    /// <summary>
    /// Миграция модуля не тронула данные, которые уже лежали в его схеме, — и перепись схемы это
    /// сверила при старте.
    ///
    /// Это вторая дорога, та, на которой есть что терять: база рабочая, схема модуля с данными на
    /// месте, миграция ещё не применена — то есть обновление. Проверять только чистую установку
    /// значило бы проверить ту дорогу, где терять нечего (урок #1046).
    /// </summary>
    [Fact]
    public async Task Данные_прошлой_версии_переживают_миграцию_модуля()
    {
        await using var conn = new NpgsqlConnection(Connection);
        await conn.OpenAsync();

        var rows = await ScalarAsync<long>(conn, $"SELECT count(*) FROM {ModuleSchemaHost.LeftoverTable}");

        Assert.True(rows == 1,
            $"Строка прошлой версии в {ModuleSchemaHost.LeftoverTable} не дожила до конца миграции " +
            "модуля. Перепись схемы обязана была остановить старт, а тест — не дойти до этой строки.");
    }

    /// <summary>
    /// История миграций модуля лежит В ЕГО СХЕМЕ, и в истории ядра его миграций нет.
    ///
    /// Самый дорогой из тихих отказов этой задачи. Без явного имени EF кладёт историю модуля в схему
    /// по умолчанию своей служебной модели — то есть в одну таблицу с историей ядра. Выглядит это
    /// исправной работой: миграции применяются, приложение поднимается, тесты зелёные. А следующее
    /// обновление ЯДРА встречает в своей истории строки, которых нет в его сборке, и останавливается
    /// у заказчика, называя чужую миграцию.
    /// </summary>
    [Fact]
    public async Task История_миграций_модуля_лежит_отдельно_от_истории_ядра()
    {
        await using var conn = new NpgsqlConnection(Connection);
        await conn.OpenAsync();

        Assert.True(await ScalarAsync<bool>(conn,
            $"SELECT to_regclass('costs.\"{CostsDbContext.HistoryTableName}\"') IS NOT NULL"),
            "Истории миграций модуля в его схеме нет.");

        var mine = await ScalarAsync<long>(conn,
            $"SELECT count(*) FROM costs.\"{CostsDbContext.HistoryTableName}\"");
        Assert.True(mine > 0, "История модуля пуста: его миграции записались не туда.");

        var inCore = await ScalarAsync<long>(conn,
            $"""
            SELECT count(*) FROM public."{CostsDbContext.HistoryTableName}" h
             WHERE h."MigrationId" IN (SELECT m."MigrationId" FROM costs."{CostsDbContext.HistoryTableName}" m)
            """);

        Assert.True(inCore == 0,
            "Миграции модуля записались и в историю ЯДРА. Обновление ядра остановится на них у " +
            "заказчика, назвав миграцию, которой в его сборке нет.");
    }

    /// <summary>
    /// Таблица модуля ложится в его схему сама, без указания схемы у каждой таблицы.
    ///
    /// Проверяется на поддельном контексте: настоящий пока без таблиц. Ломается снятием
    /// <c>HasDefaultSchema</c> в базовом контексте — тогда таблица появляется в схеме ядра, рядом с
    /// таблицами ядра, и никакой ошибки при этом не происходит.
    /// </summary>
    [Fact]
    public async Task Таблицы_модуля_ложатся_в_его_схему()
    {
        await using var db = ProbeContext();
        await CreateProbeTablesAsync(db);

        await using var conn = new NpgsqlConnection(Connection);
        await conn.OpenAsync();

        Assert.True(await ScalarAsync<bool>(conn, $"SELECT to_regclass('{ProbeSchema}.notes') IS NOT NULL"),
            "Таблицы поддельного модуля нет в его схеме.");
        Assert.True(await ScalarAsync<bool>(conn, "SELECT to_regclass('public.notes') IS NULL"),
            "Таблица модуля попала в схему ЯДРА: схема по умолчанию у контекста модуля не действует.");
    }

    /// <summary>
    /// Дописываемый набор модуля правке и удалению не подлежит — так же, как журнал ядра.
    ///
    /// Три утверждения в одном тесте нарочно: отказ на правке ценен только вместе с тем, что ДОПИСЬ
    /// проходит, а строка в базе остаётся прежней. Сторож, запрещающий всё, был бы зелёным и
    /// бесполезным.
    /// </summary>
    [Fact]
    public async Task Дописываемый_набор_модуля_правке_не_подлежит()
    {
        await using var db = ProbeContext();
        await CreateProbeTablesAsync(db);

        // Дописать — можно, и это половина утверждения.
        db.Records.Add(new ProbeRecord { Id = Guid.NewGuid(), Text = "счёт отмечен оплаченным" });
        await db.SaveChangesAsync();

        var stored = await db.Records.SingleAsync();
        stored.Text = "переписанная история";
        var edit = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("дописываемого набора", edit.Message);
        Assert.Contains(ProbeSchema, edit.Message);

        db.Entry(stored).State = EntityState.Deleted;
        var delete = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("дописываемого набора", delete.Message);

        db.Entry(stored).State = EntityState.Detached;
        await using var again = ProbeContext();
        var row = await again.Records.SingleAsync();
        Assert.Equal("счёт отмечен оплаченным", row.Text);
    }

    /// <summary>Обычный набор модуля правится как обычно: отказ выше — про дописываемые, а не про всё.</summary>
    [Fact]
    public async Task Обычный_набор_модуля_правится()
    {
        await using var db = ProbeContext();
        await CreateProbeTablesAsync(db);

        var note = new ProbeNote { Id = Guid.NewGuid(), Text = "черновик" };
        db.Notes.Add(note);
        await db.SaveChangesAsync();

        note.Text = "правленый черновик";
        await db.SaveChangesAsync();

        Assert.Equal("правленый черновик", (await db.Notes.SingleAsync()).Text);
    }

    /// <summary>
    /// Перепись схемы модуля считает строки по всем его таблицам и видит потерю — отказом, называющим
    /// схему и оба числа.
    ///
    /// История миграций из счёта исключена: её строки добавляет сама миграция, и сверка сравнивала бы
    /// число с заведомо другим.
    /// </summary>
    [Fact]
    public async Task Перепись_схемы_модуля_видит_потерю_строк()
    {
        await using var db = ProbeContext();
        await CreateProbeTablesAsync(db);
        db.Notes.Add(new ProbeNote { Id = Guid.NewGuid(), Text = "первая" });
        db.Notes.Add(new ProbeNote { Id = Guid.NewGuid(), Text = "вторая" });
        await db.SaveChangesAsync();

        var before = await MigrationCensus.ReadSchemaAsync(db, ProbeSchema);
        Assert.NotNull(before);
        Assert.Equal(2, before!.Counts[$"строк в схеме {ProbeSchema}"]);

        await db.Notes.Where(n => n.Text == "вторая").ExecuteDeleteAsync();
        var after = await MigrationCensus.ReadSchemaAsync(db, ProbeSchema);

        var ex = Assert.Throws<InvalidOperationException>(
            () => MigrationCensus.EnsureNothingLost(before, after, ProbeSchema));
        Assert.Contains(ProbeSchema, ex.Message);
        Assert.Contains("было 2, стало 1", ex.Message);
    }

    /// <summary>
    /// Строк стало БОЛЬШЕ — старт продолжается. Сравнение односторонее нарочно: миграция вправе
    /// разбить таблицу на две или наполнить справочник, и требуй здесь равенства — провести такую
    /// миграцию было бы нечем, а перепись «разрешить» ничего не умеет.
    /// </summary>
    [Fact]
    public async Task Перепись_схемы_модуля_пропускает_прибавку()
    {
        await using var db = ProbeContext();
        await CreateProbeTablesAsync(db);
        db.Notes.Add(new ProbeNote { Id = Guid.NewGuid(), Text = "первая" });
        await db.SaveChangesAsync();

        var before = await MigrationCensus.ReadSchemaAsync(db, ProbeSchema);
        db.Notes.Add(new ProbeNote { Id = Guid.NewGuid(), Text = "вторая" });
        await db.SaveChangesAsync();
        var after = await MigrationCensus.ReadSchemaAsync(db, ProbeSchema);

        MigrationCensus.EnsureNothingLost(before, after, ProbeSchema);
    }

    /// <summary>
    /// Схемы ещё нет — перепись отвечает «нечего сверять», а не нулём. Разница та же, что у ядра:
    /// «ноль строк» и «схемы нет» — про разное, и первое включение модуля сверять не с чем.
    /// </summary>
    [Fact]
    public async Task Переписи_несуществующей_схемы_не_бывает()
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();

        Assert.Null(await MigrationCensus.ReadSchemaAsync(db, "нет_такой_схемы"));
    }

    /// <summary>
    /// Создать таблицы поддельного контекста.
    ///
    /// ⚠️ Не <c>EnsureCreated</c>: он создаёт схему только в ПУСТОЙ базе, а здесь база не пуста —
    /// в ней живёт ядро. Молчаливо ничего не сделав, он оставил бы тесты падать на «relation does not
    /// exist», то есть на отсутствии таблиц, а не на том, что они проверяют. Сценарий строит EF из
    /// модели — тем же путём, каким это делает миграция, — и первым его оператором идёт создание СХЕМЫ.
    /// </summary>
    private static async Task CreateProbeTablesAsync(ProbeModuleContext db)
    {
        var script = db.Database.GenerateCreateScript();

        Assert.Contains($"CREATE SCHEMA {ProbeSchema}", script);

        await using var conn = new NpgsqlConnection(db.Database.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(script, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private ProbeModuleContext ProbeContext()
    {
        var options = new DbContextOptionsBuilder<ProbeModuleContext>();
        options.UseNpgsql(Connection);
        return new ProbeModuleContext(options.Options);
    }

    private async Task DropProbeSchemaAsync()
    {
        await using var conn = new NpgsqlConnection(Connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {ProbeSchema} CASCADE", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Поддельный контекст модуля: тот же базовый класс, своя схема, два набора — дописываемый и
    /// обычный. Своих миграций у него нет: таблицы создаются <c>EnsureCreated</c>, потому что здесь
    /// проверяется поведение контекста, а не порядок применения миграций (его проверяет настоящий
    /// контекст модуля выше).
    /// </summary>
    private sealed class ProbeModuleContext(DbContextOptions<ProbeModuleContext> options)
        : ModuleDbContext(options)
    {
        protected override string Schema => ProbeSchema;

        public DbSet<ProbeRecord> Records => Set<ProbeRecord>();

        public DbSet<ProbeNote> Notes => Set<ProbeNote>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<ProbeRecord>().ToTable("records");
            builder.Entity<ProbeNote>().ToTable("notes");
        }
    }

    /// <summary>Дописываемая строка: история изменения записи модуля, как её заведёт E2.</summary>
    private sealed class ProbeRecord : IAppendOnly
    {
        public Guid Id { get; set; }

        public string Text { get; set; } = string.Empty;
    }

    /// <summary>Обычная строка модуля — её правят и удаляют.</summary>
    private sealed class ProbeNote
    {
        public Guid Id { get; set; }

        public string Text { get; set; } = string.Empty;
    }
}

/// <summary>
/// Обратная половина: схема ВЫКЛЮЧЕННОГО модуля не создаётся вовсе (задача A2a этапа 2, issue #1072,
/// ТЗ AUTH-19).
///
/// <para>Идёт на общей тестовой базе, где состав модулей умолчательный — то есть <c>costs</c>
/// выключен. Ценность в том, что этот же тест ловит обратную правку: перебор ВСЕХ модулей вместо
/// включённых создал бы схему выключенного модуля здесь же, и заметить это иначе было бы нечем —
/// приложение поднимается, схема пустая, никто не жалуется. А выключенный модуль, чью схему кто-то
/// мигрирует, — это половина обещания «выключенный данных не теряет»: вторая половина в том, что его
/// схему не удаляют, и она держится тем же местом в коде.</para>
/// </summary>
[Collection("Integration")]
public class DisabledModuleSchemaTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Схема_выключенного_модуля_не_создаётся()
    {
        _ = fixture.CreateClient();

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) await conn.OpenAsync();
        try
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = 'costs')", conn);

            Assert.False(await cmd.ExecuteScalarAsync() is true,
                "Схема выключенного модуля создана. Значит мигрируются все модули, а не включённые: " +
                "экземпляр заказчика получил бы схемы того, за что он не платил, — и заметить это " +
                "было бы нечем.");

            Assert.DoesNotContain("costs",
                scope.ServiceProvider.GetRequiredService<BHS.CRG.Modules.ModuleRegistry>()
                    .Enabled.Select(m => m.Code));
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }
}
