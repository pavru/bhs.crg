using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Costs;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Готовность счёта (issue #1291): ответ чтения называет всё, чего не хватает до «Разобран».
///
/// <para><b>Сторож задачи — свойство «переход прошёл ⇔ перечень пуст»</b>, на каждом условии по
/// отдельности и на нескольких сразу. Перечень, собранный не той функцией, что проверяет переход, —
/// отказ, переодетый в результат: форма показала бы «всё готово» там, где переход откажет, либо
/// пугала бы пунктом, которого переход не требует.</para>
/// </summary>
[Collection("Integration")]
public class InvoiceReadinessTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private static async Task<List<JsonElement>?> GapsAsync(HttpClient client, Guid invoice)
    {
        var gaps = (await ReadAsync(client, invoice)).GetProperty("readiness").GetProperty("gaps");
        return gaps.ValueKind == JsonValueKind.Null ? null : [.. gaps.EnumerateArray()];
    }

    private static string[] Kinds(IEnumerable<JsonElement> gaps) =>
        [.. gaps.Select(g => g.GetProperty("kind").GetString()!)];

    /// <summary>
    /// Свойство: перечень пуст ⇔ переход проходит. Возвращает перечень, каким он был ДО нажатия, и
    /// ответ перехода.
    /// </summary>
    private static async Task<(List<JsonElement> Gaps, HttpResponseMessage Response)> PressAsync(HttpClient client, Guid invoice)
    {
        var gaps = await GapsAsync(client, invoice);
        Assert.NotNull(gaps);

        // Очередь «Разобрать» (issue #1292): черновик в ней стоит при ЛЮБОМ перечне, и строка списка
        // называет то же, что открытый счёт, — теми же словами. Здесь, а не отдельным тестом: так
        // согласие проверено на каждом условии готовности по отдельности и на нескольких сразу.
        var row = await QueueRowAsync(client, invoice);
        Assert.True(row is not null, "Черновик не стоит в очереди «Разобрать»");
        Assert.Equal(
            gaps.Select(g => g.GetProperty("label").GetString()),
            row.Value.GetProperty("readiness").EnumerateArray().Select(l => l.GetString()));

        var response = await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null);
        var state = (await ReadAsync(client, invoice)).GetProperty("requisites").GetProperty("Состояние").GetString();

        Assert.True(response.IsSuccessStatusCode == (gaps.Count == 0),
            $"Перечень готовности ({string.Join(", ", Kinds(gaps))}) и переход ({(int)response.StatusCode}) " +
            $"разошлись: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(gaps.Count == 0 ? "Разобран" : "Черновик", state);
        // Разобран — из очереди ушёл; отказано — остался.
        Assert.Equal(gaps.Count > 0, await QueueRowAsync(client, invoice) is not null);
        return (gaps, response);
    }

    /// <summary>Строка счёта под отбором «Разобрать»; <c>null</c> — счёт в очереди не стоит.</summary>
    private static async Task<JsonElement?> QueueRowAsync(HttpClient client, Guid invoice)
    {
        var queue = await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices?needsParsing=true");
        foreach (var row in queue.EnumerateArray())
            if (row.GetProperty("id").GetGuid() == invoice) return row;
        return null;
    }

    /// <summary>
    /// Число у чипа «Разобрать» — число строк под ним: считает база тем же условием, каким отбирает
    /// список. Разойдись они, под чипом «4» стояло бы три счёта.
    /// </summary>
    [Fact]
    public async Task Число_у_очереди_равно_числу_счетов_под_ней()
    {
        var (client, _) = await SignInAsync("Admin");
        await CreateAsync(client);
        var parsed = await CreateAsync(client, complete: true);
        await AllocatedLinesAsync(client, parsed, [Line(cable, quantity: 1, price: 100m, rate: 20)]);
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{parsed}/parsed", null));

        var listed = (await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices?needsParsing=true")).GetArrayLength();
        var counted = (await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices/queues/parsing")).GetProperty("count").GetInt32();

        Assert.True(listed > 0);
        Assert.Equal(listed, counted);
    }

    /// <summary>
    /// Причина в строке — только у черновика: у разобранного и отклонённого вопрос «чего не хватает» не
    /// стоит. В общем списке (без отбора) черновик причину несёт тоже — видно, за какой счёт браться.
    /// </summary>
    [Fact]
    public async Task В_общем_списке_причину_несёт_только_черновик()
    {
        var (client, _) = await SignInAsync("Admin");
        var draft = await CreateAsync(client);
        var parsed = await CreateAsync(client, complete: true);
        await AllocatedLinesAsync(client, parsed, [Line(cable, quantity: 1, price: 100m, rate: 20)]);
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{parsed}/parsed", null));

        var rows = (await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices")).EnumerateArray().ToList();
        JsonElement Readiness(Guid id) => rows.Single(r => r.GetProperty("id").GetGuid() == id).GetProperty("readiness");

        Assert.Equal(["плательщик", "строки", "разноска"], Readiness(draft).EnumerateArray().Select(l => l.GetString()));
        Assert.Equal(JsonValueKind.Null, Readiness(parsed).ValueKind);
    }

    /// <summary>Готовый счёт: перечень пуст, переход проходит — и у разобранного перечня нет вовсе.</summary>
    [Fact]
    public async Task Готовый_счёт_перечень_пуст_и_переход_проходит()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await AllocatedLinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m, rate: 20)]);

        var (gaps, _) = await PressAsync(client, invoice);

        Assert.Empty(gaps);
        // «Нет перечня» и «перечень пуст» — разное: у разобранного вопрос не стоит.
        Assert.Null(await GapsAsync(client, invoice));
        // Обязательные поля едут всегда — форма помечает их и у разобранного.
        var required = (await ReadAsync(client, invoice)).GetProperty("readiness").GetProperty("required");
        Assert.Equal(["Номер", "Дата", "Поставщик", "Плательщик"], required.EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task Не_хватает_только_обязательного_поля()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        await AllocatedLinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m, rate: 20)]);

        var (gaps, response) = await PressAsync(client, invoice);

        var gap = Assert.Single(gaps);
        Assert.Equal("field", gap.GetProperty("kind").GetString());
        Assert.Equal("Плательщик", gap.GetProperty("field").GetString());
        Assert.Equal("плательщик", gap.GetProperty("label").GetString());
        Assert.Contains("не заполнено обязательное — «Плательщик»", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Не_хватает_строк()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);

        var (gaps, response) = await PressAsync(client, invoice);

        Assert.Equal("lines", Kinds(gaps)[0]);
        Assert.DoesNotContain("nomenclature", Kinds(gaps));
        Assert.Contains("строк нет", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Не_хватает_только_позиции_у_строки()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await AllocatedLinesAsync(client, invoice,
        [
            Line(cable, quantity: 1, price: 100m, rate: 20),
            Line(null, quantity: 2, price: 50m, rate: 20, text: "Хомут"),
        ]);

        var (gaps, response) = await PressAsync(client, invoice);

        var gap = Assert.Single(gaps);
        Assert.Equal("nomenclature", gap.GetProperty("kind").GetString());
        Assert.Equal("1 строка без позиции", gap.GetProperty("label").GetString());
        Assert.Equal([2], gap.GetProperty("lines").EnumerateArray().Select(l => l.GetInt32()));
        Assert.Contains("строка 2 ждёт позиции номенклатуры", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Не_хватает_только_живой_позиции()
    {
        var (client, _) = await SignInAsync("Admin");
        var doomed = await EntryAsync(await TypeAsync(CostsRecordTypes.NomenclatureCode, "Номенклатура"),
            $"Позиция под удаление {Guid.NewGuid().ToString()[..6]}");
        var invoice = await CreateAsync(client, complete: true);
        await AllocatedLinesAsync(client, invoice, [Line(doomed, quantity: 2, price: 50m, rate: 20)]);
        await ForgetAsync(doomed);

        var (gaps, _) = await PressAsync(client, invoice);

        var gap = Assert.Single(gaps);
        Assert.Equal("position", gap.GetProperty("kind").GetString());
        Assert.Equal([1], gap.GetProperty("lines").EnumerateArray().Select(l => l.GetInt32()));
    }

    [Theory]
    [InlineData("Поставщик")]
    [InlineData("Плательщик")]
    public async Task Не_хватает_только_живой_организации(string field)
    {
        var (client, _) = await SignInAsync("Admin");
        var doomed = await EntryAsync(await TypeAsync(CostsRecordTypes.OrganizationCode, "Организация"),
            $"ООО под удаление {Guid.NewGuid().ToString()[..6]}");
        var invoice = await CreateAsync(client, complete: true);
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(client, invoice, field, Reference(doomed)) }));
        await AllocatedLinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m, rate: 20)]);
        await ForgetAsync(doomed);

        var (gaps, _) = await PressAsync(client, invoice);

        var gap = Assert.Single(gaps);
        Assert.Equal("reference", gap.GetProperty("kind").GetString());
        Assert.Equal(field, gap.GetProperty("field").GetString());
        // Имя поля — из объявления типа, как у пункта «не заполнено», а не из ключа.
        Assert.Equal($"{field.ToLowerInvariant()}: записи нет в справочнике", gap.GetProperty("label").GetString());
    }

    [Fact]
    public async Task Не_хватает_только_разноски()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await LinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m, rate: 20)]);

        var (gaps, response) = await PressAsync(client, invoice);

        Assert.Equal(["allocation"], Kinds(gaps));
        // Какие строки не разнесены, пункт называет: по ним форма ведёт к строке, а не к таблице целиком.
        Assert.Equal([1], gaps[0].GetProperty("lines").EnumerateArray().Select(l => l.GetInt32()));
        Assert.Contains("разнесена по стройкам не полностью", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Расхождение суммы — свой пункт, и ведёт он к полю суммы (ревью PR #1301). Раньше оно пряталось в
    /// пункте «разноска», который вёл к выбору объекта: выбор объекта расхождение не чинит, а причину
    /// человек по-прежнему узнавал нажатием.
    /// </summary>
    [Fact]
    public async Task Расхождение_суммы_названо_своим_пунктом_и_ведёт_к_полю_суммы()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await AllocatedLinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m, rate: 20)]);
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(client, invoice, "Итого", 999m) }));

        var (gaps, response) = await PressAsync(client, invoice);

        var gap = Assert.Single(gaps);
        Assert.Equal("total", gap.GetProperty("kind").GetString());
        Assert.Equal("Итого", gap.GetProperty("field").GetString());
        Assert.Contains("расходится с суммой к оплате", gap.GetProperty("label").GetString());
        Assert.Contains("сумма строк расходится с суммой к оплате", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Причин несколько — названы ВСЕ, в порядке формы сверху вниз. Раньше переход отказывал на первой, и
    /// человек узнавал об остальных следующими нажатиями: пять условий — пять отказов.
    /// </summary>
    [Fact]
    public async Task Перечень_и_отказ_называют_все_причины_а_не_первую()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        await LinesAsync(client, invoice,
        [
            Line(cable, quantity: 1, price: 100m, rate: 20),
            Line(null, quantity: 2, price: 50m, rate: 20, text: "Хомут"),
            Line(null, quantity: 3, price: 10m, rate: 20, text: "Скоба"),
        ]);

        var (gaps, response) = await PressAsync(client, invoice);

        Assert.Equal(["field", "nomenclature", "allocation"], Kinds(gaps));
        Assert.Equal("2 строки без позиции", gaps[1].GetProperty("label").GetString());

        var refusal = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // Слова первого условия — прежние, остальное названо следом.
        Assert.Contains("не заполнено обязательное — «Плательщик»", refusal);
        Assert.Contains("Кроме того, до «Разобран» осталось: 2 строки без позиции · разноска", refusal);
    }

    /// <summary>
    /// Строка убывает по мере заполнения, и последний закрытый пункт открывает переход с первого нажатия.
    /// </summary>
    [Fact]
    public async Task Перечень_убывает_по_мере_заполнения_до_пустого()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        Assert.Equal(["field", "lines", "allocation"], Kinds((await PressAsync(client, invoice)).Gaps));

        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(client, invoice, "Плательщик", Reference(payer)) }));
        Assert.Equal(["lines", "allocation"], Kinds((await PressAsync(client, invoice)).Gaps));

        var view = await LinesAsync(client, invoice, [Line(null, quantity: 1, price: 100m, rate: 20, text: "Кабель")]);
        Assert.Equal(["nomenclature", "allocation"], Kinds((await PressAsync(client, invoice)).Gaps));

        view = await LinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m, rate: 20, id: LineId(view, 1))]);
        Assert.Equal(["allocation"], Kinds((await PressAsync(client, invoice)).Gaps));

        var (site, _) = await SiteAsync("Готовность");
        await AllocateAsync(client, invoice, LineId(view, 1), [Part(site, quantity: 1)]);
        Assert.Empty((await PressAsync(client, invoice)).Gaps);
    }

    /// <summary>У отклонённого вопрос «чего не хватает до „Разобран“» не стоит: перечня нет.</summary>
    [Fact]
    public async Task У_отклонённого_счёта_перечня_нет()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        Assert.NotNull(await GapsAsync(client, invoice));

        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/rejected", null));

        Assert.Null(await GapsAsync(client, invoice));
    }
}
