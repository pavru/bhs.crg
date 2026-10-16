using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// История счёта — его книга проводок своим адресом (E2, issue #1100, PR 6b-2): форма показывает
/// секцию «Исправления» по признаку <c>book</c> и читает записи <c>GET …/book</c>.
/// </summary>
public partial class InvoicePaymentTests
{
    /// <summary>
    /// После исправления: признак в виде счёта, записи по порядку книги — открытие и исправление, у
    /// исправления причина, версия, день, сдвиг денег и имя автора — то же, что записал журнал.
    /// </summary>
    [Fact]
    public async Task История_называет_исправление_с_автором_причиной_и_сдвигом()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, a, _) = await LockedAsync(admin, today);
        var before = await ReadAsync(admin, invoice);
        Assert.Equal(0, before.GetProperty("book").GetInt32());

        // Цена строки 1 — 410 вместо 400: стройке А +1000 днём исправления.
        var body = Correction(before, "опечатка в цене", lines => Price(lines[0], 410m), total: 101_000m);
        var preview = await CorrectionPreviewAsync(admin, invoice, before, body);
        var corrected = await CorrectAsync(admin, invoice, before, body with { Stamp = preview.GetProperty("stamp").GetString() });
        Assert.Equal(2, corrected.GetProperty("book").GetInt32());

        var entries = await HistoryAsync(admin, invoice);
        Assert.Equal([(1, "opening"), (2, "correction")],
            entries.Select(e => (e.GetProperty("seq").GetInt32(), e.GetProperty("kind").GetString()!)));

        var opening = entries[0];
        Assert.Equal(100_000m, opening.GetProperty("gross").GetDecimal());
        Assert.Equal(JsonValueKind.Null, opening.GetProperty("reason").ValueKind);

        var correction = entries[1];
        Assert.Equal("опечатка в цене", correction.GetProperty("reason").GetString());
        Assert.Equal(1, correction.GetProperty("version").GetInt32());
        Assert.Equal(Iso(today), correction.GetProperty("day").GetString());
        Assert.Equal(1_000m, correction.GetProperty("gross").GetDecimal());
        Assert.Equal([(a, Iso(today), 1_000m)], Lines(correction).Select(l => (l.Site!.Value, l.Day, l.Gross)));

        // Имя — то же, что журнал записал о самом исправлении: одно имя на момент действия, а не два.
        var author = correction.GetProperty("author").GetString();
        Assert.False(string.IsNullOrWhiteSpace(author));
        Assert.Equal(await CorrectedByAsync(invoice), author);
        // Открытие — служебная запись, следствие исправления: имени у неё нет, как нет причины и версии.
        Assert.Equal(JsonValueKind.Null, opening.GetProperty("author").ValueKind);
    }

    /// <summary>
    /// История — часть счёта: её читает и тот, кому открыт счёт без права исправлять. Удалённый счёт —
    /// 404, а не пустая история («исправлений не было» у счёта, которого нет).
    /// </summary>
    [Fact]
    public async Task Историю_читает_тот_кому_открыт_счёт_а_чужой_адрес_404()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (reader, _) = await SignInAsync("Executive");
        var today = await TodayAsync();
        var (invoice, _, _) = await LockedAsync(admin, today);
        var before = await ReadAsync(admin, invoice);
        var body = Correction(before, "опечатка в назначении");
        body.Requisites["Назначение"] = "исправлено";
        var preview = await CorrectionPreviewAsync(admin, invoice, before, body);
        await CorrectAsync(admin, invoice, before, body with { Stamp = preview.GetProperty("stamp").GetString() });

        Assert.Equal(2, (await HistoryAsync(reader, invoice)).Count);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/costs/invoices/{Guid.NewGuid()}/book")).StatusCode);
    }

    private static async Task<List<JsonElement>> HistoryAsync(HttpClient client, Guid invoice)
    {
        var response = await client.GetAsync($"/api/costs/invoices/{invoice}/book");
        await OkAsync(response);
        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()];
    }

    /// <summary>Чьим именем журнал записал исправление этого счёта.</summary>
    private async Task<string> CorrectedByAsync(Guid invoice)
    {
        using var scope = host.Services.CreateScope();
        var target = invoice.ToString();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ActivityRecords
            .Where(r => r.Action == "costs.invoice.corrected" && r.TargetId == target)
            .Select(r => r.ActorName).SingleAsync();
    }
}
