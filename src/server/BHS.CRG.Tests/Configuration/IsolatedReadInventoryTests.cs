using System.Collections.Concurrent;
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
/// <para><b>Обходы, которые компилируются и работают.</b> <c>DbSet&lt;T&gt;</c> в контексте — дверь, в
/// которую войдёт любой следующий запрос. <c>Set&lt;T&gt;()</c> и <c>Find&lt;T&gt;()</c> в чужом файле —
/// чтение без охвата, все отчёты всем. Сырой SQL по таблице записи — то же, и ещё мимо модели.
/// Навигация к записи от соседней сущности — <c>Include(s =&gt; s.Reports)</c> и
/// <c>Entry(s).Collection(…)</c> без единого упоминания типа записи; её ловит модель, а не текст.
/// Запрет в контексте обходился бы любым из них; перепись — нет.</para>
///
/// <para><b>Записи и их таблицы — из модели EF</b> каждого контекста модуля, а не из текста: имя таблицы
/// задаёт настройка (<c>ToTable</c>, <c>ToView</c>), и угадывать его по имени класса значило бы
/// промахнуться на первом же переименовании. Запись без таблицы (<c>ToSqlQuery</c>) из переписи не
/// выпадает: проверки набора, <c>Set</c>, <c>Find</c> и навигаций таблицы не требуют.</para>
///
/// <para><b>PerUser-таблица — по каждой таблице, а не по модулю.</b> Одна помеченная запись не закрывает
/// чужие таблицы: служба строк таблицы с отбором по правам обязана касаться изолированной записи модуля
/// или класса из файла чтения.</para>
///
/// <para>⚠️ <b>Чего не видит.</b> Имя таблицы, собранное склейкой (<c>$"{schema}.{name}"</c>),
/// <c>Set(typeof(T))</c> и <c>Find(typeof(T), …)</c> по переменной — то же ограничение, что у прочих
/// переписей (<see cref="InventoryScopeTests" />). Миграции не читаются (общий обход
/// <c>SourceTree.Files</c>): перенос данных в миграции — не чтение для человека, и охвата там нет по
/// замыслу.</para>
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

    /// <summary>Изолированная запись: имя типа и её таблица (или представление); null — таблицы нет.</summary>
    public sealed record Isolated(string Type, string? Schema, string? Table);

    private static bool IsIsolated(Type type) => typeof(IIsolatedRecord).IsAssignableFrom(type);

    /// <summary>Изолированные записи модели — по пометке на типе сущности.</summary>
    internal static IReadOnlyList<Isolated> IsolatedOf(IModel model) =>
    [
        .. model.GetEntityTypes()
            .Where(e => IsIsolated(e.ClrType))
            .Select(e => new Isolated(
                e.ClrType.Name, e.GetSchema() ?? e.GetViewSchema(), e.GetTableName() ?? e.GetViewName())),
    ];

    /// <summary>
    /// Навигации к изолированной записи от неизолированной сущности: <c>Include</c> и
    /// <c>Entry(…).Collection</c> по ним читают запись без охвата. От изолированной к изолированной можно
    /// — такую навигацию читают только в файле чтения.
    /// </summary>
    internal static IEnumerable<string> Navigations(IModel model) =>
        model.GetEntityTypes()
            .Where(e => !IsIsolated(e.ClrType))
            .SelectMany(e => e.GetNavigations().Cast<INavigationBase>().Concat(e.GetSkipNavigations())
                .Where(n => IsIsolated(n.TargetEntityType.ClrType))
                .Select(n => $"{e.ClrType.Name}.{n.Name} → {n.TargetEntityType.ClrType.Name}"));

    /// <summary>
    /// Нарушения в одном файле. <paramref name="readFile" /> — файл из перечня чтения: там разрешены
    /// <c>Set&lt;T&gt;()</c>, <c>Find&lt;T&gt;()</c> и SQL по таблице, но не <c>DbSet&lt;T&gt;</c> — набора
    /// у записи нет нигде.
    /// </summary>
    internal static IEnumerable<string> Violations(
        string rel, string text, IReadOnlyList<Isolated> isolated, bool readFile)
    {
        var code = SourceTree.WithoutComments(text);
        foreach (var record in isolated)
        {
            var type = Regex.Escape(record.Type);
            foreach (Match m in Regex.Matches(code, $@"\bDbSet\s*<\s*(?:[\w.]+\.)?{type}\s*>"))
                yield return $"{rel}:{SourceTree.LineOf(code, m.Index)}  DbSet<{record.Type}> — у изолированной записи набора в контексте нет";

            if (readFile) continue;

            foreach (Match m in Regex.Matches(code, $@"\b(Set|Find|FindAsync)\s*<\s*(?:[\w.]+\.)?{type}\s*>\s*\("))
                yield return $"{rel}:{SourceTree.LineOf(code, m.Index)}  {m.Groups[1].Value}<{record.Type}>() вне файла чтения";

            if (SqlTouch(record) is not { } sql) continue;
            foreach (Match m in sql.Matches(code))
                yield return $"{rel}:{SourceTree.LineOf(code, m.Index)}  сырой SQL по таблице «{Name(record)}» вне файла чтения";
        }
    }

    /// <summary>
    /// Таблицы модуля с отбором по правам, за которыми не стоит изолированная запись: у модуля нет ни
    /// одной, или служба строк таблицы не касается ни записи, ни класса из файла чтения.
    /// <paramref name="readerCode" /> — исходник службы строк без комментариев; null — не найден.
    /// </summary>
    internal static IEnumerable<string> Unisolated(
        IAppModule module, IReadOnlyList<Isolated> isolated, Func<Type, string?> readerCode,
        IReadOnlyCollection<string> readClasses)
    {
        var perUser = module.Tables.Where(t => t.Isolation == ModuleTableIsolation.PerUser).ToList();
        if (perUser.Count == 0) yield break;

        if (isolated.Count == 0)
        {
            yield return $"модуль «{module.Code}»: отбор по правам объявлен " +
                         $"({string.Join(", ", perUser.Select(t => ModuleTable.Address(module.Code, t.Code)))}), изолированных записей нет";
            yield break;
        }

        var names = isolated.Select(r => r.Type).Concat(readClasses).Distinct().ToList();
        foreach (var table in perUser)
        {
            var address = ModuleTable.Address(module.Code, table.Code);
            var code = readerCode(table.Reader);
            if (code is null)
                yield return $"таблица «{address}»: исходник службы строк {table.Reader.Name} не найден — " +
                             "перепись не видит, что она читает";
            else if (!names.Any(n => Regex.IsMatch(code, $@"\b{Regex.Escape(n)}\b")))
                yield return $"таблица «{address}» отбирает по правам, а служба строк {table.Reader.Name} не касается " +
                             $"ни одной изолированной записи модуля ({string.Join(", ", isolated.Select(r => r.Type))}) " +
                             "и не зовёт файла чтения";
        }
    }

    [Fact]
    public void Изолированная_запись_читается_только_в_своём_файле()
    {
        var isolated = AllIsolated();
        var found = SourceTexts()
            .SelectMany(s => Violations(s.Rel, s.Code, isolated, ReadFiles.ContainsKey(s.Rel)))
            .ToList();

        Assert.True(found.Count == 0,
            "Изолированную запись читают мимо её файла чтения:\n" + string.Join("\n", found) + "\n\n" +
            "Такая запись читается только методом с параметром доступа (ModuleReader), который вычисляет " +
            "охват (ModuleReach): иначе монтажник получит чужие отчёты, а менеджер — чужие стройки. Чтение " +
            "перенесите в файл чтения; если новый файл чтения и правда нужен — впишите его в ReadFiles с причиной.");
    }

    [Fact]
    public void К_изолированной_записи_не_ведёт_навигация()
    {
        var found = Models().SelectMany(m => Navigations(m.Model).Select(n => $"{m.Context.Name}: {n}")).ToList();

        Assert.True(found.Count == 0,
            "К изолированной записи ведёт навигация от сущности, видимой всем:\n" + string.Join("\n", found) +
            "\n\nInclude и Entry(…).Collection по ней читают запись без охвата, и ни Set<T>, ни имени типа в " +
            "таком запросе нет. Храните ключ (SiteId) без навигации, а записи берите в файле чтения.");
    }

    [Fact]
    public void Модуль_с_отбором_по_правам_помечает_изолированные_записи()
    {
        var models = Models();
        var classes = ReadClasses();
        var found = BHS.CRG.Api.Modules.DeliveredModules.All()
            .SelectMany(module =>
            {
                var model = models.FirstOrDefault(m => m.Context == module.Schema?.ContextType).Model;
                return Unisolated(module, model is null ? [] : IsolatedOf(model), ReaderCode, classes);
            })
            .ToList();

        Assert.True(found.Count == 0,
            string.Join("\n", found) + "\n\nОтбор держится тогда одной службой строк таблицы, а прочие пути " +
            "чтения той же записи его не знают. Пометьте запись IIsolatedRecord и читайте её в файле чтения.");
    }

    [Fact]
    public void Перечень_чтения_не_хранит_умерших_записей()
    {
        var isolated = AllIsolated();
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
    /// осталась бы зелёной, что бы в модуле ни написали. Сверка — с объявлением модулей
    /// (<see cref="IAppModule.Schema" />), а не с тем же обходом проектов, которым модели и строятся.
    /// </summary>
    [Fact]
    public void Перепись_строит_модели_всех_модулей_со_схемой()
    {
        var built = Models().Select(m => m.Context).ToHashSet();
        var declared = BHS.CRG.Api.Modules.DeliveredModules.All()
            .Where(m => m.Schema is not null)
            .Select(m => m.Schema!.ContextType)
            .ToList();

        Assert.True(declared.Count >= 3, $"Модулей со схемой: {declared.Count} — ждали costs, plan и work.");
        var missed = declared.Where(c => !built.Contains(c)).Select(c => c.FullName).ToList();
        Assert.True(missed.Count == 0,
            "Перепись не построила модели контекстов, объявленных модулями: " + string.Join(", ", missed) +
            ". Их изолированные записи ни одна проверка выше не видит.");
        // Пустой модель быть может (у каркаса work сущностей пока нет), а «построена ли вообще» — выше.
        Assert.Contains(Models(), m => m.Model.GetEntityTypes().Any());
    }

    // ── Самопроверка на образцах исходника: каждое нарушение находится, файл чтения его разрешает ──

    private static readonly Isolated Report = new("WorkReport", "work", "reports");

    [Theory]
    [InlineData("DbSet", "public DbSet<WorkReport> Reports => Set<WorkReport>();", "DbSet<WorkReport>")]
    [InlineData("DbSet полным именем", "public DbSet<BHS.CRG.Modules.Work.WorkReport> Reports { get; set; }", "DbSet<WorkReport>")]
    [InlineData("Set<T>()", "var all = await db.Set<WorkReport>().ToListAsync(ct);", "Set<WorkReport>() вне файла чтения")]
    [InlineData("Find<T>()", "var one = await db.FindAsync<WorkReport>(id);", "FindAsync<WorkReport>() вне файла чтения")]
    [InlineData("Set<T>() за адресом в строке", "var u = \"https://x/\"; var all = db.Set<WorkReport>();", "Set<WorkReport>() вне файла чтения")]
    [InlineData("FromSql", "var rows = db.Set<Row>().FromSql($\"SELECT * FROM work.reports\");", "сырой SQL по таблице «work.reports»")]
    [InlineData("SqlQuery в кавычках", "db.Database.SqlQueryRaw<Guid>(\"SELECT id FROM \\\"work\\\".\\\"reports\\\"\");", "сырой SQL по таблице «work.reports»")]
    [InlineData("verbatim с удвоенными кавычками", "db.Database.SqlQueryRaw<Guid>(@\"SELECT id FROM \"\"work\"\".\"\"reports\"\"\");", "сырой SQL по таблице «work.reports»")]
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
            var one = await db.FindAsync<WorkReport>(id);
            var count = db.Database.SqlQuery<int>($"SELECT count(*) FROM work.reports WHERE author_id = {id}");
            """);
        Assert.Empty(Violations("Reading.cs", reading, [Report], readFile: true));

        var withSet = Wrap("public DbSet<WorkReport> Reports => Set<WorkReport>();");
        Assert.Single(Violations("Reading.cs", withSet, [Report], readFile: true));
    }

    [Fact]
    public void Комментарий_LINQ_и_чужая_таблица_нарушением_не_считаются()
    {
        var sample = Wrap("""
            // db.Set<WorkReport>() — читать только в файле чтения
            var x = 1; // и не db.FindAsync<WorkReport>(id)
            /* db.Set<WorkReport>() */
            var other = db.Database.SqlQuery<int>($"SELECT count(*) FROM work.reports_archive");
            var plan = db.Set<WorkReportDraft>().ToList();
            var q = from reports in db.Set<Row>() group reports by reports.Site into reports select reports;
            """);
        Assert.Empty(Violations("Sample.cs", sample, [Report], readFile: false));
    }

    [Fact]
    public void Запись_без_таблицы_проверяется_без_SQL()
    {
        var sample = Wrap("public DbSet<WorkReport> Reports { get; set; } var all = db.Set<WorkReport>();");
        var found = Violations("Sample.cs", sample, [Report with { Schema = null, Table = null }], readFile: false).ToList();

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void Модуль_с_отбором_по_правам_без_изолированной_записи_краснеет()
    {
        var module = new ModuleTableIsolationProbe.ProbeModule(ModuleTableIsolationProbe.Table());

        var red = Assert.Single(Unisolated(module, [], _ => null, []));
        Assert.Contains("отбор по правам объявлен", red);
        Assert.Contains("изолированных записей нет", red);
        Assert.Contains(ModuleTableIsolationProbe.Address, red);
    }

    [Fact]
    public void Таблица_по_правам_обязана_касаться_своей_записи()
    {
        var module = new ModuleTableIsolationProbe.ProbeModule(ModuleTableIsolationProbe.Table());

        Assert.Empty(Unisolated(module, [Report], _ => "class ReportRows { db.Set<WorkReport>() }", []));
        Assert.Empty(Unisolated(module, [Report], _ => "class ReportRows { WorkReportReads.Page(reader) }", ["WorkReportReads"]));

        var foreign = Assert.Single(Unisolated(module, [Report], _ => "class ReportRows { db.Set<Assignment>() }", []));
        Assert.Contains("не касается ни одной изолированной записи модуля (WorkReport)", foreign);
        Assert.Contains(ModuleTableIsolationProbe.Address, foreign);

        Assert.Contains("не найден", Assert.Single(Unisolated(module, [Report], _ => null, [])));
    }

    /// <summary>Пометка на сущности находится в модели — с таблицей и схемой из настройки, а не из имени класса.</summary>
    [Fact]
    public void Пометка_находится_в_модели_с_таблицей_из_настройки()
    {
        using var context = new ProbeContext(Options<ProbeContext>());

        Assert.Equal(
            [new Isolated(nameof(ProbeReport), "probe", "field_reports"), new Isolated(nameof(ProbeView), "probe", "reports_v")],
            IsolatedOf(context.Model).OrderBy(r => r.Type, StringComparer.Ordinal));
        Assert.Empty(Navigations(context.Model));
    }

    [Fact]
    public void Навигация_к_изолированной_записи_находится_в_модели()
    {
        using var context = new LeakyContext(Options<LeakyContext>());

        Assert.Equal($"{nameof(ProbeSite)}.{nameof(ProbeSite.Reports)} → {nameof(ProbeReport)}", Assert.Single(Navigations(context.Model)));
    }

    private static DbContextOptions<T> Options<T>() where T : DbContext =>
        new DbContextOptionsBuilder<T>().UseNpgsql("Host=127.0.0.1;Database=never").Options;

    private sealed class ProbeContext(DbContextOptions<ProbeContext> options) : ModuleDbContext(options)
    {
        protected override string Schema => "probe";

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<ProbeReport>().ToTable("field_reports");
            builder.Entity<ProbeView>().HasNoKey().ToView("reports_v");
            builder.Entity<ProbeNote>().ToTable("notes");
        }
    }

    private sealed class LeakyContext(DbContextOptions<LeakyContext> options) : ModuleDbContext(options)
    {
        protected override string Schema => "probe";

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<ProbeReport>().ToTable("field_reports");
            builder.Entity<ProbeSite>().ToTable("sites").HasMany(s => s.Reports).WithOne().HasForeignKey(r => r.SiteId);
        }
    }

    private sealed class ProbeReport : IIsolatedRecord
    {
        public Guid Id { get; set; }
        public Guid SiteId { get; set; }
    }

    private sealed class ProbeView : IIsolatedRecord
    {
        public Guid Id { get; set; }
    }

    private sealed class ProbeSite
    {
        public Guid Id { get; set; }
        public List<ProbeReport> Reports { get; set; } = [];
    }

    private sealed class ProbeNote
    {
        public Guid Id { get; set; }
    }

    // ── Обход ──

    private static string Wrap(string body) => $"class Sample\n{{\n    void Run()\n    {{\n{body}\n    }}\n}}\n";

    private static string Name(Isolated record) => record.Schema is null ? record.Table! : $"{record.Schema}.{record.Table}";

    private static readonly ConcurrentDictionary<Isolated, Regex?> SqlCache = new();

    /// <summary>
    /// Таблица записи в тексте SQL. Схема у записи модуля обязательна: путь поиска базы — схема ядра, и
    /// голое имя до таблицы модуля не дойдёт, а совпадало бы с LINQ (<c>from reports in …</c>).
    /// </summary>
    private static Regex? SqlTouch(Isolated record) => SqlCache.GetOrAdd(record, r =>
        r.Table is null ? null : SourceTree.SqlTouch(r.Schema, schemaOptional: false, [r.Table]));

    private static bool IsModuleContext(Type t) => !t.IsAbstract && t.IsSubclassOf(typeof(ModuleDbContext));

    private static IReadOnlyList<Isolated> AllIsolated() => [.. Models().SelectMany(m => IsolatedOf(m.Model))];

    private static readonly Lazy<IReadOnlyList<(Type Context, IModel Model)>> BuiltModels = new(BuildModels);

    /// <summary>Модели контекстов модулей — один раз на прогон: строить их заново в каждой проверке незачем.</summary>
    private static IReadOnlyList<(Type Context, IModel Model)> Models() => BuiltModels.Value;

    private static List<(Type Context, IModel Model)> BuildModels()
    {
        var models = new List<(Type, IModel)>();
        foreach (var type in SolutionModules.Names.SelectMany(name => Assembly.Load(name).GetTypes()).Where(IsModuleContext))
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
            var ctors = type.GetConstructors()
                .Where(c => c.GetParameters() is [var first, ..] && first.ParameterType.IsAssignableFrom(builder.Options.GetType()))
                .ToList();
            Assert.True(ctors.Count == 1,
                $"У контекста {type.FullName} конструкторов с параметрами контекста первым аргументом: {ctors.Count} — " +
                "перепись не знает, каким построить его модель. Оставьте один или научите перепись выбирать.");
            var ctor = ctors[0];
            using var context = (DbContext)ctor.Invoke(
                [builder.Options, .. ctor.GetParameters().Skip(1).Select(p => p.HasDefaultValue ? p.DefaultValue : null)]);
            models.Add((type, context.Model));
        }
        return models;
    }

    private static readonly Lazy<IReadOnlyList<(string Rel, string Code)>> Texts = new(() =>
    [
        .. Projects.SelectMany(SourceTree.Files)
            .Select(f => (SourceTree.Relative(f), SourceTree.WithoutComments(File.ReadAllText(f)))),
    ]);

    /// <summary>Исходники ядра и модулей без комментариев — общим обходом (без obj, bin и миграций).</summary>
    private static IReadOnlyList<(string Rel, string Code)> SourceTexts() => Texts.Value;

    /// <summary>Исходник службы строк: все файлы, где объявлен её класс (partial — тоже).</summary>
    private static string? ReaderCode(Type reader)
    {
        var declares = new Regex($@"\b(?:class|record|struct)\s+{Regex.Escape(reader.Name)}\b");
        var parts = SourceTexts().Where(s => declares.IsMatch(s.Code)).Select(s => s.Code).ToList();
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    /// <summary>Классы, объявленные в файлах чтения: служба строк, которая их зовёт, читает через охват.</summary>
    private static IReadOnlyCollection<string> ReadClasses() =>
    [
        .. SourceTexts()
            .Where(s => ReadFiles.ContainsKey(s.Rel))
            .SelectMany(s => Regex.Matches(s.Code, @"\b(?:class|record|interface)\s+(\w+)"))
            .Select(m => m.Groups[1].Value)
            .Distinct(),
    ];
}
