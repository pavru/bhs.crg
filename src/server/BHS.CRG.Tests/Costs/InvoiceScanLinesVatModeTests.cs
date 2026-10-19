using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Строки скана по режиму НДС (issue #1501): бумага разбирается без режима, режим — по арифметике, досчёт —
/// после. Числа выдуманные.
/// </summary>
public class InvoiceScanLinesVatModeTests
{
    private static IReadOnlyDictionary<string, string?> Row(string amount, string? rate = null, string? vat = null) =>
        new Dictionary<string, string?>
        {
            [CostsRecognitionProfiles.LineName] = "Кабель",
            [CostsRecognitionProfiles.LineAmount] = amount,
            [CostsRecognitionProfiles.LineVatRate] = rate,
            [CostsRecognitionProfiles.LineVatAmount] = vat,
        };

    private static IReadOnlyList<InvoiceScanLines.ScanLine> Paper(params IReadOnlyDictionary<string, string?>[] rows) =>
        InvoiceScanLines.Read(rows).Paper;

    [Fact]
    public void Бумага_разбирается_без_режима_НДС_не_досчитан()
    {
        var line = Assert.Single(Paper(Row("1 000,00", "22%")));
        Assert.Equal(1000m, line.Values.Amount);
        Assert.Null(line.Values.VatAmount);
        Assert.True(line.HasVat);
    }

    [Fact]
    public void Сверху_НДС_прибавляется_к_бумажной_сумме()
    {
        var line = Assert.Single(InvoiceScanLines.Complete(Paper(Row("1 000,00", "22%")), VatMode.OnTop, null).Lines);
        Assert.Equal(1000m, line.NetAmount);
        Assert.Equal(220m, line.VatAmount);
        Assert.Equal(1220m, line.Amount);
    }

    /// <summary>Тот же скан «в том числе» — как до #1501: НДС от суммы с НДС.</summary>
    [Fact]
    public void В_том_числе_как_прежде()
    {
        var line = Assert.Single(InvoiceScanLines.Complete(Paper(Row("1 220,00", "22%")), VatMode.Included, null).Lines);
        Assert.Equal(1220m, line.Amount);
        Assert.Equal(220m, line.VatAmount);
        Assert.Null(line.NetAmount);
    }

    [Fact]
    public void Сверху_без_ставки_суммы_с_НДС_нет()
    {
        var line = Assert.Single(InvoiceScanLines.Complete(Paper(Row("1 000,00")), VatMode.OnTop, null).Lines);
        Assert.Equal(1000m, line.NetAmount);
        Assert.Null(line.Amount);
    }

    /// <summary>Ставка из итогов — строкам сверху, у которых своей нет; сказано, откуда она.</summary>
    [Fact]
    public void Ставка_документа_ложится_строкам_сверху_без_своей()
    {
        var done = InvoiceScanLines.Complete(Paper(Row("1 000,00"), Row("500,00")), VatMode.OnTop, 22m);
        Assert.All(done.Lines, l => Assert.Equal(22m, l.VatRate));
        Assert.Equal([1220m, 610m], done.Lines.Select(l => l.Amount!.Value));
        Assert.Contains("22 %", done.RateNote);
    }

    [Fact]
    public void Ставка_документа_не_ложится_в_том_числе_и_не_перебивает_свою()
    {
        Assert.Null(InvoiceScanLines.Complete(Paper(Row("1 220,00")), VatMode.Included, 22m).Lines[0].VatRate);

        var own = InvoiceScanLines.Complete(Paper(Row("1 000,00", "10%")), VatMode.OnTop, 22m);
        Assert.Equal(10m, own.Lines[0].VatRate);
        Assert.Null(own.RateNote);
    }

    /// <summary>
    /// Ревью PR #1509: НДС, снятый правилом «все или ни одной» (#1410), полей не оставляет, но строка о нём
    /// говорила — своя 10 % под ставкой итогов 22 % стала бы неправдой, а примечания спорили бы друг с другом.
    /// </summary>
    [Fact]
    public void Ставка_документа_не_ложится_после_снятия_НДС()
    {
        var reading = InvoiceScanLines.Read([Row("1 000,00", "10%"), Row("500,00")]);
        Assert.NotNull(reading.VatNote);

        var done = InvoiceScanLines.Complete(reading.Paper, VatMode.OnTop, 22m);
        Assert.All(done.Lines, l => Assert.Null(l.VatRate));
        Assert.Null(done.RateNote);
    }

    [Theory]
    // Строки сходятся с суммой к оплате — «в том числе».
    [InlineData("1830", "330", null, VatMode.Included)]
    // Строки + НДС шапки = к оплате — сверху.
    [InlineData("1830", "330", null, VatMode.OnTop, "1000", "500")]
    // НДС шапки не прочитан, но строки сошлись с «Итого без НДС», а к оплате больше — сверху.
    [InlineData("1830", null, "1500", VatMode.OnTop, "1000", "500")]
    // Ни так ни так — не определить.
    [InlineData("2000", "330", null, null, "1000", "500")]
    public void Режим_по_арифметике(string total, string? vat, string? subtotal, VatMode? expected, params string[] amounts)
    {
        var rows = (amounts.Length == 0 ? ["1220", "610"] : amounts).Select(a => Row(a)).ToArray();
        var mode = InvoiceScanLines.ModeOf(Paper(rows), vat is null ? null : decimal.Parse(vat),
            decimal.Parse(total), subtotal is null ? null : decimal.Parse(subtotal), 0.01m);
        Assert.Equal(expected, mode);
    }

    /// <summary>Строка без суммы — сумма строк неполна, совпадение было бы случайным: режима нет.</summary>
    [Fact]
    public void Без_суммы_у_строки_режима_нет()
    {
        var rows = Paper(Row("1000"), new Dictionary<string, string?> { [CostsRecognitionProfiles.LineName] = "Доставка" });
        Assert.Null(InvoiceScanLines.ModeOf(rows, 330m, 1830m, null, 0.01m));
    }
}
