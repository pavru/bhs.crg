using BHS.CRG.Domain.DataSets;

namespace BHS.CRG.Application.DataSets;

/// <summary>
/// Единая точка извлечения строк источника: extraction → computed columns (transformation) →
/// row filter → sort. Через неё смотрят на данные генерация, превью, экспорт, материализация,
/// MCP-срез и сверка — расширение способа извлечения (новый вид источника) делается здесь один раз.
/// </summary>
public interface IDataSetRowLoader
{
    /// <summary>Требует source.File загруженным (.Include) заранее у вызывающего кода.</summary>
    Task<List<IReadOnlyDictionary<string, string?>>> LoadRowsAsync(
        DataSetSource source, CancellationToken ct);

    /// <summary>
    /// То же, но с числом строк ДО обработки (issue #592), набором колонок и оговоркой к данным
    /// (issue #661/#664). Отдельный метод, а не второй проход: сырьё известно только внутри
    /// загрузки, а извлечь его повторно значит второй раз скачать и разобрать файл — либо второй
    /// раз сходить к системному провайдеру.
    /// </summary>
    Task<LoadedRows> LoadAsync(DataSetSource source, CancellationToken ct);
}

/// <summary>
/// Строки источника вместе с тем, что было известно про них на самой загрузке.
/// </summary>
/// <param name="RawRowCount">Сколько строк дал сам источник — до вычисляемых колонок, фильтра и
/// сортировки. Расхождение с <c>Rows.Count</c> и есть работа фильтра: внешний читатель, увидевший
/// только одно из двух чисел, решает, что вторая половина строк потерялась.</param>
/// <param name="Columns">Колонки, которые источник отдал ФАКТИЧЕСКИ, — до обработки, как и
/// <paramref name="RawRowCount"/> (issue #664). null — извлечение колонок не даёт: у PDF строки
/// читаются из кэша распознавания, там описание и есть кэш.
///
/// Нужны потому, что <c>DataSetSource.CachedSchema</c> у системного источника пишется один раз при
/// создании и обновить его нечем — файла нет, определение не редактируется. Схема типа тем временем
/// меняется, провайдер начинает отдавать другой набор колонок, и описание источника расходится с его
/// же строками. Тот же случай, из-за которого в #613 живым пришлось сделать число строк.</param>
/// <param name="Warning">Оговорка к данным на момент загрузки (issue #626): строки прочитаны
/// успешно, но часть данных внутри них неизвестна — «не собрано документов: 9 из 12». Не путать с
/// отказом чтения: там строк нет вовсе.</param>
public record LoadedRows(
    IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows, int RawRowCount,
    IReadOnlyList<DataSetColumnInfo>? Columns = null, string? Warning = null);
