using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Идентификатор новой строки, выданный формой, — <c>newId</c> (issue #1656, change
/// <c>costs-invoice-lines-save-race</c>, D2, [COST-7]).
///
/// <para>Ради чего он: ключ строки на экране с рождения равен её <c>id</c> — фокус не теряется, когда ответ
/// записи приносит идентификатор, — и повтор записи после потерянного ответа не заводит строку второй раз.
/// Неизвестный <c>id</c> при этом остаётся отказом: он значит «строку удалили», а не «строка новая».</para>
/// </summary>
[Collection("Integration")]
public class InvoiceLineNewIdTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    [Fact]
    public async Task Новая_строка_заводится_с_выданным_формой_идентификатором()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        var given = Guid.NewGuid();

        var view = await LinesAsync(client, invoice, [Born(given, quantity: 3), Line(conduit, quantity: 1, price: 5m)]);

        var lines = view.GetProperty("lines");
        Assert.Equal(given, lines[0].GetProperty("id").GetGuid());
        Assert.NotEqual(given, lines[1].GetProperty("id").GetGuid());
    }

    /// <summary>
    /// Повтор того же набора с версией, прочитанной ПОСЛЕ первой записи (клиент перечитал счёт): строка с этим
    /// <c>newId</c> уже лежит и правится на месте, а не заводится второй.
    /// </summary>
    [Fact]
    public async Task Повтор_с_тем_же_newId_не_заводит_дубликата()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        var given = Guid.NewGuid();

        await LinesAsync(client, invoice, [Born(given, quantity: 3)]);
        var again = await LinesAsync(client, invoice, [Born(given, quantity: 7)]);

        var line = Assert.Single(again.GetProperty("lines").EnumerateArray());
        Assert.Equal(given, line.GetProperty("id").GetGuid());
        Assert.Equal(7m, line.GetProperty("quantity").GetDecimal());
    }

    /// <summary>
    /// Повтор с ПРЕЖНЕЙ версией — так повторяет форма, потерявшая ответ, — получает 409 раньше, чем набор дойдёт
    /// до строк (ревью PR #1811): отказ по версии ради <c>newId</c> не ослаблен, и дубликата нет и здесь.
    /// </summary>
    [Fact]
    public async Task Повтор_с_прежней_версией_отказ_409_и_дубликата_нет()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        var seen = (await ReadAsync(client, invoice)).GetProperty("version").GetString()!;
        var given = Guid.NewGuid();

        async Task<HttpResponseMessage> Send()
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/costs/invoices/{invoice}/lines")
            {
                Content = JsonContent.Create(new { lines = new object[] { Born(given, quantity: 3) } }),
            };
            request.Headers.TryAddWithoutValidation(SeenInvoiceVersion.Header, seen);
            return await client.SendAsync(request);
        }

        Assert.Equal(HttpStatusCode.OK, (await Send()).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send()).StatusCode);
        Assert.Equal(1, (await ReadAsync(client, invoice)).GetProperty("lines").GetArrayLength());
    }

    /// <summary>
    /// <c>newId</c> не ослабляет отказ на неизвестный <c>id</c>: строку, которую удалили, форма с прежним
    /// <c>id</c> молча не вернёт.
    /// </summary>
    [Fact]
    public async Task Неизвестный_id_по_прежнему_отказ()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        var given = Guid.NewGuid();
        await LinesAsync(client, invoice, [Born(given, quantity: 3)]);
        await LinesAsync(client, invoice, []);

        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/lines",
            new { lines = new object[] { Line(cable, quantity: 1, price: 1m, id: given) } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("у этого счёта нет", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Идентификатор_строки_другого_счёта_как_newId_отвергается()
    {
        var (client, _) = await SignInAsync("Admin");
        var mine = await CreateAsync(client);
        var other = await CreateAsync(client);
        var theirs = (await LinesAsync(client, other, [Line(cable, quantity: 1, price: 1m)]))
            .GetProperty("lines")[0].GetProperty("id").GetGuid();

        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{mine}/lines",
            new { lines = new object[] { Born(theirs, quantity: 1) } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("уже занят", text);
        // Нейтрально: отказ не подтверждает, где лежит строка с этим идентификатором (ревью PR #1811).
        Assert.DoesNotContain("другого счёта", text);
        Assert.Equal(1, (await ReadAsync(client, other)).GetProperty("lines").GetArrayLength());
    }

    /// <summary>
    /// Две записи в РАЗНЫЕ счета с одним <c>newId</c> разом (ревью PR #1811): сверка занятости идёт под замком
    /// своего счёта, и обе её могут пройти. Вторая обязана отказать словами — 400, а не 500 от ключа таблицы.
    /// </summary>
    [Fact]
    public async Task Один_newId_в_два_счёта_разом_одна_запись_и_отказ_словами()
    {
        var (client, _) = await SignInAsync("Admin");
        var first = await CreateAsync(client);
        var second = await CreateAsync(client);
        var given = Guid.NewGuid();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var responses = await Task.WhenAll(
                client.PutAsJsonAsync($"/api/costs/invoices/{first}/lines", new { lines = new object[] { Born(given, quantity: 1) } }),
                client.PutAsJsonAsync($"/api/costs/invoices/{second}/lines", new { lines = new object[] { Born(given, quantity: 2) } }));

            var codes = responses.Select(r => r.StatusCode).Order().ToList();
            Assert.DoesNotContain(HttpStatusCode.InternalServerError, codes);
            Assert.Contains("уже занят", await responses.Single(r => r.StatusCode == HttpStatusCode.BadRequest)
                .Content.ReadAsStringAsync());

            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
            Assert.Equal(1, await db.InvoiceLines.CountAsync(l => l.Id == given));

            // Следующая попытка — с чистого листа: строки обоих счетов сняты, идентификатор снова свободен.
            await LinesAsync(client, first, []);
            await LinesAsync(client, second, []);
        }
    }

    [Fact]
    public async Task Id_и_newId_вместе_отвергаются()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        var laid = (await LinesAsync(client, invoice, [Line(cable, quantity: 1, price: 1m)]))
            .GetProperty("lines")[0].GetProperty("id").GetGuid();

        var both = Line(cable, quantity: 1, price: 1m, id: laid);
        both["newId"] = Guid.NewGuid().ToString();
        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/lines", new { lines = new object[] { both } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("и «id», и «newId»", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Один_newId_дважды_отвергается()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        var given = Guid.NewGuid();

        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/lines",
            new { lines = new object[] { Born(given, quantity: 1), Born(given, quantity: 2) } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        // Свои слова о «newId» — без совета про «id», которого в запросе нет (ревью PR #1811).
        Assert.Contains("прислан у строк 1 и 2", text);
        Assert.DoesNotContain("без «id»", text);
        Assert.Equal(0, (await ReadAsync(client, invoice)).GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public async Task NewId_равный_id_другой_строки_набора_отвергается()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        var laid = (await LinesAsync(client, invoice, [Line(cable, quantity: 1, price: 1m)]))
            .GetProperty("lines")[0].GetProperty("id").GetGuid();

        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/lines",
            new { lines = new object[] { Line(cable, quantity: 1, price: 1m, id: laid), Born(laid, quantity: 2) } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("прислан у строк 1 и 2", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("не-идентификатор")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Негодный_newId_отвергается(string given)
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        var line = Line(cable, quantity: 1, price: 1m);
        line["newId"] = given;

        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/lines", new { lines = new object[] { line } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("идентификатор новой строки", await response.Content.ReadAsStringAsync());
    }

    private static Dictionary<string, object?> Born(Guid newId, decimal quantity)
    {
        var line = Line(cable, quantity, price: 10m);
        line["newId"] = newId.ToString();
        return line;
    }
}
