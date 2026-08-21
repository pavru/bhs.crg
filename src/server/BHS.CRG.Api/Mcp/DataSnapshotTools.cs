using System.ComponentModel;
using BHS.CRG.Application.DataSnapshots;
using ModelContextProtocol.Server;

namespace BHS.CRG.Api.Mcp;

/// <summary>
/// Инструменты MCP для внешнего агента, проводящего сверку документов на непротиворечивость (#415).
///
/// Слой намеренно ТОНКИЙ — ровно как Minimal-API эндпоинты: разбор аргументов и вызов
/// <see cref="IDataSnapshotService"/>, никакой доменной логики. Тогда HTTP-API и MCP остаются двумя
/// адаптерами над ОДНИМ ядром и не расходятся.
///
/// Инструментов ЗАПИСИ здесь нет вовсе: срез только читающий. Записывающие (алиасы/квалификации)
/// появятся отдельно, когда чтение подтвердит модель данных.
///
/// Описания методов — часть контракта: по ним агент решает, что вызывать, поэтому в них вынесено то,
/// без чего он построит неверную сверку (усечение, устаревание, происхождение данных).
/// </summary>
[McpServerToolType]
public class DataSnapshotTools(IDataSnapshotService snapshots)
{
    [McpServerTool(Name = "list_datasets", ReadOnly = true, Idempotent = true, Destructive = false,
        Title = "Список наборов данных")]
    [Description("""
        Наборы данных системы — точка входа для анализа. Возвращает идентификаторы, которые нужны
        остальным инструментам. Поле stale=true означает, что хотя бы один источник набора устарел:
        файл заменён, границы документа сдвинуты или сменён профиль распознавания — данные могут не
        соответствовать текущему содержимому. Что именно случилось, называет staleReason у источника.

        Имя набора дополнено уровнем и наименованием его стройки/раздела/комплекта (scopeName):
        одинаковые имена на разных уровнях встречаются, и без этого выбрать между ними нечем.

        Ответ — страница {items, offset, limit, total, truncated}, как у всех списков.
        """)]
    public async Task<SnapshotPage<DatasetSummary>> ListDatasetsAsync(
        CancellationToken ct,
        [Description("Необязательный фильтр области: System, Construction, Section, Set.")] string? scope = null,
        [Description("Идентификатор области (если указан scope).")] Guid? scopeId = null,
        [Description("Смещение от начала (0 — с первого набора).")] int offset = 0,
        [Description("Сколько наборов вернуть; по умолчанию 200, максимум 500.")]
        int limit = DomainSnapshotLimits.NavigationDefault)
        => await snapshots.ListDatasetsAsync(scope, scopeId, offset, limit, ct);

    [McpServerTool(Name = "get_dataset", ReadOnly = true, Idempotent = true, Destructive = false,
        Title = "Структура набора данных")]
    [Description("""
        Состав набора: его источники с колонками, признаком устаревания, origin и оговоркой к данным.

        warning у источника — оговорка К ДАННЫМ: строки верны, но часть данных внутри них неизвестна.
        Пустая ячейка при такой оговорке значит «неизвестно», а не «нуль».

        rawRowCount — строки В ИСТОЧНИКЕ, до фильтра. Если filtered=true, в выборке их будет МЕНЬШЕ;
        точное число даёт get_source (поле rowCount) либо get_rows (totalRows).
        origin=Recognized означает, что строки извлечены распознаванием (vision-LLM) и являются
        вероятностными; origin=Parsed — детерминированный разбор структурированного файла.
        Это различие существенно, если по правилам проекта первоисточником считается XML,
        а PDF/DOCX — производные.
        """)]
    public async Task<DatasetDetail?> GetDatasetAsync(
        [Description("Идентификатор набора данных.")] Guid datasetId,
        CancellationToken ct)
        => await snapshots.GetDatasetAsync(datasetId, ct);

    [McpServerTool(Name = "get_source", ReadOnly = true, Idempotent = true, Destructive = false,
        Title = "Источник данных: колонки и достоверность")]
    [Description("""
        Описание источника: колонки с примерами значений, происхождение, время последнего обновления
        данных, признак устаревания с причиной и — для таблиц из PDF — якорь на исходные листы
        (шифр, наименование документа, номера страниц), чтобы найденное расхождение можно было
        проверить глазами по чертежу.

        Строк здесь ДВА числа: rowCount — после обработки, ровно столько отдаст get_rows и столько
        попадёт в документ; rawRowCount — сколько было в источнике до фильтра. Разницу объясняет
        filtered=true, и она не означает потерю данных.

        rowsHash — сквозной отпечаток всех строк: по нему видно, менялись ли данные вообще. Если
        строки прочитать не удалось (файл недоступен, консолидации больше нет), придёт rowsError, а
        rowCount и rowsHash будут пустыми — остальное описание источника при этом верно.

        warning — оговорка К ДАННЫМ, и с rowsError её путать нельзя: строки получены и верны, но часть
        данных внутри них неизвестна («не собрано документов: 9 из 12 — количество листов и дата
        генерации известны только у собранных»). Пустая ячейка при такой оговорке значит «неизвестно»,
        а не «нуль»: rowsError — «данных нет», warning — «данные неполны».
        """)]
    public async Task<SourceDetail?> GetSourceAsync(
        [Description("Идентификатор источника данных.")] Guid sourceId,
        CancellationToken ct)
        => await snapshots.GetSourceAsync(sourceId, ct);

    [McpServerTool(Name = "get_rows", ReadOnly = true, Idempotent = true, Destructive = false,
        Title = "Строки источника (страница)")]
    [Description("""
        Строки источника после всей обработки (фильтр, вычисляемые колонки, сортировка).

        ВАЖНО: выборка страничная. Всегда сверяйте totalRows с числом полученных строк и продолжайте
        запрашивать со смещением, пока truncated=true. Анализ по неполной таблице даст неверный
        результат — например, потерянные позиции будут выглядеть как отсутствующие.

        Порядок строк значим и стабилен: адрес значения — это (sourceId, offset + позиция в массиве,
        имя колонки). Ссылайтесь на него, когда указываете, где именно найдено расхождение.

        Повторная проверка: передайте pageHash прошлого ответа в ifNoneMatch. Не изменилось —
        придёт unchanged=true без таблицы, и у вас уже есть верные строки. Именно pageHash, а не
        сквозной rowsHash: «не изменилось» относится к этому окну строк, иначе запрос следующей
        страницы вернул бы пустоту.
        """)]
    public async Task<RowsPage?> GetRowsAsync(
        [Description("Идентификатор источника данных.")] Guid sourceId,
        CancellationToken ct,
        [Description("Смещение от начала (0 — с первой строки).")] int offset = 0,
        [Description("Сколько строк вернуть; по умолчанию 200, максимум 500.")] int limit = 200,
        [Description("""
            Отпечаток страницы (pageHash), которую вы уже читали с тем же offset/limit. Совпал —
            вместо таблицы придёт unchanged=true.
            """)] string? ifNoneMatch = null)
        => await snapshots.GetRowsAsync(sourceId, offset, limit, ifNoneMatch, ct);
}
