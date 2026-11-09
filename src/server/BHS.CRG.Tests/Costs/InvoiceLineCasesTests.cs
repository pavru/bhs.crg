using System.Text.Json;
using BHS.CRG.Modules.Costs.Data;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Строка счёта по режиму НДС — общий набор примеров с формой (issue #1499, сторож С7).
///
/// <para>Правило досчёта строки живёт в двух местах: здесь (<see cref="InvoiceLineValues.Completed" />) и в
/// предпросмотре формы (<c>preview</c> в <c>invoiceLines.ts</c>) — иначе форма молчала бы о суммах до
/// сохранения. Разойдись они, суммы менялись бы на глазах при сохранении. Поэтому примеры одни, в файле
/// рядом с формой, и считают их оба теста: отставший краснеет. Первым делом набор поймал копейку —
/// двоичная дробь формы округляла 1,005 вниз.</para>
/// </summary>
public class InvoiceLineCasesTests
{
    private static readonly string CasesFile = RepoPaths.File("src", "client", "src", "features", "costs", "invoiceLineCases.json");

    public static TheoryData<string> Names() => [.. Cases().Select(c => c.GetProperty("name").GetString()!)];

    [Theory]
    [MemberData(nameof(Names))]
    public void Сервер_считает_строку_как_форма(string name)
    {
        var c = Cases().Single(x => x.GetProperty("name").GetString() == name);
        var mode = Enum.Parse<VatMode>(c.GetProperty("mode").GetString()!);
        AssertLine(Complete(c, mode), c.GetProperty("expect"), mode);
    }

    public static TheoryData<string> AfterSaveNames() => [.. Cases("afterSave").Select(c => c.GetProperty("name").GetString()!)];

    /// <summary>
    /// Строка после сохранения (issue #1654): сервер хранит только досчитанное, а различает «с бумаги» и
    /// «посчитано» форма — по равенству расчёту. Здесь сторона сервера: первая запись даёт <c>stored</c>, а то,
    /// что форма шлёт после правки (<c>sent</c>, посчитанного в нём нет), — <c>expect</c>. Сторону формы
    /// (<c>stored</c> → <c>sent</c>) проверяет <c>invoiceVat.test.ts</c> тем же набором.
    /// </summary>
    [Theory]
    [MemberData(nameof(AfterSaveNames))]
    public void Сервер_пересчитывает_посчитанное_после_сохранения(string name)
    {
        var c = Cases("afterSave").Single(x => x.GetProperty("name").GetString() == name);
        var mode = Enum.Parse<VatMode>(c.GetProperty("mode").GetString()!);

        AssertLine(Complete(c.GetProperty("saved"), mode), c.GetProperty("stored"), mode);
        AssertLine(Complete(c.GetProperty("sent"), mode), c.GetProperty("expect"), mode);
    }

    private static InvoiceLineValues Complete(JsonElement line, VatMode mode)
    {
        var paper = Number(line, "paper");
        return new InvoiceLineValues(null, "Кабель", null, "м", Number(line, "quantity"), Number(line, "price"),
            Number(line, "rate"), Number(line, "vat"),
            Amount: mode == VatMode.Included ? paper : null, Note: null,
            NetAmount: mode == VatMode.OnTop ? paper : null).Completed(mode).Values;
    }

    private static void AssertLine(InvoiceLineValues line, JsonElement expect, VatMode mode)
    {
        Assert.Equal(Number(expect, "amount"), mode == VatMode.OnTop ? line.NetAmount : line.Amount);
        Assert.Equal(Number(expect, "vat"), line.VatAmount);
        Assert.Equal(Number(expect, "gross"), line.Amount);
    }

    private static decimal? Number(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : null;

    private static IEnumerable<JsonElement> Cases(string set = "cases")
    {
        using var document = JsonDocument.Parse(File.ReadAllText(CasesFile));
        return [.. document.RootElement.GetProperty(set).EnumerateArray().Select(c => c.Clone())];
    }
}
