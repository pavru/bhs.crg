using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Строки скана → строки счёта: НДС строк (issue #1410). Главное свойство разбора — «ноль», «нет
/// данных» и «не прочитано» здесь три разных ответа: «Без НДС» ложится нулём, пустая колонка — пустым
/// НДС, непрочитанное — примечанием, а не нулём.
///
/// <para>Числа выдуманные: счета заказчика и производные от них в тесты не переносятся.</para>
/// </summary>
public class InvoiceScanLinesTests
{
    private static IReadOnlyDictionary<string, string?> Row(
        string amount, string? rate = null, string? vat = null, string name = "Кабель") =>
        new Dictionary<string, string?>
        {
            [CostsRecognitionProfiles.LineName] = name,
            [CostsRecognitionProfiles.LineQuantity] = "1",
            [CostsRecognitionProfiles.LinePrice] = amount,
            [CostsRecognitionProfiles.LineAmount] = amount,
            [CostsRecognitionProfiles.LineVatRate] = rate,
            [CostsRecognitionProfiles.LineVatAmount] = vat,
        };

    private static InvoiceLineValues Single(IReadOnlyDictionary<string, string?> row) =>
        Assert.Single(InvoiceScanLines.Read([row]).Lines);

    [Fact]
    public void Прочитанные_ставка_и_сумма_НДС_ложатся_в_строку()
    {
        var line = Single(Row("1 220,00", "22%", "220,00"));

        Assert.Equal(22m, line.VatRate);
        Assert.Equal(220m, line.VatAmount);
        Assert.Null(line.Note);
    }

    /// <summary>Модель пишет числа непостоянно — сверяется значение, а не строка.</summary>
    [Theory]
    [InlineData("22 %")]
    [InlineData("НДС 22")]
    [InlineData("22,0")]
    [InlineData("Ставка 22%")]
    public void Ставка_читается_в_любой_записи(string rate) =>
        Assert.Equal(22m, Single(Row("1220", rate, "220")).VatRate);

    /// <summary>Сумма НДС не прислана — досчитана по ставке «в том числе», как у строки из формы.</summary>
    [Fact]
    public void Сумма_НДС_без_колонки_досчитана_по_ставке()
    {
        var line = Single(Row("1100,00", "10%"));

        Assert.Equal(100m, line.VatAmount);
    }

    [Theory]
    [InlineData("Без НДС", null)]
    [InlineData("без ндс", "-")]
    [InlineData("НДС не облагается", null)]
    [InlineData("0%", null)]
    public void Без_НДС_это_ноль_а_не_пусто(string rate, string? vat)
    {
        var line = Single(Row("1200", rate, vat));

        Assert.Equal(0m, line.VatRate);
        Assert.Equal(0m, line.VatAmount);
    }

    /// <summary>Колонок НДС нет (НДС только в итогах бумаги) или в них прочерк — НДС строки пуст, не ноль.</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", " ")]
    [InlineData("-", "—")]
    public void Нет_колонки_НДС_пусто_а_не_ноль(string? rate, string? vat)
    {
        var line = Single(Row("1200", rate, vat));

        Assert.Null(line.VatRate);
        Assert.Null(line.VatAmount);
        Assert.Null(line.Note);
    }

    /// <summary>Непрочитанное — в примечание, а не нулём и не досчитанным: на бумаге стоит другое.</summary>
    [Theory]
    [InlineData("20/120", "ставка НДС «20/120»")]
    [InlineData("150%", "ставка НДС «150%»")]
    public void Непрочитанная_ставка_уходит_в_примечание(string rate, string expected)
    {
        var line = Single(Row("1200", rate));

        Assert.Null(line.VatRate);
        Assert.Null(line.VatAmount);
        Assert.Contains(expected, line.Note);
    }

    [Fact]
    public void Непрочитанная_сумма_НДС_не_досчитывается_по_ставке()
    {
        var line = Single(Row("1220", "22%", "1.220"));

        Assert.Equal(22m, line.VatRate);
        Assert.Null(line.VatAmount);
        Assert.Contains("сумма НДС «1.220»", line.Note);
    }

    /// <summary>
    /// ⚠️ НДС у части строк — ни у одной (обоснование — <see cref="InvoiceScanLines.Read" />): иначе
    /// счёт перешёл бы в отчётах на НДС по строкам, и строки без НДС посчитались бы полной суммой.
    /// Прочитанное не теряется — оно в примечании строки, причина — в примечании распознавания.
    /// </summary>
    [Fact]
    public void НДС_прочитанный_не_у_всех_строк_не_ложится_ни_у_одной()
    {
        var reading = InvoiceScanLines.Read([
            Row("1220", "22%", "220", name: "Кабель"),
            Row("600", name: "Доставка"),
        ]);

        Assert.All(reading.Lines, l => Assert.Null(l.VatAmount));
        Assert.All(reading.Lines, l => Assert.Null(l.VatRate));
        Assert.Contains("ставка 22 %, сумма 220,00", reading.Lines[0].Note);
        Assert.Null(reading.Lines[1].Note);
        Assert.Contains("у 1 из 2", reading.VatNote);
    }

    [Fact]
    public void НДС_у_всех_строк_ложится_и_причины_нет()
    {
        var reading = InvoiceScanLines.Read([Row("1220", "22%", "220"), Row("600", "Без НДС")]);

        Assert.Equal([220m, 0m], reading.Lines.Select(l => l.VatAmount));
        Assert.Null(reading.VatNote);
    }
}
