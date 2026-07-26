using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Reconciliation;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Reconciliation;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Reconciliation;

/// <inheritdoc />
public class ReconciliationRunner(
    AppDbContext db,
    IBlobStorage blob,
    DataSetParserFactory parserFactory) : IReconciliationRunner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Сторона, сведённая к «ключ → итог»: значения строк с одним ключом просуммированы,
    /// номера исходных строк сохранены для провенанса.</summary>
    private sealed record SideTotals(
        Guid SourceId,
        string ValueColumn,
        Dictionary<string, double> Values,
        Dictionary<string, List<int>> Rows,
        Dictionary<string, string> Labels);

    public async Task<ReconciliationRun> RunAsync(Guid definitionId, CancellationToken ct = default)
    {
        var definition = await db.Set<ReconciliationDefinition>()
            .FirstOrDefaultAsync(d => d.Id == definitionId, ct)
            ?? throw new KeyNotFoundException($"Сверка {definitionId} не найдена");

        var run = ReconciliationRun.Start(definitionId);
        db.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            var spec = definition.Spec.Deserialize<ReconciliationSpec>(Json)
                ?? throw new InvalidOperationException("Спека сверки пуста или нечитаема.");

            var left = await TotalsAsync(spec.Left, ct);
            var right = await TotalsAsync(spec.Right, ct);

            var findings = Compare(run.Id, spec, left, right).ToList();
            db.AddRange(findings);

            run.Complete(
                findings.Count(f => f.Status == FindingStatus.Match),
                findings.Count(f => f.Status == FindingStatus.Mismatch),
                findings.Count(f => f.Status == FindingStatus.MissingLeft),
                findings.Count(f => f.Status == FindingStatus.MissingRight));
        }
        catch (Exception ex)
        {
            // Неудача обязана быть видимой: пустой журнал без объяснения выглядел бы как «расхождений нет».
            run.Fail(ex.Message);
        }

        await db.SaveChangesAsync(ct);
        return run;
    }

    /// <summary>
    /// Строки источника после всей его обработки — тот же путь, которым их видит генерация. Иначе
    /// сверка судила бы о данных, отличных от попадающих в документ.
    /// </summary>
    private async Task<SideTotals> TotalsAsync(ReconciliationSide side, CancellationToken ct)
    {
        var source = await db.DataSetSources.AsNoTracking().Include(s => s.File)
            .FirstOrDefaultAsync(s => s.Id == side.SourceId, ct)
            ?? throw new InvalidOperationException($"Источник {side.SourceId} не найден.");

        var rows = await DataSetBindingProcessor.LoadRowsAsync(blob, parserFactory, source, ct);

        var values = new Dictionary<string, double>();
        var rowNumbers = new Dictionary<string, List<int>>();
        var labels = new Dictionary<string, string>();

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var key = ReconciliationKeys.Build(side.KeyColumns.Select(c => row.GetValueOrDefault(c)));
            if (ReconciliationKeys.IsEmpty(key)) continue; // сопоставлять нечего — это шум, не находка

            // Отсутствие значения не то же самое, что ноль: незаполненная ячейка не делает позицию
            // нулевой, но и не отменяет её присутствия в документе.
            values[key] = values.GetValueOrDefault(key)
                + (QuantityParser.Parse(row.GetValueOrDefault(side.ValueColumn)) ?? 0);

            if (!rowNumbers.TryGetValue(key, out var list)) rowNumbers[key] = list = [];
            list.Add(i);

            if (!labels.ContainsKey(key))
            {
                var labelColumn = side.LabelColumn ?? side.KeyColumns.FirstOrDefault();
                var label = labelColumn is null ? null : row.GetValueOrDefault(labelColumn);
                labels[key] = string.IsNullOrWhiteSpace(label) ? key : label;
            }
        }

        return new SideTotals(source.Id, side.ValueColumn, values, rowNumbers, labels);
    }

    private static IEnumerable<ReconciliationFinding> Compare(
        Guid runId, ReconciliationSpec spec, SideTotals left, SideTotals right)
    {
        // Ключи обеих сторон: позиция, которой нет на одной из них, — такая же находка, как и
        // расхождение в количестве, и молча пропасть не должна.
        foreach (var key in left.Values.Keys.Union(right.Values.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            var hasLeft = left.Values.TryGetValue(key, out var l);
            var hasRight = right.Values.TryGetValue(key, out var r);

            var status = (hasLeft, hasRight) switch
            {
                (false, true) => FindingStatus.MissingLeft,
                (true, false) => FindingStatus.MissingRight,
                _ => Satisfies(l, r, spec.Comparison) ? FindingStatus.Match : FindingStatus.Mismatch,
            };

            var label = left.Labels.GetValueOrDefault(key) ?? right.Labels.GetValueOrDefault(key) ?? key;

            yield return ReconciliationFinding.Create(
                runId, key, label,
                hasLeft ? l : null, hasRight ? r : null, status,
                Provenance(key, left, right));
        }
    }

    public static bool Satisfies(double left, double right, ComparisonRule rule)
    {
        var tolerance = rule.ToleranceKind == ToleranceKind.Percent
            ? Math.Abs(right) * rule.Tolerance / 100.0
            : rule.Tolerance;

        return rule.Operator switch
        {
            ComparisonOperator.Equal => Math.Abs(left - right) <= tolerance,
            ComparisonOperator.GreaterOrEqual => left >= right - tolerance,
            ComparisonOperator.LessOrEqual => left <= right + tolerance,
            _ => false,
        };
    }

    /// <summary>Файл, источник, колонка и номера строк по каждой стороне — то, что модель честно
    /// может дать. До ячейки не дотягиваем и не обещаем (P3 в #414).</summary>
    private static JsonDocument Provenance(string key, SideTotals left, SideTotals right)
        => JsonSerializer.SerializeToDocument(new
        {
            left = SideProvenance(key, left),
            right = SideProvenance(key, right),
        }, Json);

    private static object? SideProvenance(string key, SideTotals side)
        => side.Rows.TryGetValue(key, out var rows)
            ? new { sourceId = side.SourceId, column = side.ValueColumn, rows }
            : null;
}
