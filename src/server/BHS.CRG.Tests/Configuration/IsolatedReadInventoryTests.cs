using System.Reflection;
using System.Text.RegularExpressions;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Tables;
using BHS.CRG.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись чтения изолированных записей (ТЗ WORK-39, WORK-40, CORE-3; work-07): запись с пометкой
/// <see cref="IIsolatedRecord" /> читается только в своём файле чтения — методом, который принимает
/// параметр доступа и вычисляет охват. Приём <see cref="PeriodClosureInventoryTests" />.
///
/// <para><b>Три обхода, которые компилируются и работают.</b> <c>DbSet&lt;T&gt;</c> в контексте — дверь, в
/// которую войдёт любой следующий запрос. <c>Set&lt;T&gt;()</c> в чужом файле — чтение без охвата, все
/// отчёты всем. Сырой SQL по таблице записи — то же, и ещё мимо модели. Запрет в контексте обходился бы
/// любым из них; перепись — нет.</para>
///
/// <para><b>Записи и их таблицы — из модели EF</b> каждого контекста модуля, а не из текста: имя таблицы
/// задаёт настройка (<c>ToTable</c>), и угадывать его по имени класса значило бы промахнуться на первом
/// же переименовании.</para>
///
/// <para>⚠️ <b>Чего не видит.</b> Имя таблицы, собранное склейкой (<c>$"{schema}.{name}"</c>), и
/// <c>Set(typeof(T))</c> по переменной — то же ограничение, что у прочих переписей
/// (<see cref="InventoryScopeTests" />). Миграции не читаются (общий обход <c>SourceTree.Files</c>): перенос
/// данных в миграции — не чтение для человека, и охвата там нет по замыслу.</para>
/// </summary>
public class IsolatedReadInventoryTests
{
    private static readonly string[] Projects =
        SolutionModules.WithCore("BHS.CRG.Api", "BHS.CRG.Application", "BHS.CRG.Infrastructure");

    /// <summary>
    /// Файл чтения → причина. Здесь и только здесь изолированная запись читается напрямую: метод с
    /// параметром доступа, вычисляющий охват. Пусто, пока изолированных записей нет — первую (отчёт о
    /// работах) заводит <c>work-13</c>.
    /// </summary>
    private static readonly Dictionary<string, string> ReadFiles = new();

    /// <summary>Изолированная запись: имя типа и её таблица.</summary>
    public sealed record Isolated(string Type, string? Schema, string Table);

    /// <summary>Изолированные записи модели — по пометке на типе сущности.</summary>
    internal static IReadOnlyList<Isolated> IsolatedOf(IModel model) =>
    [
        .. model.GetEntityTypes()
            .Where(e => typeof(IIsolatedRecord).IsAssignableFrom(e.ClrType) && e.GetTableName() is not null)
            .Select(e => new Isolated(e.ClrType.Name, e.GetSchema(), e.GetTableName()!)),
    ];

    /// <summary>
    /// Нарушения в одном файле. <paramref name="readFile" /> — файл из перечня чтения: там разрешены
    /// <c>Set&lt;T&gt;()</c> и SQL по таблице, но не <c>DbSet&lt;T&gt;</c> — набора у записи нет нигде.
    /// </summary>
    internal static IEnumerable<string> Violations(
        string rel, string text, IReadOnlyList<Isolated> isolated, bool readFile)
    {
        var code = Code(text);
        foreach (var record in isolated)
        {
            var type = Regex.Escape(record.Type);
            foreach (Match m in Regex.Matches(code, $@"\bDbSet\s*<\s*(?:[\w.]+\.)?{type}\s*>"))
                yield return $"{rel}:{SourceTree.LineOf(code, m.Index)}  DbSet<{record.Type}> — у изолированной записи набора в контексте нет";

            if (readFile) continue;

            foreach (Match m in Regex.Matches(code, $@"\bSet\s*<\s*(?:[\w.]+\.)?{type}\s*>\s*\("))
                yield return $"{rel}:{SourceTree.LineOf(code, m.Index)}  Set<{record.Type}>() вне файла чтения";

            foreach (Match m in SqlTouch(record).Matches(code))
                yield return $"{rel}:{SourceTree.LineOf(code, m.Index)}  сырой SQL по таблице «{Name(record)}» вне файла чтения";
        }
    }

