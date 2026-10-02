using System.Text.Json;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Application.DataSets;

/// <summary>
/// Часть обработки в запросе: прислана ли она — и чем. «Не прислана» и «прислана пустой» — разное:
/// первое оставляет часть как есть, второе её сбрасывает. Одним <c>null</c> это не выразить, поэтому
/// признак отдельный.
/// </summary>
public readonly record struct ProcessingPart(bool Sent, object? Value)
{
    /// <summary>Часть прислана: значение заменит сохранённое; <c>null</c> — сбросит.</summary>
    public static ProcessingPart Of(object? value) => new(true, value);

    /// <summary>Часть прислана пустой — сбросить.</summary>
    public static readonly ProcessingPart Cleared = new(true, null);
}

/// <summary>
/// Правка обработки источника ПО ЧАСТЯМ (issue #1139): отбор, вычисляемые колонки, сортировка — каждая
/// сама по себе. Часть, которой в запросе нет, не трогается и не проверяется. Файл и кэш схемы правка
/// не трогает вовсе (в отличие от Update/CreateSourceInput).
///
/// <para>До #1139 запрос нёс обработку целиком, и диалог сортировки досылал отбор из своей копии
/// источника на странице. Копия устаревала — и сохранение сортировки молча затирало отбор, который
/// тем временем поправил другой человек. А с проверкой отбора при сохранении (issue #1137) та же
/// досылка превращала правку сортировки в проверку отбора: источник с негодным отбором, сохранённым
/// раньше, отказывал в сортировке, и обходили это сравнением «тот же ли отбор прислан».</para>
/// </summary>
public record SetSourceProcessingInput
{
    public ProcessingPart RowFilter { get; init; }
    public ProcessingPart ComputedColumns { get; init; }
    public ProcessingPart SortSpec { get; init; }

    private const string RowFilterKey = "rowFilter", ComputedColumnsKey = "computedColumns", SortSpecKey = "sortSpec";

    /// <summary>
    /// Запрос из тела <c>PUT …/processing</c>. Прислано ли поле, видно только по самому телу — поэтому
    /// разбор здесь, а не привязкой к записи: та отдала бы <c>null</c> и за отсутствующее поле, и за
    /// присланное пустым.
    ///
    /// <para>Запрос без единой части и запрос с неизвестным полем — отказ. Раньше лишнее поле
    /// пропускалось молча, и это было безвредно: опечатка в имени сбрасывала часть, и это было видно.
    /// При правке по частям та же опечатка дала бы «сохранено», не сохранив ничего.</para>
    /// </summary>
    public static SetSourceProcessingInput FromBody(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            throw new InvalidRequestException(
                "Обработка источника не сохранена: тело запроса — не объект. Ожидается объект с частями "
                + $"«{RowFilterKey}», «{ComputedColumnsKey}», «{SortSpecKey}» — любой из них или несколькими.");

        var input = new SetSourceProcessingInput();
        var sent = 0;
        foreach (var property in body.EnumerateObject())
        {
            var part = ProcessingPart.Of(property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.Clone());
            // Имена сверяем без регистра — так же, как их читала привязка к записи.
            if (Named(property, RowFilterKey)) input = input with { RowFilter = part };
            else if (Named(property, ComputedColumnsKey)) input = input with { ComputedColumns = part };
            else if (Named(property, SortSpecKey)) input = input with { SortSpec = part };
            else
                throw new InvalidRequestException(
                    $"Обработка источника не сохранена: в запросе поле «{property.Name}», которого у "
                    + $"обработки нет. Бывают «{RowFilterKey}», «{ComputedColumnsKey}» и «{SortSpecKey}».");
            sent++;
        }

        if (sent == 0)
            throw new InvalidRequestException(
                "Обработка источника не сохранена: в запросе нет ни отбора, ни вычисляемых колонок, ни "
                + "сортировки — менять нечего.");
        return input;
    }

    private static bool Named(JsonProperty property, string name) =>
        string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase);
}
