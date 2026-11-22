using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Activity;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

[Collection("Integration")]
public partial class InvoiceDuplicatePaymentTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    [Fact]
    public async Task Совпадение_блокирует_обоих_а_отклонение_и_возврат_меняют_запрет()
    {
        var (client, _) = await SignInAsync("Admin");
        var number = UniqueNumber();
        var one = await CreateNamedAsync(client, number);
        var other = await CreateNamedAsync(client, number, total: 350m);
        var today = await TodayAsync();

        Assert.Equal(other, Blocker(await ReadAsync(client, one)).GetProperty("id").GetGuid());
        var otherView = await ReadAsync(client, other);
        Assert.Equal(one, Blocker(otherView).GetProperty("id").GetGuid());
        Assert.Equal("discrepancy", Payment(otherView).GetProperty("moneyRefusalKind").GetString());
        var preview = await PreviewAsync(client, one, today);
        Assert.Equal("duplicate", preview.GetProperty("refusalKind").GetString());
        Assert.Empty(preview.GetProperty("rows").EnumerateArray());
        Assert.Equal("", preview.GetProperty("stamp").GetString());
        Assert.Equal(Payment(await ReadAsync(client, one)).GetProperty("refusal").GetString(),
            preview.GetProperty("refusal").GetString());

        // Разобрать и сохранить по-прежнему можно. Сумма и разноска согласованы у первого счёта.
        var edited = await UpdateAsync(client, one, "Номер", number);
        Assert.Equal(other, Blocker(edited).GetProperty("id").GetGuid());
        var (site, _) = await SiteAsync("Сверка дублей");
        var line = LineId(await ReadAsync(client, one), 1);
        await AllocateAsync(client, one, line, [Part(site, quantity: 1)]);
        await OkAsync(await client.PostAsync(Url(one, "/parsed"), null));

        await OkAsync(await client.PostAsync(Url(other, "/rejected"), null));
        Assert.Empty(Blockers(await ReadAsync(client, one)));
        Assert.Equal("rejected", Payment(await ReadAsync(client, other)).GetProperty("refusalKind").GetString());
        await OkAsync(await client.PostAsync(Url(other, "/draft"), null));
        Assert.Equal(other, Blocker(await ReadAsync(client, one)).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Оплаченный_двойник_блокирует_новую_оплату_а_общий_файл_сам_по_себе_нет()
    {
        var (client, _) = await SignInAsync("Admin");
        var number = UniqueNumber();
        var one = await CreateNamedAsync(client, number);
        var other = await CreateNamedAsync(client, UniqueNumber());
        var file = $"%PDF-1.4\n{Guid.NewGuid():N}";
        await AttachAsync(client, one, "первый.pdf", file);
        await AttachAsync(client, other, "второй.pdf", file);
        var view = await ReadAsync(client, other);
        Assert.Single(view.GetProperty("sameFile").EnumerateArray());
        Assert.Empty(Blockers(view));
        var today = await TodayAsync();
        await OkAsync(await PayAsync(client, one, today, Stamp(await PreviewAsync(client, one, today))));
        var paid = await client.GetFromJsonAsync<JsonElement>(Url(one, "/paid"));
        await UpdateAsync(client, other, "Номер", number);
        Assert.True(Blocker(await ReadAsync(client, other)).GetProperty("paid").GetBoolean());
        Assert.Equal(paid.GetRawText(), (await client.GetFromJsonAsync<JsonElement>(Url(one, "/paid"))).GetRawText());
        Assert.Equal(HttpStatusCode.Conflict, (await PayAsync(client, other, today, "старый расклад")).StatusCode);
        await OkAsync(await client.PostAsJsonAsync(Url(one, "/unpaid"), new { reason = "Платёж отозван" }));
        Assert.Equal(other, Blocker(await ReadAsync(client, one)).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Подтверждение_симметрично_и_повтор_сохраняет_исходное_событие()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (client, author) = await SignInAsync("Accountant");
        var (secondClient, _) = await SignInAsync("Admin");
        var number = UniqueNumber();
        var one = await CreateNamedAsync(admin, number);
        var two = await CreateNamedAsync(admin, number);
        var three = await CreateNamedAsync(admin, number);
        var av = await ReadAsync(client, one);
        var bv = await ReadAsync(secondClient, two);

        var response = await ConfirmAsync(client, one, Version(av), two, Revision(av, two), "  Разные поставки  ");
        await OkAsync(response);
        Assert.Equal(three, Blocker(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        Assert.True((await ReadAsync(client, one)).GetProperty("duplicates").EnumerateArray()
            .Single(d => d.GetProperty("id").GetGuid() == two).GetProperty("confirmed").GetBoolean());
        await OkAsync(await ConfirmAsync(secondClient, two, Version(bv), one, Revision(bv, one), "Другая причина"));
        Assert.Equal(three, Blocker(await ReadAsync(client, two)).GetProperty("id").GetGuid());
        Assert.Equal(2, Blockers(await ReadAsync(client, three)).Length);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var saved = Assert.Single(await db.InvoiceDuplicateConfirmations.AsNoTracking()
            .Where(p => p.FirstInvoiceId == one || p.SecondInvoiceId == one).ToListAsync());
        Assert.True(saved.FirstInvoiceId.CompareTo(saved.SecondInvoiceId) < 0);
        Assert.NotNull(saved.JournaledAt);
        var journal = scope.ServiceProvider.GetRequiredService<IActivityLog>();
        var entry = Assert.Single(await journal.ExportAsync(), r => r.Action == "costs.invoice.duplicate-confirmed"
            && r.TargetId != null && r.TargetId.Contains(one.ToString()));
        Assert.Equal(saved.EventId, entry.Id);
        Assert.Equal(saved.ConfirmedAt, entry.OccurredAt);
        Assert.Equal(author, entry.ActorId);
        Assert.Equal(saved.AuthorName, entry.ActorName);
        Assert.Contains(two.ToString(), entry.TargetId);
        Assert.Contains(number, entry.TargetLabel);
        Assert.Equal("Разные поставки", entry.After);
    }

    [Fact]
    public async Task Подтверждение_требует_право_причину_и_актуальность_обеих_сторон()
    {
        var (client, _) = await SignInAsync("Admin");
        var (supplierClient, _) = await SignInAsync("Supplier");
        var number = UniqueNumber();
        var one = await CreateNamedAsync(client, number);
        var two = await CreateNamedAsync(client, number);
        var before = await ReadAsync(client, one);
        var revision = Revision(before, two);
        const string reason = "Разные договоры";

        Assert.Equal(HttpStatusCode.Forbidden,
            (await ConfirmAsync(supplierClient, one, Version(before), two, revision, reason)).StatusCode);
        // Базовый клиент фабрики не добавляет If-Match к пишущим запросам.
        using var bare = ((Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>)host).CreateClient();
        bare.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;
        Assert.Equal(HttpStatusCode.BadRequest, (await ConfirmAsync(bare, one, null, two, revision, reason)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await ConfirmAsync(client, one, Version(before), two, revision, "  ")).StatusCode);

        var updated = await UpdateAsync(client, one, "Итого", 190m);
        Assert.Equal(HttpStatusCode.Conflict,
            (await ConfirmAsync(client, one, Version(before), two, revision, reason)).StatusCode);
        await UpdateAsync(client, two, "Дата", "2026-09-30");
        await UpdateAsync(client, two, "Дата", "2026-09-29");
        Assert.Equal(HttpStatusCode.Conflict,
            (await ConfirmAsync(client, one, Version(updated), two, revision, reason)).StatusCode);
        var fresh = await ReadAsync(client, one);
        var freshRevision = Revision(fresh, two);
        await OkAsync(await client.PostAsync(Url(two, "/rejected"), null));
        Assert.Equal(HttpStatusCode.Conflict,
            (await ConfirmAsync(client, one, Version(fresh), two, freshRevision, reason)).StatusCode);
        await OkAsync(await client.PostAsync(Url(two, "/draft"), null));
        await OkAsync(await client.DeleteAsync(Url(two)));
        Assert.Equal(HttpStatusCode.Conflict,
            (await ConfirmAsync(client, one, Version(fresh), two, freshRevision, reason)).StatusCode);
    }

    private static string UniqueNumber() => $"ДУБ-{Guid.NewGuid():N}";
    private static string Url(Guid id, string? suffix = null) => $"/api/costs/invoices/{id}{suffix}";
    private static JsonElement Payment(JsonElement view) => view.GetProperty("payment");
    private static JsonElement[] Blockers(JsonElement view) => Payment(view).GetProperty("duplicateBlockers").EnumerateArray().ToArray();
    private static JsonElement Blocker(JsonElement view) => Assert.Single(Blockers(view));
    private static Guid Revision(JsonElement view, Guid other) => Blockers(view)
        .Single(row => row.GetProperty("id").GetGuid() == other).GetProperty("otherRevision").GetGuid();
    private static string Version(JsonElement view) => view.GetProperty("version").GetString()!;
    private static string Stamp(JsonElement view) => view.GetProperty("stamp").GetString()!;

    private async Task<DateOnly> TodayAsync()
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IModuleClock>().TodayAsync();
    }

    private static async Task<Guid> CreateNamedAsync(HttpClient client, string number, decimal total = 100m, bool lines = true)
    {
        using var response = await client.PostAsJsonAsync("/api/costs/invoices", new
        {
            requisites = new Dictionary<string, object?>
            {
                ["Номер"] = number, ["Дата"] = "2026-09-29", ["Итого"] = total,
                ["Поставщик"] = Reference(supplier), ["Плательщик"] = Reference(payer),
            },
        });
        await OkAsync(response);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        if (lines) await LinesAsync(client, id, [Line(cable, quantity: 1, price: 100m)]);
        return id;
    }

    private static async Task<JsonElement> UpdateAsync(HttpClient client, Guid id, string key, object value)
    {
        using var response = await client.PutAsJsonAsync(Url(id), new { requisites = await RequisitesWithAsync(client, id, key, value) });
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> ConfirmAsync(HttpClient client, Guid id, string? version, Guid other, Guid revision, string reason) =>
        SendAsync(client, Url(id, "/duplicate-confirmations"), version, new { otherInvoiceId = other, otherRevision = revision, reason });

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string url, string? version, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", version);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> PreviewAsync(HttpClient client, Guid id, DateOnly paidOn)
    {
        using var response = await client.PostAsJsonAsync(Url(id, "/paid/preview"), new { paidOn });
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<HttpResponseMessage> PayAsync(HttpClient client, Guid id, DateOnly paidOn, string seen, string? version = null)
    {
        version ??= Version(await ReadAsync(client, id));
        return await SendAsync(client, Url(id, "/paid"), version, new { paidOn, seen });
    }

    private static async Task<JsonElement> AttachAsync(HttpClient client, Guid id, string name, string content)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", name);
        using var response = await client.PostAsync(Url(id, "/scan"), form);
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
