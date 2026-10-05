using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Activity;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Негодное число и элемент-не-объект отвечают ОТКАЗОМ с именем поля, а не 500 (issue #1163, находки
/// ревью PR #1116).
///
/// <para>⚠️ Каждый случай здесь до правки отвечал либо «внутренняя ошибка сервера», либо — хуже —
/// успехом с другим числом: лишние знаки после запятой округляла база, молча. Проверено живьём на
/// стенде до правки: <c>{"lines":[null]}</c>, <c>1e400</c> и переполнение произведения — ровно 500.</para>
///
/// <para>Тела запросов — СТРОКОЙ, а не анонимным объектом: <c>1e400</c> и <c>null</c> на месте строки
/// сериализатор из объекта не соберёт, а проверять надо именно то, что присылает сломанный клиент.</para>
///
/// <para>Своим файлом по той же причине, что <see cref="InvoiceLineTests" /> отделён от
/// <see cref="InvoiceRecordTests" />: храповик размера (#1041).</para>
/// </summary>
[Collection("Integration")]
public class InvoiceLineNumberTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    /// <summary>
    /// Строка, присланная не объектом, — отказ с её НОМЕРОМ. Первая строка набора годная нарочно: номер
    /// в отказе обязан быть номером негодной, а не «строка 1» у любого набора.
    /// </summary>
    [Theory]
    [InlineData("null", "Null")]
    [InlineData("\"кабель\"", "String")]
    [InlineData("5", "Number")]
    [InlineData("[]", "Array")]
    public async Task Строка_не_объектом_отказывает_с_номером(string sent, string kind)
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        var response = await PutAsync(client, $"/api/costs/invoices/{invoice}/lines",
            "{\"lines\":[{\"supplierText\":\"годная\"}," + sent + "]}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"Строка 2 прислана как {kind}", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, await LineCountAsync(client, invoice));
    }

    /// <summary>
    /// Число вне границ — отказ с именем поля. Четыре разных дороги к 500: число, которого нет в
    /// <c>decimal</c>; строка из тридцати цифр; число, не вмещающееся в колонку; и сама граница.
    /// </summary>
    [Theory]
    [InlineData("\"quantity\":1e400", "Количество, строка 1")]
    [InlineData("\"price\":\"99999999999999999999999999999999\"", "Цена, строка 1")]
    [InlineData("\"quantity\":999999999999999999", "Количество, строка 1")]
    [InlineData("\"amount\":1000000000000", "Сумма, строка 1")]
    [InlineData("\"vatAmount\":-1000000000000", "Сумма НДС, строка 1")]
    public async Task Число_вне_границ_отказывает_с_именем_поля(string field, string label)
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        var response = await PutAsync(client, $"/api/costs/invoices/{invoice}/lines",
            "{\"lines\":[{\"supplierText\":\"проба\"," + field + "}]}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains(label, text);
        Assert.Contains("триллион", text);
        Assert.Equal(0, await LineCountAsync(client, invoice));
    }

    /// <summary>
    /// Оба числа в границе, а произведение — нет: сумму строки считаем мы, и отказать обязаны мы же.
    /// Иначе отказывала база — переполнением колонки внутри сохранения.
    /// </summary>
    [Fact]
    public async Task Посчитанная_сумма_вне_границ_отказывает()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/lines",
            new { lines = new object[] { Line(cable, quantity: 999_999_999m, price: 999_999_999m) } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("Строка 1: количество × цена", text);
        Assert.Equal(0, await LineCountAsync(client, invoice));
    }

    /// <summary>
    /// Самое большое допустимое число ложится и приезжает обратно знак в знак. Сторож ГРАНИЦЫ: проверки
    /// выше зелены и у предела, который отказывает на всём подряд, и у предела выше колонки.
    /// </summary>
    [Fact]
    public async Task Число_под_границей_сохраняется_точно()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        var view = await LinesAsync(client, invoice,
        [
            new Dictionary<string, object?>
            {
                ["supplierText"] = "под границей",
                ["quantity"] = 999_999_999_999.999m,
                ["amount"] = 999_999_999_999.99m,
                ["vatAmount"] = -999_999_999_999.99m,
            },
        ]);

        var line = (await ReadAsync(client, invoice)).GetProperty("lines")[0];
        Assert.Equal(999_999_999_999.999m, line.GetProperty("quantity").GetDecimal());
        Assert.Equal(999_999_999_999.99m, line.GetProperty("amount").GetDecimal());
        Assert.Equal(-999_999_999_999.99m, line.GetProperty("vatAmount").GetDecimal());
        Assert.Equal(1, view.GetProperty("lines").GetArrayLength());
    }

    /// <summary>
    /// Лишние знаки после запятой — отказ, а не округление базой.
    ///
    /// <para>⚠️ До правки это был УСПЕХ: цена 45,678 ложилась как 45,68, а сумма строки считалась по
    /// неокруглённой — строка в ответе сама с собой не сходилась. Поэтому проверяется не только код
    /// ответа, но и то, что строка не записана.</para>
    /// </summary>
    [Theory]
    [InlineData("price", "45.678", "Цена, строка 1", "точнее копейки")]
    [InlineData("quantity", "1.2345", "Количество, строка 1", "точнее тысячной")]
    [InlineData("amount", "10.001", "Сумма, строка 1", "точнее копейки")]
    [InlineData("vatAmount", "\"1,005\"", "Сумма НДС, строка 1", "точнее копейки")]
    public async Task Лишние_знаки_отказывают_а_не_округляются(string key, string sent, string label, string finest)
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        var response = await PutAsync(client, $"/api/costs/invoices/{invoice}/lines",
            "{\"lines\":[{\"supplierText\":\"проба\",\"" + key + "\":" + sent + "}]}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains(label, text);
        Assert.Contains(finest, text);
        Assert.Equal(0, await LineCountAsync(client, invoice));
    }

    /// <summary>
    /// У цены отказ называет ВЫХОД: в бумаге цена с долями копейки бывает (цена за метр из цены за
    /// километр), и отказ без пути был бы тупиком. Путь проверяется делом — сумма из бумаги ложится.
    /// </summary>
    [Fact]
    public async Task Отказ_на_цене_называет_выход_и_выход_работает()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        var refused = await PutAsync(client, $"/api/costs/invoices/{invoice}/lines",
            "{\"lines\":[{\"supplierText\":\"кабель\",\"quantity\":100,\"price\":45.678}]}");
        var refusal = await refused.Content.ReadAsStringAsync();
        Assert.Contains("сумму строки возьмите из бумаги", refusal);
        // Число в отказе — с запятой, как его набирают: «45.678» читалось бы как другое число.
        Assert.Contains("«45,678»", refusal);

        var view = await LinesAsync(client, invoice,
            [Line(null, quantity: 100, price: 45.68m, text: "кабель", amount: 4567.80m)]);

        var line = view.GetProperty("lines")[0];
        Assert.Equal(45.68m, line.GetProperty("price").GetDecimal());
        Assert.Equal(4567.80m, line.GetProperty("amount").GetDecimal());
    }

    /// <summary>
    /// Нули в хвосте лишними знаками не считаются: «45,670» — это 45,67. Без этого отказывала бы
    /// вставка из таблицы, где числа выгружены с фиксированным числом знаков.
    /// </summary>
    [Fact]
    public async Task Нули_в_хвосте_лишними_знаками_не_считаются()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        var response = await PutAsync(client, $"/api/costs/invoices/{invoice}/lines",
            "{\"lines\":[{\"supplierText\":\"проба\",\"quantity\":\"2,5000\",\"price\":45.670}]}");
        await OkAsync(response);

        var line = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lines")[0];
        Assert.Equal(2.5m, line.GetProperty("quantity").GetDecimal());
        Assert.Equal(45.67m, line.GetProperty("price").GetDecimal());
        Assert.Equal(114.18m, line.GetProperty("amount").GetDecimal());
    }

    /// <summary>
    /// Та же дверь у шапки: «Итого» и «в том числе НДС» лежат в таких же колонках и разбираются тем же
    /// помощником. Закрыта вместе со строками — иначе первой разошлась бы именно она.
    /// </summary>
    [Theory]
    [InlineData("Итого", "100.005", "точнее копейки")]
    [InlineData("ВТомЧислеНДС", "100000000000000000", "триллион")]
    [InlineData("Итого", "1e400", "триллион")]
    // Отсрочка — не деньги, но число читает тот же помощник: «1e400» и здесь было 500.
    [InlineData("Отсрочка", "1e400", "триллион")]
    public async Task Сумма_шапки_вне_границ_или_точности_отказывает(string key, string sent, string expected)
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        var requisites = (await ReadAsync(client, invoice)).GetProperty("requisites").GetRawText();
        var body = "{\"requisites\":" + WithField(requisites, key, sent) + "}";

        var response = await PutAsync(client, $"/api/costs/invoices/{invoice}", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains(key, text);
        Assert.Contains(expected, text);
    }

    /// <summary>
    /// И у разноски: часть, присланная не объектом, отказывает с номером, а негодное число — с именем
    /// поля. Идентификатор части читался раньше проверки вида — та же ошибка порядка, что у строк.
    /// </summary>
    [Theory]
    [InlineData("[null]", "Часть 1 прислана как Null")]
    [InlineData("[{\"construction\":\"SITE\",\"quantity\":1e400}]", "Количество, часть 1")]
    public async Task Негодная_часть_разноски_отказывает_а_не_падает(string parts, string expected)
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        var view = await LinesAsync(client, invoice, [Line(cable, quantity: 10, price: 1m)]);
        var (site, _) = await SiteAsync("Стройка для негодной части");

        var response = await PutAsync(client,
            $"/api/costs/invoices/{invoice}/lines/{LineId(view, 1)}/allocation",
            "{\"parts\":" + parts.Replace("SITE", site.ToString()) + "}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Процент быстрой разноски: своё правило (0…100) у него есть, но до него число ещё надо прочитать —
    /// и «1e400» читалось исключением.
    /// </summary>
    [Fact]
    public async Task Процент_быстрой_разноски_вне_decimal_отказывает()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await LinesAsync(client, invoice, [Line(cable, quantity: 10, price: 1m)]);
        var (site, _) = await SiteAsync("Стройка для процента");

        var response = await client.PostAsync($"/api/costs/invoices/{invoice}/allocation/preview",
            new StringContent(
                "{\"method\":\"percent\",\"targets\":[{\"construction\":\"" + site + "\",\"percent\":1e400}]}",
                Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Процент, объект 1", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Повторное «разобран» у разобранного счёта — успех без записи в журнал: запись означает решение
    /// человека, а второе нажатие новым решением не является. Первая запись при этом ЕСТЬ — проверка
    /// «записей не прибавилось» зелена и у журнала, который не пишет вовсе.
    /// </summary>
    [Fact]
    public async Task Повторный_разобран_в_журнал_не_пишет()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await AllocatedLinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m, rate: 20)]);

        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));
        Assert.Equal(1, await ParsedRecordsAsync(invoice));

        var again = await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null);
        await OkAsync(again);
        Assert.Equal("Разобран", (await again.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("requisites").GetProperty("Состояние").GetString());
        Assert.Equal(1, await ParsedRecordsAsync(invoice));
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string address, string json) =>
        client.PutAsync(address, new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<int> LineCountAsync(HttpClient client, Guid invoice) =>
        (await ReadAsync(client, invoice)).GetProperty("lines").GetArrayLength();

    /// <summary>
    /// Реквизиты с одним полем, вписанным СЫРЫМ текстом: «1e400» объектом не собрать. Прежнее значение
    /// поля из набора убирается — два одноимённых поля разбирались бы «кто последний».
    /// </summary>
    private static string WithField(string requisites, string key, string raw)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(requisites)!;
        fields.Remove(key);
        var rest = string.Join(",", fields.Select(f => JsonSerializer.Serialize(f.Key) + ":" + f.Value.GetRawText()));
        return "{" + rest + (rest.Length > 0 ? "," : string.Empty) + JsonSerializer.Serialize(key) + ":" + raw + "}";
    }

    private async Task<int> ParsedRecordsAsync(Guid invoice)
    {
        using var scope = host.Services.CreateScope();
        var journal = scope.ServiceProvider.GetRequiredService<IActivityLog>();
        var records = await journal.ReadAsync(0, 200, ActivityVisibility.Whole, "costs.invoice.parsed");
        return records.Count(r => r.TargetId == invoice.ToString());
    }
}
