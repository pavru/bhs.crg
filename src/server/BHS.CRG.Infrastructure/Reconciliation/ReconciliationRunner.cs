using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Reconciliation;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Reconciliation;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

using BHS.CRG.Infrastructure.Common;

namespace BHS.CRG.Infrastructure.Reconciliation;

/// <inheritdoc />
public class ReconciliationRunner(
    AppDbContext db,
    IBlobStorage blob,
    DataSetParserFactory parserFactory) : IReconciliationRunner
{
    private static JsonSerializerOptions Json => ReconciliationSpecJson.Options;

    /// <summary>Сторона, сведённая к «ключ → итог»: значения строк с одним ключом просуммированы,
    /// номера исходных строк сохранены для провенанса.</summary>
    /// <summary>Строки одного источника, попавшие в позицию: своей колонкой и своими номерами.</summary>
    private sealed record PartRows(Guid SourceId, string ValueColumn, List<int> Rows);

    private sealed record SideTotals(
        Dictionary<string, double> Values,
        Dictionary<string, List<PartRows>> Parts,
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

            // Алиасы применяются на СВЁРТКЕ, до сравнения: иначе сопоставление ключей ничего не
            // даст — количества так и останутся в двух разных позициях.
            var aliases = await AliasesAsync(ct);
            var left = await TotalsAsync(spec.Left, aliases, ct);
            var right = await TotalsAsync(spec.Right, aliases, ct);

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
    /// <summary>
    /// Только ПОДТВЕРЖДЁННЫЕ алиасы. Предложенный, но не подтверждённый, попав в путь сравнения, и
    /// есть модель внутри арифметики — риск P1 из #414: отчёт начал бы меняться сам по себе.
    /// </summary>
    private async Task<AliasResolver> AliasesAsync(CancellationToken ct)
    {
        var confirmed = await db.Set<ReconciliationAlias>().AsNoTracking()
            .Where(a => a.Status == AliasStatus.Confirmed)
            .Select(a => new { a.AliasKey, a.CanonicalKey })
            .ToListAsync(ct);

        if (confirmed.Count == 0) return AliasResolver.Empty;

        // Один вариант сводится ровно к одному канону: два разных канона у одного ключа сделали бы
        // результат зависящим от порядка выборки.
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in confirmed) map.TryAdd(a.AliasKey, a.CanonicalKey);
        return new AliasResolver(map);
    }

    /// <summary>
    /// Свод стороны: количества по одному ключу суммируются по ВСЕМ её источникам (issue #450) —
    /// «сумма по четырём листам шкафов». У каждого источника свои колонки.
    ///
    /// Строки берутся после всей обработки источника — тем же путём, которым их видит генерация.
    /// </summary>
    private async Task<SideTotals> TotalsAsync(
        ReconciliationSide side, AliasResolver aliases, CancellationToken ct)
    {
        var values = new Dictionary<string, double>();
        var parts = new Dictionary<string, List<PartRows>>();
        var labels = new Dictionary<string, string>();

        foreach (var part in side.EffectiveSources)
        {
            var source = await db.DataSetSources.AsNoTracking().Include(s => s.File)
                .FirstOrDefaultAsync(s => s.Id == part.SourceId, ct)
                ?? throw new InvalidOperationException($"Источник {part.SourceId} не найден.");

            var rows = await DataSetBindingProcessor.LoadRowsAsync(blob, parserFactory, source, ct);

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var rawKey = ReconciliationKeys.Build(part.KeyColumns.Select(c => row.GetValueOrDefault(c)));
                if (ReconciliationKeys.IsEmpty(rawKey)) continue; // сопоставлять нечего — это шум, не находка
                var key = aliases.Resolve(rawKey);

                // Отсутствие значения не то же самое, что ноль: незаполненная ячейка не делает позицию
                // нулевой, но и не отменяет её присутствия в документе.
                values[key] = values.GetValueOrDefault(key)
                    + (QuantityParser.Parse(row.GetValueOrDefault(part.ValueColumn)) ?? 0);

                if (!parts.TryGetValue(key, out var list)) parts[key] = list = [];
                var slot = list.FirstOrDefault(p => p.SourceId == part.SourceId);
                if (slot is null) list.Add(slot = new PartRows(part.SourceId, part.ValueColumn, []));
                slot.Rows.Add(i);

                if (!labels.ContainsKey(key))
                {
                    var labelColumn = part.LabelColumn ?? part.KeyColumns.FirstOrDefault();
                    var label = labelColumn is null ? null : row.GetValueOrDefault(labelColumn);
                    labels[key] = string.IsNullOrWhiteSpace(label) ? key : label;
                }
            }
        }

        return new SideTotals(values, parts, labels);
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

    /// <summary>
    /// Провенанс: по каждой стороне — все источники, из которых собралась позиция. Свод из четырёх
    /// листов обязан назвать все четыре, иначе расхождение не проверить глазами, а это единственное,
    /// ради чего провенанс существует.
    ///
    /// До ячейки не дотягиваем и не обещаем (P3 в #414): строки из PDF приходят от зрительной модели.
    /// </summary>
    private static JsonDocument Provenance(string key, SideTotals left, SideTotals right)
        => JsonSerializer.SerializeToDocument(new
        {
            left = SideProvenance(key, left),
            right = SideProvenance(key, right),
        }, Json);

    private static object? SideProvenance(string key, SideTotals side)
    {
        if (!side.Parts.TryGetValue(key, out var parts) || parts.Count == 0) return null;

        var list = parts.Select(p => new { sourceId = p.SourceId, column = p.ValueColumn, rows = p.Rows }).ToList();
        // Одиночный источник пишем и прежней формой: её читают уже сохранённые находки, экран и отчёт.
        return new
        {
            sourceId = parts[0].SourceId,
            column = parts[0].ValueColumn,
            rows = parts[0].Rows,
            parts = list,
        };
    }
}
