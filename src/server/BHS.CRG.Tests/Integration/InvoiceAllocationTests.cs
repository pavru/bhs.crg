using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Activity;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Разноска строки по стройкам и баланс (задача F1, issue #1085, ТЗ COST-10, COST-11, COST-13,
/// COST-15) — через адреса модуля, на живой базе.
///
/// <para>Арифметика копеек проверяется отдельно и без базы (<c>AllocationMathTests</c>); здесь — что
/// её результат доезжает до ответа, что «разобран» с остатком не проходит и что правка строк или
/// разноски возвращает разобранный счёт в черновик.</para>
/// </summary>
// ⚠️ Собрание указывается КАЖДОМУ классу: атрибут не наследуется от базового.
[Collection("Integration")]
public class InvoiceAllocationTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    /// <summary>Признак готовности F1: строка на 300 м разносится на три объекта, суммы сходятся до копейки.</summary>
    [Fact]
    public async Task Строка_на_300_м_разносится_на_три_объекта_до_копейки()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        var view = await LinesAsync(client, invoice, [Line(cable, quantity: 300, price: 48.33m)]);
        var line = LineId(view, 1);

        var (a, sections) = await SiteAsync("Комарова 36", "4 эт.", "3 эт.");
        var (b, _) = await SiteAsync("Ливнёвка");

        // Два объекта из трёх — остаток виден.
        view = await AllocateAsync(client, invoice, line, [
            Part(a, quantity: 100, section: sections[0]),
            Part(a, quantity: 100, section: sections[1]),
        ]);

        var allocation = view.GetProperty("lines")[0].GetProperty("allocation");
        Assert.Equal("quantity", allocation.GetProperty("mode").GetString());
        Assert.False(allocation.GetProperty("balanced").GetBoolean());
        Assert.Equal(100m, allocation.GetProperty("unallocatedQuantity").GetDecimal());
        Assert.Equal(4_833m, allocation.GetProperty("unallocatedAmount").GetDecimal());
        Assert.False(view.GetProperty("allocation").GetProperty("allocated").GetBoolean());

        // Третий — баланс сходится, сумма частей равна сумме строки.
        var parts = allocation.GetProperty("parts").EnumerateArray().ToList();
        view = await AllocateAsync(client, invoice, line, [
            Part(a, quantity: 100, section: sections[0], id: parts[0].GetProperty("id").GetGuid()),
            Part(a, quantity: 100, section: sections[1], id: parts[1].GetProperty("id").GetGuid()),
            Part(b, quantity: 100),
        ]);

        allocation = view.GetProperty("lines")[0].GetProperty("allocation");
        Assert.True(allocation.GetProperty("balanced").GetBoolean());
        Assert.Equal(0m, allocation.GetProperty("unallocatedQuantity").GetDecimal());

        var amounts = allocation.GetProperty("parts").EnumerateArray()
            .Select(p => p.GetProperty("amount").GetDecimal()).ToList();
        Assert.Equal(view.GetProperty("lines")[0].GetProperty("amount").GetDecimal(), amounts.Sum());
        Assert.Equal([4_833m, 4_833m, 4_833m], amounts);

        // Части правились на месте — идентификаторы первых двух не сменились.
        Assert.Equal(parts[0].GetProperty("id").GetGuid(),
            allocation.GetProperty("parts")[0].GetProperty("id").GetGuid());

        var first = allocation.GetProperty("parts")[0];
        Assert.Equal("4 эт.", first.GetProperty("sectionName").GetString());
        Assert.StartsWith("Комарова 36", first.GetProperty("constructionName").GetString());
        Assert.True(view.GetProperty("allocation").GetProperty("allocated").GetBoolean());
    }

    /// <summary>Сторож F1: документ с остатком «не разнесено» в «разобран» не переходит.</summary>
    [Fact]
    public async Task Документ_с_остатком_не_переходит_в_разобран()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        var view = await LinesAsync(client, invoice, [
            Line(cable, quantity: 300, price: 10m),
            Line(conduit, quantity: 50, price: 2m),
        ]);
        var (site, _) = await SiteAsync("Стройка");
        await AllocateAsync(client, invoice, LineId(view, 1), [Part(site, quantity: 300)]);
        await AllocateAsync(client, invoice, LineId(view, 2), [Part(site, quantity: 40)]);

        var response = await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("строка 2 разнесена по стройкам не полностью", text);

        var state = (await ReadAsync(client, invoice)).GetProperty("requisites").GetProperty("Состояние");
        Assert.Equal("Черновик", state.GetString());
    }

    [Fact]
    public async Task Расхождение_с_суммой_к_оплате_сверх_допуска_не_даёт_разобран()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}", new
        {
            requisites = await RequisitesWithAsync(client, invoice, "Итого", 150m),
        }));
        await AllocatedLinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m)]);

        var response = await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("расходится с суммой к оплате на 50,00", (await response.Content.ReadAsStringAsync())
            .Replace('.', ','));
    }

    /// <summary>Расхождение в пределах допуска уходит в последнюю часть — и «разобран» проходит.</summary>
    [Fact]
    public async Task Расхождение_в_допуске_уходит_в_последнюю_часть()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}", new
        {
            requisites = await RequisitesWithAsync(client, invoice, "Итого", 100.40m),
        }));
        var view = await AllocatedLinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m)]);

        var part = view.GetProperty("lines")[0].GetProperty("allocation").GetProperty("parts")[0];
        Assert.Equal(100.40m, part.GetProperty("amount").GetDecimal());
        Assert.Equal(0.40m, part.GetProperty("discrepancy").GetDecimal());

        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));
    }

    /// <summary>
    /// Строку уменьшили после разноски — разобранный счёт САМ возвращается в черновик: «разобран» не
    /// может остаться утверждением, переставшим быть правдой.
    /// </summary>
    [Fact]
    public async Task Уменьшили_строку_после_разноски_счёт_возвращается_в_черновик()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        var view = await AllocatedLinesAsync(client, invoice, [Line(cable, quantity: 300, price: 1m)]);
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));

        view = await LinesAsync(client, invoice, [Line(cable, quantity: 250, price: 1m, id: LineId(view, 1))]);

        Assert.Equal("Черновик", view.GetProperty("requisites").GetProperty("Состояние").GetString());
        var allocation = view.GetProperty("lines")[0].GetProperty("allocation");
        Assert.Equal(-50m, allocation.GetProperty("unallocatedQuantity").GetDecimal());
    }

    /// <summary>
    /// Сумму к оплате разобранного счёта увели сверх допуска — счёт тоже возвращается в черновик: с F1
    /// от неё зависит, сходится ли разноска, и правка шапки обязана это заметить, как правка строк.
    /// </summary>
    [Fact]
    public async Task Правка_суммы_к_оплате_сверх_допуска_возвращает_в_черновик()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await AllocatedLinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m)]);
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));

        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}", new
        {
            requisites = await RequisitesWithAsync(client, invoice, "Итого", 150m),
        });
        await OkAsync(response);
        var view = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Черновик", view.GetProperty("requisites").GetProperty("Состояние").GetString());
        Assert.False(view.GetProperty("allocation").GetProperty("allocated").GetBoolean());
    }

    /// <summary>Правка разноски разобранного счёта (ТЗ COST-15) пишется в журнал с прежним и новым распределением.</summary>
    [Fact]
    public async Task Правка_разноски_пишется_в_журнал_и_возвращает_в_черновик()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        var view = await AllocatedLinesAsync(client, invoice, [Line(cable, quantity: 10, price: 1m)]);
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));

        var (other, _) = await SiteAsync("Другая стройка");
        view = await AllocateAsync(client, invoice, LineId(view, 1), [Part(other, quantity: 4)]);
        Assert.Equal("Черновик", view.GetProperty("requisites").GetProperty("Состояние").GetString());

        using var scope = host.Services.CreateScope();
        var journal = scope.ServiceProvider.GetRequiredService<IActivityLog>();
        var records = (await journal.ReadAsync(0, 200, "costs.invoice.allocation"))
            .Where(r => r.TargetId == invoice.ToString())
            .ToList();

        var last = records.First();
        Assert.Contains("10", last.Before);
        Assert.Contains("Другая стройка", last.After);
        Assert.Contains("4", last.After);
    }

    [Fact]
    public async Task Строка_без_количества_разносится_суммой()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        var view = await LinesAsync(client, invoice, [new Dictionary<string, object?>
        {
            ["nomenclature"] = Reference(conduit),
            ["supplierText"] = "Доставка",
            ["amount"] = 1_500m,
        }]);
        var (a, _) = await SiteAsync("А");
        var (b, _) = await SiteAsync("Б");

        view = await AllocateAsync(client, invoice, LineId(view, 1), [Part(a, amount: 1_000m), Part(b, amount: 500m)]);

        var allocation = view.GetProperty("lines")[0].GetProperty("allocation");
        Assert.Equal("amount", allocation.GetProperty("mode").GetString());
        Assert.True(allocation.GetProperty("balanced").GetBoolean());

        // Сумма частей та же 1 500, но одна стройка получила бы больше строки, а другая — минус.
        var response = await AllocateRawAsync(client, invoice, LineId(view, 1),
            [Part(a, amount: 2_000m), Part(b, amount: -500m)]);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Знак части", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("amount", "считается")]
    [InlineData("over", "Разнести больше, чем куплено")]
    [InlineData("foreignSection", "раздела нет у стройки")]
    [InlineData("noSite", "не выбрано, куда")]
    [InlineData("twice", "идут на одну и ту же цель")]
    [InlineData("precise", "точнее тысячной")]
    public async Task Неверная_разноска_отвергается_с_причиной(string kind, string expected)
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        var view = await LinesAsync(client, invoice, [Line(cable, quantity: 10, price: 1m)]);
        var (site, _) = await SiteAsync("Своя");
        var (_, foreign) = await SiteAsync("Чужая", "1 эт.");

        object[] parts = kind switch
        {
            "amount" => [Part(site, quantity: 10, amount: 10m)],
            "over" => [Part(site, quantity: 11)],
            "foreignSection" => [Part(site, quantity: 10, section: foreign[0])],
            "noSite" => [new Dictionary<string, object?> { ["quantity"] = 10 }],
            "twice" => [Part(site, quantity: 5), Part(site, quantity: 5)],
            "precise" => [Part(site, quantity: 1.0005m)],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var response = await AllocateRawAsync(client, invoice, LineId(view, 1), parts);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Стройки_с_разделами_отдаются_модулем()
    {
        var (client, _) = await SignInAsync("Admin");
        var (site, sections) = await SiteAsync("Список", "Б-раздел", "А-раздел");

        var list = await client.GetFromJsonAsync<JsonElement>("/api/costs/constructions");
        var found = list.EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == site);

        Assert.Equal(["А-раздел", "Б-раздел"],
            found.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("name").GetString()));
        Assert.Contains(sections[0], found.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("id").GetGuid()));
    }
}
