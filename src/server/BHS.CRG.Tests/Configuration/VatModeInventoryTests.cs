using System.Text.RegularExpressions;
using BHS.CRG.Tests.Common;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись мест, которые знают режим НДС счёта, бумажную сумму без НДС и цену строки (сторож С1,
/// issue #1497).
///
/// <para><b>От чего это.</b> Модель «сверху» держится на одном: сумма строки <c>amount</c> — С НДС в
/// обоих режимах, режим приводится к ней один раз, при записи. Поэтому разноска, книга, оплата, отчёты и
/// реестр режима не знают — и знать не должны. Отчёт, начавший читать <c>NetAmount</c> или цену «чтобы
/// посчитать без НДС точнее», заведёт вторую арифметику рядом с первой, и разойдутся они ровно на НДС —
/// молча. Цена здесь потому, что она единственное число строки, смысл которого зависит от режима.</para>
///
/// <para>Новое место, которому режим правда нужен, вписывается сюда — с причиной. Подсказка «похоже, НДС
/// сверху» (#1500) и режим со скана (#1501) придут своими PR.</para>
/// </summary>
public class VatModeInventoryTests
{
    private const string Module = "BHS.CRG.Modules.Costs";

    /// <summary>Кому позволено знать режим, бумажную сумму и цену строки — и почему.</summary>
    private static readonly Dictionary<string, string> MayKnowTheMode = new()
    {
        [$"{Module}/Data/VatMode.cs"] = "сам режим",
        [$"{Module}/Data/Invoice.cs"] = "режим — признак счёта; меняет его только запись строк",
        [$"{Module}/Data/InvoiceLine.cs"] =
            "строка и граница записи: Completed(mode) приводит бумагу к сумме с НДС, CompletedLine — единственный вход записи",
        [$"{Module}/Data/CostsDbContext.cs"] = "колонки и проверка в базе amount = net_amount + vat_amount",
        [$"{Module}/Data/InvoiceVersion.cs"] = "снимок версии хранит бумагу счёта: режим, цену и сумму без НДС",
        [$"{Module}/Endpoints/InvoiceLineContracts.cs"] =
            "контракт строк: бумажная сумма полем своего режима, отказ на чужое поле, итоги с суммой без НДС",
        [$"{Module}/Endpoints/InvoiceLineEndpoints.cs"] = "адрес строк: режим едет со строками и пишется с ними",
        [$"{Module}/Endpoints/InvoiceContracts.cs"] = "ответ чтения: режим счёта, цена и сумма без НДС строки",
        [$"{Module}/Endpoints/InvoiceDesk.Correction.cs"] =
            "исправление досчитывает присланные строки по режиму, чтобы узнать вид разноски",
        [$"{Module}/Endpoints/InvoiceReadiness.cs"] =
            "готовность: у строк сверху без ставки нет суммы с НДС; расхождение, объяснённое НДС от итога",
        [$"{Module}/Endpoints/InvoiceScanLines.cs"] = "распознавание: строки скана читаются «в том числе» и цена из бумаги",
        [$"{Module}/Endpoints/InvoiceScanReading.cs"] =
            "распознавание не кладёт строки «в том числе» в счёт, которому человек поставил «сверху»",
    };

    // Цена — обращением к свойству и именем аргумента; режим и бумажная сумма — по имени и по колонке.
    private static readonly Regex Knows = new(
        @"\bVatMode\b|\bVatModes\b|\bNetAmount\b|\bnet_amount\b|\bvat_mode\b|\.Price\b|\bPrice:",
        RegexOptions.Compiled);

    [Fact]
    public void Режим_НДС_знают_только_граница_записи_и_её_соседи()
    {
        var outsiders = new List<string>();
        foreach (var file in SourceTree.Files(Module))
        {
            var rel = SourceTree.Relative(file);
            if (MayKnowTheMode.ContainsKey(rel)) continue;

            var text = string.Join("\n", File.ReadAllLines(file).Select(l => l.TrimStart().StartsWith("//") ? "" : l));
            foreach (Match m in Knows.Matches(text))
                outsiders.Add($"{rel}:{SourceTree.LineOf(text, m.Index)}");
        }

        Assert.True(outsiders.Count == 0,
            "Режим НДС, сумму без НДС или цену строки читают мимо границы записи:\n" + string.Join("\n", outsiders) +
            "\n\nСумма строки amount — С НДС в обоих режимах, и разноске, книге, отчётам и реестру режим не нужен: " +
            "вторая арифметика рядом с первой разошлась бы с ней на НДС молча. Если месту режим правда нужен — " +
            "впишите файл в MayKnowTheMode с причиной.");
    }

    [Fact]
    public void Перепись_не_хранит_умерших_записей()
    {
        var stale = MayKnowTheMode.Keys
            .Where(rel => !File.Exists(Path.Combine(SourceTree.SolutionDir, rel))
                          || !Knows.IsMatch(File.ReadAllText(Path.Combine(SourceTree.SolutionDir, rel))))
            .ToList();

        Assert.True(stale.Count == 0, "В переписи режима НДС файлы, где обращения больше нет: " + string.Join(", ", stale));
    }
}
