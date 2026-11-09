using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Порядок ленты счетов — по моменту заведения, новые сверху, под любым отбором ([COST-6.3], issue #1818,
/// п. 3). Раньше лента шла по дате счёта: брошенный последним файл от давнего числа после распознавания
/// уезжал вниз, под счета, заведённые раньше, — и его приходилось искать.
///
/// <para>Момент заведения ставится тестом явно, а не берётся «как получилось»: счета, заведённые подряд,
/// различаются на доли миллисекунды, и проверка порядка держалась бы на скорости машины. Моменты — в
/// прошлом: заведённые соседними тестами счета остаются выше и сравнению не мешают, сравниваются только
/// свои.</para>
/// </summary>
// ⚠️ Собрание указывается КАЖДОМУ классу: атрибут не наследуется от базового.
[Collection("Integration")]
public class InvoiceListOrderTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private static readonly DateTimeOffset Morning = new(2020, 1, 8, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Лента_идёт_по_заведению_а_не_по_дате_счёта()
    {
        var (client, _) = await SignInAsync("Admin");

        // Заведены по очереди: давняя дата — новая — без даты. По дате счёта порядок был бы
        // «без даты, новая, давняя» случайно совпавшим; поэтому последним заводится счёт с ДАВНЕЙ датой.
        var undated = await CreateOnAsync(client, null);
        var recent = await CreateOnAsync(client, "2026-10-22");
        var old = await CreateOnAsync(client, "2026-10-09");
        await CreatedAtAsync(undated, Morning);
        await CreatedAtAsync(recent, Morning.AddMinutes(1));
        await CreatedAtAsync(old, Morning.AddMinutes(2));

        Guid[] expected = [old, recent, undated];
        Assert.Equal(expected, await OrderAsync(client, "/api/costs/invoices", expected));
        Assert.Equal(expected, await OrderAsync(client, "/api/costs/invoices?needsParsing=true", expected));

        // Дата счёта, проставленная позже (так её ставит распознавание), места не меняет: счёт без даты
        // получает самую давнюю — и остаётся последним по заведению, а не уходит за дату.
        await DatedAsync(client, undated, "2026-01-15");
        await DatedAsync(client, old, "2026-12-31");
        Assert.Equal(expected, await OrderAsync(client, "/api/costs/invoices", expected));
    }

    /// <summary>
    /// Пакет файлов заводит счета в одну миллисекунду. Без второго ключа их порядок решала бы база — и
    /// мог бы меняться от чтения к чтению, а с ним сосед «‹ N из M ›».
    /// </summary>
    [Fact]
    public async Task Заведённые_в_один_момент_стоят_в_постоянном_порядке()
    {
        var (client, _) = await SignInAsync("Admin");
        var first = await CreateOnAsync(client, "2026-10-01");
        var second = await CreateOnAsync(client, "2026-10-02");
        await CreatedAtAsync(first, Morning.AddHours(1));
        await CreatedAtAsync(second, Morning.AddHours(1));

        Guid[] mine = [first, second];
        var once = await OrderAsync(client, "/api/costs/invoices", mine);
        var again = await OrderAsync(client, "/api/costs/invoices", mine);

        Assert.Equal(once, again);
        // Не только постоянный, но и названный: ничью решает ключ по убыванию. Сравнением строк, а не Guid:
        // база упорядочивает uuid побайтно, в порядке записи, и строка ему равна.
        Assert.Equal([.. mine.OrderByDescending(id => id.ToString(), StringComparer.Ordinal)], once);
    }

    private static async Task<Guid> CreateOnAsync(HttpClient client, string? issuedOn)
    {
        var requisites = new Dictionary<string, object?>
        {
            ["Номер"] = $"СЧ-П{Guid.NewGuid().ToString()[..6]}",
            ["Дата"] = issuedOn,
            ["Поставщик"] = Reference(supplier),
        };
        var response = await client.PostAsJsonAsync("/api/costs/invoices", new { requisites });
        await OkAsync(response);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task DatedAsync(HttpClient client, Guid invoice, string issuedOn) =>
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(client, invoice, "Дата", issuedOn) }));

    private async Task CreatedAtAsync(Guid invoice, DateTimeOffset at)
    {
        using var scope = host.Services.CreateScope();
        var costs = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var changed = await costs.Invoices.Where(i => i.Id == invoice)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.CreatedAt, at));
        Assert.Equal(1, changed);
    }

    /// <summary>Свои счета — в том порядке, в каком они стоят в ленте.</summary>
    private static async Task<Guid[]> OrderAsync(HttpClient client, string address, Guid[] mine) =>
        [.. (await client.GetFromJsonAsync<JsonElement>(address))
            .EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Where(mine.Contains)];
}
