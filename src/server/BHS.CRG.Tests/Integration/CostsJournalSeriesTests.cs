using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Activity;
using BHS.CRG.Modules.Costs.Endpoints;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Правка счёта сворачивается в журнале (ТЗ COST-6, CORE-28.3): шапка и строки черновика — двумя
/// рядами, всё значимое между ними — отдельно.
///
/// <para>Через ЖИВЫЕ адреса: свёртку объявляет вид модуля, порт несёт её в журнал ядра, а запись
/// делают обработчики. Проверка через службу журнала подтвердила бы только саму службу, а потеря
/// свёртки на любом шаге пути выглядела бы как исправный журнал из одиночных строк.</para>
/// </summary>
[Collection("Integration")]
public class CostsJournalSeriesTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    [Fact]
    public async Task Сессия_правки_черновика_два_ряда_и_без_значений_полей()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        object[] lines = [];

        for (var i = 0; i < 8; i++)
        {
            await UpdateAsync(client, invoice, i % 2 == 0 ? "Номер" : "Дата",
                i % 2 == 0 ? $"СЧ-{Guid.NewGuid().ToString()[..6]}" : $"2026-09-{10 + i}");
            if (i < 6)
            {
                lines = [.. lines, Line(cable, quantity: i + 1, price: 1234.56m)];
                await LinesAsync(client, invoice, lines);
            }
        }

        var rows = await RowsAsync(invoice);
        var header = Assert.Single(rows, r => r.Action == InvoiceActions.Changed.Code);
        var edited = Assert.Single(rows, r => r.Action == InvoiceActions.LinesChanged.Code);
        Assert.Equal(8, header.Count);
        Assert.Equal(6, edited.Count);
        Assert.Equal(header.SessionId, edited.SessionId);
        Assert.StartsWith("строк: 6", edited.After);

        // Ряд «Счёт изменён» называет поля — и ни одного значения: ни номера, ни суммы (#1190).
        Assert.Equal("Номер, Дата", header.Before);
        Assert.DoesNotContain("СЧ-", header.Before);
        Assert.DoesNotContain("1234", $"{header.Before}{header.After}{edited.Before}{edited.After}");
    }

    [Fact]
    public async Task Разобран_посреди_правки_стоит_отдельным_рядом()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await AllocatedLinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m)]);

        await UpdateAsync(client, invoice, "Номер", "СЧ-первый");
        await UpdateAsync(client, invoice, "Номер", "СЧ-второй");
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));
        await UpdateAsync(client, invoice, "Номер", "СЧ-третий");
        await UpdateAsync(client, invoice, "Номер", "СЧ-четвёртый");

        var rows = Chronological(await RowsAsync(invoice), InvoiceActions.Changed, InvoiceActions.Parsed);
        Assert.Equal(
            [(InvoiceActions.Changed.Code, 2), (InvoiceActions.Parsed.Code, 1), (InvoiceActions.Changed.Code, 2)],
            rows.Select(r => (r.Action, r.Count)));
    }

    [Fact]
    public async Task Возврат_в_черновик_сервером_рвёт_сессию_правки_строк()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await AllocatedLinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m)]);
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));

        await LinesAsync(client, invoice, [Line(null, quantity: 1, price: 100m, text: "позицию сняли")]);
        await LinesAsync(client, invoice, [Line(null, quantity: 2, price: 100m, text: "позицию сняли")]);

        var rows = await RowsAsync(invoice);
        var returned = Assert.Single(rows, r => r.Action == InvoiceActions.Draft.Code);
        Assert.Equal(1, returned.Count);
        // Правка строк после возврата — не продолжение правки до «разобран»: между ними чужое событие.
        var edits = rows.Where(r => r.Action == InvoiceActions.LinesChanged.Code).ToList();
        Assert.True(edits.Count >= 2, "Правка строк до и после возврата склеилась в один ряд.");
        Assert.Equal(edits.Count, edits.Select(r => r.SessionId).Distinct().Count());
    }

    [Fact]
    public async Task Всё_верно_трижды_три_ряда()
    {
        var (client, _) = await SignInAsync("Admin");
        string[] fields = ["Номер", "Дата", "Поставщик"];
        var created = await client.PostAsJsonAsync("/api/costs/invoices", new
        {
            requisites = new Dictionary<string, object?>
            {
                ["Номер"] = $"СЧ-{Guid.NewGuid().ToString()[..6]}",
                ["Дата"] = "2026-09-29",
                ["Поставщик"] = Reference(supplier),
            },
            unconfirmed = fields,
        });
        await OkAsync(created);
        var invoice = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        foreach (var field in fields)
            await OkAsync(await client.PostAsJsonAsync($"/api/costs/invoices/{invoice}/confirmed", new { fields = new[] { field } }));

        var confirmed = (await RowsAsync(invoice)).Where(r => r.Action == InvoiceActions.Confirmed.Code).ToList();
        Assert.Equal(3, confirmed.Count);
        Assert.All(confirmed, r => Assert.Equal(1, r.Count));
    }

    /// <summary>
    /// Ряды журнала об этом счёте. Страница — первая: тесты собрания идут по очереди, и ряды только что
    /// сделанного счёта — самые свежие.
    /// </summary>
    private async Task<List<ActivityRow>> RowsAsync(Guid invoice)
    {
        using var scope = host.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IActivityLog>()
            .ReadAsync(0, 200, ActivityVisibility.Whole);
        return [.. rows.Where(r => r.TargetId == invoice.ToString())];
    }

    private static List<ActivityRow> Chronological(IEnumerable<ActivityRow> rows,
        params BHS.CRG.Modules.Ports.ModuleActivityAction[] actions) =>
        [.. rows.Where(r => actions.Any(a => a.Code == r.Action)).OrderBy(r => r.OccurredAt)];

    /// <summary>Правка одного поля шапки — как её присылает форма: все реквизиты, одно изменено.</summary>
    private static async Task UpdateAsync(HttpClient client, Guid invoice, string key, object value)
    {
        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(client, invoice, key, value) });
        await OkAsync(response);
    }
}
