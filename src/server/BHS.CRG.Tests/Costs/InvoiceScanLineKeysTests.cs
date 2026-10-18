using System.Reflection;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Строки скана раскладываются по ключам колонок профиля (issue #1480): колонка с ключом поля строки
/// заполняет это поле, колонка с другим ключом не теряется молча — она названа в примечании
/// распознавания.
///
/// <para><b>Сторож задачи</b> — первые тесты: новое поле строки без решения «цель или нет» краснеет,
/// таблица ключей не расходится ни с разбором, ни с инструкцией администратора. Ломается он тем, что
/// поле объявлено целью, а разбор его по-прежнему не читает, — так раньше молча терялся артикул.</para>
///
/// <para>Значения выдуманные: счета заказчика в тесты не переносятся.</para>
/// </summary>
public class InvoiceScanLineKeysTests
{
    /// <summary>
    /// Поля строки, которые распознавание не заполняет, — и почему. Новое поле строки обязано попасть
    /// либо в <see cref="ScanLineField" />, либо сюда.
    /// </summary>
    private static readonly Dictionary<string, string> NotTargets = new(StringComparer.Ordinal)
    {
        [nameof(InvoiceLineValues.NomenclatureId)] =
            "позицию подставляет запомненное для поставщика или выбирает человек — не бумага",
        [nameof(InvoiceLineValues.MatchedBy)] = "пометка подстановки из запомненного ставится сервером, а не читается",
        [nameof(InvoiceLineValues.Note)] = "примечание строки собирает сам разбор: что в файле не прочиталось",
    };

    /// <summary>Каждое поле строки — либо цель распознавания, либо исключение с причиной; не оба.</summary>
    [Fact]
    public void Каждое_поле_строки_решено_целью_или_исключением()
    {
        var fields = typeof(InvoiceLineValues).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        var targets = Enum.GetNames<ScanLineField>().ToHashSet(StringComparer.Ordinal);

        Assert.Empty(fields.Where(f => !targets.Contains(f) && !NotTargets.ContainsKey(f)));
        Assert.Empty(fields.Where(f => targets.Contains(f) && NotTargets.ContainsKey(f)));
        // Перечень не называет того, чего у строки нет: опечатка в имени иначе была бы целью-призраком.
        Assert.Empty(targets.Concat(NotTargets.Keys).Where(f => !fields.Contains(f)));
        // У каждой цели ровно один ключ: два ключа одного поля спорили бы, чья ячейка легла.
        Assert.Equal(
            Enum.GetValues<ScanLineField>().Order(),
            InvoiceScanLines.Targets.Values.Order());
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
            var (key, field) = (pair.Key, pair.Value.ToString());
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

    /// <summary>
    /// Таблица ключей в инструкции администратора — та же, что в коде (ревью PR #1485): администратор
    /// называет колонку по инструкции, и расхождение молча сделало бы её «не положенной».
    /// </summary>
    [Fact]
    public void Инструкция_называет_те_же_ключи_строк()
    {
        var guide = File.ReadAllLines(Path.Combine(RepoRoot(), "docs", "ADMIN_GUIDE.md"));
        var start = Array.FindIndex(guide, l => l.Contains("«Счёт на оплату»: ключи строк"));
        Assert.True(start >= 0, "В ADMIN_GUIDE нет врезки «Счёт на оплату: ключи строк».");

        var documented = guide.Skip(start).TakeWhile(l => l.StartsWith('>'))
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"^>\s*\|\s*`([^`]+)`\s*\|"))
            .Where(m => m.Success).Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal);

        Assert.Equal(InvoiceScanLines.Targets.Keys.Order(StringComparer.Ordinal), documented);
    }

    [Fact]
    public void Артикул_ложится_в_строку()
    {
        var reading = InvoiceScanLines.Read([new Dictionary<string, string?>
        {
            [CostsRecognitionProfiles.LineName] = "Кабель",
            [CostsRecognitionProfiles.LineCode] = "ВВГ-3х2,5",
            [CostsRecognitionProfiles.LineAmount] = "10,00",
        }]);

        var line = Assert.Single(reading.Lines);
        Assert.Equal("ВВГ-3х2,5", line.SupplierCode);
        Assert.Null(line.Note);
        Assert.Null(reading.StrayNote);
    }

    /// <summary>
    /// Прочерк или крест в графе артикула — «артикула нет» (ревью PR #1485): общий «—» у всех строк
    /// сделал бы их одним товаром для соответствий, где артикул старше наименования.
    /// </summary>
    [Theory]
    [InlineData("—")]
    [InlineData("-")]
    [InlineData("Х")]
    public void Прочерк_в_артикуле_не_артикул(string dash)
    {
        var line = Single(new Dictionary<string, string?> { [CostsRecognitionProfiles.LineName] = "Кабель", [CostsRecognitionProfiles.LineCode] = dash });

        Assert.Null(line.SupplierCode);
        Assert.Null(line.Note);
    }

    /// <summary>Ключ сравнивается после обрезки краевых пробелов — и только так: регистр — другой ключ.</summary>
    [Fact]
    public void Ключ_с_пробелами_по_краям_тот_же_а_со_строчной_другой()
    {
        var spaced = Single(new Dictionary<string, string?> { [" Артикул "] = "А-1", [CostsRecognitionProfiles.LineName] = "Кабель" });
        Assert.Equal("А-1", spaced.SupplierCode);

        var lower = InvoiceScanLines.Read([new Dictionary<string, string?> { ["артикул"] = "А-1", [CostsRecognitionProfiles.LineName] = "Кабель" }]);
        Assert.Null(Assert.Single(lower.Lines).SupplierCode);
        Assert.Contains("«артикул» — у строки счёта нет поля с таким ключом (строк: 1, например «А-1»)", lower.StrayNote);
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
    /// Колонка без поля в счёте названа одним примечанием распознавания на все строки, а не в
    /// примечании каждой строки (ревью PR #1485): примечание строки — поле человека, оно едет в версии
    /// счёта. Пустая колонка не названа: ей нечего терять.
    /// </summary>
    [Fact]
    public void Колонка_без_поля_названа_в_примечании_распознавания_а_не_строки()
    {
        var reading = InvoiceScanLines.Read(
        [
            new Dictionary<string, string?>
            {
                [CostsRecognitionProfiles.LineName] = "Кабель",
                [CostsRecognitionProfiles.LineQuantity] = "много",
                ["Страна"] = "Россия",
                ["ГТД"] = " ",
            },
            new Dictionary<string, string?> { [CostsRecognitionProfiles.LineName] = "Провод", ["Страна"] = "Китай" },
        ]);

        Assert.Equal("В файле не прочитано: количество «много»", reading.Lines[0].Note);
        Assert.Null(reading.Lines[1].Note);
        Assert.StartsWith(
            "Прочитано, в счёт не положено: «Страна» — у строки счёта нет поля с таким ключом (строк: 2, например «Россия»).",
            reading.StrayNote);
        Assert.DoesNotContain("ГТД", reading.StrayNote);
    }

    /// <summary>
    /// Строка, где заполнены только колонки без поля в счёте, — не строка счёта (ревью PR #1485):
    /// подзаголовок раздела с одним «№» иначе лёг бы пустой строкой в очередь «Разобрать».
    /// </summary>
    [Fact]
    public void Строка_без_единого_поля_счёта_не_строка()
    {
        var reading = InvoiceScanLines.Read(
        [
            new Dictionary<string, string?> { [CostsRecognitionProfiles.LineName] = "Кабель", ["№"] = "1" },
            new Dictionary<string, string?> { [CostsRecognitionProfiles.LineName] = null, ["№"] = "5" },
        ]);

        Assert.Equal("Кабель", Assert.Single(reading.Lines).SupplierText);
        Assert.Contains("«№» — у строки счёта нет поля с таким ключом (строк: 2", reading.StrayNote);
    }

    /// <summary>
    /// Два ключа одного поля: поле берёт первый непустой, второй непустой назван (ревью PR #1485) —
    /// прочитаны два разных числа, и о втором человек обязан узнать.
    /// </summary>
    [Fact]
    public void Второй_ключ_того_же_поля_назван_а_не_отброшен()
    {
        var reading = InvoiceScanLines.Read([new Dictionary<string, string?>
        {
            [CostsRecognitionProfiles.LineName] = "Кабель",
            ["Цена"] = "5,00",
            ["Цена "] = "50,00",
        }]);

        Assert.Equal(5m, Assert.Single(reading.Lines).Price);
        Assert.Contains("«Цена » — повторяет ключ «Цена», поле уже заполнено им (строк: 1, например «50,00»)", reading.StrayNote);
    }

    /// <summary>Строка без колонки «Артикул» — как раньше: артикула нет, примечаний нет.</summary>
    [Fact]
    public void Строка_без_артикула_как_раньше()
    {
        var reading = InvoiceScanLines.Read([new Dictionary<string, string?>
        {
            [CostsRecognitionProfiles.LineName] = "Кабель",
            [CostsRecognitionProfiles.LineQuantity] = "2",
            [CostsRecognitionProfiles.LinePrice] = "5,00",
        }]);

        var line = Assert.Single(reading.Lines);
        Assert.Null(line.SupplierCode);
        Assert.Equal(10m, line.Amount);
        Assert.Null(line.Note);
        Assert.Null(reading.StrayNote);
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

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "docs", "ADMIN_GUIDE.md"))) return dir.FullName;
        throw new InvalidOperationException("Корень репозитория не найден: нет docs/ADMIN_GUIDE.md выше каталога тестов.");
    }
}
