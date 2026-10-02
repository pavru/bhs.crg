using System.Text.Json;
using BHS.CRG.Application.DataSets;

namespace BHS.CRG.Api.Endpoints.DataSets;

/// <summary>
/// Тело <c>PUT …/processing</c> → правка обработки по частям (issue #1139). Прислано ли поле, видно
/// только по самому телу — поэтому разбор свой, а не привязкой к записи: та отдала бы <c>null</c> и за
/// отсутствующее поле, и за присланное пустым.
///
/// <para>Тело, которое нельзя понять однозначно, — отказ: неизвестное поле и поле, присланное
/// дважды. Раньше лишнее поле пропускалось молча, и это было безвредно: опечатка в имени сбрасывала
/// часть, и это было видно. При правке по частям та же опечатка дала бы «сохранено», не сохранив
/// ничего. Тело без единой части здесь не отказ — его отклоняет служба, одинаково для любого входа.</para>
///
/// <para>Отказ — причиной, а не исключением: слой Api отвечает кодом (<c>DomainExceptionPolicyTests</c>).</para>
/// </summary>
public static class SourceProcessingBody
{
    private const string RowFilterKey = "rowFilter", ComputedColumnsKey = "computedColumns", SortSpecKey = "sortSpec";
    private static readonly string[] Keys = [RowFilterKey, ComputedColumnsKey, SortSpecKey];

    /// <summary>Имена сверяем без регистра — так же, как их читала привязка к записи.</summary>
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    /// <summary>Правка из тела; <c>false</c> — тело не разобрано, и <paramref name="refusal" /> говорит почему.</summary>
    public static bool TryParse(JsonElement body, out SetSourceProcessingInput input, out string refusal)
    {
        input = new SetSourceProcessingInput();
        refusal = "";
        if (body.ValueKind != JsonValueKind.Object)
        {
            refusal = "Обработка источника не сохранена: тело запроса — не объект. Ожидается объект с частями "
                + $"«{RowFilterKey}», «{ComputedColumnsKey}», «{SortSpecKey}» — любой из них или несколькими.";
            return false;
        }

        var parts = new Dictionary<string, ProcessingPart>(Names);
        foreach (var property in body.EnumerateObject())
        {
            if (!Keys.Contains(property.Name, Names))
            {
                refusal = $"Обработка источника не сохранена: в запросе поле «{property.Name}», которого у "
                    + $"обработки нет. Бывают «{RowFilterKey}», «{ComputedColumnsKey}» и «{SortSpecKey}».";
                return false;
            }

            // Повтор — отказ: «rowFilter» и «RowFilter» в одном теле — одна и та же часть, и молча взять
            // последнее значило бы, например, сбросить отбор, который прислали рядом.
            // Значение копируем: документ запроса к моменту сохранения может быть уже закрыт.
            if (!parts.TryAdd(property.Name, Part(property.Value)))
            {
                refusal = $"Обработка источника не сохранена: поле «{property.Name}» прислано дважды — какое из "
                    + "значений сохранять, неясно.";
                return false;
            }
        }

        input = new SetSourceProcessingInput
        {
            RowFilter = parts.GetValueOrDefault(RowFilterKey),
            ComputedColumns = parts.GetValueOrDefault(ComputedColumnsKey),
            SortSpec = parts.GetValueOrDefault(SortSpecKey),
        };
        return true;
    }

    private static ProcessingPart Part(JsonElement value) =>
        ProcessingPart.Of(value.ValueKind == JsonValueKind.Null ? null : value.Clone());
}