    /// <summary>Модуль с таблицей, отбирающей по правам, и без единой изолированной записи.</summary>
    internal static IEnumerable<string> Unisolated(IAppModule module, IReadOnlyList<Isolated> isolated)
    {
        var perUser = module.Tables.Where(t => t.Isolation == ModuleTableIsolation.PerUser).ToList();
        if (perUser.Count == 0 || isolated.Count > 0) yield break;

        yield return $"модуль «{module.Code}»: отбор по правам объявлен " +
                     $"({string.Join(", ", perUser.Select(t => $"{module.Code}.{t.Code}"))}), изолированных записей нет";
    }

    [Fact]
    public void Изолированная_запись_читается_только_в_своём_файле()
    {
        var isolated = Models().SelectMany(m => IsolatedOf(m.Model)).ToList();

        var found = Sources()
            .SelectMany(file =>
            {
                var rel = SourceTree.Relative(file);
                return Violations(rel, File.ReadAllText(file), isolated, ReadFiles.ContainsKey(rel));
            })
            .ToList();

        Assert.True(found.Count == 0,
            "Изолированную запись читают мимо её файла чтения:\n" + string.Join("\n", found) + "\n\n" +
            "Такая запись читается только методом с параметром доступа (ModuleReader), который вычисляет " +
            "охват (ModuleReach): иначе монтажник получит чужие отчёты, а менеджер — чужие стройки. Чтение " +
            "перенесите в файл чтения; если новый файл чтения и правда нужен — впишите его в ReadFiles с причиной.");
    }

    [Fact]
    public void Модуль_с_отбором_по_правам_помечает_изолированные_записи()
    {
        var found = Models()
            .GroupBy(m => m.Assembly)
            .SelectMany(g => BHS.CRG.Api.Modules.DeliveredModules.All()
                .Where(module => module.GetType().Assembly == g.Key)
                .SelectMany(module => Unisolated(module, [.. g.SelectMany(m => IsolatedOf(m.Model))])))
            .ToList();

        // Модуль с таблицей по правам, но без своей схемы, сюда бы не попал вовсе — его ловит отдельно:
        var schemaless = BHS.CRG.Api.Modules.DeliveredModules.All()
            .Where(module => Models().All(m => m.Assembly != module.GetType().Assembly))
            .SelectMany(module => Unisolated(module, []));

        var all = found.Concat(schemaless).ToList();
        Assert.True(all.Count == 0,
            string.Join("\n", all) + "\n\nОтбор держится тогда одной службой строк таблицы, а прочие пути " +
            "чтения той же записи его не знают. Пометьте запись IIsolatedRecord и читайте её в файле чтения.");
    }

    [Fact]
    public void Перечень_чтения_не_хранит_умерших_записей()
    {
        var isolated = Models().SelectMany(m => IsolatedOf(m.Model)).ToList();
        var stale = ReadFiles.Keys
            .Where(rel =>
            {
                var path = Path.Combine(SourceTree.SolutionDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) return true;
                // Файл чтения без чтения — список разрешает то, чего уже не делают.
                return !Violations(rel, File.ReadAllText(path), isolated, readFile: false).Any();
            })
            .ToList();

        Assert.True(stale.Count == 0,
            "В ReadFiles файлы, где изолированные записи больше не читаются: " + string.Join(", ", stale));
    }

