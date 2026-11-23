using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Objects;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BHS.CRG.Tests.Integration;

public partial class InvoicePaymentTests
{
    [Fact]
    public async Task Карточка_предпросмотр_и_отказ_записи_называют_все_реквизиты()
    {
        var (admin, _) = await SignInAsync("Admin");
        var organization = await PaymentSupplierAsync();
        var invoice = await PaymentInvoiceAsync(admin, new()
        {
            ["Поставщик"] = Reference(organization), ["Итого"] = 0,
        });
        await DeletePaymentSupplierAsync(organization);
        var view = await ReadAsync(admin, invoice);
        var card = view.GetProperty("payment");
        var preview = await PreviewAsync(admin, invoice, await TodayAsync());
        var before = await PaymentRecordAsync(invoice);

        Assert.Equal("requisites", card.GetProperty("refusalKind").GetString());
        Assert.Equal(card.GetProperty("refusal").GetString(), preview.GetProperty("refusal").GetString());
        Assert.Equal(card.GetProperty("requisiteGaps").GetRawText(), preview.GetProperty("requisiteGaps").GetRawText());
        Assert.Equal([("Поставщик", "lost"), ("Номер", "missing"), ("Дата", "missing"), ("Итого", "missing")],
            RequisiteKeys(card));
        Assert.Empty(preview.GetProperty("rows").EnumerateArray());
        Assert.Equal("", preview.GetProperty("stamp").GetString());

        var readiness = view.GetProperty("readiness").GetProperty("gaps").EnumerateArray().ToArray();
        foreach (var (field, _) in RequisiteKeys(card))
            Assert.Contains("payment", ReadinessBlocks(readiness, field));
        Assert.Equal(["parsed"], ReadinessBlocks(readiness, "Плательщик"));

        var rejected = await PayAtVersionAsync(admin, invoice, view, await TodayAsync(), "ранее увиденный расклад");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var body = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("requisites", body.GetProperty("refusalKind").GetString());
        Assert.Equal(card.GetProperty("requisiteGaps").GetRawText(), body.GetProperty("requisiteGaps").GetRawText());
        Assert.Contains(preview.GetProperty("refusal").GetString()!, body.GetProperty("error").GetString());
        Assert.Equal(before, await PaymentRecordAsync(invoice));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Состояние_поставщика_после_предпросмотра_проверяется_повторно(bool archived)
    {
        var (admin, _) = await SignInAsync("Admin");
        var (invoice, _, _) = await TwoSitesAsync(admin);
        var organization = await PaymentSupplierAsync();
        await PutPaymentFieldAsync(admin, invoice, "Поставщик", Reference(organization));
        var view = await ReadAsync(admin, invoice);
        var today = await TodayAsync();
        var preview = await PreviewAsync(admin, invoice, today);
        var stamp = preview.GetProperty("stamp").GetString()!;
        Assert.NotEmpty(stamp);
        var before = await PaymentRecordAsync(invoice);

        if (archived)
        {
            using var scope = host.Services.CreateScope();
            Assert.Equal(ArchiveOutcome.Changed,
                await scope.ServiceProvider.GetRequiredService<IRecordArchive>().SetAsync(organization, archived: true));
        }
        else
            await DeletePaymentSupplierAsync(organization);

        var result = await PayAtVersionAsync(admin, invoice, view, today, stamp);
        if (archived)
        {
            await OkAsync(result);
            var payment = (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("payment");
            Assert.True(payment.GetProperty("paid").GetBoolean());
            Assert.Empty(RequisiteKeys(payment));
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
            var current = await PreviewAsync(admin, invoice, today);
            Assert.Equal([("Поставщик", "lost")], RequisiteKeys(current));
            var refusal = await result.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(RequisiteKeys(current), RequisiteKeys(refusal));
            Assert.Equal(before, await PaymentRecordAsync(invoice));
        }
    }

    [Fact]
    public async Task Старая_версия_отказывает_до_проверки_очищенного_номера()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (invoice, _, _) = await TwoSitesAsync(admin);
        var today = await TodayAsync();
        var old = await ReadAsync(admin, invoice);
        var preview = await PreviewAsync(admin, invoice, today);
        var stamp = preview.GetProperty("stamp").GetString()!;
        Assert.NotEmpty(stamp);
        await PutPaymentFieldAsync(admin, invoice, "Номер", null);
        var before = await PaymentRecordAsync(invoice);

        using var stale = await PayAtVersionAsync(admin, invoice, old, today, stamp);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("изменили", await ErrorAsync(stale));
        Assert.Equal(before, await PaymentRecordAsync(invoice));
        var fresh = await ReadAsync(admin, invoice);
        Assert.Equal([("Номер", "missing")], RequisiteKeys(fresh.GetProperty("payment")));
        using var refused = await PayAtVersionAsync(admin, invoice, fresh, today, stamp);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(before, await PaymentRecordAsync(invoice));
    }

    [Fact]
    public async Task Сохранённый_разобранный_без_номера_показывает_пробел_только_в_оплате()
    {
        var (admin, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(admin, complete: true, total: 50);
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CostsDbContext>().Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE costs.invoices SET number = NULL, state = 'Parsed' WHERE id = {invoice}");

        var view = await ReadAsync(admin, invoice);
        Assert.Equal("Разобран", view.GetProperty("requisites").GetProperty("Состояние").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("readiness").GetProperty("gaps").ValueKind);
        Assert.Equal([("Номер", "missing")], RequisiteKeys(view.GetProperty("payment")));
        Assert.Equal([("Номер", "missing")], RequisiteKeys(await PreviewAsync(admin, invoice, await TodayAsync())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Черновик_без_плательщика_и_без_разбора_можно_оплатить(bool withLine)
    {
        var (admin, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(admin, total: withLine ? 100 : -100);
        if (withLine) await LinesAsync(admin, invoice, [Line(null, 1, 100, text: "Без номенклатуры")]);
        var today = await TodayAsync();
        var preview = await PreviewAsync(admin, invoice, today);
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("refusal").ValueKind);
        Assert.Empty(RequisiteKeys(preview));
        var paid = await PayAsync(admin, invoice, today, preview, null);
        Assert.True(paid.GetProperty("payment").GetProperty("paid").GetBoolean());
        Assert.Equal("Черновик", paid.GetProperty("requisites").GetProperty("Состояние").GetString());
    }

    [Fact]
    public async Task Старую_оплату_с_пустым_номером_можно_прочитать_исправить_и_отменить()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, _, _) = await LockedAsync(admin, today);
        var url = $"/api/costs/invoices/{invoice}";
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CostsDbContext>().Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE costs.invoices SET number = NULL WHERE id = {invoice}");

        var before = await ReadAsync(admin, invoice);
        var payment = before.GetProperty("payment");
        Assert.True(payment.GetProperty("paid").GetBoolean());
        Assert.Equal(JsonValueKind.Null, payment.GetProperty("refusal").ValueKind);
        Assert.Empty(RequisiteKeys(payment));
        var posted = await admin.GetFromJsonAsync<JsonElement>($"{url}/paid");
        Assert.Empty(RequisiteKeys(posted));
        Assert.NotEmpty(posted.GetProperty("rows").EnumerateArray());

        var described = await admin.PutAsJsonAsync($"{url}/paid", new { document = "Платёжное поручение 7" });
        await OkAsync(described);
        var current = await ReadAsync(admin, invoice);
        Assert.Equal("Платёжное поручение 7", current.GetProperty("payment").GetProperty("document").GetString());
        var correction = Correction(current, "Уточнили назначение");
        correction.Requisites["Назначение"] = "Для монтажа";
        var preview = await CorrectionPreviewAsync(admin, invoice, current, correction);
        await CorrectAsync(admin, invoice, current, correction with { Stamp = preview.GetProperty("stamp").GetString() });
        await OkAsync(await admin.PostAsJsonAsync($"{url}/unpaid", new { reason = "Платёж отменён" }));
        var unpaid = await ReadAsync(admin, invoice);
        Assert.False(unpaid.GetProperty("payment").GetProperty("paid").GetBoolean());
        Assert.Equal([("Номер", "missing")], RequisiteKeys(unpaid.GetProperty("payment")));
        var beforeRetry = await PaymentRecordAsync(invoice);
        using var retry = await PayAtVersionAsync(admin, invoice, unpaid, today, posted.GetProperty("stamp").GetString()!);
        Assert.Equal(HttpStatusCode.BadRequest, retry.StatusCode);
        Assert.Equal(beforeRetry, await PaymentRecordAsync(invoice));
    }

    [Fact]
    public async Task Отказ_порта_прерывает_предпросмотр_и_запись_без_побочных_эффектов()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (invoice, _, _) = await TwoSitesAsync(admin);
        var view = await ReadAsync(admin, invoice);
        var today = await TodayAsync();
        var preview = await PreviewAsync(admin, invoice, today);
        var before = await PaymentRecordAsync(invoice);
        await using var broken = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IModuleReferenceTargets>();
            services.AddScoped<IModuleReferenceTargets, UnavailablePaymentTargets>();
        }));
        using var client = broken.CreateClient();
        client.DefaultRequestHeaders.Authorization = admin.DefaultRequestHeaders.Authorization;

