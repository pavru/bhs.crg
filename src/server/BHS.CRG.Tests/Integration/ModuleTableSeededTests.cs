using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.AspNetCore.Identity;
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

    protected const string Address = "costs.invoices";
    protected const string Marker = "system:table:" + Address;

    // ── Два исполнителя ───────────────────────────────────────────────────────

    /// <summary>Экран таблицы: отбор исполняет запрос к базе. Отдаёт номера подошедших счетов.</summary>
    protected static async Task<List<string>> SqlAsync(HttpClient client, string filter)
    {
        var response = await client.GetAsync(
            $"/api/tables/{Address}?columns=Номер&limit=1000&filter={Uri.EscapeDataString(filter)}");
        await OkAsync(response);
        var table = await response.Content.ReadFromJsonAsync<JsonElement>();
        return [.. table.GetProperty("rows").EnumerateArray()
            .Select(r => r.GetProperty("Номер").GetString() ?? "")
            .OrderBy(n => n, StringComparer.Ordinal)];
    }

    /// <summary>Набор данных на той же таблице: строки целиком, отбор — в памяти.</summary>
    protected async Task<DataSetParseResult> MemoryRowsAsync(Guid user)
    {
        using var scope = host.Services.CreateScope();
        var access = await scope.ServiceProvider.GetRequiredService<DataAccessResolver>().ForUserAsync(user, default);
        var provider = scope.ServiceProvider.GetServices<ISystemDataProvider>().Single(p => p.Handles(Marker));
        return await provider.ProvideAsync(Marker, CatalogScope.System, null, access, default);
    }

    protected static List<string> Numbers(IEnumerable<IReadOnlyDictionary<string, string?>> rows) =>
        [.. rows.Select(r => r.GetValueOrDefault("Номер") ?? "").OrderBy(n => n, StringComparer.Ordinal)];

    /// <summary>Своя роль с одним правом — системных ролей «модуль есть, счетов нет» не бывает.</summary>
    protected async Task<string> RoleAsync(string permission)
    {
        var name = $"Narrow_{Guid.NewGuid():N}";
        using var scope = host.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var role = new IdentityRole<Guid>(name);
        Assert.True((await roles.CreateAsync(role)).Succeeded);
        Assert.True((await roles.AddClaimAsync(role, new Claim(RoleSynchronizer.PermissionClaim, permission))).Succeeded);
        return name;
    }

    /// <summary>Источник набора данных на таблице счетов — в своём системном наборе; отдаёт его id.</summary>
    protected static async Task<Guid> SourceAsync(HttpClient client)
    {
        var file = await client.PostAsJsonAsync("/api/datasets/files/system", new { scope = "System", name = "Системные" });
        await OkAsync(file);
        var fileId = (await file.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var created = await client.PostAsJsonAsync($"/api/datasets/files/{fileId}/sources",
            new { name = $"Счета {Guid.NewGuid():N}", sheetOrPath = Marker });
        await OkAsync(created);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    // ── Посев ─────────────────────────────────────────────────────────────────

    /// <summary>Сколько полей в схеме типа счёта — общей на все тесты хоста.</summary>
    protected async Task<int> InvoiceFieldsAsync()
    {
        using var scope = host.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.AppDbContext>();
        var type = await core.DocumentTypes.AsNoTracking().SingleAsync(t => t.Code == CostsRecordTypes.InvoiceCode);
        return type.Schema.RootElement.GetProperty("fields").GetArrayLength();
    }

    protected sealed record Seed(string Tag, string Weight, string Warranty, string Note);

    /// <summary>Восемь счетов с грязными данными; номера — «{метка}-1…8».</summary>
    protected async Task<Seed> SeedAsync(HttpClient client)
    {
        var tag = $"П{Guid.NewGuid().ToString("N")[..6]}";
        var seed = new Seed(tag, "ПробаВес", "ПробаГарантия", "ПробаПометка");

        var ids = new List<Guid>();
        for (var i = 0; i < 8; i++) ids.Add(await CreateAsync(client));

        using var scope = host.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.AppDbContext>();
        var type = await core.DocumentTypes.SingleAsync(t => t.Code == CostsRecordTypes.InvoiceCode);
        var root = System.Text.Json.Nodes.JsonNode.Parse(type.Schema.RootElement.GetRawText())!.AsObject();
        var fields = root["fields"]!.AsArray();
        var known = fields.Select(f => f!["key"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);

        // Поля схемы — ОДНИ на все тесты, и заводятся один раз. Прежде у каждого посева были свои
        // («Вес_{метка}»), и каждый дописывал в общий тип три поля: за прогон схема вырастала на сотни
        // колонок, а за десятки прогонов — до 578 (issue #1142), и каждое чтение таблицы отдавало по
        // колонке на каждое. Своими у теста остаются СТРОКИ: значения полей лежат в счёте, а счета
        // отделяет метка в номере.
        var missing = new[] { (seed.Weight, "number"), (seed.Warranty, "date"), (seed.Note, "string") }
            .Where(f => !known.Contains(f.Item1)).ToList();
        foreach (var (key, kind) in missing)
            fields.Add(new System.Text.Json.Nodes.JsonObject { ["key"] = key, ["title"] = key, ["type"] = kind });
        if (missing.Count > 0)
        {
            type.UpdateSchema(JsonDocument.Parse(root.ToJsonString()));
            await core.SaveChangesAsync();
        }

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