    /// <summary>
    /// Сторож обязан видеть модели: контекст, не построенный отражением, выпал бы из переписи молча, и она
    /// осталась бы зелёной, что бы в модуле ни написали.
    /// </summary>
    [Fact]
    public void Перепись_строит_модели_всех_модулей_со_схемой()
    {
        var models = Models();
        Assert.True(models.Count >= 3, $"Построено моделей модулей: {models.Count} — ждали costs, plan и work.");
        // Пустой модель быть может (у каркаса work сущностей пока нет), а «построена ли вообще» — ниже.
        Assert.Contains(models, m => m.Model.GetEntityTypes().Any());

        var withContext = SolutionModules.Names
            .Where(name => Assembly.Load(name).GetTypes().Any(IsModuleContext))
            .ToList();
        Assert.Equal(withContext.Order(), models.Select(m => m.Assembly.GetName().Name!).Distinct().Order());
    }

    // ── Самопроверка на образцах исходника: каждое нарушение находится, файл чтения его разрешает ──

    private static readonly Isolated Report = new("WorkReport", "work", "reports");

    [Theory]
    [InlineData("DbSet", "public DbSet<WorkReport> Reports => Set<WorkReport>();", "DbSet<WorkReport>")]
    [InlineData("DbSet полным именем", "public DbSet<BHS.CRG.Modules.Work.WorkReport> Reports { get; set; }", "DbSet<WorkReport>")]
    [InlineData("Set<T>()", "var all = await db.Set<WorkReport>().ToListAsync(ct);", "Set<WorkReport>() вне файла чтения")]
    [InlineData("FromSql", "var rows = db.Set<Row>().FromSql($\"SELECT * FROM work.reports\");", "сырой SQL по таблице «work.reports»")]
    [InlineData("SqlQuery в кавычках", "db.Database.SqlQueryRaw<Guid>(\"SELECT id FROM \\\"work\\\".\\\"reports\\\"\");", "сырой SQL по таблице «work.reports»")]
    [InlineData("ExecuteSql", "await db.Database.ExecuteSqlAsync($\"UPDATE work.reports SET x = 1\", ct);", "сырой SQL по таблице «work.reports»")]
    public void Обход_находится(string what, string sample, string expected)
    {
        var found = Violations("Sample.cs", Wrap(sample), [Report], readFile: false).ToList();
        Assert.True(found.Any(f => f.Contains(expected)), $"{what}: ждали «{expected}», найдено: {string.Join("; ", found)}");
    }

    [Fact]
    public void Файл_чтения_разрешает_Set_и_SQL_но_не_DbSet()
    {
        var reading = Wrap("""
            var mine = db.Set<WorkReport>().Where(r => reach.Covers(r.SiteId, r.AuthorId == reader.UserId));
            var count = db.Database.SqlQuery<int>($"SELECT count(*) FROM work.reports WHERE author_id = {id}");
            """);
        Assert.Empty(Violations("Reading.cs", reading, [Report], readFile: true));

        var withSet = Wrap("public DbSet<WorkReport> Reports => Set<WorkReport>();");
        Assert.Single(Violations("Reading.cs", withSet, [Report], readFile: true));
    }

    [Fact]
    public void Комментарий_и_чужая_таблица_нарушением_не_считаются()
    {
        var sample = Wrap("""
            // db.Set<WorkReport>() — читать только в файле чтения
            var other = db.Database.SqlQuery<int>($"SELECT count(*) FROM work.reports_archive");
            var plan = db.Set<WorkReportDraft>().ToList();
            """);
        Assert.Empty(Violations("Sample.cs", sample, [Report], readFile: false));
    }

    [Fact]
    public void Модуль_с_отбором_по_правам_без_изолированной_записи_краснеет()
    {
        var module = new ModuleTableIsolationProbe.ProbeModule(ModuleTableIsolationProbe.Table());

        var red = Assert.Single(Unisolated(module, []));
        Assert.Contains("отбор по правам объявлен", red);
        Assert.Contains("изолированных записей нет", red);
        Assert.Contains(ModuleTableIsolationProbe.Address, red);

        Assert.Empty(Unisolated(module, [Report]));
    }