        using var noPreview = await client.PostAsJsonAsync($"/api/costs/invoices/{invoice}/paid/preview", new { });
        Assert.Equal(HttpStatusCode.InternalServerError, noPreview.StatusCode);
        using var noPayment = await PayAtVersionAsync(client, invoice, view, today, preview.GetProperty("stamp").GetString()!);
        Assert.Equal(HttpStatusCode.InternalServerError, noPayment.StatusCode);
        Assert.Equal(before, await PaymentRecordAsync(invoice));
    }

    [Fact]
    public async Task Дубль_и_отклонение_имеют_приоритет_над_реквизитами()
    {
        var (admin, _) = await SignInAsync("Admin");
        var organization = await PaymentSupplierAsync();
        var requisites = new Dictionary<string, object?>
        {
            ["Поставщик"] = Reference(organization), ["Номер"] = $"СЧ-{Guid.NewGuid():N}", ["Дата"] = "2026-09-29",
        };
        var invoice = await PaymentInvoiceAsync(admin, requisites);
        requisites["Итого"] = 300;
        await PaymentInvoiceAsync(admin, requisites);
        await DeletePaymentSupplierAsync(organization);

        var card = (await ReadAsync(admin, invoice)).GetProperty("payment");
        Assert.Equal("duplicate", card.GetProperty("refusalKind").GetString());
        Assert.Equal("amount", card.GetProperty("moneyRefusalKind").GetString());
        Assert.Equal([("Поставщик", "lost"), ("Итого", "missing")], RequisiteKeys(card));
        var today = await TodayAsync();
        var preview = await PreviewAsync(admin, invoice, today);
        Assert.Equal("duplicate", preview.GetProperty("refusalKind").GetString());
        Assert.Equal(RequisiteKeys(card), RequisiteKeys(preview));
        Assert.Equal(HttpStatusCode.Conflict, (await PayRawAsync(admin, invoice, today, "старый расклад")).StatusCode);

        await OkAsync(await admin.PostAsync($"/api/costs/invoices/{invoice}/rejected", null));
        Assert.Equal("rejected", (await ReadAsync(admin, invoice)).GetProperty("payment").GetProperty("refusalKind").GetString());
        Assert.Equal("rejected", (await PreviewAsync(admin, invoice, today)).GetProperty("refusalKind").GetString());
    }

    [Fact]
    public async Task Снимки_счетов_проверяют_поставщиков_одним_пакетом()
    {
        var (admin, _) = await SignInAsync("Admin");
        var first = await CreateAsync(admin, total: 200);
        var second = await CreateAsync(admin, total: 200);
        var organization = await PaymentSupplierAsync();
        await PutPaymentFieldAsync(admin, second, "Поставщик", Reference(organization));
        await DeletePaymentSupplierAsync(organization);
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        var invoices = await services.GetRequiredService<CostsDbContext>().Invoices.AsNoTracking()
            .Where(invoice => invoice.Id == first || invoice.Id == second).ToListAsync();
        var targets = new PaymentSnapshotTargets(services.GetRequiredService<IModuleReferenceTargets>());
        var snapshots = await ActivatorUtilities.CreateInstance<InvoiceSnapshots>(services, targets).OfAsync(invoices, default);

        Assert.Single(targets.Calls);
        Assert.Equal(new[] { supplier, organization }.Order(), targets.Calls[0].Order());
        Assert.Empty(snapshots[first].PaymentRequisites);
        Assert.Equal("lost", Assert.Single(snapshots[second].PaymentRequisites).Kind);
    }

    private sealed class PaymentSnapshotTargets(IModuleReferenceTargets inner) : IModuleReferenceTargets
    {
        public List<Guid[]> Calls { get; } = [];

        public Task<IReadOnlyDictionary<Guid, ReferenceState>> StatesAsync(
            ReferenceTarget target, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
        {
            Assert.Equal(ReferenceTarget.Record, target);
            Calls.Add([.. ids]);
            return inner.StatesAsync(target, ids, ct);
        }

        public Task<ReferenceFindings> NotPresentAsync(string moduleCode, bool includeArchived, CancellationToken ct = default) =>
            inner.NotPresentAsync(moduleCode, includeArchived, ct);
    }

    private sealed class UnavailablePaymentTargets : IModuleReferenceTargets
    {
        public Task<IReadOnlyDictionary<Guid, ReferenceState>> StatesAsync(
            ReferenceTarget target, IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            throw new InvalidOperationException("Справочник недоступен");

        public Task<ReferenceFindings> NotPresentAsync(string moduleCode, bool includeArchived, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static string?[] ReadinessBlocks(JsonElement[] gaps, string field) =>
        Assert.Single(gaps, gap => gap.TryGetProperty("field", out var key) && key.GetString() == field)
            .GetProperty("blocks").EnumerateArray().Select(block => block.GetString()).ToArray();

    private static (string? Field, string? Kind)[] RequisiteKeys(JsonElement response) =>
        response.GetProperty("requisiteGaps").EnumerateArray()
            .Select(gap => (gap.GetProperty("field").GetString(), gap.GetProperty("kind").GetString())).ToArray();

    private async Task<Guid> PaymentSupplierAsync() =>
        await EntryAsync(await TypeAsync(TestDirectories.OrganizationCode, "Организация"), $"Поставщик {Guid.NewGuid():N}");

    private async Task DeletePaymentSupplierAsync(Guid organization)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM domain_objects WHERE "Id" = {organization}""");
    }

    private static async Task<Guid> PaymentInvoiceAsync(HttpClient client, Dictionary<string, object?> requisites)
    {
        using var response = await client.PostAsJsonAsync("/api/costs/invoices", new { requisites });
        await OkAsync(response);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task PutPaymentFieldAsync(HttpClient client, Guid invoice, string field, object? value) =>
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(client, invoice, field, value) }));

    private static async Task<HttpResponseMessage> PayAtVersionAsync(
        HttpClient client, Guid invoice, JsonElement view, DateOnly paidOn, string seen)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/costs/invoices/{invoice}/paid")
        {
            Content = JsonContent.Create(new { paidOn = Iso(paidOn), seen }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", view.GetProperty("version").GetString());
        return await client.SendAsync(request);
    }

    private async Task<(InvoicePaymentState State, DateOnly? PaidOn, DateTimeOffset? PaidAt, string? Document,
        DateOnly? Remainder, string Dates, (int, int) Book, int Journal)> PaymentRecordAsync(Guid invoice)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var row = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoice);
        var dates = string.Join(", ", (await DatesAsync(invoice)).OrderBy(d => d.Key));
        var journal = await scope.ServiceProvider.GetRequiredService<IActivityLog>().ExportAsync();
        return (row.Payment, row.PaidOn, row.PaidAt, row.PaymentDocument, row.RemainderAccountingOn, dates,
            await BookAsync(invoice), journal.Count(r => r.TargetId == invoice.ToString()));
    }
}
