using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Objects;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Endpoints;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Список соответствий наименований (задача C3, issue #1079, ТЗ COST-7.1): прочитать, направить на
/// другую позицию, забыть — и то, ради чего он заведён: соответствие держит поставщика и позицию от
/// удаления, а список — то, чем держателя снимают.
///
/// <para>У каждого теста свой поставщик и свои позиции: база у класса общая с соседями.</para>
/// </summary>
[Collection("Integration")]
public class SupplierMatchListTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private readonly InvoiceLineHost host = host;

    [Fact]
    public async Task Список_отбирает_по_поставщику_и_словам_бумаги_и_называет_сколько_всего()
    {
        var (client, _) = await SignInAsync("Supplier");
        // Порядок по названию обязан РАЗОЙТИСЬ с порядком по идентификатору, иначе проверка ниже
        // проходила бы и при сортировке по идентификатору (проверено поломкой: так и было). Идентификаторы
        // случайны, поэтому «А» заводим, пока его идентификатор не окажется БОЛЬШЕ, чем у «Я».
        var vendor = await VendorAsync("Я-поставщик");
        var other = await VendorAsync("А-поставщик");
        while (other.CompareTo(vendor) < 0) other = await VendorAsync("А-поставщик");
        var position = await PositionAsync();
        await RememberAsync(client, vendor, position, "Кабель силовой 3х2,5", "Гофра 20", "Муфта 100%");
        await RememberAsync(client, other, position, "Кабель силовой 3х2,5");

        var all = await ListAsync(client, $"supplierId={vendor}");
        Assert.Equal(3, all.GetProperty("total").GetInt32());
        // По наименованию у поставщика — так человек ищет глазами.
        Assert.Equal(["Гофра 20", "Кабель силовой 3х2,5", "Муфта 100%"],
            all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("source").GetString()));
        Assert.All(all.GetProperty("items").EnumerateArray(), item =>
        {
            Assert.Equal(vendor, item.GetProperty("supplierId").GetGuid());
            Assert.Equal("name", item.GetProperty("by").GetString());
            Assert.Equal(position, item.GetProperty("nomenclatureId").GetGuid());
            Assert.False(string.IsNullOrEmpty(item.GetProperty("version").GetString()));
            Assert.Equal(JsonValueKind.Null, item.GetProperty("issue").ValueKind);
        });

        // Поставщики отбора — все, у кого есть соответствия, а не только отобранный: иначе к другому не перейти.
        var suppliers = (await client.GetFromJsonAsync<JsonElement>("/api/costs/supplier-matches/suppliers"))
            .EnumerateArray().ToDictionary(s => s.GetProperty("id").GetGuid(), s => s.GetProperty("count").GetInt32());
        Assert.Equal(3, suppliers[vendor]);
        Assert.Equal(1, suppliers[other]);

        // Поиск — по словам бумаги, без учёта регистра; знак процента — буква, а не маска.
        var found = await ListAsync(client, $"supplierId={vendor}&query=КАБЕЛЬ");
        Assert.Equal("Кабель силовой 3х2,5", Assert.Single(found.GetProperty("items").EnumerateArray()).GetProperty("source").GetString());
        Assert.Equal(1, (await ListAsync(client, $"supplierId={vendor}&query=100%25")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await ListAsync(client, $"supplierId={vendor}&query=%25")).GetProperty("total").GetInt32());

        // Без отбора по поставщику блоки идут по НАЗВАНИЮ поставщика — как в выпадающем списке, а не по
        // идентификатору: иначе не понять, в какой порции искать нужного.
        var mixed = (await ListAsync(client, "query=" + Uri.EscapeDataString("Кабель силовой 3х2,5") + "&take=200"))
            .GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("supplierId").GetGuid() is var id && (id == vendor || id == other))
            .Select(i => i.GetProperty("supplierId").GetGuid()).ToList();
        Assert.Equal([other, vendor], mixed);

        // Порция меньше списка — и число «из» остаётся полным.
        var page = await ListAsync(client, $"supplierId={vendor}&skip=1&take=1");
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal("Кабель силовой 3х2,5", Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("source").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/costs/supplier-matches?issue=странное")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/costs/supplier-matches?take=100000")).StatusCode);
    }

    [Fact]
    public async Task Соответствие_с_архивной_позицией_названо_сосчитано_и_отбирается()
    {
        var (client, _) = await SignInAsync("Supplier");
        var vendor = await VendorAsync();
        var (alive, archived) = (await PositionAsync(), await PositionAsync());
        await RememberAsync(client, vendor, alive, "Лоток 100");
        await RememberAsync(client, vendor, archived, "Лоток 200");
        await ArchiveAsync(archived);

        var all = await ListAsync(client, $"supplierId={vendor}");
        Assert.Equal(2, all.GetProperty("total").GetInt32());
        Assert.Equal(1, all.GetProperty("counts").GetProperty("archived").GetInt32());
        Assert.Equal(0, all.GetProperty("counts").GetProperty("lost").GetInt32());

        var only = await ListAsync(client, $"supplierId={vendor}&issue=archived");
        var item = Assert.Single(only.GetProperty("items").EnumerateArray());
        Assert.Equal("Лоток 200", item.GetProperty("source").GetString());
        Assert.Equal("archived", item.GetProperty("issue").GetString());
        // Числа чипов считаются ДО отбора по состоянию: нажатый чип не обнуляет соседний.
        Assert.Equal(1, only.GetProperty("counts").GetProperty("archived").GetInt32());
    }

    [Fact]
    public async Task Смена_позиции_требует_виденной_версии_пишет_журнал_и_меняет_подстановку()
    {
        var (client, _) = await SignInAsync("Supplier");
        var vendor = await VendorAsync();
        var (before, after, archived) = (await PositionAsync(), await PositionAsync(), await PositionAsync());
        await ArchiveAsync(archived);

        // Строка счёта, подставленная по соответствию, — она узнает о смене.
        var match = Assert.Single(await RememberAsync(client, vendor, before, "Щит ЩРН-12"));
        var id = match.GetProperty("id").GetGuid();
        var version = match.GetProperty("version").GetString()!;
        var invoice = await InvoiceAsync(client, vendor);
        var marked = Line(before, 1, 5m, text: "Щит ЩРН-12");
        marked["matchedBy"] = id.ToString();
        await LinesAsync(client, invoice, [marked]);

        // Без версии — отказ: правка обязана сказать, какое состояние списка человек видел.
        Assert.Equal(HttpStatusCode.BadRequest, (await PointAsync(client, id, after, version: null)).StatusCode);
        // Архивная позиция — не цель: она не подставляется.
        var refused = await PointAsync(client, id, archived, version);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("в архиве", await refused.Content.ReadAsStringAsync());

        var journal = await CountAsync(InvoiceActions.MatchPointed);
        var changed = await PointAsync(client, id, after, version);
        await OkAsync(changed);
        var view = await changed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(after, view.GetProperty("nomenclatureId").GetGuid());
        Assert.NotEqual(version, view.GetProperty("version").GetString());
        Assert.Equal(journal + 1, await CountAsync(InvoiceActions.MatchPointed));

        // Прежняя версия больше не годится: вторая правка «по старому списку» чужую не затрёт.
        Assert.Equal(HttpStatusCode.Conflict, (await PointAsync(client, id, before, version)).StatusCode);

        // Следующий счёт получит новую позицию, а строка прежнего счёта — узнает, что соответствие сменили.
        var offered = Assert.Single(await SuggestAsync(client, vendor, "Щит ЩРН-12"));
        Assert.Equal(after, offered.GetProperty("nomenclatureId").GetGuid());
        var line = (await ReadAsync(client, invoice)).GetProperty("lines")[0];
        Assert.Equal(before, line.GetProperty("nomenclatureId").GetGuid());
        Assert.Equal("changed", line.GetProperty("match").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Забытое_соответствие_больше_не_подставляется_а_строки_счетов_остаются()
    {
        var (client, _) = await SignInAsync("Supplier");
        var vendor = await VendorAsync();
        var position = await PositionAsync();
        var match = Assert.Single(await RememberAsync(client, vendor, position, "Короб 40х25"));
        var id = match.GetProperty("id").GetGuid();
        var version = match.GetProperty("version").GetString()!;

        var invoice = await InvoiceAsync(client, vendor);
        var marked = Line(position, 1, 5m, text: "Короб 40х25");
        marked["matchedBy"] = id.ToString();
        await LinesAsync(client, invoice, [marked]);

        Assert.Equal(HttpStatusCode.BadRequest, (await ForgetAsync(client, id, version: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ForgetAsync(client, id, version: "1")).StatusCode);

        var journal = await CountAsync(InvoiceActions.MatchForgotten);
        Assert.Equal(HttpStatusCode.NoContent, (await ForgetAsync(client, id, version)).StatusCode);
        Assert.Equal(journal + 1, await CountAsync(InvoiceActions.MatchForgotten));

        Assert.Empty(await SuggestAsync(client, vendor, "Короб 40х25"));
        Assert.Equal(0, (await ListAsync(client, $"supplierId={vendor}")).GetProperty("total").GetInt32());
        // Позиция в счёте осталась, а пометка говорит, что соответствия больше нет.
        var line = (await ReadAsync(client, invoice)).GetProperty("lines")[0];
        Assert.Equal(position, line.GetProperty("nomenclatureId").GetGuid());
        Assert.Equal("gone", line.GetProperty("match").GetProperty("state").GetString());

        // Повтор — «такого нет», а не успех: список у человека устарел.
        Assert.Equal(HttpStatusCode.NotFound, (await ForgetAsync(client, id, version)).StatusCode);
    }

    /// <summary>
    /// СТОРОЖ РЕШЕНИЯ (владелец продукта, 09.10.2026). Соответствие держит и позицию, и поставщика; отказ
    /// называет, кто держит и где это снять. «Забыть» — снимает: иначе правило значило бы «не удаляется
    /// никогда», и ради этого колонки до появления списка были «помнящими».
    /// </summary>
    [Fact]
    public async Task Соответствие_держит_поставщика_и_позицию_пока_его_не_забудут()
    {
        var (client, _) = await SignInAsync("Admin");
        var vendor = await VendorAsync();
        var position = await PositionAsync();

        // Счёт, запомнивший выбор, убираем из держателей: строку — правкой, поставщика — сменой.
        var invoice = await InvoiceAsync(client, vendor);
        await LinesAsync(client, invoice, [Line(position, 1, 5m, text: "Хомут 200")]);
        await LinesAsync(client, invoice, []);
        var requisites = new Dictionary<string, object?>
        {
            ["Номер"] = "СЧ-свободный", ["Дата"] = "2026-09-29", ["Поставщик"] = Reference(supplier),
        };
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}", new { requisites }));

        foreach (var held in new[] { position, vendor })
        {
            var refusal = await client.DeleteAsync($"/api/common-data/{held}");
            Assert.Equal(HttpStatusCode.Conflict, refusal.StatusCode);
            var text = (await refusal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;
            Assert.Contains("запомненные соответствия", text);
            Assert.Contains("«Соответствия»", text);
        }

        var match = Assert.Single((await ListAsync(client, $"supplierId={vendor}")).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent,
            (await ForgetAsync(client, match.GetProperty("id").GetGuid(), match.GetProperty("version").GetString())).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/common-data/{position}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/common-data/{vendor}")).StatusCode);
    }

    /// <summary>
    /// СТОРОЖ РЕШЕНИЯ (владелец продукта, 10.10.2026). Поставщика держит каждое его соответствие — и
    /// снять их можно разом: иначе поставщик-дубль с сотней запомненных строк не удалялся бы на деле.
    /// Чужие соответствия при этом целы, а адрес без поставщика не стирает ничего.
    /// </summary>
    [Fact]
    public async Task Все_соответствия_поставщика_забываются_разом_и_поставщик_удаляется()
    {
        var (client, _) = await SignInAsync("Admin");
        var (vendor, other) = (await VendorAsync(), await VendorAsync());
        var position = await PositionAsync();
        // Соответствия заводим напрямую: счёт, запомнивший выбор, сам держал бы поставщика.
        var invoice = await InvoiceAsync(client, vendor);
        await LinesAsync(client, invoice, [.. new[] { "Хомут 100", "Хомут 200", "Хомут 300" }.Select(t => Line(position, 1, 5m, text: t))]);
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}", new
        {
            requisites = new Dictionary<string, object?>
            {
                ["Номер"] = "СЧ-переехал", ["Дата"] = "2026-09-29", ["Поставщик"] = Reference(supplier),
            },
        }));
        await RememberAsync(client, other, position, "Хомут 100");

        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/common-data/{vendor}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync("/api/costs/supplier-matches")).StatusCode);

        var journal = await CountAsync(InvoiceActions.MatchesForgotten);
        var forgotten = await client.DeleteAsync($"/api/costs/supplier-matches?supplierId={vendor}");
        await OkAsync(forgotten);
        Assert.Equal(3, (await forgotten.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("forgotten").GetInt32());
        // Одной записью с числом, а не тремя.
        Assert.Equal(journal + 1, await CountAsync(InvoiceActions.MatchesForgotten));

        Assert.Equal(0, (await ListAsync(client, $"supplierId={vendor}")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await ListAsync(client, $"supplierId={other}")).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/common-data/{vendor}")).StatusCode);

        var (accountant, _) = await SignInAsync("Accountant");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await accountant.DeleteAsync($"/api/costs/supplier-matches?supplierId={other}")).StatusCode);
    }

    [Fact]
    public async Task Список_соответствий_открыт_правом_правки_счёта()
    {
        var (accountant, _) = await SignInAsync("Accountant");
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.GetAsync("/api/costs/supplier-matches")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.GetAsync("/api/costs/supplier-matches/suppliers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ForgetAsync(accountant, Guid.NewGuid(), "1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PointAsync(accountant, Guid.NewGuid(), Guid.NewGuid(), "1")).StatusCode);

        var (buyer, _) = await SignInAsync("Supplier");
        Assert.Equal(HttpStatusCode.OK, (await buyer.GetAsync("/api/costs/supplier-matches")).StatusCode);
    }

    // ── Помощники ────────────────────────────────────────────────────────────

    private static async Task<JsonElement> ListAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/costs/supplier-matches?{query}");
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> PointAsync(HttpClient client, Guid id, Guid position, string? version)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/costs/supplier-matches/{id}")
        {
            Content = JsonContent.Create(new { nomenclatureId = position }),
        };
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", version);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> ForgetAsync(HttpClient client, Guid id, string? version)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/costs/supplier-matches/{id}");
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", version);
        return client.SendAsync(request);
    }

    private static async Task<List<JsonElement>> SuggestAsync(HttpClient client, Guid vendor, string text)
    {
        var response = await client.PostAsJsonAsync("/api/costs/supplier-matches/suggestions", new
        {
            supplierId = vendor,
            lines = new[] { new { supplierText = text } },
        });
        await OkAsync(response);
        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray()];
    }

    /// <summary>Запомнить выбор по строкам — счётом, как это делает человек; ответ — соответствия поставщика.</summary>
    private static async Task<List<JsonElement>> RememberAsync(HttpClient client, Guid vendor, Guid position, params string[] texts)
    {
        var invoice = await InvoiceAsync(client, vendor);
        var saved = await LinesAsync(client, invoice, [.. texts.Select(text => Line(position, 1, 5m, text: text))]);
        Assert.Equal(texts.Length, saved.GetProperty("memory").GetProperty("remembered").GetInt32());

        return [.. (await ListAsync(client, $"supplierId={vendor}")).GetProperty("items").EnumerateArray()
            .Where(item => texts.Contains(item.GetProperty("source").GetString()))];
    }

    private static async Task<Guid> InvoiceAsync(HttpClient client, Guid vendor)
    {
        var requisites = new Dictionary<string, object?>
        {
            ["Номер"] = $"СЧ-{Guid.NewGuid().ToString()[..6]}",
            ["Дата"] = "2026-09-29",
            ["Поставщик"] = Reference(vendor),
        };

        var response = await client.PostAsJsonAsync("/api/costs/invoices", new { requisites });
        await OkAsync(response);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task ArchiveAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        Assert.Equal(ArchiveOutcome.Changed,
            await scope.ServiceProvider.GetRequiredService<IRecordArchive>().SetAsync(id, archived: true));
    }

    private async Task<Guid> VendorAsync(string word = "Поставщик") =>
        await EntryAsync(await TypeAsync(CostsRecordTypes.OrganizationCode, "Организация"),
            $"ООО «{word} {Guid.NewGuid().ToString("N")[..6]}»");

    private async Task<Guid> PositionAsync() =>
        await EntryAsync(await TypeAsync(CostsRecordTypes.NomenclatureCode, "Номенклатура"),
            $"Позиция {Guid.NewGuid().ToString("N")[..6]}");

    private async Task<int> CountAsync(BHS.CRG.Modules.Ports.ModuleActivityAction action)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<BHS.CRG.Application.Activity.IActivityLog>()
            .CountAsync(BHS.CRG.Application.Activity.ActivityVisibility.Whole, action.Code);
    }
}
