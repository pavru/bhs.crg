using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Себестоимость сметы — только с <c>plan.estimate.cost</c> (change <c>plan-06-estimate-model</c>, ТЗ
/// PLAN-11, PLAN-23, D4 зонтика <c>plan-00-epic</c>).
///
/// <para>⚠️ Проверка — обходом JSON ответа ЦЕЛИКОМ, а не по известным ключам: посеянные деньги и
/// сметное наполнение — значения, которых больше нигде нет, и ни одно из них не обязано встретиться
/// в ответе ни под каким ключом. Проверка по ключам пропустила бы поле, добавленное в ответ под новым
/// именем, — ровно тот случай, ради которого право и заведено.</para>
/// </summary>
[Collection(PlanOnlyCollection.Name)]
public class EstimateCostVisibilityTests(PlanOnlyHost host)
{
    // Значения, которых в ответе без права быть не должно ни под каким ключом.
    private const decimal UnitCost = 98765.43m;
    private const decimal Total = 13579.24m;
    private const decimal UnitPrice = 4321.09m;
    private const decimal Amount = 8642.18m;
    private const string Secret = "СЕКРЕТНАЯ-РАСЦЕНКА-ГЭСН-7";

    /// <summary>Ключи, под которыми деньги и наполнение лежат в ответе с правом.</summary>
    private static readonly string[] CostKeys = ["cost", "data", "unitCost", "total", "unitPrice", "amount"];

    [Fact]
    public async Task Без_права_ответы_строк_и_смет_не_несут_денег_и_наполнения()
    {
        var (construction, revision) = await SeedAsync();
        var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead);

        foreach (var url in Urls(construction, revision))
        {
            var raw = await reader.GetStringAsync(url);
            using var doc = JsonDocument.Parse(raw);

            var keys = Keys(doc.RootElement).ToList();
            Assert.DoesNotContain(keys, CostKeys.Contains);
            Assert.DoesNotContain(Numbers(doc.RootElement), n => n is UnitCost or Total or UnitPrice or Amount);
            Assert.DoesNotContain(Secret, raw);

            // Носитель «скрыто правами» с кодом права — без него клиент не отличил бы «скрыто» от «нет».
            var hidden = Assert.Single(doc.RootElement.GetProperty("hidden").EnumerateArray());
            Assert.Equal("cost", hidden.GetProperty("code").GetString());
            Assert.Equal(PlanModule.EstimateCost, hidden.GetProperty("permission").GetString());
        }
    }

    [Fact]
    public async Task С_правом_видны_стоимость_итог_цены_и_наполнение()
    {
        var (construction, revision) = await SeedAsync();
        var estimator = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead, PlanModule.EstimateCost);

        var page = await estimator.GetFromJsonAsync<JsonElement>($"/api/plan/revisions/{revision}/rows");
        Assert.False(page.TryGetProperty("hidden", out _));
        var row = Assert.Single(page.GetProperty("items").EnumerateArray());
        var cost = row.GetProperty("cost");
        Assert.Equal(UnitCost, cost.GetProperty("unitCost").GetDecimal());
        Assert.Equal(Total, cost.GetProperty("total").GetDecimal());
        Assert.Equal(Secret, cost.GetProperty("data").GetProperty("Обоснование").GetString());
        var material = Assert.Single(row.GetProperty("materials").EnumerateArray()).GetProperty("cost");
        Assert.Equal(UnitPrice, material.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(Amount, material.GetProperty("amount").GetDecimal());

        var list = await estimator.GetFromJsonAsync<JsonElement>($"/api/plan/estimates?constructionId={construction}");
        var revisionTotal = Assert.Single(Assert.Single(list.GetProperty("items").EnumerateArray())
            .GetProperty("revisions").EnumerateArray()).GetProperty("cost").GetProperty("total").GetDecimal();
        Assert.Equal(Total, revisionTotal);
    }

    /// <summary>Сторож обязан срабатывать: обход находит посеянные деньги в ответе того, у кого право есть.</summary>
    [Fact]
    public async Task Обход_ответа_находит_деньги_там_где_они_есть()
    {
        var (_, revision) = await SeedAsync();
        var estimator = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead, PlanModule.EstimateCost);

        var raw = await estimator.GetStringAsync($"/api/plan/revisions/{revision}/rows");
        using var doc = JsonDocument.Parse(raw);

        Assert.Contains(Numbers(doc.RootElement), n => n == UnitCost);
        Assert.Contains(Keys(doc.RootElement), k => k == "data");
        Assert.Contains(Secret, raw);
    }

    private async Task<(Guid Construction, Guid Revision)> SeedAsync()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var row = await EstimateSeed.MatchedRowAsync(host, site, 1m) with
        {
            UnitCost = UnitCost,
            Total = Total,
            Data = JsonSerializer.Serialize(new Dictionary<string, object> { ["Обоснование"] = Secret }),
            Materials = [new EstimateMaterialDraft("Кабель", 105m, UnitPrice: UnitPrice, Amount: Amount)],
        };
        return (site.Construction, (await EstimateSeed.RevisionAsync(host, estimate, row)).Id);
    }

    private static IEnumerable<string> Urls(Guid construction, Guid revision)
    {
        yield return $"/api/plan/estimates?constructionId={construction}";
        yield return $"/api/plan/revisions/{revision}/rows";
    }

    private static IEnumerable<string> Keys(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => Keys(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Keys),
        _ => [],
    };

    private static IEnumerable<decimal> Numbers(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => Numbers(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Numbers),
        JsonValueKind.Number => [decimal.Parse(element.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture)],
        _ => [],
    };
}