    /// <summary>Пометка на сущности находится в модели — с таблицей и схемой из настройки, а не из имени класса.</summary>
    [Fact]
    public void Пометка_находится_в_модели_с_таблицей_из_настройки()
    {
        using var context = new ProbeContext(
            new DbContextOptionsBuilder<ProbeContext>().UseNpgsql("Host=127.0.0.1;Database=never").Options);

        var record = Assert.Single(IsolatedOf(context.Model));
        Assert.Equal(new Isolated(nameof(ProbeReport), "probe", "field_reports"), record);
    }

    private sealed class ProbeContext(DbContextOptions<ProbeContext> options) : ModuleDbContext(options)
    {
        protected override string Schema => "probe";

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<ProbeReport>().ToTable("field_reports");
            builder.Entity<ProbeNote>().ToTable("notes");
        }
    }

    private sealed class ProbeReport : IIsolatedRecord
    {
        public Guid Id { get; set; }
    }

    private sealed class ProbeNote
    {
        public Guid Id { get; set; }
    }

    // ── Обход ──

    private static string Wrap(string body) => $"class Sample\n{{\n    void Run()\n    {{\n{body}\n    }}\n}}\n";

    private static string Name(Isolated record) => record.Schema is null ? record.Table : $"{record.Schema}.{record.Table}";

    /// <summary>Таблица записи в тексте SQL: после FROM / JOIN / INTO / UPDATE…, со схемой и кавычками или без.</summary>
    private static Regex SqlTouch(Isolated record)
    {
        var quoted = (string name) => $@"\\?""?{Regex.Escape(name)}\\?""?";
        var schema = record.Schema is null ? "" : $@"(?:{quoted(record.Schema)}\.)?";
        return new Regex(
            $@"\b(?:FROM|INTO|UPDATE|JOIN|TABLE|TRUNCATE)\s+(?:ONLY\s+)?{schema}\\?""?{Regex.Escape(record.Table)}\b(?!\w)",
            RegexOptions.IgnoreCase);
    }

    private static bool IsModuleContext(Type t) => !t.IsAbstract && t.IsSubclassOf(typeof(ModuleDbContext));

    private static List<(Assembly Assembly, IModel Model)> Models()
    {
        var models = new List<(Assembly, IModel)>();
        foreach (var name in SolutionModules.Names)
        {
            var assembly = Assembly.Load(name);
            foreach (var type in assembly.GetTypes().Where(IsModuleContext))
            {
                // Тот же путь, что у хоста: настройку контекста задаёт сам модуль (Configure), строка
                // подключения не открывается — модели база не нужна.
                var configure = type.GetMethod("Configure", BindingFlags.Public | BindingFlags.Static,
                    [typeof(DbContextOptionsBuilder), typeof(string)]);
                Assert.True(configure is not null,
                    $"У контекста {type.FullName} нет Configure(options, connectionString) — перепись не может построить его модель.");

                var builder = (DbContextOptionsBuilder)Activator.CreateInstance(typeof(DbContextOptionsBuilder<>).MakeGenericType(type))!;
                configure.Invoke(null, [builder, "Host=127.0.0.1;Database=never"]);
                // Конструктор с параметрами контекста первым; остальное (пользователь модуля и т. п.) —
                // умолчаниями: модели они не меняют.
                var ctor = type.GetConstructors().Single(c =>
                    c.GetParameters() is [var first, ..] && first.ParameterType.IsAssignableFrom(builder.Options.GetType()));
                using var context = (DbContext)ctor.Invoke(
                    [builder.Options, .. ctor.GetParameters().Skip(1).Select(p => p.HasDefaultValue ? p.DefaultValue : null)]);
                models.Add((assembly, context.Model));
            }
        }
        return models;
    }

    /// <summary>Исходники ядра и модулей — общим обходом (без obj, bin и миграций).</summary>
    private static IEnumerable<string> Sources() => Projects.SelectMany(SourceTree.Files);

    private static string Code(string text) =>
        string.Join('\n', text.Split('\n').Select(line => line.TrimStart().StartsWith("//") ? string.Empty : line));
}
