using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

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
    /// Повтор того же набора — ответ первой записи потерялся, форма шлёт снова: строка с этим <c>newId</c> уже
    /// лежит и правится на месте, а не заводится второй.
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
        Assert.Contains("уже занят строкой другого счёта", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, (await ReadAsync(client, other)).GetProperty("lines").GetArrayLength());
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
        Assert.Contains("прислана дважды", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, (await ReadAsync(client, invoice)).GetProperty("lines").GetArrayLength());
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
