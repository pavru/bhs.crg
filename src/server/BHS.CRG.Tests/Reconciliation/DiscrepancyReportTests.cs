using System.Text.Json;
using BHS.CRG.Application.Reconciliation;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Reconciliation;
using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.Reconciliation;

/// <summary>
/// «Отчёт о расхождениях» (issue #444) — артефакт, который сегодня ведут руками. Он уходит из системы
/// в переписку и на совещания, поэтому должен быть самодостаточным и не смазывать происхождение данных.
/// </summary>
public class DiscrepancyReportTests
{
    private static ReconciliationFinding Finding(
        string label, double? left, double? right, FindingStatus status)
        => ReconciliationFinding.Create(Guid.NewGuid(), label.ToLowerInvariant(), label, left, right, status,
            JsonDocument.Parse("""{"left":{"column":"ДлинаФакт","rows":[1,2]},"right":null}"""));

    private static ReconciliationRun CompletedRun()
    {
        var run = ReconciliationRun.Start(Guid.NewGuid());
        run.Complete(8, 1, 1, 0);
        return run;
    }

    [Fact]
    public void Findings_ComputeDifference_SoReaderDoesNotSubtractInHead()
    {
        var sheet = DiscrepancyReport.Findings("Кабель", CompletedRun(),
            [new FindingView(Finding("ВВГнг 3х2.5", 441, 430.5, FindingStatus.Mismatch), false, null)]);

        var row = Assert.Single(sheet.Rows);
        var diff = row[Array.IndexOf([.. sheet.Columns], "Разница")];
        Assert.Equal("10,5", diff);
    }

    /// <summary>
    /// Пустая таблица без объяснения читается как «расхождений нет» — самое опасное недоразумение
    /// подсистемы, и в файле, уходящем заказчику, оно опаснее всего.
    /// </summary>
    [Fact]
    public void FailedRun_SaysSoInPreamble()
    {
        var run = ReconciliationRun.Start(Guid.NewGuid());
        run.Fail("Источник не найден");

        var sheet = DiscrepancyReport.Findings("Кабель", run, []);

        Assert.Empty(sheet.Rows);
        Assert.Contains(sheet.Preamble, p => p.Contains("НЕ ВЫПОЛНЕН") && p.Contains("Источник не найден"));
    }

    [Fact]
    public void NoRuns_SaysSo_InsteadOfLookingClean()
    {
        var sheet = DiscrepancyReport.Findings("Кабель", null, []);
        Assert.Contains(sheet.Preamble, p => p.Contains("Прогонов не было"));
    }

    [Fact]
    public void Findings_CarryDecisionAndResolved()
    {
        var decision = ReconciliationDecision.Create(
            Guid.NewGuid(), "k", DecisionKind.Accepted, "Давальческий", "alex");
        var sheet = DiscrepancyReport.Findings("Кабель", CompletedRun(),
            [new FindingView(Finding("ВВГ", 50, 100, FindingStatus.Mismatch), Resolved: true, decision)]);

        var row = Assert.Single(sheet.Rows);
        Assert.Equal("да", row[Array.IndexOf([.. sheet.Columns], "Устранено")]);
        Assert.Equal("Признано нормой", row[Array.IndexOf([.. sheet.Columns], "Решение")]);
        Assert.Equal("Давальческий", row[Array.IndexOf([.. sheet.Columns], "Примечание")]);
        // Провенанс человеческим текстом: голый JSON в отчёте бесполезен.
        Assert.Contains("ДлинаФакт", row[Array.IndexOf([.. sheet.Columns], "Где смотреть")]);
    }

    /// <summary>
    /// Файл уходит в переписку и на совещания — там утверждение агента не должно сойти за результат
    /// проверки системы.
    /// </summary>
    [Fact]
    public void Observations_DeclareTheirOrigin()
    {
        var o = AgentObservation.Create(CatalogScope.Set, Guid.NewGuid(), "k",
            "Организация не совпадает", "Подробности", ObservationSeverity.Error,
            JsonDocument.Parse("""{"documentIds":["a","b"],"note":"акт 5"}"""), "агент");

        var sheet = DiscrepancyReport.Observations([o]);

        Assert.Contains(sheet.Preamble, p => p.Contains("НЕ результат проверки системы"));
        var row = Assert.Single(sheet.Rows);
        Assert.Equal("Существенно", row[0]);
        Assert.Equal("Не разобрано", row[1]);
        Assert.Contains("документов: 2", row[Array.IndexOf([.. sheet.Columns], "Где смотреть")]);
    }

    /// <summary>Ссылки, пришедшие строкой с JSON внутри, разворачиваются и в отчёте (#442).</summary>
    [Fact]
    public void Observations_UnwrapStringifiedReferences()
    {
        var stringified = JsonDocument.Parse(
            JsonSerializer.Serialize("""{"documentIds":["a"],"note":"строкой"}"""));
        var o = AgentObservation.Create(CatalogScope.Set, Guid.NewGuid(), "k", "Заголовок", null,
            ObservationSeverity.Info, stringified, "агент");

        var row = Assert.Single(DiscrepancyReport.Observations([o]).Rows);
        Assert.Contains("документов: 1", row[4]);
    }

    [Fact]
    public void MultiSheetExport_ProducesReadableWorkbook()
    {
        var findings = DiscrepancyReport.Findings("Кабель", CompletedRun(),
            [new FindingView(Finding("ВВГ", 1, 2, FindingStatus.Mismatch), false, null)]);
        var observations = DiscrepancyReport.Observations([]);

        var (bytes, ext, _) = SpreadsheetExporter.ExportSheets(SpreadsheetFormat.Xlsx,
        [
            new(findings.Name, findings.Columns, findings.Rows, findings.Preamble),
            new(observations.Name, observations.Columns, observations.Rows, observations.Preamble),
        ]);

        Assert.Equal("xlsx", ext);
        using var wb = new NPOI.XSSF.UserModel.XSSFWorkbook(new MemoryStream(bytes));
        Assert.Equal(2, wb.NumberOfSheets);
        Assert.Equal("Сверка", wb.GetSheetAt(0).SheetName);
        Assert.Equal("Замечания", wb.GetSheetAt(1).SheetName);
        // Шапка выше таблицы: без неё через неделю не понять, к чему относился файл.
        Assert.Contains("Кабель", wb.GetSheetAt(0).GetRow(0).GetCell(0).StringCellValue);
    }

    /// <summary>У CSV вкладок нет, и молча склеить их в один файл — потерять границу между ними.</summary>
    [Fact]
    public void CsvIsRejected_RatherThanFlattened()
        => Assert.Throws<InvalidRequestException>(() =>
            SpreadsheetExporter.ExportSheets(SpreadsheetFormat.Csv, [new("A", ["c"], [], null)]));
}
