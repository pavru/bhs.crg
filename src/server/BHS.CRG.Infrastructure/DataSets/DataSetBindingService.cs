using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Привязки набора данных к документу/записи каталога (CRUD + предпросмотр значений).
/// Часть декомпозиции <see cref="DataSetService"/> (см. архитектурный отчёт, «Предложение 3»).
/// </summary>
public class DataSetBindingService(
    AppDbContext db,
    IBlobStorage blob,
    DataSetParserFactory parserFactory,
    ILogger<DataSetBindingService> logger)
{
    public async Task<IReadOnlyList<DataSetBindingDto>> ListBindingsAsync(Guid ownerId, CancellationToken ct)
    {
        var bindings = await db.DataSetBindings
            .Include(b => b.Source).ThenInclude(s => s.File)
            .Where(b => b.OwnerId == ownerId)
            .AsNoTracking()
            .ToListAsync(ct);
        return bindings.Select(DataSetDtoMapper.MapBinding).ToList();
    }

    public async Task<DataSetBindingDto?> CreateBindingAsync(CreateBindingInput input, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File)
            .FirstOrDefaultAsync(s => s.Id == input.SourceId, ct);
        if (source == null) return null;

        var binding = DataSetBinding.For(input.OwnerId, input.SourceId, input.TargetFieldKey,
            DataSetDtoMapper.SerializeMapping(input.Mapping));
        db.DataSetBindings.Add(binding);
        await db.SaveChangesAsync(ct);

        await db.Entry(binding).Reference(b => b.Source).LoadAsync(ct);
        await db.Entry(binding.Source).Reference(s => s.File).LoadAsync(ct);
        return DataSetDtoMapper.MapBinding(binding);
    }

    public async Task<DataSetBindingDto?> UpdateBindingAsync(Guid id, UpdateBindingInput input, CancellationToken ct)
    {
        var binding = await db.DataSetBindings.Include(b => b.Source).ThenInclude(s => s.File)
            .FirstOrDefaultAsync(b => b.Id == id, ct);
        if (binding == null) return null;

        binding.Update(input.TargetFieldKey, DataSetDtoMapper.SerializeMapping(input.Mapping));
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapBinding(binding);
    }

    public async Task<bool> DeleteBindingAsync(Guid id, CancellationToken ct)
    {
        var binding = await db.DataSetBindings.FindAsync([id], ct);
        if (binding == null) return false;
        db.DataSetBindings.Remove(binding);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<BindingPreviewDto>> PreviewBindingsAsync(Guid ownerId, CancellationToken ct)
    {
        var bindings = await db.DataSetBindings
            .Include(b => b.Source).ThenInclude(s => s.File)
            .Where(b => b.OwnerId == ownerId)
            .AsNoTracking()
            .ToListAsync(ct);

        var results = new List<BindingPreviewDto>();
        foreach (var binding in bindings)
        {
            try
            {
                var rows = await DataSetBindingProcessor.LoadRowsAsync(blob, parserFactory, binding.Source, ct);

                // Материализованный источник (issue #19/#23): привязка без своего маппинга берёт маппинг
                // с источника — как и резолвер генерации. Иначе превью пустое и материалы/сертификаты не
                // извлекаются на вкладке «Документы качества».
                var mappingJson = DataSetMappingValue.EffectiveMappingJson(
                    binding.Mapping, binding.Source.MaterializeTypeId, binding.Source.MaterializeMapping);
                var mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(mappingJson) ?? [];

                if (binding.TargetFieldKey is null)
                {
                    var row = rows.Count > 0 ? rows[0] : null;
                    var data = new Dictionary<string, object?>();
                    foreach (var (fieldKey, colName) in mapping)
                        if (!string.IsNullOrEmpty(colName))
                            data[fieldKey] = DataSetDtoMapper.PreviewCell(colName, row);

                    results.Add(new BindingPreviewDto(binding.Id, binding.Source.Name, binding.Source.File.Name,
                        "scalar", null, rows.Count, data, null));
                }
                else
                {
                    var mapped = rows.Select(row =>
                    {
                        var obj = new Dictionary<string, object?>();
                        foreach (var (fieldKey, colName) in mapping)
                            if (!string.IsNullOrEmpty(colName))
                                obj[fieldKey] = DataSetDtoMapper.PreviewCell(colName, row);
                        return obj;
                    }).ToList();

                    results.Add(new BindingPreviewDto(binding.Id, binding.Source.Name, binding.Source.File.Name,
                        "tabular", binding.TargetFieldKey, mapped.Count, mapped, null));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Не удалось построить предпросмотр привязки {BindingId}", binding.Id);
                results.Add(new BindingPreviewDto(binding.Id, binding.Source?.Name ?? "?",
                    binding.Source?.File?.Name ?? "?", "error", binding.TargetFieldKey, 0, new { }, ex.Message));
            }
        }
        return results;
    }
}
