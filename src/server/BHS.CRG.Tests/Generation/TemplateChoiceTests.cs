using System.Linq.Expressions;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Templates;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Выбор шаблона документа одним правилом (issue #1401, [ID-3]; change id-debug-bundle-template-choice,
/// задача 1.1). Каждое звено цепочки, и главное — ответ не зависит от порядка, в котором база отдала
/// кандидатов: каждый сценарий прогоняется на прямом и обратном порядке.
/// </summary>
public class TemplateChoiceTests
{
    private static readonly Guid Type = Guid.NewGuid();
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Template Tpl(string name, int version = 1, bool active = true, bool isDefault = false,
        int day = 0, Guid? type = null)
        => Template.Restore(Guid.NewGuid(), type ?? Type, name, "= x", version, active, isDefault,
            T0.AddDays(day), T0.AddDays(day));

    private static string Ids(params Template[] t) => JsonSerializer.Serialize(t.Select(x => x.Id));

    /// <summary>Ответ на прямом и обратном порядке кандидатов — обязан совпасть.</summary>
    private static IReadOnlyList<Template> Choose(Guid? templateId, string? templateIds, params Template[] candidates)
    {
        var forward = TemplateChoice.For(Type, templateId, templateIds, candidates, firstCreated: null);
        var backward = TemplateChoice.For(Type, templateId, templateIds, candidates.Reverse(), firstCreated: null);
        Assert.Equal(forward.Select(t => t.Id), backward.Select(t => t.Id));
        return forward;
    }

    [Fact]
    public void Набор_в_порядке_выбора_только_действующие()
    {
        var a = Tpl("А");
        var b = Tpl("Б", day: 1);
        var oldC = Tpl("В", version: 1, active: false, day: 2);
        var c = Tpl("В", version: 2, day: 3);

        var chosen = Choose(null, Ids(c, oldC, a), a, b, oldC, c);

        Assert.Equal([c.Id, a.Id], chosen.Select(t => t.Id));
    }

    [Fact]
    public void Набор_сильнее_одиночного_пина_и_по_умолчанию()
    {
        var a = Tpl("А", isDefault: true);
        var b = Tpl("Б", day: 1);
        var c = Tpl("В", day: 2);

        Assert.Equal([c.Id], Choose(b.Id, Ids(c), a, b, c).Select(t => t.Id));
    }

    [Fact]
    public void Выбраны_только_погашенные_отказ()
    {
        var old = Tpl("А", version: 1, active: false);
        var current = Tpl("А", version: 2, day: 1);

        var ex = Assert.Throws<ConflictException>(() => TemplateChoice.For(Type, null, Ids(old), [old, current], firstCreated: null));
        Assert.Equal(TemplateChoice.NoneSelectedActive, ex.Message);
    }

    [Fact]
    public void Выбор_из_чужих_id_как_выбор_из_погашенных()
    {
        // Ссылку не на шаблон типа запись отвергает (#1960) — сюда она доходит только мимо адреса.
        // Набор выбран, выпускать нечем: тот же отказ, а не молча другой шаблон.
        var a = Tpl("А");
        var foreign = Tpl("Чужой", type: Guid.NewGuid());

        Assert.Throws<ConflictException>(() => TemplateChoice.For(Type, null, Ids(foreign), [a, foreign], firstCreated: null));
    }

    [Fact]
    public void Нечитаемые_элементы_набора_пропускаются()
    {
        var a = Tpl("А");
        var b = Tpl("Б", day: 1);
        var json = $"[42, null, \"{b.Id:N}\", \"{b.Id}\"]";

        Assert.Equal([b.Id], Choose(null, json, a, b).Select(t => t.Id));
    }

    [Fact]
    public void Одиночный_пин_берёт_и_историческую_версию()
    {
        var v1 = Tpl("А", version: 1, active: false);
        var v2 = Tpl("А", version: 2, isDefault: true, day: 1);

        Assert.Equal([v1.Id], Choose(v1.Id, null, v1, v2).Select(t => t.Id));
    }

    [Fact]
    public void Одиночный_пин_на_шаблон_чужого_типа_не_берётся()
    {
        var a = Tpl("А");
        var b = Tpl("Б", isDefault: true, day: 1);
        var foreign = Tpl("Чужой", type: Guid.NewGuid());

        Assert.Equal([b.Id], Choose(foreign.Id, null, a, b, foreign).Select(t => t.Id));
    }

    [Fact]
    public void Без_выбора_по_умолчанию()
    {
        var a = Tpl("А");
        var b = Tpl("Б", isDefault: true, day: 1);

        Assert.Equal([b.Id], Choose(null, null, a, b).Select(t => t.Id));
    }

    [Fact]
    public void По_умолчанию_только_действующий()
    {
        // Признак у погашенной версии — остаток прежних данных: выпускать ею нельзя.
        var a = Tpl("А", day: 1);
        var oldB = Tpl("Б", version: 1, active: false, isDefault: true);

        Assert.Equal([a.Id], Choose(null, null, a, oldB).Select(t => t.Id));
    }

    [Fact]
    public void Без_выбора_и_по_умолчанию_самый_ранний_шаблон()
    {
        var late = Tpl("А", day: 5);
        var early = Tpl("Я", day: 1);

        Assert.Equal([early.Id], Choose(null, null, late, early).Select(t => t.Id));
    }

    [Fact]
    public void Новая_версия_раннего_шаблона_выбор_не_меняет()
    {
        // Порядок по версиям перекинул бы выбор на «Б»: v2 «А» моложе всех.
        var a1 = Tpl("А", version: 1, active: false, day: 0);
        var b = Tpl("Б", day: 1);
        var a2 = Tpl("А", version: 2, day: 9);

        Assert.Equal([a2.Id], Choose(null, null, a1, b, a2).Select(t => t.Id));
    }

    [Fact]
    public void Равное_создание_по_названию()
    {
        var b = Tpl("Б");
        var a = Tpl("А");

        Assert.Equal([a.Id], Choose(null, null, b, a).Select(t => t.Id));
    }

    [Fact]
    public void Нет_действующих_пусто()
    {
        Assert.Empty(Choose(null, null, Tpl("А", active: false)));
    }

    [Fact]
    public void Порядок_списка_шаблоны_по_созданию_версии_от_новой()
    {
        var a1 = Tpl("А", version: 1, active: false, day: 0);
        var b = Tpl("Б", day: 1);
        var a2 = Tpl("А", version: 2, day: 9);

        var ordered = TemplateChoice.Order([b, a1, a2], firstCreated: null);

        Assert.Equal([a2.Id, a1.Id, b.Id], ordered.Select(t => t.Id));
    }

    [Fact]
    public void Две_действующие_версии_шаблона_берётся_новая()
    {
        // Гонка #1922 или данные до #1921: как у TemplateCurrentVersion — новая, а не первая по номеру.
        var a1 = Tpl("А", version: 1, day: 0);
        var a2 = Tpl("А", version: 2, day: 9);
        var b = Tpl("Б", day: 1);

        Assert.Equal([a2.Id], Choose(null, null, a1, b, a2).Select(t => t.Id));
        Assert.Equal(a2.Id, TemplateChoice.Order([a1, b, a2], firstCreated: null).First(t => t.IsActive).Id);
    }

    [Fact]
    public void Возраст_шаблона_можно_дать_без_исторических_версий()
    {
        // Только действующие версии: «А» v2 моложе «Б», но шаблон «А» создан раньше — его и берут.
        var a2 = Tpl("А", version: 2, day: 9);
        var b = Tpl("Б", day: 1);
        var firstCreated = new Dictionary<string, DateTimeOffset> { ["А"] = T0, ["Б"] = T0.AddDays(1) };

        Assert.Equal(a2.Id, TemplateChoice.Fallback([b, a2], firstCreated)?.Id);
        Assert.Equal(b.Id, TemplateChoice.Fallback([b, a2], firstCreated: null)?.Id);
    }

    // ── Чтение кандидатов из базы (issue #1969) ──────────────────────────────────────────────

    /// <summary>Шаблоны в «базе»: фильтр запроса применяется как есть — видно, что читает выбор.</summary>
    private sealed class Store(params Template[] rows) : IRepository<Template>
    {
        public List<Guid> Read { get; } = [];
        public Task<IReadOnlyList<Template>> FindAsync(Expression<Func<Template, bool>> predicate, CancellationToken ct = default)
        {
            IReadOnlyList<Template> found = rows.Where(predicate.Compile()).ToList();
            Read.AddRange(found.Select(t => t.Id));
            return Task.FromResult(found);
        }
        public Task<Template?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Template>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddAsync(Template entity, CancellationToken ct = default) => throw new NotSupportedException();
        public void Update(Template entity) => throw new NotSupportedException();
        public void Remove(Template entity) => throw new NotSupportedException();
        public Task SaveChangesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>Возраст шаблонов — по ВСЕМ версиям, как у настоящего запроса; считает обращения.</summary>
    private sealed class Ages(params Template[] all) : ITemplateNames
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyDictionary<string, DateTimeOffset>> FirstCreatedAsync(Guid documentTypeId, CancellationToken ct = default)
        {
            Calls++;
            IReadOnlyDictionary<string, DateTimeOffset> ages = all.Where(t => t.DocumentTypeId == documentTypeId)
                .GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.Min(t => t.CreatedAt));
            return Task.FromResult(ages);
        }
        public Task<bool> IsTakenAsync(Guid documentTypeId, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlySet<string>> StartingWithAsync(Guid documentTypeId, string prefix, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> VersionIdsAsync(Guid documentTypeId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static DomainObject Doc(Guid? templateId = null, string? templateIds = null)
        => DomainObject.RestoreDocument(Guid.NewGuid(), Type, null, JsonDocument.Parse("{}"), Guid.NewGuid(),
            T0, T0, null, DocumentStatus.Draft, 0, templateId, templateIds, null, JsonDocument.Parse("{}"));

    [Fact]
    public async Task Закреплённая_историческая_версия_читается_остальные_исторические_нет()
    {
        var a1 = Tpl("А", version: 1, active: false);
        var a2 = Tpl("А", version: 2, active: false, day: 1);
        var a3 = Tpl("А", version: 3, day: 2);
        var store = new Store(a1, a2, a3);
        var ages = new Ages(a1, a2, a3);

        var chosen = await TemplateChoice.ForAsync(Doc(templateId: a1.Id), store, ages, default);

        Assert.Equal([a1.Id], chosen.Select(t => t.Id));
        Assert.DoesNotContain(a2.Id, store.Read);
        // Закрепление нашлось — запасной выбор не нужен, и возраст шаблонов не спрашивается.
        Assert.Equal(0, ages.Calls);
    }

    [Fact]
    public async Task Без_выбора_запасной_по_возрасту_шаблона_а_не_по_прочитанным_версиям()
    {
        // «А» v1 — самая ранняя версия типа, но она не действует и не читается. Без возраста шаблона
        // выбор ушёл бы к «Б»: из прочитанного «А» v2 моложе.
        var a1 = Tpl("А", version: 1, active: false);
        var b = Tpl("Б", day: 1);
        var a2 = Tpl("А", version: 2, day: 9);
        var store = new Store(a1, b, a2);
        var ages = new Ages(a1, b, a2);

        var chosen = await TemplateChoice.RequireAsync(Doc(), store, ages, default);

        Assert.Equal([a2.Id], chosen.Select(t => t.Id));
        Assert.DoesNotContain(a1.Id, store.Read);
        Assert.Equal(1, ages.Calls);
    }

    [Fact]
    public async Task Выбранный_набор_без_запроса_возраста()
    {
        var a = Tpl("А");
        var b = Tpl("Б", day: 1);
        var ages = new Ages(a, b);

        var chosen = await TemplateChoice.ForAsync(Doc(templateIds: Ids(b)), new Store(a, b), ages, default);

        Assert.Equal([b.Id], chosen.Select(t => t.Id));
        Assert.Equal(0, ages.Calls);
    }

    [Fact]
    public async Task Закреплённая_версия_удалена_запасной_выбор()
    {
        // Ссылка ни на что (версию удалили мимо уборки) — как если бы закрепления не было.
        var a = Tpl("А");
        var ages = new Ages(a);

        var chosen = await TemplateChoice.ForAsync(Doc(templateId: Guid.NewGuid()), new Store(a), ages, default);

        Assert.Equal([a.Id], chosen.Select(t => t.Id));
        Assert.Equal(1, ages.Calls);
    }
}
