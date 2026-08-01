using System.Text.Json;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Tests.QualityDocs;

/// <summary>
/// Чем опознаётся материал (issue #569). Разбор живого случая: тэг identity носит и «Единица
/// измерения», и пока ключи собирались по всем составным типам, ключом строки становилась единица
/// («шт»). На генерации такая связка подтянула бы один сертификат ко ВСЕМ материалам с этой
/// единицей, а список материалов схлопывался со 151 строки до четырёх.
/// </summary>
public class MaterialIdentityTests
{
    private static DocumentType Composite(string name, string schema)
        => DocumentType.Create(name, name, DocumentTypeKind.Composite, null, JsonDocument.Parse(schema), false);

    private static readonly DocumentType Material = Composite("Материал", """
        { "fields": [
            { "key": "Наименование", "tags": ["identity"] },
            { "key": "Артикул", "tags": ["identity"] },
            { "key": "ДокументПодтверждающийКачество", "tags": ["material.qualityDocLink"] } ] }
        """);

    private static readonly DocumentType Unit = Composite("Единица измерения", """
        { "fields": [ { "key": "ЕдиницаИзмерения", "tags": ["identity"] } ] }
        """);

    private static readonly DocumentType Org = Composite("Наименование организации", """
        { "fields": [ { "key": "Сокращённое", "tags": ["identity"] } ] }
        """);

    [Fact]
    public void KeysOf_TakesOnlyTypesThatCanCarryQualityDoc()
        => Assert.Equal(["Наименование", "Артикул"], MaterialIdentity.KeysOf([Unit, Org, Material]));

    [Fact]
    public void KeysOf_UnitOfMeasureNeverIdentifiesMaterial()
        => Assert.DoesNotContain("ЕдиницаИзмерения", MaterialIdentity.KeysOf([Unit, Org, Material]));

    /// <summary>Порядок типов приходит из БД/API — от него состав ключей зависеть не должен.</summary>
    [Fact]
    public void KeysOf_IsIndependentOfTypeOrder()
        => Assert.Equal(
            MaterialIdentity.KeysOf([Material, Unit, Org]),
            MaterialIdentity.KeysOf([Org, Unit, Material]));

    [Fact]
    public void KeysOf_NoMaterialType_IsEmpty()
        => Assert.Empty(MaterialIdentity.KeysOf([Unit, Org]));

    /// <summary>
    /// Подтип материала — тоже материал: поле документа качества он наследует, а собственное поле
    /// идентичности («ДлинаБарабана» у кабеля) добавляет. Клиент решает это через
    /// resolveEffectiveFields; разойдись семантики — подтип показывался бы на вкладке, но при
    /// генерации не сопоставлялся.
    /// </summary>
    [Fact]
    public void KeysOf_SubtypeInheritsMaterialityAndAddsOwnKeys()
    {
        var cable = Composite("Кабель", """
            { "fields": [ { "key": "МаркаКабеля", "tags": ["identity"] } ] }
            """);
        cable.SetParent(Material.Id);
        var all = new List<DocumentType> { Unit, Org, Material, cable };

        Assert.True(MaterialIdentity.IsMaterial(cable, all));
        Assert.Contains("МаркаКабеля", MaterialIdentity.KeysOf(all));
        Assert.Contains("Наименование", MaterialIdentity.KeysOf(all));
        Assert.DoesNotContain("ЕдиницаИзмерения", MaterialIdentity.KeysOf(all));
    }

    [Fact]
    public void QualityDocFieldOf_FindsTargetFieldByTag()
        => Assert.Equal("ДокументПодтверждающийКачество",
            MaterialIdentity.QualityDocFieldOf([Unit, Org, Material]));
}
