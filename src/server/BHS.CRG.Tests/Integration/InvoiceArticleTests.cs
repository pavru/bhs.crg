using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Статьи вне строек (задача F3, issue #1087, ТЗ COST-10.1): счёт «на склад» разносится на статью, а не на
/// выдуманную стройку; цель части — стройка или статья, ровно одно.
/// </summary>
// ⚠️ Собрание указывается КАЖДОМУ классу: атрибут не наследуется от базового.
[Collection("Integration")]
public class InvoiceArticleTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    [Fact]
    public async Task Счёт_на_склад_разносится_на_статью_заведённую_бухгалтером()
    {
        // Статью заводит бухгалтер — у него право справочника, но нет ни разноски, ни core.catalog.edit.
        var (accountant, _) = await SignInAsync("Accountant");
        var article = await ArticleAsync(accountant, "Склад");

        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        var view = await LinesAsync(client, invoice, [Line(cable, quantity: 10, price: 5m)]);
        view = await AllocateAsync(client, invoice, LineId(view, 1), [ArticlePart(article.Id, quantity: 10)]);

        var part = view.GetProperty("lines")[0].GetProperty("allocation").GetProperty("parts")[0];
        Assert.Equal(article.Id, part.GetProperty("articleId").GetGuid());
        Assert.Equal(article.Name, part.GetProperty("articleName").GetString());
        Assert.Equal(JsonValueKind.Null, part.GetProperty("constructionId").ValueKind);
        Assert.False(part.GetProperty("targetLost").GetBoolean());
        Assert.True(view.GetProperty("allocation").GetProperty("allocated").GetBoolean());
    }

    [Fact]
    public async Task Статья_не_появляется_там_где_выбирают_стройку()
    {
        var (client, _) = await SignInAsync("Admin");
        var article = await ArticleAsync(client, "Общие расходы");

        // Выбор стройки у разноски и список строек ядра — статьи нет ни там, ни там: «Склад» стройкой не
        // заводится, иначе всплыл бы в назначениях, сметах и комплектах (ТЗ COST-10.1).
        foreach (var address in (string[])["/api/costs/constructions", "/api/constructions"])
        {
            var text = await client.GetStringAsync(address);
            Assert.DoesNotContain(article.Id.ToString(), text);
            Assert.DoesNotContain(article.Name, text);
        }

        var articles = await client.GetFromJsonAsync<JsonElement>("/api/costs/articles");
        Assert.Contains(articles.EnumerateArray(), a => a.GetProperty("id").GetGuid() == article.Id);
    }

    [Theory]
    [InlineData("both", "и стройка, и статья")]
    [InlineData("sectionOfArticle", "раздел указан у статьи")]
    [InlineData("unknown", "такой статьи вне строек нет")]
    public async Task Часть_с_двумя_целями_или_чужой_статьёй_отвергается(string kind, string expected)
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        var view = await LinesAsync(client, invoice, [Line(cable, quantity: 10, price: 1m)]);
        var (site, sections) = await SiteAsync("Своя", "1 эт.");
        var article = await ArticleAsync(client, "Склад");

        var part = kind switch
        {
            "both" => With(Part(site, quantity: 10), "article", article.Id),
            "sectionOfArticle" => With(ArticlePart(article.Id, quantity: 10), "section", sections[0]),
            "unknown" => ArticlePart(Guid.NewGuid(), quantity: 10),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var response = await AllocateRawAsync(client, invoice, LineId(view, 1), [part]);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Ограничение базы — второй рубеж под разбором: часть с двумя целями или без цели не ложится, даже если
    /// код однажды пропустит её мимо разбора.
    /// </summary>
    [Theory]
    [InlineData("both")]
    [InlineData("none")]
    [InlineData("sectionOfArticle")]
    public async Task База_не_принимает_часть_без_цели_или_с_двумя(string kind)
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        var view = await LinesAsync(client, invoice, [Line(cable, quantity: 10, price: 1m)]);

        var target = kind switch
        {
            "both" => new AllocationTarget(Guid.NewGuid(), null, Guid.NewGuid()),
            "none" => default,
            "sectionOfArticle" => new AllocationTarget(null, Guid.NewGuid(), Guid.NewGuid()),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var part = InvoiceAllocation.Create(invoice, LineId(view, 1));
        part.Apply(1, new AllocationValues(target, 10m, null));
        db.InvoiceAllocations.Add(part);

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("ck_invoice_allocations_one_target", error.InnerException?.Message);
    }

    [Fact]
    public async Task Поровну_между_стройкой_и_статьёй_предпросмотр_равен_записанному()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await LinesAsync(client, invoice, [Line(cable, quantity: 11, price: 1m)]);
        var (site, _) = await SiteAsync("Объект");
        var article = await ArticleAsync(client, "Склад");

        var response = await client.PostAsJsonAsync($"/api/costs/invoices/{invoice}/allocation/preview", new
        {
            method = "equal",
            targets = new object[] { new { construction = site }, new { article = article.Id } },
        });
        await OkAsync(response);
        var preview = await response.Content.ReadFromJsonAsync<JsonElement>();

        var apply = preview.GetProperty("apply");
        var remainder = preview.GetProperty("remainders")[0];
        Assert.Equal(article.Id, remainder.GetProperty("article").GetGuid());

        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(apply.GetRawText())!;
        body["stamp"] = JsonSerializer.SerializeToElement(
            (await ReadAsync(client, invoice)).GetProperty("allocation").GetProperty("stamp").GetString());
        var put = await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/allocation", body);
        await OkAsync(put);
        var view = await put.Content.ReadFromJsonAsync<JsonElement>();

        var parts = view.GetProperty("lines")[0].GetProperty("allocation").GetProperty("parts");
        Assert.Equal([5m, 6m], parts.EnumerateArray().Select(p => p.GetProperty("quantity").GetDecimal()));
        Assert.Equal(article.Id, parts[1].GetProperty("articleId").GetGuid());
    }

    [Fact]
    public async Task Занятую_статью_убрать_нельзя_свободную_можно()
    {
        var (client, _) = await SignInAsync("Admin");
        var article = await ArticleAsync(client, "Склад");
        var invoice = await CreateAsync(client);
        var view = await LinesAsync(client, invoice, [Line(cable, quantity: 10, price: 1m)]);
        await AllocateAsync(client, invoice, LineId(view, 1), [ArticlePart(article.Id, quantity: 4)]);

        var refused = await client.DeleteAsync($"/api/costs/articles/{article.Id}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("разнесено частей: 1", await refused.Content.ReadAsStringAsync());

        await AllocateAsync(client, invoice, LineId(view, 1), []);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/costs/articles/{article.Id}")).StatusCode);
    }

    [Fact]
    public async Task Переименование_и_повтор_названия()
    {
        var (client, _) = await SignInAsync("Admin");
        var first = await ArticleAsync(client, "Склад");
        var second = await ArticleAsync(client, "Общие");

        var renamed = await client.PutAsJsonAsync($"/api/costs/articles/{second.Id}", new { name = second.Name + " расходы" });
        await OkAsync(renamed);

        var twin = await client.PostAsJsonAsync("/api/costs/articles", new { name = first.Name.ToUpperInvariant() });
        Assert.Equal(HttpStatusCode.Conflict, twin.StatusCode);
        Assert.Contains("уже есть", await twin.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Узкое право не становится широким: дверь статей не правит запись другого справочника — организацию —
    /// даже тому, у кого правка справочника статей есть.
    /// </summary>
    [Fact]
    public async Task Дверь_статей_не_правит_чужой_справочник()
    {
        var (accountant, _) = await SignInAsync("Accountant");

        var rename = await accountant.PutAsJsonAsync($"/api/costs/articles/{supplier}", new { name = "Взлом" });
        Assert.Equal(HttpStatusCode.NotFound, rename.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await accountant.DeleteAsync($"/api/costs/articles/{supplier}")).StatusCode);
    }

    /// <summary>
    /// То же — на уровне ПОРТА, а не двери: дверь статей и сама отвечает «такой статьи нет», и тест выше
    /// прошёл бы, даже если порт правил бы что угодно. А порт — это то, что достанется следующему справочнику
    /// следующего модуля, уже без проверки в двери.
    /// </summary>
    [Fact]
    public async Task Порт_своих_справочников_не_трогает_запись_чужого_типа()
    {
        using var scope = host.Services.CreateScope();
        var own = scope.ServiceProvider.GetRequiredService<BHS.CRG.Modules.Ports.IModuleOwnCatalog>();

        Assert.Null(await own.RenameAsync(BHS.CRG.Modules.Costs.CostsRecordTypes.ArticleCode, supplier, "Взлом"));
        Assert.False(await own.DeleteAsync(BHS.CRG.Modules.Costs.CostsRecordTypes.ArticleCode, supplier));
    }

    [Theory]
    [InlineData("User")]
    [InlineData("Supplier")]
    public async Task Без_права_справочника_статью_не_завести(string role)
    {
        var (client, _) = await SignInAsync(role);
        var response = await client.PostAsJsonAsync("/api/costs/articles", new { name = $"Склад {Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

}
