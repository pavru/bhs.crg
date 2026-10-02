namespace BHS.CRG.Application.DataSets;

/// <summary>
/// Часть обработки в запросе: прислана ли она — и чем. «Не прислана» и «прислана пустой» — разное:
/// первое оставляет часть как есть, второе её сбрасывает. Одним <c>null</c> это не выразить, поэтому
/// признак отдельный.
///
/// <para>Присланную часть делает только <see cref="Of" />, не присланная — значение по умолчанию.
/// Конструктор закрыт нарочно: «не прислана, но со значением» собрать нельзя — такое значение
/// молча потерялось бы.</para>
/// </summary>
public readonly record struct ProcessingPart
{
    private ProcessingPart(object? value)
    {
        Sent = true;
        Value = value;
    }

    public bool Sent { get; }
    public object? Value { get; }

    /// <summary>Часть прислана: значение заменит сохранённое; <c>null</c> — сбросит.</summary>
    public static ProcessingPart Of(object? value) => new(value);
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
///
/// <para>Как части читаются из тела запроса — дело входа (у HTTP это <c>SourceProcessingBody</c>).
/// Правку без единой части служба отклоняет сама, с какого бы входа та ни пришла.</para>
/// </summary>
public record SetSourceProcessingInput
{
    public ProcessingPart RowFilter { get; init; }
    public ProcessingPart ComputedColumns { get; init; }
    public ProcessingPart SortSpec { get; init; }

    /// <summary>Не прислано ни одной части — менять нечего.</summary>
    public bool IsEmpty => !(RowFilter.Sent || ComputedColumns.Sent || SortSpec.Sent);
}
