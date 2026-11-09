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

    /// <summary>
    /// Сценарий спецификации «Распознанный счёт с давней датой»: в ленте счета от 19 и 22 октября, последним
    /// брошен файл без даты, и потом ему проставлена дата 9 октября. Счёт первый и до, и после.
    ///
    /// <para>Даты подобраны так, чтобы прежний порядок (по дате счёта, без даты — первым) на каждом шаге
    /// давал другую ленту. До даты он переставил бы два старых счёта между собой, после — увёл бы брошенный
    /// вниз. Совпади порядки хоть на одном шаге — возврат к сортировке по дате (и по дате перед моментом
    /// заведения) прошёл бы эту проверку зелёным.</para>
    /// </summary>
    [Fact]
    public async Task Брошенный_последним_первый_и_до_и_после_даты()
    {
        var (client, _) = await SignInAsync("Admin");

        var later = await CreateOnAsync(client, "2026-10-22");
        var earlier = await CreateOnAsync(client, "2026-10-19");
        var dropped = await CreateOnAsync(client, null);
        await CreatedAtAsync(later, Morning);
        await CreatedAtAsync(earlier, Morning.AddMinutes(1));
        await CreatedAtAsync(dropped, Morning.AddMinutes(2));

        Guid[] expected = [dropped, earlier, later];
        Assert.Equal(expected, await OrderAsync(client, "/api/costs/invoices", expected));
        Assert.Equal(expected, await OrderAsync(client, "/api/costs/invoices?needsParsing=true", expected));

        // Дата — правкой шапки. Распознавание пишет её на сервере, своим путём (InvoiceScanReading), но
        // место от пути записи не зависит: ключ ленты — CreatedAt, а его пишет только Invoice.Create.
        await DatedAsync(client, dropped, "2026-10-09");
        Assert.Equal(expected, await OrderAsync(client, "/api/costs/invoices", expected));
        Assert.Equal(expected, await OrderAsync(client, "/api/costs/invoices?needsParsing=true", expected));
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
