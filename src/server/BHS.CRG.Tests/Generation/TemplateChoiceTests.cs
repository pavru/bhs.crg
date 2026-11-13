using System.Text.Json;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Common;
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
        var forward = TemplateChoice.For(Type, templateId, templateIds, candidates);
        var backward = TemplateChoice.For(Type, templateId, templateIds, candidates.Reverse());
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

        var ex = Assert.Throws<ConflictException>(() => TemplateChoice.For(Type, null, Ids(old), [old, current]));
        Assert.Equal(TemplateChoice.NoneSelectedActive, ex.Message);
    }

    [Fact]
    public void Выбор_из_чужих_id_как_выбор_из_погашенных()
    {
        // Ссылку не на шаблон типа запись отвергает (#1960) — сюда она доходит только мимо адреса.
        // Набор выбран, выпускать нечем: тот же отказ, а не молча другой шаблон.
        var a = Tpl("А");
        var foreign = Tpl("Чужой", type: Guid.NewGuid());

        Assert.Throws<ConflictException>(() => TemplateChoice.For(Type, null, Ids(foreign), [a, foreign]));
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
    public void Порядок_списка_шаблоны_по_созданию_версии_по_номеру()
    {
        var a1 = Tpl("А", version: 1, active: false, day: 0);
        var b = Tpl("Б", day: 1);
        var a2 = Tpl("А", version: 2, day: 9);

        var ordered = TemplateChoice.Order([b, a2, a1]);

        Assert.Equal([a1.Id, a2.Id, b.Id], ordered.Select(t => t.Id));
    }
}
