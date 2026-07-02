using System.Text.Json;
using BHS.CRG.Application.DataSets;

namespace BHS.CRG.Application.Documents;

/// <summary>
/// Мёрджит результаты резолва DataSetBinding (см. IDataSetService.PreviewBindingsAsync) в текущие
/// данные записи каталога перед сохранением — CommonDataEntry.Data читается EntityResolver
/// живьём из БД при каждом $ref без отдельного кэша, поэтому синхронизация происходит здесь,
/// на момент сохранения записи, а не при чтении.
/// </summary>
public static class CommonDataBindingMerge
{
    public static JsonDocument Merge(JsonDocument current, IReadOnlyList<BindingPreviewDto> previews)
    {
        var dict = new Dictionary<string, JsonElement>();
        foreach (var prop in current.RootElement.EnumerateObject())
            dict[prop.Name] = prop.Value.Clone();

        foreach (var preview in previews)
        {
            if (preview.Error is not null) continue;

            if (preview.Mode == "scalar" && preview.Data is Dictionary<string, string?> scalarData)
            {
                foreach (var (key, value) in scalarData)
                {
                    // Пустое значение колонки не затирает ранее сохранённое (или введённое вручную
                    // до создания биндинга) — источник мог временно не дать данных по этой строке.
                    if (string.IsNullOrEmpty(value)) continue;
                    dict[key] = JsonSerializer.SerializeToElement(value);
                }
            }
            else if (preview.Mode == "tabular" && preview.TargetFieldKey is { } targetKey
                     && preview.Data is List<Dictionary<string, string?>> rows)
            {
                // Табличное поле целиком управляется источником — пишем как есть, включая [].
                dict[targetKey] = JsonSerializer.SerializeToElement(rows);
            }
        }

        return JsonDocument.Parse(JsonSerializer.Serialize(dict));
    }
}
