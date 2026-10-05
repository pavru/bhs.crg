using System.Text.Json;
using System.Text.RegularExpressions;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Modules;
using BHS.CRG.Tests.Common;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Условие входа в этап 2: суммы закрыты от того, у кого модуля нет, — и следует это из МЕСТА ХРАНЕНИЯ,
/// а не из полноты перечня путей чтения (задача H1, issue #1104; ТЗ STG-6, CORE-24.2, COST-29).
///
/// <para>Четыре сторожа. Правило ядра: денежный тэг у типа в общей таблице останавливает старт и
/// называет тип. Правило редактора схем: тот же тэг не сохраняется полю типа в общей таблице. Перепись
/// модулей, ведущих деньги: их типы в общей таблице и числовые поля без денежного тэга названы
/// поимённо. Перепись обращений ядра к коду и к схеме модуля.</para>
///
/// <para>⚠️ Чего сторожа НЕ видят, и это сказано, а не подразумевается: сумму, объявленную строкой или
/// числом БЕЗ денежного тэга у модуля, который денежных тэгов не объявляет вовсе, и запрос к схеме
/// модуля, имя которой собрано в коде по частям. Перепись по тексту ловит привычные формы, а не все.</para>
/// </summary>
public class ModuleMoneyStorageTests
{
    private const string Money = "probe.amount";

