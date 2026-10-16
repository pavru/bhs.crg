using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Сумма, набранная в форме, — строкой «1 234,50» (поле ввода присылает текст человека как есть). Разбор
/// модуля её принимает, а охрана записи ядра судит вид по схеме типа и строку в числовом поле отвергала:
/// «ожидается число, а хранится строка». Форма счёта не сохраняла сумму к оплате вовсе — ни правкой, ни
/// исправлением запертого счёта.
/// </summary>
public partial class InvoicePaymentTests
{
    /// <summary>
    /// Правка шапки: сумма, НДС и отсрочка строками ложатся числами, а повтор той же строки правкой не
    /// считается — журнал «счёт изменён» о нём молчит (иначе снималась бы метка распознавания).
    /// </summary>
    [Fact]
    public async Task Сумма_строкой_с_запятой_сохраняется_правкой()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (invoice, _, _) = await TwoSitesAsync(admin);

        var requisites = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            (await ReadAsync(admin, invoice)).GetProperty("requisites").GetRawText())!;
        requisites["Итого"] = "1 234,50";
        requisites["ВТомЧислеНДС"] = "205,75";
        requisites["Отсрочка"] = "15";

        var untouched = await ChangedRecordsAsync(invoice);
        await OkAsync(await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}", new { requisites }));
        // Настоящая правка журналом замечена — иначе «повтор молчит» ниже доказывал бы лишь то, что счёт
        // записей не видит ничего (0 = 0).
        var changes = await ChangedRecordsAsync(invoice);
        Assert.Equal(untouched + 1, changes);

        var saved = (await ReadAsync(admin, invoice)).GetProperty("requisites");
        Assert.Equal(1234.50m, saved.GetProperty("Итого").GetDecimal());
        Assert.Equal(205.75m, saved.GetProperty("ВТомЧислеНДС").GetDecimal());
        Assert.Equal(15, saved.GetProperty("Отсрочка").GetInt32());

        await OkAsync(await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}", new { requisites }));
        Assert.Equal(changes, await ChangedRecordsAsync(invoice));
    }

    /// <summary>Исправление запертого счёта зовёт ту же правку шапки — и сумму строкой принимает так же.</summary>
    [Fact]
    public async Task Сумма_строкой_с_запятой_сохраняется_исправлением()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, _, _) = await LockedAsync(admin, today);
        var before = await ReadAsync(admin, invoice);

        // Цена строки 2 — 1200,01 вместо 1200: 50 × 1200,01 = 60 000,50, и сумма к оплате следом.
        var body = Correction(before, "опечатка в цене", lines => Price(lines[1], 1200.01m));
        body.Requisites["Итого"] = "100 000,50";

        var preview = await CorrectionPreviewAsync(admin, invoice, before, body);
        var corrected = await CorrectAsync(admin, invoice, before, body with { Stamp = preview.GetProperty("stamp").GetString() });

        Assert.Equal(100_000.50m, corrected.GetProperty("requisites").GetProperty("Итого").GetDecimal());
    }

    /// <summary>
    /// Сколько раз журнал записал «счёт изменён» об этом счёте. Счётом по набору, а не страницей службы
    /// журнала: страница — последние N записей всей базы, и соседние тесты, пишущие в журнал параллельно,
    /// сдвигали бы её между двумя чтениями.
    /// </summary>
    private async Task<int> ChangedRecordsAsync(Guid invoice)
    {
        using var scope = host.Services.CreateScope();
        var target = invoice.ToString();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ActivityRecords
            .CountAsync(r => r.Action == "costs.invoice.changed" && r.TargetId == target);
    }
}
