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
    [InlineData("--", "Х")]   // прочерк в две черты и крест — тоже «нечего указать», а не непрочитанное
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

    /// <summary>
    /// Сумма НДС стояла и не прочиталась — не досчитывается по ставке, и ставка одна в строке не
    /// остаётся: сохранение формы досчитало бы НДС по ней, и счёт перешёл бы на НДС по строкам без
    /// прочитанного числа (ревью PR #1444). Ставка — в примечании, рядом с непрочитанным.
    /// </summary>
    [Fact]
    public void Непрочитанная_сумма_НДС_не_досчитывается_и_ставка_не_остаётся_одна()
    {
        var line = Single(Row("1220", "22%", "1.220"));

        Assert.Null(line.VatRate);
        Assert.Null(line.VatAmount);
        Assert.Contains("сумма НДС «1.220»", line.Note);
        Assert.Contains("ставка 22 %", line.Note);
    }

    /// <summary>
    /// ⚠️ Строка со ставкой, но без суммы НДС, — тоже «НДС тронут» (ревью PR #1444): её соседку без НДС
    /// правило «все или ни одной» обязано увидеть, хотя полной суммы НДС нет ни у одной строки.
    /// </summary>
    [Fact]
    public void Ставка_без_суммы_НДС_у_одной_строки_снимается_и_причина_названа()
    {
        var reading = InvoiceScanLines.Read([Row("1220", "22%", "1.220"), Row("600")]);

        Assert.All(reading.Lines, l => Assert.Null(l.VatRate));
        Assert.All(reading.Lines, l => Assert.Null(l.VatAmount));
        Assert.NotNull(reading.VatNote);
    }

    /// <summary>
    /// Сумма строки стояла и не прочиталась — НДС от «количество × цена» не досчитывается: у строки без
    /// суммы стоял бы НДС из числа, которого никто не видел (ревью PR #1444).
    /// </summary>
    [Fact]
    public void НДС_не_досчитывается_от_непрочитанной_суммы_строки()
    {
        var line = Single(new Dictionary<string, string?>
        {
            [CostsRecognitionProfiles.LineName] = "Кабель",
            [CostsRecognitionProfiles.LineQuantity] = "10",
            [CostsRecognitionProfiles.LinePrice] = "122,00",
            [CostsRecognitionProfiles.LineAmount] = "1 22О,00",   // буква «О» вместо нуля
            [CostsRecognitionProfiles.LineVatRate] = "22%",
        });

        Assert.Null(line.Amount);
        Assert.Null(line.VatAmount);
    }

    /// <summary>
    /// В примечание снятого НДС пишется только прочитанное: досчитанной по ставке суммы в файле нет, и
    /// «НДС в файле: сумма 100,00» человек перенёс бы как число с бумаги (ревью PR #1444).
    /// </summary>
    [Fact]
    public void Досчитанная_сумма_НДС_не_выдаётся_за_прочитанную()
    {
        var reading = InvoiceScanLines.Read([Row("1100", "10%"), Row("600")]);

        Assert.Contains("ставка 10 %", reading.Lines[0].Note);
        Assert.DoesNotContain("сумма", reading.Lines[0].Note);
    }

    /// <summary>
    /// Прочерк в сумме НДС у строки «Без НДС» — не непрочитанное: иначе одна такая ячейка снимала бы НДС
    /// со всего счёта (ревью PR #1444).
    /// </summary>
    [Fact]
    public void Прочерк_в_сумме_НДС_не_снимает_НДС_со_счёта()
    {
        var reading = InvoiceScanLines.Read([Row("1220", "22%", "220"), Row("600", "Без НДС", "--")]);

        Assert.Equal([220m, 0m], reading.Lines.Select(l => l.VatAmount));
        Assert.Null(reading.VatNote);
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
