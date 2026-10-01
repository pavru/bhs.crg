using System.Text.Json;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Счета с ГРЯЗНЫМИ данными для тестов таблицы (задача G1c, issue #1090): в числовом поле схемы лежат
/// и числа, и «12 шт», в поле даты — «скоро», у части счетов нет срока, суммы, поставщика. На чистых
/// данных два исполнителя отбора согласны всегда — расходятся они именно на таких.
///
/// <para>Счета у каждого теста свои, с меткой в номере: стенд общий, и отбор «номер начинается с
/// метки» отделяет свои строки от чужих.</para>
/// </summary>
public abstract class ModuleTableSeededTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private readonly InvoiceLineHost host = host;

    protected sealed record Seed(string Tag, string Weight, string Warranty, string Note);

    /// <summary>Восемь счетов с грязными данными; номера — «{метка}-1…8».</summary>
    protected async Task<Seed> SeedAsync(HttpClient client)
    {
        var tag = $"П{Guid.NewGuid().ToString("N")[..6]}";
        var seed = new Seed(tag, $"Вес_{tag}", $"Гарантия_{tag}", $"Пометка_{tag}");

        var ids = new List<Guid>();
        for (var i = 0; i < 8; i++) ids.Add(await CreateAsync(client));

        using var scope = host.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.AppDbContext>();
        var type = await core.DocumentTypes.SingleAsync(t => t.Code == CostsRecordTypes.InvoiceCode);
        var root = System.Text.Json.Nodes.JsonNode.Parse(type.Schema.RootElement.GetRawText())!.AsObject();
        foreach (var (key, kind) in new[] { (seed.Weight, "number"), (seed.Warranty, "date"), (seed.Note, "string") })
            root["fields"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject { ["key"] = key, ["title"] = key, ["type"] = kind });
        type.UpdateSchema(JsonDocument.Parse(root.ToJsonString()));
        await core.SaveChangesAsync();

        // Прямо в базу: через адрес счёта нечисло в числовое поле не положить — а на живых данных оно
        // лежит (распознавание, загрузка), и исполнители расходятся именно на нём.
        var rows = new (string? Purpose, decimal? Total, string? Due, Guid? Supplier, string Payment, string Data)[]
        {
            ("Оплата за кабель", 110.00m, "2026-05-15", supplier, "Unpaid", $$"""{"{{seed.Weight}}": 12.5, "{{seed.Warranty}}": "2026-05-01", "{{seed.Note}}": "срочно, до пятницы"}"""),
            ("ОПЛАТА ЗА КАБЕЛЬ", 110m, "2026-05-20", supplier, "Paid", $$"""{"{{seed.Weight}}": "12 шт", "{{seed.Warranty}}": "скоро"}"""),
            ("Оплата: кабель и труба", 5000m, "2026-06-30", supplier, "Partial", $$"""{"{{seed.Weight}}": 7, "{{seed.Warranty}}": "2026-07-01T00:00:00"}"""),
            ("Аванс", 50m, "2026-07-01", supplier, "Unpaid", $$"""{"{{seed.Weight}}": "7", "{{seed.Warranty}}": "2026-12-31"}"""),
            ("Труба", 99.99m, null, supplier, "Unpaid", $$"""{"{{seed.Weight}}": 100, "{{seed.Note}}": "СРОЧНО"}"""),
            (null, 1500m, null, payer, "Paid", "{}"),
            ("", null, null, null, "Unpaid", $$"""{"{{seed.Weight}}": "много"}"""),
            (null, null, null, null, "Unpaid", "{}"),
        };

        var costs = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        for (var i = 0; i < rows.Length; i++)
        {
            var (purpose, total, due, org, payment, data) = rows[i];
            var number = $"{tag}-{i + 1}";
            DateOnly? dueDate = due is null ? null : DateOnly.Parse(due);
            await costs.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE costs.invoices
                SET number = {number}, purpose = {purpose}, total = {total}, due_date = {dueDate},
                    supplier_id = {org}, payment = {payment}, data = {data}::jsonb
                WHERE id = {ids[i]}
                """);
        }

        return seed;
    }
}
