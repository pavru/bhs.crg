using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Activity;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// «Разобран» и очередь «Разобрать» говорят правду (issue #1166, находки ревью PR #1116).
///
/// <para>«Позиция указана» и «позиция есть в справочнике» — разное: внешнего ключа между схемой модуля
/// и справочником ядра нет, и запись можно удалить. Переход проверял только первое.</para>
///
/// <para>Каждый тест заводит СВОЮ запись под удаление: посев общий статический, и удалённая общая
/// позиция уронила бы соседей.</para>
/// </summary>
[Collection("Integration")]
public class InvoiceParsedReferenceTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    /// <summary>
    /// Позицию удалили — «разобран» отказывает и называет строку. До правки переход ПРОХОДИЛ, а ответ
    /// тем же запросом сообщал у строки <c>nomenclatureLost</c>.
    /// </summary>
    [Fact]
    public async Task Разобран_отказывает_пока_позиции_строки_нет_в_справочнике()
    {
        var (client, _) = await SignInAsync("Admin");
        var doomed = await EntryAsync(await TypeAsync(CostsRecordTypes.NomenclatureCode, "Номенклатура"),
            $"Позиция под удаление {Guid.NewGuid().ToString()[..6]}");
        var invoice = await CreateAsync(client, complete: true);
        await AllocatedLinesAsync(client, invoice,
        [
            Line(cable, quantity: 1, price: 100m, rate: 20),
            Line(doomed, quantity: 2, price: 50m, rate: 20),
        ]);

        await ForgetAsync(doomed);

        var response = await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        // Строка названа номером — и только потерянная: первая на месте.
        Assert.Contains("позиции номенклатуры нет в справочнике у строки 2", text);
        Assert.DoesNotContain("строк 1", text);

        Assert.Equal("Черновик", await StateAsync(client, invoice));
        Assert.Equal(0, await ParsedRecordsAsync(invoice));
    }

    /// <summary>
    /// Отказ не тупик: заменили потерянную позицию — переход проходит. Сторож ещё и того, что проверка
    /// не отказывает на всём подряд: тест «отказывает» зелен и у проверки, которая не пускает никого.
    /// </summary>
    [Fact]
    public async Task После_замены_потерянной_позиции_разобран_проходит()
    {
        var (client, _) = await SignInAsync("Admin");
        var doomed = await EntryAsync(await TypeAsync(CostsRecordTypes.NomenclatureCode, "Номенклатура"),
            $"Позиция под удаление {Guid.NewGuid().ToString()[..6]}");
        var invoice = await CreateAsync(client, complete: true);
        var view = await AllocatedLinesAsync(client, invoice, [Line(doomed, quantity: 2, price: 50m, rate: 20)]);

        await ForgetAsync(doomed);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null)).StatusCode);

        // Та же строка (с её «id» — разноска держится за него), другая позиция.
        await LinesAsync(client, invoice, [Line(conduit, quantity: 2, price: 50m, rate: 20, id: LineId(view, 1))]);

        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));
        Assert.Equal("Разобран", await StateAsync(client, invoice));
    }

    /// <summary>
    /// Та же дверь у шапки: «Поставщик» и «Плательщик» — ссылки на записи того же справочника, а
    /// обязательность поля отвечает на «заполнено», а не на «запись на месте».
    /// </summary>
    [Theory]
    [InlineData("Поставщик")]
    [InlineData("Плательщик")]
    public async Task Разобран_отказывает_пока_организации_шапки_нет_в_справочнике(string field)
    {
        var (client, _) = await SignInAsync("Admin");
        var doomed = await EntryAsync(await TypeAsync(CostsRecordTypes.OrganizationCode, "Организация"),
            $"ООО под удаление {Guid.NewGuid().ToString()[..6]}");
        var invoice = await CreateAsync(client, complete: true);
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(client, invoice, field, Reference(doomed)) }));
        await AllocatedLinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m, rate: 20)]);

        await ForgetAsync(doomed);

        var response = await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"организации из поля «{field}» нет в справочнике",
            await response.Content.ReadAsStringAsync());
        Assert.Equal("Черновик", await StateAsync(client, invoice));
    }

    /// <summary>
    /// Потерь две — названы обе: чинить поставщика, чтобы узнать о строке, человек не должен.
    /// </summary>
    [Fact]
    public async Task Отказ_называет_все_потери_разом()
    {
        var (client, _) = await SignInAsync("Admin");
        var organization = await EntryAsync(await TypeAsync(CostsRecordTypes.OrganizationCode, "Организация"),
            $"ООО под удаление {Guid.NewGuid().ToString()[..6]}");
        var position = await EntryAsync(await TypeAsync(CostsRecordTypes.NomenclatureCode, "Номенклатура"),
            $"Позиция под удаление {Guid.NewGuid().ToString()[..6]}");
        var invoice = await CreateAsync(client, complete: true);
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(client, invoice, "Поставщик", Reference(organization)) }));
        await AllocatedLinesAsync(client, invoice, [Line(position, quantity: 1, price: 100m, rate: 20)]);

        await ForgetAsync(organization);
        await ForgetAsync(position);

        var text = await (await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null))
            .Content.ReadAsStringAsync();

        Assert.Contains("организации из поля «Поставщик» нет в справочнике", text);
        Assert.Contains("позиции номенклатуры нет в справочнике у строки 1", text);
    }

    /// <summary>
    /// Отклонённый счёт в очереди «Разобрать» не стоит, сколько бы строк у него ни ждало позиции:
    /// переход «разобран» на нём отвечает «отклонён — разбирать его незачем».
    ///
    /// <para>⚠️ Состояние ставится ЗАПРОСОМ К БАЗЕ: перехода в «отклонён» в API ещё нет. Состояние при
    /// этом объявлено, и правила на него уже опираются (срок оплаты, «разобран») — отбор обязан быть
    /// готов к нему раньше, чем переход появится, иначе дефект приедет вместе с ним и без теста.</para>
    ///
    /// <para>Тот же счёт ДО отклонения в очереди стоит: проверка «его там нет» зелена и у очереди,
    /// которая пуста всегда.</para>
    /// </summary>
    [Fact]
    public async Task Отклонённый_счёт_в_очереди_разобрать_не_стоит()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        await LinesAsync(client, invoice, [Line(null, quantity: 5, price: 20m, text: "Лоток (в справочнике нет)")]);

        Assert.Contains(invoice, await QueueAsync(client));

        using (var scope = host.Services.CreateScope())
        {
            var costs = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
            await costs.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE costs.invoices SET state = {"Rejected"} WHERE id = {invoice}");
        }

        Assert.DoesNotContain(invoice, await QueueAsync(client));

        // А из реестра он никуда не делся — и счётчик «ждут позиции» у него прежний.
        var list = await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices");
        var item = list.EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == invoice);
        Assert.Equal("Отклонён", item.GetProperty("state").GetString());
        Assert.Equal(1, item.GetProperty("linesWithoutNomenclature").GetInt32());
    }

    private static async Task<List<Guid>> QueueAsync(HttpClient client) =>
        [.. (await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices?needsParsing=true"))
            .EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];

    private static async Task<string?> StateAsync(HttpClient client, Guid invoice) =>
        (await ReadAsync(client, invoice)).GetProperty("requisites").GetProperty("Состояние").GetString();

    private async Task<int> ParsedRecordsAsync(Guid invoice)
    {
        using var scope = host.Services.CreateScope();
        var journal = scope.ServiceProvider.GetRequiredService<IActivityLog>();
        var records = await journal.ReadAsync(0, 200, ActivityVisibility.Whole, "costs.invoice.parsed");
        return records.Count(r => r.TargetId == invoice.ToString());
    }
}
