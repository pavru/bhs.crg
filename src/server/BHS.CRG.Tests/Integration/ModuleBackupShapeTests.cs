using System.Data;
using System.Text.Json;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Backup;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Data;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Копия схемы модуля на таблицах НЕ той формы, что у первой таблицы счетов: иерархия, вычисляемая
/// колонка, свой счётчик, колонка, появившаяся после снятия копии (issue #1158, повторное ревью
/// PR #1108).
///
/// <para>Механизм копии заявлен общим — «состав модуля не перечисляется, а спрашивается», — а
/// проверялся на одной форме таблицы. Каждая проверка здесь — форма, на которой он отказывал или,
/// хуже, молча портил данные. Модуль поддельный по той же причине, что в
/// <see cref="ModuleDataBackupTests" />: у настоящего таких таблиц сегодня нет, а появятся они без
/// единой правки в коде копии — то есть без повода её перепроверить.</para>
///
/// <para>Зовётся сам <see cref="ModuleSchemaBackup" /> в транзакции ядра, без архива: всё, что здесь
/// проверяется, происходит между секцией манифеста и базой.</para>
/// </summary>
[Collection("Integration")]
public class ModuleBackupShapeTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private const string ModuleCode = "shapes";
    private const string SchemaName = "probe_shapes";
    private const string NumberSequence = "doc_no_seq";

    public async Task InitializeAsync()
    {
        _ = fixture.CreateClient();
        await fixture.ResetDatabaseAsync();
        await DropSchemaAsync();

        await using var provider = ProbeProvider();
        using var scope = provider.CreateScope();
        await ExecuteAsync(scope.ServiceProvider.GetRequiredService<ShapesContext>()
            .Database.GenerateCreateScript());
    }

    public async Task DisposeAsync() => await DropSchemaAsync();

    /// <summary>
    /// Копия старше схемы: колонки, которой в копии нет, восстановление не касается.
    ///
    /// <para>Обычный случай, а не крайний: копию сняли, систему обновили, миграция модуля добавила
    /// колонку. Первая редакция писала в неё явный <c>NULL</c> — обязательная колонка с умолчанием
    /// роняла восстановление целиком, необязательная молча затирала живое значение.</para>
    /// </summary>
    [Fact]
    public async Task Колонка_которой_в_копии_нет_получает_умолчание_а_живое_значение_остаётся()
    {
        var existing = await SeedNodeAsync("живой", state: "оплачен", note: "записано после копии");
        var fresh = await SeedNodeAsync("утраченный", state: "оплачен");
        var (data, _) = await ReadAsync();

        // Копия «до миграции»: те же строки без двух колонок. Одной из строк в системе уже нет.
        var old = Rows(data, "nodes").Select(r => Without(r, "state", "note")).ToArray();
        await ExecuteAsync($"DELETE FROM {SchemaName}.nodes WHERE id = '{fresh}'");

        var warnings = await RestoreAsync(Section(("nodes", old)));

        Assert.Equal("черновик", await ScalarAsync<string>($"SELECT state FROM {SchemaName}.nodes WHERE id = '{fresh}'"));
        Assert.Equal("оплачен", await ScalarAsync<string>($"SELECT state FROM {SchemaName}.nodes WHERE id = '{existing}'"));
        Assert.Equal("записано после копии",
            await ScalarAsync<string>($"SELECT note FROM {SchemaName}.nodes WHERE id = '{existing}'"));

        // И это названо: у восстановленных строк в этих колонках не то, что было, а умолчание.
        Assert.Contains(warnings, w =>
            w.Contains("в копии нет колонок", StringComparison.Ordinal)
            && w.Contains("state", StringComparison.Ordinal)
            && w.Contains("note", StringComparison.Ordinal));
    }

    /// <summary>
    /// Строки, в которых нет первичного ключа нынешней схемы, не вставляются с ключом по умолчанию —
    /// таблица пропускается, и это названо.
    ///
    /// <para>Так выглядит копия, снятая до того, как модуль сменил ключ. Слить такие строки не с чем —
    /// как и у таблицы без ключа вовсе; а вставка с выданным базой ключом удваивала бы их при каждом
    /// повторном восстановлении.</para>
    /// </summary>
    [Fact]
    public async Task Строки_без_ключа_нынешней_схемы_пропускаются_с_оговоркой()
    {
        await SeedNodeAsync("строка");
        var (data, _) = await ReadAsync();
        var keyless = Rows(data, "nodes").Select(r => Without(r, "id")).ToArray();

        var warning = Assert.Single(await RestoreAsync(Section(("nodes", keyless))));

        Assert.Contains("нет колонок первичного ключа", warning);
        Assert.Contains("id", warning);
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM {SchemaName}.nodes"));
    }

    /// <summary>
    /// Иерархия длиннее порции восстанавливается: потомки в копии стоят раньше родителя и уходят в
    /// базу другой порцией.
    ///
    /// <para>Внешний ключ таблицы на себя база проверяет в конце КОМАНДЫ. Пока каждая порция была
    /// своей командой вставки, потомок без родителя откатывал восстановление целиком — и только на
    /// таблице больше порции, то есть не на тестовых данных.</para>
    /// </summary>
    [Fact]
    public async Task Иерархия_длиннее_порции_восстанавливается()
    {
        var parent = Guid.NewGuid();
        var rows = Enumerable.Range(0, 520)
            .Select(i => Json($$"""{"id":"{{Guid.NewGuid()}}","parent_id":"{{parent}}","name":"потомок {{i}}"}"""))
            .Append(Json($$"""{"id":"{{parent}}","parent_id":null,"name":"родитель"}"""))
            .ToArray();

        await RestoreAsync(Section(("nodes", rows)));

        Assert.Equal(521L, await ScalarAsync<long>($"SELECT count(*) FROM {SchemaName}.nodes"));
        Assert.Equal(520L, await ScalarAsync<long>(
            $"SELECT count(*) FROM {SchemaName}.nodes WHERE parent_id = '{parent}'"));
    }

    /// <summary>
    /// Колонки, которые пишет сама база, восстановлению не мешают — ни при вставке, ни при повторном
    /// восстановлении поверх.
    ///
    /// <para>Вычисляемая колонка в копии есть (<c>to_jsonb</c> отдаёт её как обычную), а писать её
    /// нельзя вовсе. Счётчик <c>GENERATED ALWAYS</c> можно вставить, но не обновить. Обе формы давали
    /// отказ на первой же таблице, где встречались.</para>
    /// </summary>
    [Fact]
    public async Task Вычисляемая_колонка_и_строгий_счётчик_восстановлению_не_мешают()
    {
        var id = await SeedNodeAsync("кабель");
        var stamp = await ScalarAsync<int>($"SELECT stamp FROM {SchemaName}.nodes WHERE id = '{id}'");
        var (data, _) = await ReadAsync();
        Assert.Contains("name_upper", Rows(data, "nodes")[0].EnumerateObject().Select(p => p.Name));

        // На чистую таблицу — вставка.
        await ExecuteAsync($"TRUNCATE {SchemaName}.nodes RESTART IDENTITY");
        var warnings = await RestoreAsync(data);

        Assert.Equal("КАБЕЛЬ", await ScalarAsync<string>($"SELECT name_upper FROM {SchemaName}.nodes WHERE id = '{id}'"));
        Assert.Equal(stamp, await ScalarAsync<int>($"SELECT stamp FROM {SchemaName}.nodes WHERE id = '{id}'"));

        // Поверх себя — обновление.
        warnings.AddRange(await RestoreAsync(data));

        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM {SchemaName}.nodes"));
        // Вычисляемая колонка — не «потерянная» и не «отсутствующая»: оговорок о ней нет.
        Assert.Empty(warnings);
    }

    /// <summary>
    /// Счётчик, из которого модуль берёт номера сам, уезжает в копию и возвращается из неё.
    ///
    /// <para>За колонкой он не закреплён, и вывести его из строк нельзя: после восстановления на
    /// чистую установку нумерация пошла бы с начала — по уже выданным номерам.</para>
    /// </summary>
    [Fact]
    public async Task Самостоятельный_счётчик_возвращается_из_копии()
    {
        await ExecuteAsync($"SELECT setval('{SchemaName}.{NumberSequence}', 41)");
        var (data, _) = await ReadAsync();

        Assert.Contains(data.Single().Sequences!, s => s is { Name: NumberSequence, LastValue: 41 });

        // Чистая установка: счётчик в начале, и строк у модуля нет ни в базе, ни в копии.
        await ExecuteAsync($"ALTER SEQUENCE {SchemaName}.{NumberSequence} RESTART");
        await RestoreAsync(data);

        Assert.Equal(42L, await ScalarAsync<long>($"SELECT nextval('{SchemaName}.{NumberSequence}')"));
    }

    /// <summary>
    /// Восстановление не двигает счётчики назад — ни тот, что за колонкой, ни самостоятельный.
    ///
    /// <para>В живой системе счётчик бывает впереди строк: их удалили, а идентификаторы остались в
    /// журнале и в ссылках из ядра. Сдвиг назад раздал бы те же идентификаторы новым строкам.</para>
    /// </summary>
    [Fact]
    public async Task Счётчики_назад_не_двигаются()
    {
        await SeedNodeAsync("первый");
        await ExecuteAsync($"SELECT setval('{SchemaName}.{NumberSequence}', 7)");
        var (data, _) = await ReadAsync();

        // После копии система жила дальше: оба счётчика ушли вперёд.
        var owned = await ScalarAsync<string>($"SELECT pg_get_serial_sequence('{SchemaName}.nodes', 'seq')");
        await ExecuteAsync($"SELECT setval('{owned}', 100)");
        await ExecuteAsync($"SELECT setval('{SchemaName}.{NumberSequence}', 300)");

        await RestoreAsync(data);

        Assert.Equal(101L, await ScalarAsync<long>($"SELECT nextval('{owned}')"));
        Assert.Equal(301L, await ScalarAsync<long>($"SELECT nextval('{SchemaName}.{NumberSequence}')"));
    }

    /// <summary>
    /// Данные выключенного модуля в копию не входят — и паспорт копии это говорит.
    ///
    /// <para>Иначе копия выглядит полной: модуль временно выключили, ночные копии исправны, а после
    /// аварии восстановление возвращает систему без счетов.</para>
    /// </summary>
    [Fact]
    public async Task Данные_выключенного_модуля_названы_в_паспорте_копии()
    {
        var off = new ModuleRegistry([], [new ShapesModule()]);

        // Пустая схема выключенного модуля — не повод для тревоги.
        var (_, quiet) = await ReadAsync(off);
        Assert.Empty(quiet);

        await SeedNodeAsync("счёт выключенного модуля");
        var (data, warnings) = await ReadAsync(off);

        Assert.Empty(data);
        var warning = Assert.Single(warnings);
        Assert.Contains(ModuleCode, warning);
        Assert.Contains("выключен", warning);
        Assert.Contains("nodes", warning);
    }

    /// <summary>
    /// Копия без данных включённого здесь модуля названа при восстановлении — когда у модуля есть
    /// данные.
    /// </summary>
    [Fact]
    public async Task Копия_без_данных_включённого_модуля_названа_при_восстановлении()
    {
        // У модуля ничего нет — и сообщать не о чем.
        Assert.Empty(await RestoreAsync([]));

        await SeedNodeAsync("живой счёт");
        var warning = Assert.Single(await RestoreAsync([]));

        Assert.Contains("В копии нет данных модуля", warning);
        Assert.Contains(ModuleCode, warning);
    }

    /// <summary>
    /// Контекст модуля, собранный на источнике данных, после копии работает как работал.
    ///
    /// <para>Копия берёт у контекста только модель. Первая редакция подключала сам контекст к
    /// соединению ядра и возвращала ему адрес строкой — а строка у контекста на источнике данных
    /// отдаётся без пароля и вытесняет сам источник: следующий запрос модуля в той же области
    /// отказывал на входе в базу.</para>
    /// </summary>
    [Fact]
    public async Task Контекст_модуля_на_источнике_данных_после_копии_работает()
    {
        await SeedNodeAsync("строка");

        await using var source = NpgsqlDataSource.Create(Connection);
        await using var provider = new ServiceCollection()
            .AddDbContext<ShapesContext>(o => o.UseNpgsql(source))
            .BuildServiceProvider();
        using var probeScope = provider.CreateScope();
        var moduleDb = probeScope.ServiceProvider.GetRequiredService<ShapesContext>();
        Assert.Equal(1, await moduleDb.Nodes.CountAsync());

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead))
        {
            await new ModuleSchemaBackup(Registry(), probeScope.ServiceProvider)
                .ReadAsync(tx.GetDbTransaction(), [], default);
            await tx.CommitAsync();
        }

        Assert.Equal(1, await moduleDb.Nodes.CountAsync());
        Assert.Null(moduleDb.Database.CurrentTransaction);
    }

    // ── Хозяйство ─────────────────────────────────────────────────────────────

    private string Connection => fixture.Services.GetRequiredService<IConfiguration>()
        .GetConnectionString("Postgres")!;

    private static ModuleRegistry Registry() => new([new ShapesModule()], []);

    private ServiceProvider ProbeProvider() => new ServiceCollection()
        .AddDbContext<ShapesContext>(o => o.UseNpgsql(Connection))
        .BuildServiceProvider();

    private async Task<(BackupModuleSchema[] Data, List<string> Warnings)> ReadAsync(
        ModuleRegistry? registry = null)
    {
        using var scope = fixture.Services.CreateScope();
        await using var probe = ProbeProvider();
        using var probeScope = probe.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);

        var warnings = new List<string>();
        var data = await new ModuleSchemaBackup(registry ?? Registry(), probeScope.ServiceProvider)
            .ReadAsync(tx.GetDbTransaction(), warnings, default);
        await tx.CommitAsync();

        return (data, warnings);
    }

    private async Task<List<string>> RestoreAsync(BackupModuleSchema[] data)
    {
        using var scope = fixture.Services.CreateScope();
        await using var probe = ProbeProvider();
        using var probeScope = probe.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();

        var warnings = new List<string>();
        await new ModuleSchemaBackup(Registry(), probeScope.ServiceProvider)
            .RestoreAsync(data, tx.GetDbTransaction(), warnings, default);
        await tx.CommitAsync();

        return warnings;
    }

    private static BackupModuleSchema[] Section(params (string Table, JsonElement[] Rows)[] tables) =>
        [new(ModuleCode, SchemaName, [.. tables.Select(t => new BackupModuleTable(t.Table, t.Rows))])];

    private static JsonElement[] Rows(BackupModuleSchema[] data, string table) =>
        data.Single().Tables.Single(t => t.Table == table).Rows;

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>Та же строка без названных колонок — так она выглядела до миграции, их добавившей.</summary>
    private static JsonElement Without(JsonElement row, params string[] columns) => Json(
        JsonSerializer.Serialize(row.EnumerateObject()
            .Where(p => !columns.Contains(p.Name))
            .ToDictionary(p => p.Name, p => p.Value)));

    private async Task<Guid> SeedNodeAsync(string name, string? state = null, string? note = null)
    {
        await using var provider = ProbeProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShapesContext>();
        var node = new ShapeNode { Id = Guid.NewGuid(), Name = name, Note = note };
        if (state is not null) node.State = state;

        db.Nodes.Add(node);
        await db.SaveChangesAsync();
        return node.Id;
    }

    private Task DropSchemaAsync() => ExecuteAsync($"DROP SCHEMA IF EXISTS {SchemaName} CASCADE");

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(Connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(Connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private sealed class ShapesModule : IAppModule
    {
        public string Code => ModuleCode;

        public string Title => "Поддельный модуль: формы таблиц";

        public IReadOnlyList<AppPermission> Permissions => [];

        public IReadOnlyList<string> RoutePrefixes => [];

        public ModuleSchema? Schema => new(SchemaName, typeof(ShapesContext));

        public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }

        public void MapEndpoints(IEndpointRouteBuilder endpoints) { }

        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Одна таблица со всем, чего не было у поддельного счёта: ссылка на себя, колонка с умолчанием,
    /// вычисляемая колонка, счётчик «всегда», счётчик за колонкой и самостоятельная последовательность
    /// схемы. Объявлено средствами EF — так же, как объявит настоящий модуль.
    /// </summary>
    private sealed class ShapesContext(DbContextOptions<ShapesContext> options) : ModuleDbContext(options)
    {
        protected override string Schema => SchemaName;

        public DbSet<ShapeNode> Nodes => Set<ShapeNode>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.HasSequence<long>(NumberSequence, SchemaName);
            builder.Entity<ShapeNode>(e =>
            {
                e.ToTable("nodes");
                e.Property(x => x.Id).HasColumnName("id");
                e.Property(x => x.ParentId).HasColumnName("parent_id");
                e.HasOne<ShapeNode>().WithMany().HasForeignKey(x => x.ParentId);
                e.Property(x => x.Name).HasColumnName("name");
                e.Property(x => x.State).HasColumnName("state").HasDefaultValue("черновик");
                e.Property(x => x.Note).HasColumnName("note");
                e.Property(x => x.NameUpper).HasColumnName("name_upper")
                    .HasComputedColumnSql("upper(name)", stored: true);
                e.Property(x => x.Stamp).HasColumnName("stamp").UseIdentityAlwaysColumn();
                e.Property(x => x.Seq).HasColumnName("seq").UseIdentityByDefaultColumn();
            });
        }
    }

    private sealed class ShapeNode
    {
        public Guid Id { get; set; }

        public Guid? ParentId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string State { get; set; } = null!;

        public string? Note { get; set; }

        public string? NameUpper { get; set; }

        public int Stamp { get; set; }

        public int Seq { get; set; }
    }
}
