using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Activity;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Срок «оплатить до» по правилу из настройки модуля (ТЗ COST-9.1; задача C4, issue #1080).
///
/// <para>Сторожа задачи: срок подставляется от отгрузки и отсрочки; ручная правка держится и помечена;
/// «По правилу» её снимает; правило — настройка, а не код: смена настройки меняет подстановку новых
/// счетов. И решение владельца 14.10.2026 — записанные сроки смена правила не сдвигает.</para>
/// </summary>
public class InvoiceDueRuleTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private const string RuleKey = "costs.due.shipping-day-counts";

    [Fact]
    public async Task Срок_подставляется_правилом_от_отгрузки_и_отсрочки()
    {
        var (client, _) = await SignInAsync("Admin");

        var view = await CreateAsync(client, shipped: "2026-10-01", deferral: 10);

        // Умолчание — правило действующего реестра: день отгрузки — первый день отсрочки.
        Assert.Equal("2026-10-10", Due(view));
        Assert.False(view.GetProperty("due").GetProperty("manual").GetBoolean());
        Assert.Equal("2026-10-10", view.GetProperty("due").GetProperty("byRule").GetString());
    }

    [Fact]
    public async Task Правка_отгрузки_пересчитывает_срок_который_не_правили_руками()
    {
        var (client, _) = await SignInAsync("Admin");
        var id = Id(await CreateAsync(client, shipped: "2026-10-01", deferral: 10));

        // Форма присылает лежащий срок как есть — меняется только отгрузка.
        var view = await UpdateAsync(client, id, "ДатаОтгрузки", "2026-10-05");

        Assert.Equal("2026-10-14", Due(view));
        Assert.False(view.GetProperty("due").GetProperty("manual").GetBoolean());
    }

    [Fact]
    public async Task Ручной_срок_держится_помечен_и_записан_в_журнал()
    {
        var (client, _) = await SignInAsync("Admin");
        var id = Id(await CreateAsync(client, shipped: "2026-10-01", deferral: 10));

        var byHand = await UpdateAsync(client, id, "Срок", "2026-10-20");
        Assert.Equal("2026-10-20", Due(byHand));
        Assert.True(byHand.GetProperty("due").GetProperty("manual").GetBoolean());

        // Отгрузка сменилась — ручной срок правило обходит стороной.
        var shifted = await UpdateAsync(client, id, "ДатаОтгрузки", "2026-10-05");
        Assert.Equal("2026-10-20", Due(shifted));
        Assert.Equal("2026-10-14", shifted.GetProperty("due").GetProperty("byRule").GetString());

        var record = Assert.Single(await RecordsAsync(id, "costs.invoice.due-by-hand"));
        Assert.Equal(("10.10.2026", "20.10.2026"), (record.Before, record.After));
    }

    [Fact]
    public async Task По_правилу_снимает_ручную_правку_и_подставляет_срок()
    {
        var (client, _) = await SignInAsync("Admin");
        var id = Id(await CreateAsync(client, shipped: "2026-10-01", deferral: 10));
        await UpdateAsync(client, id, "Срок", "2026-10-20");

        var response = await client.PostAsync($"/api/costs/invoices/{id}/due/rule", null);
        await OkAsync(response);
        var view = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("2026-10-10", Due(view));
        Assert.False(view.GetProperty("due").GetProperty("manual").GetBoolean());
        var record = Assert.Single(await RecordsAsync(id, "costs.invoice.due-by-rule"));
        Assert.Equal(("20.10.2026", "10.10.2026"), (record.Before, record.After));

        // Повтор менять нечего — и в журнал он не идёт.
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{id}/due/rule", null));
        Assert.Single(await RecordsAsync(id, "costs.invoice.due-by-rule"));
    }

    /// <summary>
    /// Правило — настройка, а не код (сторож «зашить правило вместо настройки — падает»): выключенный
    /// флаг меняет подстановку у нового счёта. А записанный срок смена не трогает даже правкой счёта —
    /// решение владельца 14.10.2026, — и ответ называет, что дало бы правило теперь.
    /// </summary>
    [Fact]
    public async Task Смена_правила_меняет_подстановку_но_не_записанные_сроки()
    {
        var (client, _) = await SignInAsync("Admin");
        var old = Id(await CreateAsync(client, shipped: "2026-10-01", deferral: 10));

        try
        {
            await OkAsync(await RuleAsync(client, "false"));

            var fresh = await CreateAsync(client, shipped: "2026-10-01", deferral: 10);
            Assert.Equal("2026-10-11", Due(fresh));

            var untouched = await UpdateAsync(client, old, "Назначение", "кабель для этажа 3");
            Assert.Equal("2026-10-10", Due(untouched));
            Assert.Equal("2026-10-11", untouched.GetProperty("due").GetProperty("byRule").GetString());
        }
        finally
        {
            await OkAsync(await RuleAsync(client, null));
        }
    }

    [Fact]
    public async Task Без_даты_отгрузки_срок_не_определён()
    {
        var (client, _) = await SignInAsync("Admin");

        var view = await CreateAsync(client, shipped: null, deferral: 10);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("requisites").GetProperty("Срок").ValueKind);

        var id = Id(await UpdateAsync(client, Id(view), "ДатаОтгрузки", "2026-10-01"));
        var cleared = await UpdateAsync(client, id, "ДатаОтгрузки", null);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("requisites").GetProperty("Срок").ValueKind);
        Assert.False(cleared.GetProperty("due").GetProperty("manual").GetBoolean());
    }

    /// <summary>
    /// «Просрочен» и «осталось дней» не хранятся (сторож задачи): у счёта таких колонок нет вовсе —
    /// признак считает выражение от срока и «сегодня» (<see cref="InvoiceDue" />), и сдвиг даты меняет
    /// его без записи.
    /// </summary>
    [Fact]
    public async Task Просрочен_не_хранится_у_счёта()
    {
        using var scope = host.Services.CreateScope();
        var entity = scope.ServiceProvider.GetRequiredService<CostsDbContext>().Model.FindEntityType(typeof(Invoice))!;

        var columns = entity.GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(columns, c => c.Contains("Overdue", StringComparison.OrdinalIgnoreCase)
            || c.Contains("DaysLeft", StringComparison.OrdinalIgnoreCase));

        var invoice = Invoice.Create(Guid.NewGuid(), null);
        invoice.ApplyRequisites(new InvoiceColumns(null, null, null, null, null, null, null,
            new DateOnly(2026, 10, 1), 10, null), JsonDocument.Parse("{}"), dueDateByHand: true, new InvoiceDueRule(true));
        Assert.False(InvoiceDue.OverdueOf(invoice, new DateOnly(2026, 10, 10)));
        Assert.True(InvoiceDue.OverdueOf(invoice, new DateOnly(2026, 10, 11)));
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    private static async Task<JsonElement> CreateAsync(HttpClient client, string? shipped, int? deferral)
    {
        var requisites = new Dictionary<string, object?>
        {
            ["Номер"] = $"СР-{Guid.NewGuid().ToString()[..6]}",
            ["Дата"] = "2026-09-29",
            ["Поставщик"] = Reference(supplier),
            ["ДатаОтгрузки"] = shipped,
            ["Отсрочка"] = deferral,
        };
        var response = await client.PostAsJsonAsync("/api/costs/invoices", new { requisites });
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Правка одного поля — как её присылает форма: все реквизиты, одно изменено.</summary>
    private static async Task<JsonElement> UpdateAsync(HttpClient client, Guid id, string key, object? value)
    {
        var requisites = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            (await ReadAsync(client, id)).GetProperty("requisites").GetRawText())!;
        requisites[key] = value;
        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{id}", new { requisites });
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> RuleAsync(HttpClient client, string? value) =>
        client.PutAsJsonAsync("/api/settings/modules/costs",
            new { values = new Dictionary<string, string?> { [RuleKey] = value } });

    private async Task<List<BHS.CRG.Domain.Activity.ActivityRecord>> RecordsAsync(Guid invoice, string action)
    {
        using var scope = host.Services.CreateScope();
        var records = await scope.ServiceProvider.GetRequiredService<IActivityLog>()
            .ReadAsync(0, 200, ActivityVisibility.Whole, action);
        return [.. records.Where(r => r.TargetId == invoice.ToString())];
    }

    private static Guid Id(JsonElement view) => view.GetProperty("id").GetGuid();

    private static string? Due(JsonElement view) =>
        view.GetProperty("requisites").GetProperty("Срок").GetString();
}
