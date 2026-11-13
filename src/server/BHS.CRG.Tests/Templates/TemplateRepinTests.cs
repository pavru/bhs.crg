using System.Text.Json;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Templates;

namespace BHS.CRG.Tests.Templates;

/// <summary>
/// <see cref="DomainObject.RepinTemplates" /> — пересадка закреплений на шаблон (id-template-version-integrity,
/// D6, задача 3.1) — и <see cref="TemplateVersionRepair" /> (D8, задача 4.1) без базы. Сверка с SQL
/// миграции — в <c>TemplateVersionIntegrityMigrationTests</c>.
/// </summary>
public class TemplateRepinTests
{
    private static readonly Guid Type = Guid.NewGuid();
    private static readonly Guid V4 = Guid.NewGuid(), V5 = Guid.NewGuid(), Other = Guid.NewGuid();

    private static DomainObject Doc(Guid? single = null, string? ids = null, string? parameters = null)
        => DomainObject.RestoreDocument(Guid.NewGuid(), Type, "Документ", JsonDocument.Parse("{}"), Guid.NewGuid(),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, DocumentStatus.Generated, 0, single, ids, parameters,
            JsonDocument.Parse("{}"));

    private static List<Guid> Ids(DomainObject o) => JsonSerializer.Deserialize<List<Guid>>(o.TemplateIds!)!;

    private static Dictionary<string, JsonElement> Params(DomainObject o)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(o.TemplateParams!)!;

    private static readonly Dictionary<Guid, Guid> V4ToV5 = new() { [V4] = V5 };

    [Fact]
    public void Одиночный_пин_и_его_переопределения_переходят_на_новую()
    {
        var o = Doc(single: V4, parameters: $$$"""{"{{{V4}}}":{"orientation":"landscape"}}""");

        Assert.True(o.RepinTemplates(V4ToV5));

        Assert.Equal(V5, o.TemplateId);
        Assert.Equal("landscape", Params(o)[V5.ToString()].GetProperty("orientation").GetString());
        Assert.False(Params(o).ContainsKey(V4.ToString()));
    }

    [Fact]
    public void Набор_сохраняет_порядок_и_соседей()
    {
        var o = Doc(ids: $"""["{Other}","{V4}"]""");

        o.RepinTemplates(V4ToV5);

        Assert.Equal([Other, V5], Ids(o));
    }

    [Fact]
    public void Повтор_после_замены_убирается_а_переопределения_новой_сильнее()
    {
        var o = Doc(ids: $"""["{V4}","{V5}"]""",
            parameters: $$$"""{"{{{V4}}}":{"a":1},"{{{V5}}}":{"a":2}}""");

        o.RepinTemplates(V4ToV5);

        Assert.Equal([V5], Ids(o));
        Assert.Equal(2, Params(o)[V5.ToString()].GetProperty("a").GetInt32());
        Assert.Single(Params(o));
    }

    [Fact]
    public void Не_закреплённый_за_гаснущей_не_меняется()
    {
        var o = Doc(ids: $"""["{Other}"]""", parameters: $$$"""{"{{{Other}}}":{"a":1}}""");
        var ids = o.TemplateIds;
        var parameters = o.TemplateParams;

        Assert.False(o.RepinTemplates(V4ToV5));

        Assert.Equal(ids, o.TemplateIds);
        Assert.Equal(parameters, o.TemplateParams);
    }

    [Fact]
    public void Разовая_починка_не_трогает_одиночный_пин_и_его_переопределения()
    {
        var o = Doc(single: V4, parameters: $$$"""{"{{{V4}}}":{"a":1}}""");

        Assert.False(o.RepinTemplates(V4ToV5, selectionOnly: true));

        Assert.Equal(V4, o.TemplateId);
        Assert.True(Params(o).ContainsKey(V4.ToString()));
    }

    [Fact]
    public void Выпущенный_файл_выбранной_версии_переходит_вместе_с_выбором()
    {
        // Иначе следующая генерация добавила бы второй PDF того же шаблона, а сборка комплекта
        // поставила бы прежний в конец (ревью PR #1958).
        var o = Doc(ids: $"""["{V4}","{Other}"]""");
        o.AddGeneratedFile(OutputFormat.Pdf, "a.pdf", V4);
        o.AddGeneratedFile(OutputFormat.Pdf, "b.pdf", Other);

        o.RepinTemplates(V4ToV5, selectionOnly: true);

        Assert.Equal([V5, Other], o.GeneratedFiles.Select(f => f.TemplateId!.Value).ToList());
    }

    [Fact]
    public void Файл_новой_версии_уже_есть_прежний_не_трогается()
    {
        var o = Doc(ids: $"""["{V4}","{V5}"]""");
        o.AddGeneratedFile(OutputFormat.Pdf, "a.pdf", V4);
        o.AddGeneratedFile(OutputFormat.Pdf, "b.pdf", V5);

        o.RepinTemplates(V4ToV5);

        Assert.Equal([V4, V5], o.GeneratedFiles.Select(f => f.TemplateId!.Value).ToList());
    }

    // ── TemplateVersionRepair ────────────────────────────────────────────────

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static Template V(int version, bool active, int day, bool isDefault = false, string name = "Основной")
        => Template.Restore(Guid.NewGuid(), Type, name, "x", version, active, isDefault, T0.AddDays(day), T0.AddDays(day));

    [Fact]
    public void Две_действующие_v1_остаётся_поздняя_с_номером_2_и_умолчанием()
    {
        var early = V(1, true, 1, isDefault: true);
        var late = V(1, true, 2);

        var changes = TemplateVersionRepair.Apply([early, late]);

        Assert.False(early.IsActive);
        Assert.False(early.IsDefault);
        Assert.Equal(1, early.Version);
        Assert.True(late.IsActive);
        Assert.True(late.IsDefault);
        Assert.Equal(2, late.Version);
        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.Kind == TemplateVersionRepair.ChangeKind.Retired && c.Template == early);
        Assert.Contains(changes, c => c.Kind == TemplateVersionRepair.ChangeKind.Renumbered && c.ToVersion == 2);
    }

    [Fact]
    public void Чистый_шаблон_и_соседи_по_названию_не_меняются()
    {
        var a = V(1, false, 1);
        var b = V(2, true, 2);
        var other = V(1, true, 3, name: "Краткий");

        Assert.Empty(TemplateVersionRepair.Apply([a, b, other]));
        Assert.True(b.IsActive);
        Assert.True(other.IsActive);
    }

    [Fact]
    public void Погашенная_версия_ведёт_к_действующей_своего_шаблона()
    {
        var map = TemplateVersionRepair.ActiveByRetired([
            (V4, Type, "Основной", false), (V5, Type, "Основной", true), (Other, Type, "Без действующей", false),
        ]);

        Assert.Equal(V5, map[V4]);
        Assert.False(map.ContainsKey(Other));
        Assert.False(map.ContainsKey(V5));
    }
}
