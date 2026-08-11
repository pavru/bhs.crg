using System.Text.Json;
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
    IDataSetRowLoader rowLoader,
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
                var rows = await rowLoader.LoadRowsAsync(binding.Source, ct);

                // Материализация ссылкой на существующий документ (issue #725) — до маппинга, его в
                // этом режиме нет. Показываем НАИМЕНОВАНИЯ документов: идентификатор в таблице не
                // говорит человеку ничего, а именно сюда он идёт проверять, те ли документы уедут.
                if (binding.Source.MaterializeTypeId is not null
                    && DataSetMappingValue.IsEmptyMapping(binding.Mapping)
                    && MaterializeByIdMode.IsOn(binding.Source.MaterializeByIdColumn))
                {
                    results.Add(await PreviewDocumentRefsAsync(binding, rows, ct));
                    continue;
                }

                // Материализованный источник (issue #19/#23): привязка без своего маппинга берёт маппинг
                // с источника — как и резолвер генерации. Иначе превью пустое и материалы/сертификаты не
                // извлекаются на вкладке «Документы качества».
                var mappingJson = DataSetMappingValue.EffectiveMappingJson(
                    binding.Mapping, binding.Source.MaterializeTypeId, binding.Source.MaterializeMapping);
                var mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(mappingJson) ?? [];

                // Вариант union'а по типу документа строки (issue #716) — тот же выбор, что делает
                // генерация. Показывать здесь ВСЕ варианты сразу значило бы врать о результате: в
                // документ уедет ровно один ключ на строку, а строки без варианта не уедут вовсе.
                // Экран этот открывают, чтобы понять, почему поле выглядит не так, — и он обязан
                // показывать то же, что получится.
                var selector = binding.Source.MaterializeTypeId is not null
                    && DataSetMappingValue.IsEmptyMapping(binding.Mapping)
                    ? await BuildVariantSelectorAsync(binding.Source.MaterializeDiscriminator, rows, ct)
                    : null;

                // Материализация настроена, а маппинг пуст (issue #715) — то же, о чём говорит
                // резолвер при генерации. Сказать это ОБЯЗАН и предпросмотр: именно сюда человек
                // идёт выяснять, почему поле пустое, и таблица пустых объектов ответа не даёт.
                if (binding.Source.MaterializeTypeId is not null && DataSetMappingValue.IsEmptyMapping(mappingJson))
                {
                    results.Add(new BindingPreviewDto(binding.Id, binding.Source.Name, binding.Source.File.Name,
                        "error", binding.TargetFieldKey, rows.Count, new { },
                        $"Источник «{binding.Source.Name}» материализован, но маппинг колонок пуст — " +
                        "поле не заполняется. Задайте маппинг в диалоге материализации источника."));
                    continue;
                }

                var skipped = 0;
                if (binding.TargetFieldKey is null)
                {
                    var row = rows.Count > 0 ? rows[0] : null;
                    var data = new Dictionary<string, object?>();
                    foreach (var (fieldKey, colName) in PairsFor(selector, mapping, row, ref skipped))
                        if (!string.IsNullOrEmpty(colName))
                            data[fieldKey] = await DataSetDtoMapper.PreviewCellAsync(colName, row, ct);

                    results.Add(new BindingPreviewDto(binding.Id, binding.Source.Name, binding.Source.File.Name,
                        "scalar", null, rows.Count, data, null));
                }
                else
                {
                    var mapped = new List<Dictionary<string, object?>>();
                    foreach (var row in rows)
                    {
                        var pairs = PairsFor(selector, mapping, row, ref skipped);
                        if (selector is not null && pairs.Count == 0) continue;

                        var obj = new Dictionary<string, object?>();
                        foreach (var (fieldKey, colName) in pairs)
                            if (!string.IsNullOrEmpty(colName))
                                obj[fieldKey] = await DataSetDtoMapper.PreviewCellAsync(colName, row, ct);
                        mapped.Add(obj);
                    }

                    results.Add(new BindingPreviewDto(binding.Id, binding.Source.Name, binding.Source.File.Name,
                        "tabular", binding.TargetFieldKey, mapped.Count, mapped,
                        // Пропущенные не прячем в ноль строк: «показано меньше, чем в источнике» —
                        // это то, ради чего сюда и смотрят.
                        skipped > 0
                            ? $"Строк пропущено при материализации: {skipped} — их тип документа не назначен ни одному варианту."
                            : null));
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

    /// <summary>
    /// Предпросмотр материализации ссылкой на существующий документ (issue #725): по строке на
    /// документ, значение — наименование, а для отсутствующего документа сказано, что его нет.
    /// Пропущенные строки (пустая ячейка, не-Ид) показываются числом с причиной — как и у генерации.
    /// </summary>
    private async Task<BindingPreviewDto> PreviewDocumentRefsAsync(
        DataSetBinding binding,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows,
        CancellationToken ct)
    {
        var column = binding.Source.MaterializeByIdColumn!;

        if (binding.TargetFieldKey is null)
            return new BindingPreviewDto(binding.Id, binding.Source.Name, binding.Source.File.Name,
                "error", null, rows.Count, new { },
                $"Источник «{binding.Source.Name}» материализован ссылкой на существующий документ — " +
                "такую строку можно положить только в поле-документ или в список документов, а не в отдельные поля.");

        var ids = new List<Guid>();
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var (id, reason, _) = MaterializeByIdMode.ReadId(row, column);
            if (id is null) skipped[reason!] = skipped.GetValueOrDefault(reason!) + 1;
            else ids.Add(id.Value);
        }

        var labels = await MaterializeByIdMode.ResolveLabelsAsync(db, ids.Distinct().ToList(), ct);
        var mapped = ids
            .Select(id => new Dictionary<string, object?>
            {
                ["Документ"] = labels.TryGetValue(id, out var label) ? label : MaterializeByIdMode.NotFoundLabel,
            })
            .ToList();

        var warning = skipped.Count == 0
            ? null
            : "Строк пропущено при материализации: " + skipped.Values.Sum() + " — "
              + string.Join("; ", skipped.OrderByDescending(p => p.Value)
                  .Select(p => $"{p.Value} — {MaterializeSkipReason.Describe(p.Key)}"));

        return new BindingPreviewDto(binding.Id, binding.Source.Name, binding.Source.File.Name,
            "tabular", binding.TargetFieldKey, mapped.Count, mapped, warning);
    }

    /// <summary>Тот же выбор варианта, что у генерации (issue #716); null — правила нет.</summary>
    private async Task<MaterializeVariantSelector?> BuildVariantSelectorAsync(
        string? discriminatorJson,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows,
        CancellationToken ct)
    {
        var config = MaterializeVariantSelector.ParseConfig(discriminatorJson);
        if (config is null) return null;

        var typesById = await db.DocumentTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        Dictionary<Guid, Guid>? typeByDocument = null;
        var documentIds = MaterializeVariantSelector.DocumentIdsIn(config, rows).ToList();
        if (documentIds.Count > 0)
            typeByDocument = await db.DomainObjects.AsNoTracking()
                .Where(o => documentIds.Contains(o.Id))
                .ToDictionaryAsync(o => o.Id, o => o.CompositeTypeId, ct);

        return MaterializeVariantSelector.Create(config, typesById, typeByDocument);
    }

    /// <summary>Что показать по этой строке: без правила — весь маппинг, с правилом — пару
    /// победившего варианта. Пустой список = строка пропущена.</summary>
    private static List<KeyValuePair<string, string>> PairsFor(
        MaterializeVariantSelector? selector,
        Dictionary<string, string> mapping,
        IReadOnlyDictionary<string, string?>? row,
        ref int skipped)
    {
        if (selector is null || row is null) return [.. mapping];

        var choice = selector.Choose(row);
        if (choice.VariantKey is null || !mapping.TryGetValue(choice.VariantKey, out var token))
        {
            skipped++;
            return [];
        }
        return [new KeyValuePair<string, string>(choice.VariantKey, token)];
    }
}
