namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Единая точка применения одного значения маппинга «токен → значение поля» (issue #374). Раньше логика
/// дублировалась у генерации (<c>DataSetResolver.MapValueAsync</c>) и превью (<c>DataSetDtoMapper.PreviewCell</c>);
/// они расходились ТОЛЬКО в ветке <c>@@ref</c> (генерация резолвит запись каталога, превью показывает
/// маркер). Здесь общие ветки — обычная колонка, <c>@@file</c> и рекурсивный <c>@@inline</c> — а <c>@@ref</c>
/// делегируется вызывающему через <see cref="RefResolver"/>. Грамматика рекурсивна: под-поля inline —
/// те же токены (колонка / @@ref / вложенный @@inline).
/// </summary>
public static class DataSetMappingApplier
{
    /// <summary>Разрешение ссылочного (@@ref) токена: генерация → {$ref:catalog,entryId}; превью → маркер «🔗 …».</summary>
    public delegate Task<object?> RefResolver(
        DataSetRefMapping refMap, IReadOnlyDictionary<string, string?> row, string path, CancellationToken ct);

    public static async Task<object?> ApplyAsync(
        string mapVal, IReadOnlyDictionary<string, string?>? row,
        RefResolver resolveRef, string path, CancellationToken ct)
    {
        var fileMap = DataSetMappingValue.ParseFile(mapVal);
        if (fileMap is not null)
            return row is null ? null : DataSetMappingValue.ResolveFileValue(fileMap, row);

        var inlineMap = DataSetMappingValue.ParseInline(mapVal);
        if (inlineMap is not null)
        {
            // Inline это ДАННЫЕ: строим встроенный объект из под-маппинга той же строки. Под-поля @@ref
            // дают $ref (доразрешит 2-й проход резолвера). Пустой (все под-поля пусты) → null.
            var obj = new Dictionary<string, object?>();
            foreach (var (subKey, subToken) in inlineMap.Fields)
            {
                var v = await ApplyAsync(subToken, row, resolveRef, $"{path}.{subKey}", ct);
                if (v is not null) obj[subKey] = v;
            }
            return obj.Count == 0 ? null : obj;
        }

        var refMap = DataSetMappingValue.ParseRef(mapVal);
        if (refMap is not null)
            return row is null ? null : await resolveRef(refMap, row, path, ct);

        // Обычная колонка.
        return row is not null && row.TryGetValue(mapVal, out var val) ? val : null;
    }
}
