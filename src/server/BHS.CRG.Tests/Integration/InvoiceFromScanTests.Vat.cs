using System.Text.Json;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Счёт из файла: НДС строк (issue #1410). Отдельной частью — по занятию: остальной класс о том, как
/// скан становится черновиком, эта часть — о том, чем у строк «ноль», «нет данных» и «не прочитано»
/// различаются в отчётах. Разбор каждого значения — в <c>InvoiceScanLinesTests</c>, здесь — путь целиком.
/// </summary>
public sealed partial class InvoiceFromScanTests
{
    /// <summary>
    /// НДС строк из файла ложится в строки (issue #1410): разные ставки одного счёта — 22 % и «Без НДС» —
    /// видны отчётам по строкам, а не грубее, по НДС шапки.
    /// </summary>
    [Fact]
    public async Task НДС_строк_из_файла_ложится_в_строки()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromResult(Read(Header(number: "СЧ-511", total: "1 820,00", vat: "220,00"),
            Row("Кабель", "м", "10", "122,00", "1 220,00", "22%", "220,00"),
            Row("Доставка", "усл", "1", "600,00", "600,00", "Без НДС", "-"))));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        Assert.Equal("done", (await OutcomeAsync(client, id)).GetProperty("state").GetString());

        var view = await ReadAsync(client, id);
        var lines = view.GetProperty("lines");
        Assert.Equal(22m, lines[0].GetProperty("vatRate").GetDecimal());
        Assert.Equal(220m, lines[0].GetProperty("vatAmount").GetDecimal());
        Assert.Equal(0m, lines[1].GetProperty("vatRate").GetDecimal());
        Assert.Equal(0m, lines[1].GetProperty("vatAmount").GetDecimal());
    }

    /// <summary>
    /// НДС прочитан не у всех строк — в строки не ложится ни у одной, и причина названа (issue #1410):
    /// иначе отчёты перешли бы на НДС по строкам и посчитали бы строку без НДС полной суммой.
    /// </summary>
    [Fact]
    public async Task НДС_прочитанный_у_части_строк_не_ложится_и_причина_названа()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromResult(Read(Header(number: "СЧ-512", vat: "220,00"),
            Row("Кабель", "м", "10", "122,00", "1 220,00", "22%", "220,00"),
            Row("Доставка", "усл", "1", "600,00", "600,00"))));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        var recognition = await OutcomeAsync(client, id);
        Assert.Contains(recognition.GetProperty("notes").EnumerateArray(),
            n => n.GetString()!.Contains("НДС прочитан не у всех строк"));

        var view = await ReadAsync(client, id);
        Assert.All(view.GetProperty("lines").EnumerateArray(),
            l => Assert.Equal(JsonValueKind.Null, l.GetProperty("vatAmount").ValueKind));
        Assert.Equal(220m, view.GetProperty("requisites").GetProperty("ВТомЧислеНДС").GetDecimal());
    }
}
