using System.Text.Json;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Два справочника не делят записи (issue #1101, ревью PR #1318): роли на одном типе и роли на типе
/// и его предке сохранение схемы не пропускает.
///
/// <para>Потолок носителей считает каждую роль отдельно и такого не видит, а справочник — носитель
/// со всеми потомками: запись входила бы в организации и в номенклатуру сразу.</para>
/// </summary>
public class RoleOverlapTests
{
    private static readonly TagCatalog Tags = TagCatalog.Build(TagRegistry.Core, []);

    private const string Org = """{"tags":["role.organization"],"fields":[]}""";
    private const string Nom = """{"tags":["role.nomenclature"],"fields":[]}""";
    private const string Bare = """{"fields":[]}""";

    [Fact]
    public void Две_роли_на_одном_типе_отказ_названный_один_раз()
    {
        var both = Type("Запись", """{"tags":["role.organization","role.nomenclature"],"fields":[]}""");

        var problem = Assert.Single(RoleResolution.OverlapProblems(Tags, both, []));

        Assert.Contains("не могут стоять на одном типе («Запись»)", problem);
    }

    [Fact]
    public void Роль_предку_носителя_другой_роли_отказ()
    {
        var party = Type("Сторона", Bare);
        var nomenclature = Type("Номенклатура", Nom, party.Id);

        var problem = Assert.Single(
            RoleResolution.OverlapProblems(Tags, party.WithSchema(JsonDocument.Parse(Org)), [party, nomenclature]));

        Assert.Contains("«Номенклатура»", problem);
        Assert.Contains("подтипом «Сторона»", problem);
    }

    [Fact]
    public void Роль_потомку_носителя_другой_роли_отказ()
    {
        var organizations = Type("Организация", Org);
        var child = Type("Поставщик", Nom, organizations.Id);

        Assert.Single(RoleResolution.OverlapProblems(Tags, child, [organizations]));
    }

    /// <summary>
    /// Третья дверь — смена родителя: схему не трогали, а тип между двумя носителями встал в цепочку.
    /// </summary>
    [Fact]
    public void Тип_переставленный_между_двумя_носителями_отказ()
    {
        var organizations = Type("Организация", Org);
        var middle = Type("Средний", Bare, organizations.Id);   // уже с новым родителем
        var nomenclature = Type("Номенклатура", Nom, middle.Id);

        Assert.Single(RoleResolution.OverlapProblems(Tags, middle, [organizations, nomenclature]));
    }

    /// <summary>
    /// Вторая половина: правило, которое отказывает всегда, заперло бы редактор. Раздельные
    /// справочники сохраняются, и чужой тип правится даже при пересечении, уже лежащем в базе.
    /// </summary>
    [Fact]
    public void Раздельные_справочники_и_посторонний_тип_сохраняются()
    {
        var organizations = Type("Организация", Org);
        var nomenclature = Type("Номенклатура", Nom);
        Assert.Empty(RoleResolution.OverlapProblems(Tags, organizations, [organizations, nomenclature]));

        var broken = Type("Поставщик", Nom, organizations.Id);
        var stranger = Type("Акт", Bare);
        Assert.Empty(RoleResolution.OverlapProblems(Tags, stranger, [organizations, broken, stranger]));
    }

    private static DocumentType Type(string code, string schema, Guid? parent = null) =>
        DocumentType.Create(code, code, DocumentTypeKind.Composite, parent, JsonDocument.Parse(schema),
            TypeOwner.Core, TypeVisibility.Shared);
}