    // ── 1. Правило ядра: объявления модулей ───────────────────────────────────────────────────────

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
        var refusal = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddAppModules(Enabled(enabled), [], new ProbeModule("id", [], []), Probe(ModuleStorage.SharedObject)));

        Assert.Contains("«ПробныйСчёт»", refusal.Message);
        Assert.Contains("общей таблице", refusal.Message);
        // Совет обязан иметь путь: смена носителя у заведённого типа сама останавливает старт.
        Assert.Contains("миграцией", refusal.Message);
    }

    /// <summary>Состав поставки целиком — тем же списком, что берёт старт, а не одним модулем.</summary>
    [Fact]
    public void Поставка_правило_не_нарушает()
    {
        Assert.True(DeliveredModules.All().Length >= 2, "в составе поставки меньше двух модулей — список читается не тот");
        Assert.Empty(ModuleMoneyStorage.Problems(DeliveredModules.All()));
    }

    // ── 2. Правило редактора схем ─────────────────────────────────────────────────────────────────

    private static readonly TagCatalog Catalog = TagCatalog.Build(TagRegistry.Core,
        [new TagDefinition(Money, "Сумма", "Деньги", TagScope.Field, ["number"], Multiple: false, Owner: "probe", Money: true)]);

    private static DocumentType Saved(string name, TypeStorage storage, string? tag, Guid? parent = null) =>
        DocumentType.Create(name, name, DocumentTypeKind.Document, parent,
            JsonDocument.Parse(tag is null
                ? """{"fields":[]}"""
                : $$"""{"fields":[{"key":"Лимит","type":"number","tags":["{{tag}}"]}]}"""),
            "probe", TypeVisibility.Shared, storage: storage);

    [Fact]
    public void Денежный_тэг_полю_типа_в_общей_таблице_не_сохраняется()
    {
        var type = Saved("Статья", TypeStorage.SharedObject, Money);

        var said = Assert.Single(TagMoneyStorageValidator.Validate(Catalog, type, [type])).Describe();

        Assert.Contains("«Статья»", said);
        Assert.Contains("«Лимит»", said);
    }

    [Fact]
    public void Тот_же_тэг_у_записи_модуля_и_обычный_тэг_в_общей_таблице_проходят()
    {
        var record = Saved("Счёт", TypeStorage.ModuleTable, Money);
        var plain = Saved("Акт", TypeStorage.SharedObject, FunctionalTag.DocNumber);

        Assert.Empty(TagMoneyStorageValidator.Validate(Catalog, record, [record]));
        Assert.Empty(TagMoneyStorageValidator.Validate(Catalog, plain, [plain]));
    }

    /// <summary>Поле наследуется: потомок в общей таблице получает сумму правкой предка.</summary>
    [Fact]
    public void Денежное_поле_предка_названо_у_потомка_в_общей_таблице()
    {
        var parent = Saved("Основа", TypeStorage.ModuleTable, Money);
        var child = Saved("Потомок", TypeStorage.SharedObject, null, parent.Id);

        var said = Assert.Single(TagMoneyStorageValidator.Validate(Catalog, parent, [parent, child])).Describe();

        Assert.Contains("«Потомок»", said);
    }

    /// <summary>
    /// Признак доезжает от объявления модуля до каталога тэгов экземпляра. Потеряйся он в переводе —
    /// тесты выше остались бы зелёными, а редактор сохранял бы сумму в общую таблицу.
    /// </summary>
    [Fact]
    public void Признак_денег_доезжает_до_каталога_тэгов()
    {
        var codes = string.Join(",", DeliveredModules.All().Select(m => m.Code));
        using var services = new ServiceCollection()
            .AddAppModules(Enabled(codes), [], DeliveredModules.All())
            .AddTagCatalog()
            .BuildServiceProvider();
        var catalog = services.GetRequiredService<TagCatalog>();

        var declared = DeliveredModules.All().SelectMany(m => m.Tags).Where(t => t.Money).ToList();
        Assert.NotEmpty(declared);
        Assert.All(declared, t => Assert.True(catalog.Find(t.Code)?.Money, $"тэг «{t.Code}» потерял признак денег по дороге в каталог"));
    }

    // ── 3. Перепись модулей, ведущих деньги ───────────────────────────────────────────────────────

    /// <summary>Типы таких модулей, которым позволено лежать в общей таблице, — и почему.</summary>
    private static readonly Dictionary<string, string> SharedOnPurpose = new()
    {
        ["СтатьяВнеСтроек"] =
            "справочник модуля счетов (F3, issue #1087): одно название, сумм нет; деньги на статью лежат " +
            "долей разноски в таблице модуля. Тип открыт для правки, но денежный тэг его полю не сохранится " +
            "(TagMoneyStorageValidator)",
    };

    /// <summary>Числовые поля типов таких модулей, которые НЕ деньги, — и почему.</summary>
    private static readonly Dictionary<string, string> NotMoney = new()
    {
        ["СчётНаОплату.Отсрочка"] = "срок оплаты в днях (C4), не сумма",
    };

    /// <summary>Модуль ведёт деньги, если объявил хоть один денежный тэг.</summary>
    private static IEnumerable<IAppModule> MoneyModules() => DeliveredModules.All().Where(m => m.Tags.Any(t => t.Money));

    [Fact]
    public void Типы_модуля_с_деньгами_в_общей_таблице_названы_поимённо()
    {
        Assert.NotEmpty(MoneyModules());
        var shared = MoneyModules().SelectMany(m => m.RecordTypes)
            .Where(t => t.Storage == ModuleStorage.SharedObject && !SharedOnPurpose.ContainsKey(t.Code))
            .Select(t => t.Code).ToList();

        Assert.True(shared.Count == 0,
            "Типы модуля, ведущего деньги, в общей таблице объектов: " + string.Join(", ", shared) + ".\n" +
            "Записи такого модуля лежат в его таблицах, под его правами: общую таблицу читают пути, которые " +
            "на права модуля не смотрят. Если тип денег не несёт и в общей таблице он нарочно — впишите его " +
            "в SharedOnPurpose с причиной.");
    }

    /// <summary>
    /// Признак «деньги» ставит автор тэга, и забытый признак правило ядра не поймает. Здесь перечисляет
    /// машина: числовое поле типа такого модуля либо несёт денежный тэг, либо названо «не деньги».
    /// ⚠️ Сумму, объявленную строкой, перепись не видит: отличить её от названия нечем.
    /// </summary>
    [Fact]
    public void Числовое_поле_модуля_с_деньгами_несёт_денежный_тэг()
    {
        var money = DeliveredModules.All().SelectMany(m => m.Tags).Where(t => t.Money).Select(t => t.Code).ToHashSet();

        var unmarked = (from module in MoneyModules()
                        from type in module.RecordTypes
                        from field in type.Fields
                        where field.Type == "number" && !field.Tags.Any(money.Contains)
                        let name = $"{type.Code}.{field.Key}"
                        where !NotMoney.ContainsKey(name)
                        select name).ToList();

        Assert.True(unmarked.Count == 0,
            "Числовые поля без денежного тэга: " + string.Join(", ", unmarked) + ".\n" +
            "Поставьте тэгу поля признак Money либо впишите поле в NotMoney с причиной: без признака " +
            "правило «деньги не в общей таблице» это поле не видит.");
    }

    [Fact]
    public void Исключения_переписи_называют_то_что_есть()
    {
        var types = DeliveredModules.All().SelectMany(m => m.RecordTypes).ToList();
        var fields = types.SelectMany(t => t.Fields.Select(f => $"{t.Code}.{f.Key}")).ToHashSet();

        Assert.DoesNotContain(SharedOnPurpose.Keys, code => types.All(t => t.Code != code));
        Assert.DoesNotContain(NotMoney.Keys, name => !fields.Contains(name));
    }

    // ── 4. Перепись обращений ядра к модулю ───────────────────────────────────────────────────────

    private static readonly string[] CoreProjects =
        ["BHS.CRG.Api", "BHS.CRG.Application", "BHS.CRG.Infrastructure", "BHS.CRG.Domain", "BHS.CRG.Modules", "BHS.CRG.Plugins"];

    /// <summary>
    /// Кому в ядре позволено назвать код модуля по имени — и почему. Исключение — на СТРОКИ подключения
    /// и создания модуля, а не на файл: запрос к схеме модуля не позволен и здесь.
    /// </summary>
    private static readonly Dictionary<string, string> MayNameTheModule = new()
    {
        ["BHS.CRG.Api/Modules/DeliveredModules.cs"] =
            "состав поставки: единственное место, где модуль называют, чтобы отдать его реестру",
    };

    private static readonly Regex NamingLine = new(@"^\s*using\s+[\w.]+;\s*$|\bnew\s+\w+Module\(\)", RegexOptions.Compiled);

    /// <summary>
    /// Ядро не называет код модуля и не пишет запросов к его схеме: данные модуля оно получает только
    /// через контракты (таблицы, порты, отчёт закрытия), где права проверяет сам модуль либо объявление.
    /// Обращение мимо них — чтение сумм без прав модуля, и поймать его можно только перечислением.
    /// </summary>
    [Fact]
    public void Ядро_не_читает_таблицы_модуля_мимо_контрактов()
    {
        var names = Directory.EnumerateDirectories(SourceTree.SolutionDir, "BHS.CRG.Modules.*")
            // Шаблон «BHS.CRG.Modules.*» в Windows находит и сам проект контрактов — он не модуль.
            .Select(Path.GetFileName).Where(n => n != "BHS.CRG.Modules").Select(n => Regex.Escape(n!)).ToList();
        var schemas = DeliveredModules.All().Select(m => m.Schema?.Name).OfType<string>().ToList();
        Assert.NotEmpty(names);
        Assert.NotEmpty(schemas);
        // Каждый проект модуля обязан быть в составе поставки: иначе его схемы в переписи не было бы.
        Assert.True(names.Count <= DeliveredModules.All().Length,
            $"проектов модулей {names.Count}, а в составе поставки модулей {DeliveredModules.All().Length}");

        var byName = new Regex($@"\b({string.Join("|", names)})\b", RegexOptions.Compiled);
        var outsiders = new List<string>();
        foreach (var file in CoreProjects.SelectMany(SourceTree.Files))
        {
            var rel = SourceTree.Relative(file);
            var text = File.ReadAllText(file);
            var named = byName.Matches(text)
                .Where(m => !(MayNameTheModule.ContainsKey(rel) && NamingLine.IsMatch(SourceTree.LineAt(text, m.Index))));
            outsiders.AddRange(named.Concat(SchemaQueries(text, schemas))
                .Select(m => $"{rel}:{SourceTree.LineOf(text, m.Index)} — «{m.Value.Trim()}»"));
        }

        Assert.True(outsiders.Count == 0,
            "Ядро обратилось к модулю мимо контрактов:\n" + string.Join("\n", outsiders) + "\n\n" +
            "Данные модуля ядро читает через объявления (ModuleTable, порты, отчёт закрытия): только там " +
            "у строки есть право, которым она закрыта. Если модуль всё-таки нужно назвать — впишите файл в " +
            "MayNameTheModule с причиной; запрос к схеме модуля не позволен нигде.");
    }

    /// <summary>
    /// Обращения к схеме модуля в тексте. Форм несколько, и каждая встречается в коде ядра у его
    /// собственных таблиц: за ключевым словом, в кавычках (в строке C# — экранированных), вторым в
    /// списке через запятую, в привязке сущности.
    /// </summary>
    private static IEnumerable<Match> SchemaQueries(string text, IReadOnlyList<string> schemas)
    {
        var s = $"({string.Join("|", schemas.Select(Regex.Escape))})";
        // Кавычка идентификатора: в обычной строке C# экранирована (\"), в дословной удвоена ("").
        const string q = @"(\\""|""{1,2})";
        Regex[] forms =
        [
            new($@"\b(FROM|JOIN|INTO|UPDATE|TABLE|TRUNCATE|ONLY)\s+{q}?{s}{q}?\s*\.", RegexOptions.IgnoreCase),
            new($@"{q}{s}{q}\s*\.\s*{q}?\w"),
            new($@"\b(SELECT|FROM|WHERE|DELETE)\b[^\n]*,\s*{s}\.\w+"),
            new($@"\b(ToTable|ToView|HasDefaultSchema)\([^)\n]*""{s}""|\bschema:\s*""{s}"""),
        ];
        return forms.SelectMany(form => form.Matches(text));
    }

    [Theory]
    [InlineData("""db.Database.ExecuteSqlRaw("SELECT total FROM \"costs\".\"invoices\"");""")]
    [InlineData("""var sql = "SELECT 1 FROM costs.invoices";""")]
    [InlineData("""var sql = "SELECT 1 FROM objects o, costs.invoices i";""")]
    [InlineData("""var sql = "TRUNCATE costs.invoices";""")]
    [InlineData("""builder.ToTable("invoices", "costs");""")]
    [InlineData("""builder.ToTable("invoices", schema: "costs");""")]
    [InlineData("""var sql = @"UPDATE ""costs"".""invoices"" SET total = 0";""")]
    public void Перепись_видит_запрос_к_схеме_модуля(string line)
        => Assert.NotEmpty(SchemaQueries(line, ["costs"]));

    /// <summary>Код права, адрес таблицы и обычная проза запросом не считаются — иначе перепись краснела бы на каждом файле.</summary>
    [Theory]
    [InlineData("""["costs.invoice.read", "costs.invoice.pay"],""")]
    [InlineData("""/// <summary>Маркер источника: <c>system:table:costs.invoices</c>.</summary>""")]
    [InlineData("""// права costs.allocation.edit, costs.request.read""")]
    public void Перепись_не_путает_запрос_с_кодом_права_и_адресом_таблицы(string line)
        => Assert.Empty(SchemaQueries(line, ["costs"]));

    [Fact]
    public void Перепись_не_хранит_умерших_записей()
    {
        var dead = MayNameTheModule.Keys.Where(rel => !File.Exists(Path.Combine(SourceTree.SolutionDir, rel))).ToList();
        Assert.True(dead.Count == 0, "В MayNameTheModule названы файлы, которых нет: " + string.Join(", ", dead));
    }

    // ── Помощники ─────────────────────────────────────────────────────────────────────────────────

    private static IConfiguration Enabled(string codes) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:Enabled"] = codes }).Build();

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
}
