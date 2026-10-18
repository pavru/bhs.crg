using System.Reflection;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Строки скана раскладываются по ключам колонок профиля (issue #1480): колонка с ключом поля строки
/// заполняет это поле, колонка с другим ключом не теряется молча — она в примечании строки.
///
/// <para><b>Сторож задачи</b> — пара первых тестов: новое поле строки без решения «цель или нет»
/// краснеет, и таблица ключей не расходится с разбором. Ломается он тем, что поле объявлено целью, а
/// разбор его по-прежнему не читает, — так раньше молча терялся артикул.</para>
///
/// <para>Значения выдуманные: счета заказчика в тесты не переносятся.</para>
/// </summary>
public class InvoiceScanLineKeysTests
{
    /// <summary>Каждое поле строки — либо цель распознавания, либо исключение с причиной; не оба.</summary>
    [Fact]
    public void Каждое_поле_строки_решено_целью_или_исключением()
    {
        var fields = typeof(InvoiceLineValues).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        var targets = InvoiceScanLines.Targets.Values.ToHashSet(StringComparer.Ordinal);

        Assert.Empty(fields.Where(f => !targets.Contains(f) && !InvoiceScanLines.NotTargets.ContainsKey(f)));
        Assert.Empty(fields.Where(f => targets.Contains(f) && InvoiceScanLines.NotTargets.ContainsKey(f)));
        // Таблица не называет того, чего у строки нет: опечатка в имени свойства иначе была бы целью-призраком.
        Assert.Empty(targets.Concat(InvoiceScanLines.NotTargets.Keys).Where(f => !fields.Contains(f)));
        Assert.All(InvoiceScanLines.NotTargets.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
        // Одно поле — один ключ: два ключа одного поля спорили бы, чья ячейка легла.
        Assert.Equal(targets.Count, InvoiceScanLines.Targets.Count);
    }

    /// <summary>
    /// Таблица и разбор согласны: смена ячейки под ключом меняет ровно то поле, которое таблица ему
    /// назначила. Объяви кто-то ключ целью, не научив разбор его читать, — поле не изменится.
    /// </summary>
    [Fact]
    public void Ключ_из_таблицы_заполняет_своё_поле()
    {
        var first = Full("Кабель", "К-100", "м", "2", "5,00", "10,00", "22", "1,80");
        var second = Full("Провод", "К-200", "шт", "3", "6,00", "11,00", "10", "1,00");
        var baseline = Single(first);

        Assert.All(InvoiceScanLines.Targets, pair =>
        {
            var (key, field) = (pair.Key, pair.Value);
            var row = new Dictionary<string, string?>(first) { [key] = second[key] };
            var before = Value(baseline, field);
            Assert.NotNull(before);
            Assert.NotEqual(before, Value(Single(row), field));
        });
    }

    /// <summary>Заводской профиль не читает колонок строки, которым в счёте некуда лечь.</summary>
    [Fact]
    public void У_заводского_профиля_все_колонки_строки_с_целью() =>
        Assert.Empty(CostsRecognitionProfiles.InvoiceLines.Select(c => c.Name).Where(k => !InvoiceScanLines.Targets.ContainsKey(k)));

    [Fact]
    public void Артикул_ложится_в_строку()
    {
        var line = Single(new Dictionary<string, string?>
        {
            [CostsRecognitionProfiles.LineName] = "Кабель",
            [CostsRecognitionProfiles.LineCode] = "ВВГ-3х2,5",
            [CostsRecognitionProfiles.LineAmount] = "10,00",
        });

        Assert.Equal("ВВГ-3х2,5", line.SupplierCode);
        Assert.Null(line.Note);
    }

    /// <summary>Ключ сравнивается после обрезки краевых пробелов — и только так: регистр — другой ключ.</summary>
    [Fact]
    public void Ключ_с_пробелами_по_краям_тот_же_а_со_строчной_другой()
    {
        var spaced = Single(new Dictionary<string, string?> { [" Артикул "] = "А-1", [CostsRecognitionProfiles.LineName] = "Кабель" });
        Assert.Equal("А-1", spaced.SupplierCode);

        var lower = Single(new Dictionary<string, string?> { ["артикул"] = "А-1", [CostsRecognitionProfiles.LineName] = "Кабель" });
        Assert.Null(lower.SupplierCode);
        Assert.Equal("Прочитано, в счёт не положено: артикул «А-1»", lower.Note);
    }

    /// <summary>Длинный артикул не обрезается и не роняет разбор — он в примечании, как непрочитанное число.</summary>
    [Fact]
    public void Артикул_длиннее_колонки_уходит_в_примечание()
    {
        var code = new string('А', InvoiceLine.SupplierCodeLength + 1);
        var line = Single(new Dictionary<string, string?> { [CostsRecognitionProfiles.LineName] = "Кабель", [CostsRecognitionProfiles.LineCode] = code });

        Assert.Null(line.SupplierCode);
        Assert.Contains($"В файле не прочитано: артикул «{code[..40]}…» (длиннее {InvoiceLine.SupplierCodeLength} знаков)", line.Note);
    }

    /// <summary>
    /// Колонка без поля в счёте названа в примечании вместе с непрочитанным; пустая — не названа: ей
    /// нечего терять.
    /// </summary>
    [Fact]
    public void Колонка_без_поля_в_примечании_рядом_с_непрочитанным()
    {
        var line = Single(new Dictionary<string, string?>
        {
            [CostsRecognitionProfiles.LineName] = "Кабель",
            [CostsRecognitionProfiles.LineQuantity] = "много",
            ["Страна"] = "Россия",
            ["ГТД"] = " ",
        });

        Assert.Equal("Кабель", line.SupplierText);
        Assert.Equal("В файле не прочитано: количество «много»; Прочитано, в счёт не положено: Страна «Россия»", line.Note);
    }

    /// <summary>Строка без колонки «Артикул» — как раньше: артикула нет, примечания нет.</summary>
    [Fact]
    public void Строка_без_артикула_как_раньше()
    {
        var line = Single(new Dictionary<string, string?>
        {
            [CostsRecognitionProfiles.LineName] = "Кабель",
            [CostsRecognitionProfiles.LineQuantity] = "2",
            [CostsRecognitionProfiles.LinePrice] = "5,00",
        });

        Assert.Null(line.SupplierCode);
        Assert.Equal(10m, line.Amount);
        Assert.Null(line.Note);
    }

    private static Dictionary<string, string?> Full(
        string name, string code, string unit, string quantity, string price, string amount, string rate, string vat) =>
        new()
        {
            [CostsRecognitionProfiles.LineName] = name,
            [CostsRecognitionProfiles.LineCode] = code,
            [CostsRecognitionProfiles.LineUnit] = unit,
            [CostsRecognitionProfiles.LineQuantity] = quantity,
            [CostsRecognitionProfiles.LinePrice] = price,
            [CostsRecognitionProfiles.LineAmount] = amount,
            [CostsRecognitionProfiles.LineVatRate] = rate,
            [CostsRecognitionProfiles.LineVatAmount] = vat,
        };

    private static object? Value(InvoiceLineValues line, string field) =>
        typeof(InvoiceLineValues).GetProperty(field)!.GetValue(line);

    private static InvoiceLineValues Single(IReadOnlyDictionary<string, string?> row) =>
        Assert.Single(InvoiceScanLines.Read([row]).Lines);
}
