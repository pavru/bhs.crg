using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Generation;

public class DataSetResolver(
    AppDbContext db,
    IBlobStorage blobStorage,
    DataSetParserFactory parserFactory,
    ILogger<DataSetResolver> logger
) : IDataSetResolver
{
    public async Task InjectAsync(GenerationContext ctx, DocumentInstance instance, CancellationToken ct = default)
    {
        var bindings = await db.DataSetBindings
            .Include(b => b.Source)
                .ThenInclude(s => s.File)
            .Where(b => b.InstanceId == instance.Id)
            .AsNoTracking()
            .ToListAsync(ct);

        if (bindings.Count == 0) return;

        foreach (var binding in bindings)
        {
            try
            {
                // Download → parse → computed columns → filter (shared with preview via DataSetBindingProcessor).
                var rows = await DataSetBindingProcessor.LoadRowsAsync(
                    blobStorage, parserFactory, binding.Source.File.BlobPath, binding.Source.File.Format,
                    binding.Source.SheetOrPath, binding.ComputedColumns, binding.RowFilter, ct);

                var mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(binding.Mapping)
                    ?? [];

                if (binding.TargetFieldKey is null)
                {
                    // Скалярный: первая строка → отдельные поля контекста
                    if (rows.Count > 0)
                    {
                        var row = rows[0];
                        foreach (var (fieldKey, colName) in mapping)
                            if (row.TryGetValue(colName, out var val))
                                ctx.Set(fieldKey, val);
                    }
                }
                else
                {
                    // Табличный: все строки → List<Dict> в array-поле
                    var mapped = rows.Select(row =>
                    {
                        var obj = new Dictionary<string, string?>();
                        foreach (var (fieldKey, colName) in mapping)
                            if (row.TryGetValue(colName, out var val))
                                obj[fieldKey] = val;
                        return obj;
                    }).ToList();
                    ctx.Set(binding.TargetFieldKey, mapped);
                }
            }
            catch (Exception ex)
            {
                // Пропускаем невалидные привязки, чтобы не блокировать генерацию,
                // но фиксируем причину — иначе "пустые" поля невозможно отладить.
                logger.LogWarning(ex,
                    "Привязка набора данных пропущена при генерации. BindingId={BindingId}, SourceId={SourceId}, Instance={InstanceId}",
                    binding.Id, binding.SourceId, instance.Id);
            }
        }
    }
}
