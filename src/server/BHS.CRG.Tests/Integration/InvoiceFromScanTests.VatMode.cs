using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Счёт из файла: режим НДС со скана (эпик #1496, issue #1501). Порядок разбора — шапка → режим → досчёт
/// строк: строки «сверху», досчитанные «в том числе», получили бы неверный НДС каждая. Числа выдуманные.
/// Разбор правил — в <c>InvoiceScanLinesVatModeTests</c>, здесь — путь целиком.
/// </summary>
public sealed partial class InvoiceFromScanTests
{
    /// <summary>Бумага «сверху»: строки без НДС, «НДС 22 %» и «Всего к оплате» — в итогах.</summary>
    private static ModuleRecognitionResult OnTopPaper(string number) =>
        Read(Paper(Header(number: number, total: "1 830,00", vat: "330,00"), subtotal: "1 500,00", rate: "22%"),
            Row("Кабель", "м", "10", "100,00", "1 000,00"),
            Row("Труба", "м", "5", "100,00", "500,00"));

    private static Dictionary<string, string?> Paper(Dictionary<string, string?> header, string? subtotal, string? rate)
    {
        header[CostsRecognitionProfiles.Subtotal] = subtotal;
        header[CostsRecognitionProfiles.DocumentVatRate] = rate;
        return header;
    }

    private static decimal? Money(JsonElement line, string key) =>
        line.GetProperty(key).ValueKind == JsonValueKind.Null ? null : line.GetProperty(key).GetDecimal();

    [Fact]
    public async Task Скан_сверху_ложится_сверху_и_сходится()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromResult(OnTopPaper("СЧ-1501-1")));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        var recognition = await OutcomeAsync(client, id);
        Assert.Equal("done", recognition.GetProperty("state").GetString());

        var view = await ReadAsync(client, id);
        Assert.Equal("OnTop", view.GetProperty("vatMode").GetString());
        // Режим поставило распознавание — он «не подтверждён», пока человек не скажет «Верно».
        Assert.Contains("vatMode", Unconfirmed(view));

        var lines = view.GetProperty("lines");
        // Бумажная сумма — как напечатана; ставка — из итогов (прочитанная), НДС — сверху, построчно.
        Assert.Equal(1000m, Money(lines[0], "netAmount"));
        Assert.Equal(22m, Money(lines[0], "vatRate"));
        Assert.Equal(220m, Money(lines[0], "vatAmount"));
        Assert.Equal(1220m, Money(lines[0], "amount"));
        Assert.Equal(610m, Money(lines[1], "amount"));
        Assert.Equal(1830m, view.GetProperty("totals").GetProperty("amount").GetDecimal());
        Assert.Contains(recognition.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("из итогов файла: 22 %"));
    }

    /// <summary>Регрессия #1410: бумага «в том числе» ложится, как до #1501, — без отметки и без метки.</summary>
    [Fact]
    public async Task Скан_в_том_числе_ложится_как_прежде()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromResult(Read(
            Paper(Header(number: "СЧ-1501-2", total: "1 830,00", vat: "330,00"), subtotal: null, rate: "22%"),
            Row("Кабель", "м", "10", "122,00", "1 220,00"),
            Row("Труба", "м", "5", "122,00", "610,00"))));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        Assert.Equal("done", (await OutcomeAsync(client, id)).GetProperty("state").GetString());

        var view = await ReadAsync(client, id);
        Assert.Equal("Included", view.GetProperty("vatMode").GetString());
        Assert.DoesNotContain("vatMode", Unconfirmed(view));
        var lines = view.GetProperty("lines");
        Assert.Equal(1220m, Money(lines[0], "amount"));
        // Ставка документа строкам «в том числе» не ложится: НДС шапки и так берут отчёты (#1410).
        Assert.Null(Money(lines[0], "vatRate"));
        Assert.Null(Money(lines[0], "netAmount"));
    }

    /// <summary>
    /// Режим человека распознавание не перезаписывает: отмечено «сверху», а бумага «в том числе» — строки не
    /// кладутся, и сказано почему. Положи их сверху — к суммам с НДС прибавился бы НДС ещё раз.
    /// </summary>
    [Fact]
    public async Task Режим_человека_распознавание_не_перезаписывает()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromException<ModuleRecognitionResult>(
            new RecognitionRefusedException(RecognitionRefusal.Unavailable, "Движок занят.") { RetryAfterSeconds = 30 }));
        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        Assert.Equal("failed", (await OutcomeAsync(client, id)).GetProperty("state").GetString());

        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{id}/lines",
            new Dictionary<string, object?> { ["vatMode"] = "OnTop", ["lines"] = Array.Empty<object>() }));

        host.Recognition.On(scan, () => Task.FromResult(Read(Header(number: "СЧ-1501-3", total: "1 830,00", vat: "330,00"),
            Row("Кабель", "м", "10", "122,00", "1 220,00"),
            Row("Труба", "м", "5", "122,00", "610,00"))));
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{id}/recognition", null));
        var recognition = await OutcomeAsync(client, id);

        var view = await ReadAsync(client, id);
        Assert.Equal("OnTop", view.GetProperty("vatMode").GetString());
        Assert.Equal(0, view.GetProperty("lines").GetArrayLength());
        Assert.Contains(recognition.GetProperty("notes").EnumerateArray(),
            n => n.GetString()!.Contains("суммы строк уже с НДС"));

        // Та же отметка и бумага «сверху» — строки ложатся сверху; метки нет: режим решил человек.
        host.Recognition.On(scan, () => Task.FromResult(OnTopPaper("СЧ-1501-3")));
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{id}/recognition", null));
        Assert.Equal("done", (await OutcomeAsync(client, id)).GetProperty("state").GetString());
        view = await ReadAsync(client, id);
        Assert.Equal(2, view.GetProperty("lines").GetArrayLength());
        Assert.Equal(1000m, Money(view.GetProperty("lines")[0], "netAmount"));
        Assert.DoesNotContain("vatMode", Unconfirmed(view));
    }

    /// <summary>Смена режима человеком снимает метку распознавания, как правка поля — метку поля.</summary>
    [Fact]
    public async Task Смена_режима_человеком_снимает_метку_распознавания()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromResult(OnTopPaper("СЧ-1501-4")));
        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        await OutcomeAsync(client, id);
        Assert.Contains("vatMode", Unconfirmed(await ReadAsync(client, id)));

        // Человек снял отметку — его решение, метка снята той же записью строк.
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{id}/lines",
            new Dictionary<string, object?> { ["vatMode"] = "Included", ["lines"] = Array.Empty<object>() }));
        var view = await ReadAsync(client, id);
        Assert.Equal("Included", view.GetProperty("vatMode").GetString());
        Assert.DoesNotContain("vatMode", Unconfirmed(view));
    }

    [Fact]
    public async Task Верно_снимает_метку_режима()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromResult(OnTopPaper("СЧ-1501-5")));
        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        await OutcomeAsync(client, id);

        // «Верно» — тот же адрес, что «Всё верно» у блоков шапки.
        await OkAsync(await client.PostAsJsonAsync($"/api/costs/invoices/{id}/confirmed", new { fields = new[] { "vatMode" } }));
        var view = await ReadAsync(client, id);
        Assert.Equal("OnTop", view.GetProperty("vatMode").GetString());
        Assert.DoesNotContain("vatMode", Unconfirmed(view));
    }
}
