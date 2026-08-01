using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Tests.QualityDocs;

/// <summary>
/// Метка материала на связке (issue #554) — снимок человеческого имени на момент привязки.
///
/// Зачем вообще: связка хранит машинный ключ, и у 41 из 113 живых связок это голый артикул
/// (<c>mb15-07-01m-54</c> — боковая панель ВРУ). Именно в артикульной половине сидят неверные
/// связки (#552), то есть без метки экран контроля нечитаем ровно там, где нужен.
/// </summary>
public class MaterialQualityLinkTests
{
    private static MaterialQualityLink Link(string? label = null)
        => MaterialQualityLink.Create(CatalogScope.System, null, "mb15-07-01m-54", Guid.NewGuid(), label);

    [Fact]
    public void Create_StoresLabel()
    {
        Assert.Equal("Боковая панель ВРУ", Link("Боковая панель ВРУ").MaterialLabel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyLabel_IsNotStored(string? label)
    {
        Assert.Null(Link(label).MaterialLabel);
    }

    [Fact]
    public void Label_IsTrimmed()
    {
        Assert.Equal("Боковая панель ВРУ", Link("  Боковая панель ВРУ  ").MaterialLabel);
    }

    /// <summary>
    /// Перепривязка с экрана контроля идёт БЕЗ имени: там его взять неоткуда. Пустая метка не должна
    /// отнимать имя, добытое при первой привязке, — иначе один разбор неверных связок стёр бы ровно
    /// то, ради чего метка заводилась.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DescribeMaterial_WithoutLabel_KeepsExisting(string? label)
    {
        var link = Link("Боковая панель ВРУ");

        link.DescribeMaterial(label);

        Assert.Equal("Боковая панель ВРУ", link.MaterialLabel);
    }

    [Fact]
    public void DescribeMaterial_FillsMissingLabel()
    {
        var link = Link();

        link.DescribeMaterial("Боковая панель ВРУ");

        Assert.Equal("Боковая панель ВРУ", link.MaterialLabel);
    }

    /// <summary>Материал переименовали — метка догоняет: это снимок, а не история версий.</summary>
    [Fact]
    public void DescribeMaterial_ReplacesLabel()
    {
        var link = Link("Старое имя");

        link.DescribeMaterial("Новое имя");

        Assert.Equal("Новое имя", link.MaterialLabel);
    }

    /// <summary>Та же метка — не изменение: не трогаем UpdatedAt на ровном месте.</summary>
    [Fact]
    public void DescribeMaterial_SameLabel_DoesNotTouchUpdatedAt()
    {
        var link = Link("Боковая панель ВРУ");
        var before = link.UpdatedAt;

        link.DescribeMaterial("Боковая панель ВРУ");

        Assert.Equal(before, link.UpdatedAt);
    }

    /// <summary>Перенацеливание метку не трогает: сменился документ, а материал тот же.</summary>
    [Fact]
    public void Retarget_KeepsLabel()
    {
        var link = Link("Боковая панель ВРУ");

        link.Retarget(Guid.NewGuid());

        Assert.Equal("Боковая панель ВРУ", link.MaterialLabel);
    }

    /// <summary>
    /// Легаси-маркер ссылки в метку не попадает. Составные поля когда-то хранились строкой «🔗 …»,
    /// и на рабочей базе он оказался у ВСЕХ 113 меток — имя склеивается из полей идентичности, одно
    /// из которых приносит этот значок в каждую.
    /// </summary>
    [Fact]
    public void LegacyRefMarker_IsStrippedFromLabel()
    {
        var link = MaterialQualityLink.Create(CatalogScope.System, null, "шт", Guid.NewGuid());
        link.DescribeMaterial("🔗 шт · Панель монтажная EKF 710х480");

        Assert.Equal("шт · Панель монтажная EKF 710х480", link.MaterialLabel);
    }

    /// <summary>
    /// Метка обрезается до предела колонки (512). Она склеивается из ВСЕХ полей идентичности и
    /// систематически длиннее ключа; переполнение уронило бы весь пакет привязки на 22001 —
    /// несоразмерная плата за декоративный снимок.
    /// </summary>
    [Fact]
    public void OverlongLabel_IsTruncatedToColumnLimit()
    {
        var link = Link(new string('я', 900));

        Assert.NotNull(link.MaterialLabel);
        Assert.Equal(MaterialQualityLink.MaxLabelLength, link.MaterialLabel!.Length);
        Assert.EndsWith("…", link.MaterialLabel);
    }

    [Fact]
    public void LabelAtTheLimit_IsKeptWhole()
    {
        var label = new string('я', MaterialQualityLink.MaxLabelLength);

        Assert.Equal(label, Link(label).MaterialLabel);
    }
}
