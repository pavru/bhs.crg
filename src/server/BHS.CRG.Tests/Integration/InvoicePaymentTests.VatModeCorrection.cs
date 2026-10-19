using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Activity;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Режим НДС запертого счёта (эпик #1496, issue #1498) — тот же класс, что исправление
/// (<c>InvoicePaymentTests.Correction.cs</c>), отдельным файлом по занятию. Счёт — <c>LockedAsync</c>: строка 1 —
/// 100 × 400 на стройку А, строка 2 — 50 × 1200 на стройку Б, к оплате 100 000, оплачен и заперт закрытием А.
/// </summary>
public partial class InvoicePaymentTests
{
    /// <summary>
    /// Сторож С8: обычная правка режима запертому счёту — 409, как правка суммы; исправление без режима — 400
    /// (тело заменяет содержимое целиком, и «не менять» из отсутствия не читается); исправлением — пункт «режим» в
    /// предпросмотре, запись книги с парой режимов, версия с прежним режимом и журнал.
    ///
    /// <para>Числа выдуманные и подобраны так, чтобы суммы с НДС не сдвинулись: бумага 32 000 + 48 000 без НДС,
    /// ставка 25 % — те же 40 000 и 60 000. Сдвигаются только суммы без НДС — НДС, которого у строк не было,
    /// стал известен (−20 000). Сам режим строками книги не назвать, и без пункта «режим» исправление читалось
    /// бы «НДС вписали», а не «цены в бумаге без НДС».</para>
    /// </summary>
    [Fact]
    public async Task Режим_НДС_запертому_счёту_меняет_только_исправление()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, _, _) = await LockedAsync(admin, today);
        var before = await ReadAsync(admin, invoice);

        // Обычной правкой — отказ «заперт», и режим на месте.
        var plain = await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}/lines", new
        {
            vatMode = "OnTop",
            lines = before.GetProperty("lines").EnumerateArray().Select(l => new
            {
                id = l.GetProperty("id").GetGuid(), nomenclature = Reference(l.GetProperty("nomenclatureId").GetGuid()),
                quantity = l.GetProperty("quantity").GetDecimal(), price = l.GetProperty("price").GetDecimal(), vatRate = 25m,
            }).ToArray(),
        });
        Assert.Equal(HttpStatusCode.Conflict, plain.StatusCode);
        Assert.Contains("только исправлением", await ErrorAsync(plain));
        Assert.Equal("Included", (await ReadAsync(admin, invoice)).GetProperty("vatMode").GetString());

        var version = before.GetProperty("version").GetString();
        var missing = await CorrectionRawAsync(admin, invoice, "preview", version,
            Correction(before, "режим не назван") with { VatMode = null });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("vatMode", await ErrorAsync(missing));

        // Сверху без ставки суммы с НДС нет — и пробел называется ставкой, а не ценой: цена у строки есть.
        var blind = await CorrectionRawAsync(admin, invoice, "preview", version,
            Correction(before, "цены без НДС", lines => lines.ForEach(l => OnTop(l, rate: null))) with { VatMode = "OnTop" });
        Assert.Equal(HttpStatusCode.BadRequest, blind.StatusCode);
        var words = await ErrorAsync(blind);
        Assert.Contains("у строки 1 нет ставки НДС", words);
        Assert.DoesNotContain("не вписана цена", words);

        var body = Correction(before, "цены в счёте без НДС", lines =>
        {
            OnTop(lines[0], rate: 25m, price: 320m);
            OnTop(lines[1], rate: 25m, price: 960m);
        }) with { VatMode = "OnTop" };
        var preview = await CorrectionPreviewAsync(admin, invoice, before, body);
        var shift = preview.GetProperty("vatMode");
        Assert.Equal(("Included", "OnTop"), (shift.GetProperty("from").GetString(), shift.GetProperty("to").GetString()));
        var moved = preview.GetProperty("records").EnumerateArray().Single(r => r.GetProperty("kind").GetString() == "correction")
            .GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal((0m, -20_000m), (moved.Sum(l => l.GetProperty("gross").GetDecimal()), moved.Sum(l => l.GetProperty("net").GetDecimal())));

        var corrected = await CorrectAsync(admin, invoice, before, body with { Stamp = preview.GetProperty("stamp").GetString() });
        Assert.Equal("OnTop", corrected.GetProperty("vatMode").GetString());
        var first = corrected.GetProperty("lines")[0];
        Assert.Equal((32_000m, 40_000m), (first.GetProperty("netAmount").GetDecimal(), first.GetProperty("amount").GetDecimal()));

        // История: смена режима — пунктом своей записи; у открытия его нет.
        var book = await admin.GetFromJsonAsync<JsonElement>($"/api/costs/invoices/{invoice}/book");
        Assert.Equal([JsonValueKind.Null, JsonValueKind.Object],
            book.EnumerateArray().Select(e => e.GetProperty("vatMode").ValueKind));
        Assert.Equal("Included", book[1].GetProperty("vatMode").GetProperty("from").GetString());

        using (var scope = host.Services.CreateScope())
        {
            // Версия — содержимое ДО исправления, с прежним режимом.
            var content = await scope.ServiceProvider.GetRequiredService<CostsDbContext>().InvoiceVersions
                .Where(v => v.InvoiceId == invoice).Select(v => v.Content).SingleAsync();
            Assert.Equal("Included", JsonDocument.Parse(content).RootElement.GetProperty("vatMode").GetString());

            var record = (await scope.ServiceProvider.GetRequiredService<IActivityLog>()
                    .ReadAsync(0, 50, ActivityVisibility.Whole, "costs.invoice.corrected"))
                .Single(r => r.TargetId == invoice.ToString());
            Assert.Equal("версия 1; НДС: в том числе", record.Before);
            Assert.Equal("цены в счёте без НДС; НДС: сверху", record.After);
        }

        // Исправление, режим не менявшее, пары в книге не пишет: бумага сверху едет netAmount, сдвиг — деньгами.
        var view = await ReadAsync(admin, invoice);
        var again = Correction(view, "цена строки 1", lines => Price(lines[0], 330m), total: 101_250m);
        Assert.Equal("OnTop", again.VatMode);
        var second = await CorrectionPreviewAsync(admin, invoice, view, again);
        Assert.Equal(JsonValueKind.Null, second.GetProperty("vatMode").ValueKind);
        await CorrectAsync(admin, invoice, view, again with { Stamp = second.GetProperty("stamp").GetString() });
        book = await admin.GetFromJsonAsync<JsonElement>($"/api/costs/invoices/{invoice}/book");
        Assert.Equal(JsonValueKind.Null, book[book.GetArrayLength() - 1].GetProperty("vatMode").ValueKind);
        Assert.Equal(1_250m, book[book.GetArrayLength() - 1].GetProperty("gross").GetDecimal());
    }

    /// <summary>Строка — бумагой НДС сверху: цена без НДС, ставка; суммы досчитает сервер.</summary>
    private static void OnTop(Dictionary<string, object?> line, decimal? rate, decimal? price = null)
    {
        line.Remove("amount");
        line["netAmount"] = null;
        line["vatRate"] = rate;
        if (price is { } paper) line["price"] = paper;
    }
}
