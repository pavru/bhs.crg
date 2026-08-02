using BHS.CRG.Application.QualityDocs;

namespace BHS.CRG.Tests.QualityDocs;

/// <summary>
/// Правдоподобие связки по области продукции (issue #586). Случаи взяты с живых данных: сертификат
/// на автоматы EKF AV-125 держал 68 связок, среди которых термоусаживаемые трубки, светильники,
/// короба, счётчик и клеммные коробки.
///
/// Правила — зеркало клиентского qualityMatch.ts (#552): разойдись они, система предупреждала бы при
/// привязке об одном, а при выпуске о другом.
/// </summary>
public class ProductScopeMatcherTests
{
    /// <summary>Тот самый сертификат.</summary>
    private static readonly string[] Av125 =
    [
        "EKF — автоматические выключатели",
        "Выключатели автоматические, торговой марки «EKF», модель: AV-125. Продукция изготовлена в соответствии с Директивами.",
    ];

    [Fact]
    public void MaterialFromTheCertificateScope_Fits()
        => Assert.Equal(ProductScopeVerdict.Fits,
            ProductScopeMatcher.Assess("Выключатель автоматический AV-125 3P 63А EKF", Av125));

    [Fact]
    public void ForeignMaterial_IsMismatched()
        => Assert.Equal(ProductScopeVerdict.Mismatched,
            ProductScopeMatcher.Assess("Трубка термоусаживаемая ТУТ нг 20/10", Av125));

    /// <summary>
    /// Голый артикул проверять нечем: 58 живых связок из 113 дают ровно ноль, и почти все нули —
    /// именно артикулы. Объявить их «не похожими» значит приучить человека игнорировать сигнал на
    /// самой аккуратной половине данных.
    /// </summary>
    [Fact]
    public void BareArticle_IsUnverifiable()
        => Assert.Equal(ProductScopeVerdict.Unverifiable, ProductScopeMatcher.Assess("mb15-07-01m-54", Av125));

    /// <summary>Нераспознанный документ (название и пустые реквизиты) не даёт судить ни о чём —
    /// иначе «не похоже» получили бы ВСЕ материалы подряд, включая верные.</summary>
    [Fact]
    public void DocumentWithoutWords_IsUnverifiable()
        => Assert.Equal(ProductScopeVerdict.Unverifiable,
            ProductScopeMatcher.Assess("Трубка термоусаживаемая", ["[PDF] Письмо"]));

    /// <summary>Склонения не должны ссорить верную связку: «Кабель …, РЭМЗ» и «РЭМЗ — Кабели силовые».</summary>
    [Fact]
    public void RussianInflections_DoNotBreakAMatch()
        => Assert.Equal(ProductScopeVerdict.Fits, ProductScopeMatcher.Assess(
            "Кабель ВВГнг(А)-FRLS 4х1,5 ГОСТ, РЭМЗ",
            ["Рыбинский электромонтажный завод — Кабели силовые",
             "Кабели силовые с медными жилами ГОСТ 31996-2012"]));

    [Fact]
    public void CollectStrings_TakesNestedRequisites()
    {
        var doc = System.Text.Json.JsonDocument.Parse("""
            { "Продукция": "Выключатели автоматические", "Орган": { "Название": "Эксперт-Сертификация" },
              "Приложения": ["AV-125", 7] }
            """);
        var texts = new List<string>();
        ProductScopeMatcher.CollectStrings(doc.RootElement, texts);
        Assert.Equal(["Выключатели автоматические", "Эксперт-Сертификация", "AV-125"], texts);
    }
}
