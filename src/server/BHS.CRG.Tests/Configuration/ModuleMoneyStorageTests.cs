using System.Text.RegularExpressions;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Costs;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Условие входа в этап 2: суммы закрыты от того, у кого модуля нет, — и следует это из МЕСТА ХРАНЕНИЯ,
/// а не из полноты перечня путей чтения (задача H1, issue #1104; ТЗ STG-6, CORE-24.2, COST-29).
///
/// <para>Три сторожа. Первый — правило ядра: денежный тэг у типа в общей таблице останавливает старт и
/// называет тип. Второй — перепись самого модуля счетов: ни один его тип в общей таблице не лежит, а
/// числовое поле без денежного тэга названо поимённо (забытый признак правило ядра не поймает). Третий —
/// перепись обращений ядра к коду и к схеме модуля: мимо контрактов его таблицы не читает никто.</para>
/// </summary>
public class ModuleMoneyStorageTests
{
    private const string Money = "probe.amount";

    // ── 1. Правило ядра ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Денежный_тэг_у_типа_в_общей_таблице_назван_вместе_с_типом()
    {
        var problem = Assert.Single(ModuleMoneyStorage.Problems([Probe(ModuleStorage.SharedObject)]));

        Assert.Contains("«ПробныйСчёт»", problem);
        Assert.Contains("«Сумма»", problem);
        Assert.Contains($"«{Money}»", problem);
    }

    [Fact]
    public void Тот_же_тип_в_таблице_модуля_проходит()
        => Assert.Empty(ModuleMoneyStorage.Problems([Probe(ModuleStorage.ModuleTable)]));

    /// <summary>Тэг объявил один модуль, поле несёт тип другого — правило смотрит на всю сборку.</summary>
    [Fact]
    public void Денежный_тэг_чужого_модуля_тоже_считается()
    {
        var owner = new ProbeModule("tags", [new(Money, "Сумма", "Деньги", ModuleTagScope.Field, Money: true)], []);
        var user = new ProbeModule("probe", [], [Type(ModuleStorage.SharedObject)]);

        Assert.Single(ModuleMoneyStorage.Problems([owner, user]));
    }

    /// <summary>
    /// Отказ — при старте, а не в тесте: сборка служб с таким модулем не собирается. И даже если модуль
    /// выключен — его тип уже мог лечь в базу.
    /// </summary>
    [Theory]
    [InlineData("probe")]
    [InlineData("id")]
    public void Старт_отказывает_и_называет_тип(string enabled)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:Enabled"] = enabled }).Build();

