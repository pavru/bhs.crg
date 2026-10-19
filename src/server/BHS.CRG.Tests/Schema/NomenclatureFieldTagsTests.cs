using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;

namespace BHS.CRG.Tests.Schema;

/// <summary>
/// Тэги артикула и единицы справочника номенклатуры (issue #1512, эпик #1511): ядро узнаёт поля по
/// тэгу, а не по названию, — тем же расчётом, что порт модулей и окно «Новая позиция».
/// </summary>
public class NomenclatureFieldTagsTests
{
    private static readonly TagCatalog Tags = TagCatalog.Build(TagRegistry.Core, []);
    private static readonly TagDefinition Article = Tags.Find(FunctionalTag.NomenclatureArticle)!;
    private static readonly TagDefinition Unit = Tags.Find(FunctionalTag.NomenclatureUnit)!;

    [Fact]
    public void Реестр_тэги_поля_номенклатуры_без_параметров_по_одному_на_тип()
    {
        foreach (var tag in (TagDefinition[])[Article, Unit])
        {
            Assert.Equal(TagScope.Field, tag.Scope);
            Assert.Equal(FunctionalTag.RoleNomenclature, tag.Role);
            Assert.Null(tag.Parameter);
            Assert.False(tag.Multiple);
        }
        // Единица у номенклатуры на живой базе — ССЫЛКА на справочник единиц: тэг обязан ей ставиться.
        Assert.Contains("complex", Unit.AppliesTo);
        Assert.DoesNotContain("complex", Article.AppliesTo);
    }

    [Fact]
    public void Поле_на_тэге_находится_и_ссылка_единицы_годится()
    {
        var type = Nomenclature(
            Field("Наименование", "string"),
            Field("КодИзготовителя", "string", FunctionalTag.NomenclatureArticle),
            Field("ЕдиницаИзмерения", "complex", FunctionalTag.NomenclatureUnit));
        var role = RoleResolution.Role(FunctionalTag.RoleNomenclature, [type]);

        Assert.Equal("КодИзготовителя", RoleResolution.Field(role, Article, [type]).Key);
        Assert.Equal("ЕдиницаИзмерения", RoleResolution.Field(role, Unit, [type]).Key);
    }

    /// <summary>Тэг на двух полях — «не сопоставлено», а не первое попавшееся.</summary>
    [Fact]
    public void Тэг_на_двух_полях_не_сопоставлен_ни_одному()
    {
        var type = Nomenclature(
            Field("Артикул", "string", FunctionalTag.NomenclatureArticle),
            Field("АртикулСтарый", "string", FunctionalTag.NomenclatureArticle));
        var role = RoleResolution.Role(FunctionalTag.RoleNomenclature, [type]);

        var field = RoleResolution.Field(role, Article, [type]);

        Assert.Null(field.Key);
        Assert.Equal(RoleFieldFault.Several, field.Fault);
    }

    [Fact]
    public void Артикул_на_ссылке_не_того_вида()
    {
        var type = Nomenclature(Field("Артикул", "complex", FunctionalTag.NomenclatureArticle));
        var role = RoleResolution.Role(FunctionalTag.RoleNomenclature, [type]);

        Assert.Equal(RoleFieldFault.WrongKind, RoleResolution.Field(role, Article, [type]).Fault);
    }

    /// <summary>Окно «Новая позиция» получает тэг у поля — и только у сопоставленного.</summary>
    [Fact]
    public void Описание_вида_несёт_тэг_поля()
    {
        var type = Nomenclature(
            """{"key":"Наименование","type":"string","title":"Наименование","tags":["identity:1"]}""",
            """{"key":"Артикул","type":"string","title":"Артикул","tags":["identity:2"]}""");
        var types = new Dictionary<Guid, DocumentType> { [type.Id] = type };

        var kind = NomenclatureIntakeLayout.Describe(
            type, types, new Dictionary<string, string> { ["Артикул"] = FunctionalTag.NomenclatureArticle });

        Assert.Equal(FunctionalTag.NomenclatureArticle, kind.Fields.Single(f => f.Key == "Артикул").Tag);
        Assert.Null(kind.Fields.Single(f => f.Key == "Наименование").Tag);
        Assert.All(NomenclatureIntakeLayout.Describe(type, types).Fields, f => Assert.Null(f.Tag));
    }

    private static string Field(string key, string kind, string? tag = null) =>
        $$"""{"key":"{{key}}","type":"{{kind}}","title":"{{key}}"{{(tag is null ? "" : $$""","tags":["{{tag}}"]""")}}}""";

    private static DocumentType Nomenclature(params string[] fields) =>
        DocumentType.Create("Номенклатура", "Номенклатура", DocumentTypeKind.Composite, null,
            JsonDocument.Parse($$"""{"tags":["{{FunctionalTag.RoleNomenclature}}"],"fields":[{{string.Join(",", fields)}}]}"""),
            TypeOwner.Core, TypeVisibility.Shared);
}
