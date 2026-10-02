using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Infrastructure.DataSets;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Отбор, сортировка, страницы и итоги таблицы модуля (задача G1c, issue #1090, ТЗ CORE-33).
///
/// <para>Главные проверки — <see cref="Итог_считается_по_всему_отбору_а_не_по_странице" /> (посчитать
/// по странице — тест падает) и <see cref="Итог_по_полю_с_нечислом_называет_число_неучтённых" />.</para>
/// </summary>
public sealed class ModuleTableQueryTests(InvoiceLineHost host) : ModuleTableSeededTests(host)
{
    [Fact]
    public async Task Итог_считается_по_всему_отбору_а_не_по_странице()
    {
        var (client, _) = await SignInAsync("Supplier");
        var seed = await SeedAsync(client);

        var table = await ReadAsync(client, $"columns=Номер,Итого&limit=2&totals=Итого&filter={Own(seed)}");

        // На странице две строки, в отборе восемь, а сумма — всех шести счетов с суммой.
        Assert.Equal(2, table.GetProperty("rows").GetArrayLength());
        Assert.Equal(8, table.GetProperty("count").GetInt32());
        Assert.Equal(2, table.GetProperty("limit").GetInt32());
        var total = table.GetProperty("totals").GetProperty("Итого");
        Assert.Equal(6869.99m, total.GetProperty("sum").GetDecimal());
        Assert.Equal(6, total.GetProperty("count").GetInt64());
        Assert.Equal(50m, total.GetProperty("min").GetDecimal());
        Assert.Equal(5000m, total.GetProperty("max").GetDecimal());
        Assert.Equal(Math.Round(6869.99m / 6, 6), Math.Round(total.GetProperty("average").GetDecimal(), 6));
        Assert.Equal(0, total.GetProperty("skipped").GetInt64());
    }

    /// <summary>
    /// В числовом поле схемы лежат и числа, и «12 шт», и «много». Сумма — по числам, а про остальное
    /// итог говорит вслух: молча меньшая сумма выглядела бы правильной.
    /// </summary>
    [Fact]
    public async Task Итог_по_полю_с_нечислом_называет_число_неучтённых()
    {
        var (client, _) = await SignInAsync("Supplier");
        var seed = await SeedAsync(client);

        var table = await ReadAsync(client,
            $"columns=Номер&totals={seed.Weight},{seed.Warranty},Срок,Назначение&filter={Own(seed)}");
        var totals = table.GetProperty("totals");

        var weight = totals.GetProperty(seed.Weight);
        Assert.Equal(126.5m, weight.GetProperty("sum").GetDecimal());   // 12.5 + 7 + «7» + 100
        Assert.Equal(4, weight.GetProperty("count").GetInt64());
        Assert.Equal(2, weight.GetProperty("skipped").GetInt64());      // «12 шт», «много»; пустые — не в счёт
        Assert.Equal("не число", weight.GetProperty("skippedReason").GetString());

        var warranty = totals.GetProperty(seed.Warranty);
        Assert.Equal(3, warranty.GetProperty("count").GetInt64());
        Assert.Equal(1, warranty.GetProperty("skipped").GetInt64());    // «скоро»
        Assert.Equal("не дата", warranty.GetProperty("skippedReason").GetString());
        Assert.Equal("2026-05-01", warranty.GetProperty("min").GetString());
        Assert.Equal("2026-12-31", warranty.GetProperty("max").GetString());

        // У настоящей даты — минимум и максимум, суммы нет; у текста — только количество непустых.
        var due = totals.GetProperty("Срок");
        Assert.Equal("2026-05-15", due.GetProperty("min").GetString());
        Assert.Equal("2026-07-01", due.GetProperty("max").GetString());
        Assert.Equal(JsonValueKind.Null, due.GetProperty("sum").ValueKind);
        Assert.Equal(JsonValueKind.Null, due.GetProperty("skippedReason").ValueKind);
        Assert.Equal(5, totals.GetProperty("Назначение").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task Сортировка_идёт_по_виду_колонки_и_пустые_всегда_в_конце()
    {
        var (client, _) = await SignInAsync("Supplier");
        var seed = await SeedAsync(client);
        async Task<string[]> Order(string sort) => [.. (await ReadAsync(client,
                $"columns=Номер&sort={Uri.EscapeDataString(sort)}&filter={Own(seed)}"))
            .GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("Номер").GetString()![^1..])];

        // Число: по значению, а не по записи («5000» строкой стоял бы после «99.99»). Равные и пустые
        // довершает порядок таблицы — свежие сверху.
        Assert.Equal(["3", "6", "2", "1", "5", "4", "8", "7"], await Order("Итого:desc"));
        Assert.Equal(["4", "5", "2", "1", "6", "3", "8", "7"], await Order("Итого"));

        // Справочник: по НАЗВАНИЮ организации, не по ссылке; счета без поставщика — в конце.
        var bySupplier = await Order("Поставщик,Номер");
        Assert.Equal(["1", "2", "3", "4", "5", "6", "7", "8"], bySupplier);
        Assert.Equal(["6", "1", "2", "3", "4", "5", "7", "8"], await Order("Поставщик:desc,Номер"));

        // Поле схемы с числами и текстом: числа по значению, не-числа и пустые — после них.
        var byWeight = await Order($"{seed.Weight}:desc,Номер");
        Assert.Equal(["5", "1", "3", "4"], byWeight[..4]);

        // Справочник-перечисление (в базе код строкой): по НАЗВАНИЮ — «Не оплачен», «Оплачен»,
        // «Частично оплачен», — а не по коду и не по порядку значений перечисления.
        Assert.Equal(["1", "4", "5", "7", "8", "2", "6", "3"], await Order("СостояниеОплаты,Номер"));
    }

    /// <summary>
    /// В строках едут только ЗАПРОШЕННЫЕ колонки. Отбирать и сортировать при этом можно по любой
    /// открытой — в том числе по той, которой на экране нет.
    /// </summary>
    [Fact]
    public async Task В_строках_только_запрошенные_колонки_а_отбор_идёт_по_любой_открытой()
    {
        var (client, _) = await SignInAsync("Supplier");
        var seed = await SeedAsync(client);
        var filter = Uri.EscapeDataString(
            $$"""{"type":"group","logic":"and","children":[{"type":"condition","column":"Номер","op":"starts_with","value":"{{seed.Tag}}"},{"type":"condition","column":"Итого","op":"gt","value":"1000"}]}""");

        var table = await ReadAsync(client, $"columns=Номер,Срок&sort=Итого:desc&filter={filter}");

        var rows = table.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(
            ["Номер", "Срок"], r.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)));
        Assert.EndsWith("-3", rows[0].GetProperty("Номер").GetString());   // 5000 раньше 1500
    }

    /// <summary>
    /// Длинный отбор идёт телом запроса: в адресе он упирается в потолок длины строки запроса, который
    /// ставит сервер перед приложением, — и отказ пришёл бы без причины.
    /// </summary>
    [Fact]
    public async Task Длинный_отбор_идёт_телом_запроса()
    {
        var (client, _) = await SignInAsync("Supplier");
        var seed = await SeedAsync(client);
        var many = Enumerable.Range(0, 400).Select(i => $"Поставщик с очень длинным названием № {i}")
            .Append("ООО «Наша компания»");
        var filter = JsonSerializer.Serialize(new
        {
            type = "group", logic = "and",
            children = new object[]
            {
                new { type = "condition", column = "Номер", op = "starts_with", value = seed.Tag },
                new { type = "condition", column = "Поставщик", op = "in", values = many },
            },
        });
        Assert.True(Uri.EscapeDataString(filter).Length > 16_000);

        var response = await client.PostAsJsonAsync($"/api/tables/{Address}/query", new
        {
            columns = new[] { "Номер" }, filter, sort = new[] { new { column = "Номер", descending = false } },
            limit = 5, totals = new[] { "Итого" },
        });
        await OkAsync(response);
        var table = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, table.GetProperty("count").GetInt32());
        Assert.EndsWith("-6", table.GetProperty("rows")[0].GetProperty("Номер").GetString());
        Assert.Equal(1500m, table.GetProperty("totals").GetProperty("Итого").GetProperty("sum").GetDecimal());
    }

    [Fact]
    public async Task Страница_отдаёт_свой_кусок_отбора()
    {
        var (client, _) = await SignInAsync("Supplier");
        var seed = await SeedAsync(client);
        async Task<string[]> Page(int offset, int limit) => [.. (await ReadAsync(client,
                $"columns=Номер&sort=Номер&offset={offset}&limit={limit}&filter={Own(seed)}"))
            .GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("Номер").GetString()![^1..])];

        Assert.Equal(["1", "2", "3"], await Page(0, 3));
        Assert.Equal(["4", "5", "6"], await Page(3, 3));
        Assert.Equal(["7", "8"], await Page(6, 3));
        Assert.Empty(await Page(8, 3));
    }

    /// <summary>
    /// Битый отбор ОТКАЗЫВАЕТ, а не возвращает все строки или «ничего не найдено»: обе выдачи
    /// неотличимы от правильных.
    /// </summary>
    [Theory]
    [InlineData("""{"type":"condition","column":"НетТакой","op":"eq","value":"1"}""", "такой колонки в таблице нет")]
    [InlineData("""{"type":"condition","column":"Итого","op":"contains","value":"1"}""", "не применяется")]
    [InlineData("""{"type":"condition","column":"Итого","op":"gt","value":"много"}""", "не число")]
    [InlineData("""{"type":"condition","column":"Срок","op":"lt","value":"01.05.2026"}""", "не дата")]
    [InlineData("""{"type":"condition","column":"Срок","op":"between","values":["2026-05-01"]}""", "две границы")]
    [InlineData("""{"type":"condition","column":"Номер","op":"in","values":[null]}""", "пустое место")]
    [InlineData("""{"type":"condition","column":"Номер","op":"eq","values":["а","б"]}""", "одно значение")]
    [InlineData("""{"type":"condition","column":"Номер","op":"похоже","value":"1"}""", "которого нет")]
    [InlineData("""["не дерево"]""", "не разбирается")]
    public async Task Битый_отбор_отказывает(string filter, string reason)
    {
        var (client, _) = await SignInAsync("Supplier");

        var response = await client.GetAsync($"/api/tables/{Address}?filter={Uri.EscapeDataString(filter)}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(reason, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// По закрытой колонке не отбирают и не сортируют: отбор «сумма больше миллиона» выдал бы суммы
    /// по одной — строка есть, значит больше. Итог по ней молча не считается: колонка и так приходит
    /// с причиной.
    /// </summary>
    [Fact]
    public async Task По_закрытой_колонке_нельзя_ни_отобрать_ни_отсортировать()
    {
        var (narrow, user) = await SignInAsync(await RoleAsync("costs.waybill.read"));
        const string filter = """{"type":"condition","column":"Итого","op":"gt","value":"100"}""";

        var filtered = await narrow.GetAsync($"/api/tables/{Address}?filter={Uri.EscapeDataString(filter)}");
        Assert.Equal(HttpStatusCode.Forbidden, filtered.StatusCode);
        Assert.Contains("нет права на суммы", await filtered.Content.ReadAsStringAsync());

        var sorted = await narrow.GetAsync($"/api/tables/{Address}?sort=Итого");
        Assert.Equal(HttpStatusCode.Forbidden, sorted.StatusCode);

        var totals = await ReadAsync(narrow, "totals=Итого,Номер");
        Assert.False(totals.GetProperty("totals").TryGetProperty("Итого", out _));
        Assert.True(totals.GetProperty("totals").TryGetProperty("Номер", out _));

        // И в наборе данных — тот же отказ: у пустых клеток отбор вернул бы «ничего не нашлось», и
        // человек без права на суммы получил бы пустой набор вместо причины.
        using var scope = host.Services.CreateScope();
        var access = await scope.ServiceProvider.GetRequiredService<DataAccessResolver>().ForUserAsync(user, default);
        var provider = scope.ServiceProvider.GetServices<ISystemDataProvider>()
            .Single(p => p.Handles("system:table:" + Address));
        var provided = await provider.ProvideAsync("system:table:" + Address, CatalogScope.System, null, access, default);

        var refusal = Assert.Throws<ConflictException>(() =>
            DataSetRowFilterExecutor.Apply(filter, [.. provided.Rows], "Счета", provided.Types));
        Assert.Contains("нет права на суммы", refusal.Message);
    }

    /// <summary>Экран, не попросивший страницу, получает её всё равно — а не таблицу целиком.</summary>
    [Fact]
    public async Task Без_размера_страницы_приходит_страница_по_умолчанию()
    {
        var (client, _) = await SignInAsync("Supplier");

        var table = await ReadAsync(client, "columns=Номер");

        Assert.Equal(100, table.GetProperty("limit").GetInt32());
        Assert.True(table.GetProperty("rows").GetArrayLength() <= 100);
    }

    /// <summary>
    /// Источник набора на таблице модуля отдаёт клиенту колонки с видом и операторами (issue #1133):
    /// по ним диалог отбора предлагает колонке её операторы, а не все подряд. Проверяется путь целиком
    /// — от поставщика до списка наборов; сама запись колонки — в <c>SourceSchemaTests</c>.
    /// </summary>
    [Fact]
    public async Task Источник_на_таблице_отдаёт_колонки_с_видом_и_операторами()
    {
        var (client, _) = await SignInAsync("Admin");

        var id = await SourceAsync(client);

        var files = await client.GetFromJsonAsync<JsonElement>("/api/datasets/files?scope=System");
        var source = files.EnumerateArray().SelectMany(f => f.GetProperty("sources").EnumerateArray())
            .Single(s => s.GetProperty("id").GetGuid() == id);
        var schema = JsonDocument.Parse(source.GetProperty("cachedSchema").GetString()!).RootElement;

        List<string?> Operators(string column) => [.. schema.EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == column)
            .GetProperty("operators").EnumerateArray().Select(o => o.GetString())];

        Assert.DoesNotContain("contains", Operators("Итого"));
        Assert.Contains("between", Operators("Итого"));
        Assert.Contains("between", Operators("Срок"));
        Assert.Contains("contains", Operators("Номер"));
        Assert.DoesNotContain("gt", Operators("Номер"));

        // Перечень (#1090) — тоже колонка с видом: без операторов диалог предложил бы ей общий
        // список, в том числе «больше», которого у перечня нет.
        Assert.Contains("contains", Operators("ОбъектыРазноски"));
        Assert.DoesNotContain("gt", Operators("ОбъектыРазноски"));
        // Колонка, чьё значение зависит от отбора, в набор не едет — значит, и отбирать по ней нечего.
        Assert.DoesNotContain(schema.EnumerateArray(), c => c.GetProperty("name").GetString() == "СуммаПоОтбору");
    }

    private static string Own(Seed seed) => Uri.EscapeDataString(
        $$"""{"type":"condition","column":"Номер","op":"starts_with","value":"{{seed.Tag}}"}""");

    private static async Task<JsonElement> ReadAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/tables/{Address}?{query}");
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