        var refusal = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddAppModules(config, [], new ProbeModule("id", [], []), Probe(ModuleStorage.SharedObject)));

        Assert.Contains("«ПробныйСчёт»", refusal.Message);
        Assert.Contains("общей таблице", refusal.Message);
    }

    // ── 2. Модуль счетов ──────────────────────────────────────────────────────────────────────────

    /// <summary>Типы модуля счетов, которым позволено лежать в общей таблице, — и почему. Сегодня таких нет.</summary>
    private static readonly Dictionary<string, string> SharedOnPurpose = new()
    {
        ["СтатьяВнеСтроек"] =
            "справочник модуля (F3, issue #1087): одно название, сумм нет; деньги на статью лежат долей " +
            "разноски в таблице модуля. Заводится через порт собственного справочника, под правом модуля",
    };

    /// <summary>Числовые поля типов модуля счетов, которые НЕ деньги, — и почему.</summary>
    private static readonly Dictionary<string, string> NotMoney = new()
    {
        ["СчётНаОплату.Отсрочка"] = "срок оплаты в днях (C4), не сумма",
    };

    [Fact]
    public void Модули_сборки_правило_не_нарушают()
        => Assert.Empty(ModuleMoneyStorage.Problems([new CostsModule()]));

    [Fact]
    public void Ни_один_тип_модуля_счетов_не_хранится_в_общей_таблице()
    {
        var shared = new CostsModule().RecordTypes
            .Where(t => t.Storage == ModuleStorage.SharedObject && !SharedOnPurpose.ContainsKey(t.Code))
            .Select(t => t.Code).ToList();

        Assert.True(shared.Count == 0,
            "Типы модуля счетов в общей таблице объектов: " + string.Join(", ", shared) + ".\n" +
            "Модуль ведёт деньги, и его записи лежат в таблицах модуля, под его правами: общую таблицу " +
            "читают пути, которые на права модуля не смотрят. Если тип денег не несёт и в общей таблице " +
            "он нарочно — впишите его в SharedOnPurpose с причиной.");
    }

    /// <summary>
    /// Признак «деньги» ставит автор тэга, и забытый признак правило ядра не поймает. Здесь перечисляет
    /// машина: числовое поле типа модуля счетов либо несёт денежный тэг, либо названо «не деньги».
    /// </summary>
    [Fact]
    public void Числовое_поле_типа_модуля_счетов_несёт_денежный_тэг()
    {
        var module = new CostsModule();
        var money = module.Tags.Where(t => t.Money).Select(t => t.Code).ToHashSet();

        var unmarked = (from type in module.RecordTypes
                        from field in type.Fields
                        where field.Type == "number" && !field.Tags.Any(money.Contains)
                        let name = $"{type.Code}.{field.Key}"
                        where !NotMoney.ContainsKey(name)
                        select name).ToList();

        Assert.True(unmarked.Count == 0,
            "Числовые поля без денежного тэга: " + string.Join(", ", unmarked) + ".\n" +
            "Поставьте тэгу поля признак Money либо впишите поле в NotMoney с причиной: без признака " +
            "правило «деньги не в общей таблице» это поле не видит.");
        Assert.NotEmpty(money);
    }

    // ── 3. Перепись обращений ядра к модулю ───────────────────────────────────────────────────────

    private static readonly string[] CoreProjects =
        ["BHS.CRG.Api", "BHS.CRG.Application", "BHS.CRG.Infrastructure", "BHS.CRG.Domain", "BHS.CRG.Modules", "BHS.CRG.Plugins"];

    /// <summary>Кому в ядре позволено назвать код модуля по имени — и почему.</summary>
    private static readonly Dictionary<string, string> MayNameTheModule = new()
    {
        ["BHS.CRG.Api/Configuration/ServiceRegistration.Workers.cs"] =
            "состав поставки: единственное место, где модуль называют, чтобы отдать его реестру",
    };

    /// <summary>
    /// Ядро не называет код модуля и не пишет запросов к его схеме: данные модуля оно получает только
    /// через контракты (таблицы, порты, отчёт закрытия), где права проверяет сам модуль либо объявление.
    /// Обращение мимо них — чтение сумм без прав модуля, и поймать его можно только перечислением.
    /// </summary>
    [Fact]
    public void Ядро_не_читает_таблицы_модуля_мимо_контрактов()
    {
        var names = Directory.EnumerateDirectories(SolutionDir, "BHS.CRG.Modules.*")
            // Шаблон «BHS.CRG.Modules.*» в Windows находит и сам проект контрактов — он не модуль.
            .Select(Path.GetFileName).Where(n => n != "BHS.CRG.Modules").Select(n => Regex.Escape(n!)).ToList();
        var schemas = new[] { new CostsModule() }.Select(m => m.Schema?.Name).OfType<string>().Select(Regex.Escape).ToList();
        Assert.NotEmpty(names);
        Assert.NotEmpty(schemas);

        var byName = new Regex($@"\b({string.Join("|", names)})\b", RegexOptions.Compiled);
        // Запрос к схеме модуля: FROM costs.invoices, JOIN "costs"."invoices", UPDATE costs.…
        var bySql = new Regex($@"\b(FROM|JOIN|INTO|UPDATE|TABLE)\s+""?({string.Join("|", schemas)})""?\s*\.",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        var outsiders = new List<string>();
        foreach (var file in CoreProjects.SelectMany(SourceFiles))
        {
            var rel = Relative(file);
            if (MayNameTheModule.ContainsKey(rel)) continue;
            var text = File.ReadAllText(file);
            outsiders.AddRange(byName.Matches(text).Concat(bySql.Matches(text))
                .Select(m => $"{rel}:{LineOf(text, m.Index)} — «{m.Value.Trim()}»"));
        }

        Assert.True(outsiders.Count == 0,
            "Ядро обратилось к модулю мимо контрактов:\n" + string.Join("\n", outsiders) + "\n\n" +
            "Данные модуля ядро читает через объявления (ModuleTable, порты, отчёт закрытия): только там " +
            "у строки есть право, которым она закрыта. Если обращение всё-таки нужно — впишите файл в " +
            "MayNameTheModule с причиной.");
    }

    [Fact]
    public void Перепись_не_хранит_умерших_записей()
    {
        var dead = MayNameTheModule.Keys.Where(rel => !File.Exists(Path.Combine(SolutionDir, rel))).ToList();
        Assert.True(dead.Count == 0, "В MayNameTheModule названы файлы, которых нет: " + string.Join(", ", dead));
    }

    // ── Помощники ─────────────────────────────────────────────────────────────────────────────────

    private static ModuleRecordType Type(ModuleStorage storage) => new(
        "ПробныйСчёт", "Пробный счёт", ModuleSchemaLevel.Closed,
        [new("Сумма", "Сумма", "number", [Money])], Storage: storage);

    private static ProbeModule Probe(ModuleStorage storage) => new(
        "probe", [new(Money, "Сумма", "Деньги", ModuleTagScope.Field, Money: true)], [Type(storage)]);

    private sealed class ProbeModule(string code, ModuleTag[] tags, ModuleRecordType[] types) : IAppModule
    {
        public string Code => code;
        public string Title => code;
        public IReadOnlyList<AppPermission> Permissions => [];
        public IReadOnlyList<string> RoutePrefixes => [];
        public IReadOnlyList<ModuleTag> Tags => tags;
        public IReadOnlyList<ModuleRecordType> RecordTypes => types;
        public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }
        public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }

    private static IEnumerable<string> SourceFiles(string project) =>
        Directory.EnumerateFiles(Path.Combine(SolutionDir, project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static string Relative(string full) => Path.GetRelativePath(SolutionDir, full).Replace('\\', '/');

    private static string SolutionDir { get; } = FindSolutionDir();

    private static string FindSolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BHS.CRG.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Не найден каталог решения (BHS.CRG.slnx) выше " + AppContext.BaseDirectory);
    }
}
