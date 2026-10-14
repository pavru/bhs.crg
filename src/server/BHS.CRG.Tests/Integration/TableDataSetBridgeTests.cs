using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Modules.Tables;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Tables;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Мост «Сделать источником данных» (задача G3b, issue #1096; ТЗ CORE-33, COST-22): экран таблицы —
/// в обычный источник системного набора.
///
/// <para>Сторож задачи — <see cref="Правка_представления_не_меняет_созданный_из_него_источник" /> вместе
/// с <see cref="Ни_тело_моста_ни_источник_не_знают_представления" />: первый без второго зелёный всегда,
/// пока связи нет по построению, — второй падает, как только её заведут.</para>
/// </summary>
[Collection("Integration")]
public sealed class TableDataSetBridgeTests(InvoiceLineHost host) : ModuleTableSeededTests(host)
{
    private const string Bridge = $"/api/tables/{Address}/dataset";
    private const string Views = $"/api/tables/{Address}/views";

    private static string Mine(Seed seed) =>
        JsonSerializer.Serialize(new { type = "condition", column = InvoiceRequisites.NumberKey, op = "starts_with", value = seed.Tag });

    private static string Name() => $"Из таблицы {Guid.NewGuid():N}";

    [Fact]
    public async Task Источник_несёт_колонки_отбор_и_сортировку_экрана()
    {
        var (admin, _) = await SignInAsync("Admin");
        // Чужие счета рядом — отбору есть что отсекать: без них источник без отбора дал бы те же восемь.
        await SeedAsync(admin);
        var seed = await SeedAsync(admin);
        var name = Name();

        var created = await CreatedAsync(await admin.PostAsJsonAsync(Bridge, new
        {
            name,
            columns = new[] { InvoiceRequisites.NumberKey, InvoiceRequisites.TotalKey, InvoiceTable.AmountKey },
            filter = Mine(seed),
            sort = new[] { new { column = InvoiceRequisites.NumberKey, descending = true } },
        }));

        Assert.Equal(name, created.GetProperty("name").GetString());
        // Доля по отбору в источник не едет — и ответ называет её подписью, а не ключом.
        Assert.Equal(["Сумма"], created.GetProperty("dropped").EnumerateArray().Select(e => e.GetString()));

        var preview = await PreviewAsync(admin, created.GetProperty("sourceId").GetGuid());
        var columns = preview.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Equal([InvoiceRequisites.NumberKey, InvoiceRequisites.TotalKey], columns);
        Assert.Equal(8, preview.GetProperty("totalRows").GetInt32());
        var numbers = preview.GetProperty("rows").EnumerateArray().Select(r => r[0].GetString()).ToList();
        Assert.All(numbers, n => Assert.StartsWith(seed.Tag, n));
        Assert.Equal($"{seed.Tag}-8", numbers[0]);
        Assert.Equal($"{seed.Tag}-1", numbers[^1]);
    }

    /// <summary>
    /// Отбор стоит на колонке, которой на экране нет. Источник отбирает строки сам — без этой колонки в
    /// маркере условие не нашло бы ничего, и источник молча был бы пустым.
    /// </summary>
    [Fact]
    public async Task Колонка_условия_едет_в_источник_даже_вне_экрана()
    {
        var (admin, _) = await SignInAsync("Admin");
        var seed = await SeedAsync(admin);

        var created = await CreatedAsync(await admin.PostAsJsonAsync(Bridge, new
        {
            name = Name(), columns = new[] { InvoiceRequisites.TotalKey }, filter = Mine(seed),
        }));

        var preview = await PreviewAsync(admin, created.GetProperty("sourceId").GetGuid());
        Assert.Equal(8, preview.GetProperty("totalRows").GetInt32());
        Assert.Equal([InvoiceRequisites.TotalKey, InvoiceRequisites.NumberKey],
            preview.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public async Task Правка_представления_не_меняет_созданный_из_него_источник()
    {
        var (admin, _) = await SignInAsync("Admin");
        var seed = await SeedAsync(admin);

        var state = new
        {
            columns = new[] { InvoiceRequisites.NumberKey, InvoiceRequisites.TotalKey },
            filter = Mine(seed),
            sort = new[] { new { column = InvoiceRequisites.NumberKey, descending = false } },
        };
        var saved = await admin.PostAsJsonAsync(Views, new { title = Name(), shared = true, state });
        await OkAsync(saved);
        var view = await saved.Content.ReadFromJsonAsync<JsonElement>();

        var created = await CreatedAsync(await admin.PostAsJsonAsync(Bridge, new
        {
            name = Name(), state.columns, state.filter, state.sort,
        }));
        var source = created.GetProperty("sourceId").GetGuid();

        // Представление сужено до одной строки и одной колонки — источник обязан остаться прежним.
        var narrowed = new HttpRequestMessage(HttpMethod.Put, $"{Views}/{view.GetProperty("id").GetGuid()}")
        {
            Content = JsonContent.Create(new
            {
                title = view.GetProperty("title").GetString(), shared = true,
                state = new
                {
                    columns = new[] { InvoiceRequisites.NumberKey },
                    filter = JsonSerializer.Serialize(new { type = "condition", column = InvoiceRequisites.NumberKey, op = "eq", value = $"{seed.Tag}-1" }),
                },
            }),
        };
        narrowed.Headers.TryAddWithoutValidation("If-Match", view.GetProperty("version").GetString());
        await OkAsync(await admin.SendAsync(narrowed));

        var preview = await PreviewAsync(admin, source);
        Assert.Equal(8, preview.GetProperty("totalRows").GetInt32());
        Assert.Contains(InvoiceRequisites.TotalKey, preview.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
    }

    /// <summary>Связи нет по построению — и этот тест держит построение: заведи её, и он упадёт.</summary>
    [Fact]
    public void Ни_тело_моста_ни_источник_не_знают_представления()
    {
        static IEnumerable<string> ViewLike(Type type) => type.GetProperties()
            .Select(p => p.Name).Where(n => n.Contains("View", StringComparison.OrdinalIgnoreCase));

        Assert.Empty(ViewLike(typeof(TableDataSetBody)));
        Assert.Empty(ViewLike(typeof(DataSetSource)));
    }

    [Fact]
    public async Task Отбор_по_колонке_зависящей_от_отбора_мост_отвергает()
    {
        var (admin, _) = await SignInAsync("Admin");
        var name = Name();
        var filter = JsonSerializer.Serialize(new { type = "condition", column = InvoiceTable.AmountKey, op = "gt", value = "100" });

        var response = await admin.PostAsJsonAsync(Bridge, new { name, filter });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await ErrorAsync(response);
        Assert.Contains("«Сумма»", error);
        Assert.Contains("зависит от самого отбора", error);
        Assert.False(await SourceExistsAsync(admin, name));
    }

    [Fact]
    public async Task Отбор_по_закрытой_колонке_мост_отвергает()
    {
        var (narrow, _) = await SignInAsync(await RoleAsync("costs.waybill.read", "core.datasets.edit"));
        var name = Name();
        var filter = JsonSerializer.Serialize(new { type = "condition", column = InvoiceRequisites.TotalKey, op = "gt", value = "100" });

        var response = await narrow.PostAsJsonAsync(Bridge, new { name, columns = new[] { InvoiceRequisites.NumberKey }, filter });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("закрыта", await ErrorAsync(response));
        var (admin, _) = await SignInAsync("Admin");
        Assert.False(await SourceExistsAsync(admin, name));
    }

    [Fact]
    public async Task Без_права_настройки_наборов_моста_нет()
    {
        var (reader, _) = await SignInAsync(await RoleAsync("costs.invoice.read"));

        var response = await reader.PostAsJsonAsync(Bridge, new { name = Name() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("costs.invoices", null, "system:table:costs.invoices")]
    [InlineData("costs.invoices", new[] { "Номер", "Итого" }, "system:table:costs.invoices?columns=Номер,Итого")]
    [InlineData("costs.invoices", new[] { "a,b", "50%" }, "system:table:costs.invoices?columns=a%2Cb,50%25")]
    public void Маркер_таблицы_несёт_колонки_и_разбирается_обратно(string address, string[]? columns, string marker)
    {
        Assert.Equal(marker, SystemDataSets.TableMarker(address, columns));

        Assert.True(SystemDataSets.TryParseTableMarker(marker, out var parsed, out var back));
        Assert.Equal(address, parsed);
        Assert.Equal(columns, back);
    }

    private static async Task<JsonElement> CreatedAsync(HttpResponseMessage response)
    {
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> PreviewAsync(HttpClient client, Guid source)
    {
        var response = await client.GetAsync($"/api/datasets/sources/{source}/preview?maxRows=100");
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<bool> SourceExistsAsync(HttpClient client, string name)
    {
        var files = await client.GetFromJsonAsync<JsonElement>("/api/datasets/files?scope=System");
        foreach (var file in files.EnumerateArray().Where(f => f.GetProperty("format").GetString() == "System"))
        {
            var sources = await client.GetFromJsonAsync<JsonElement>($"/api/datasets/files/{file.GetProperty("id").GetGuid()}/sources");
            if (sources.EnumerateArray().Any(s => s.GetProperty("name").GetString() == name)) return true;
        }
        return false;
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;
}
