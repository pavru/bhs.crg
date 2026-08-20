using BHS.CRG.Domain.DataSets;

namespace BHS.CRG.Application.DataSnapshots;

/// <summary>
/// Снимок данных набора для ВНЕШНЕГО потребителя (issue #415) — прежде всего для агента, который
/// проводит сверку документов на непротиворечивость. Формы намеренно отличаются от внутренних DTO:
/// здесь важны не поля редактора, а то, без чего внешний анализ становится НЕВЕРНЫМ, — полнота
/// выборки, свежесть и происхождение данных.
/// </summary>


/// <param name="Stale">Данные могли устареть относительно текущего содержимого файла.</param>
/// <param name="ScopeName">Наименование стройки/раздела/комплекта, на котором лежит набор (#593).
/// Имена наборов повторяются — два набора «250701-ЭОМ-ЕЦДМ, ДНС-Сити_ИД» различались только уровнем
/// и идентификатором, — и без имени уровня выбрать между ними в списке нечем. Null у System: там
/// контейнера нет.</param>
public record DatasetSummary(
    Guid Id, string Name, string Format, string Scope, Guid? ScopeId, string? ScopeName,
    int SourceCount, bool Stale);

/// <inheritdoc cref="DatasetSummary" />
public record DatasetDetail(
    Guid Id, string Name, string Format, string Scope, Guid? ScopeId, string? ScopeName,
    bool Stale, string? RecognitionScenario,
    IReadOnlyList<SourceSummary> Sources);

/// <param name="RawRowCount">Строк В ИСТОЧНИКЕ — до фильтра, вычисляемых колонок и сортировки.
/// Именно сырьё, а не то, что увидит документ: раньше это число называлось <c>rowCount</c> и
/// расходилось с <c>totalRows</c> выборки (48 против 44 на «Кабельном журнале без ГРЩ»), а по ответу
/// разница была неразличима (#592).</param>
/// <param name="Filtered">У источника задан фильтр строк — значит итоговых строк МЕНЬШЕ, чем
/// <paramref name="RawRowCount"/>. Точное число даёт <c>get_source</c> либо <c>get_rows</c>: считать
/// его для каждого источника набора значило бы скачать и разобрать файл столько раз, сколько в нём
/// листов, ради навигационной выдачи.</param>
/// <param name="Warning">Оговорка к данным (issue #661) — см. <see cref="SourceDetail"/>.</param>
public record SourceSummary(
    Guid Id, string Name, DataOrigin Origin, int RawRowCount, bool Filtered, bool Stale,
    IReadOnlyList<string> Columns, SheetAnchor? Sheet, string? Warning = null);

/// <param name="StaleReason">Почему данные считаются устаревшими — чтобы агент мог решить сам,
/// а не гадать по булеву флагу.</param>
/// <param name="UpdatedAt">Когда кэш источника последний раз обновлялся (для распознанных — фактически
/// момент распознавания).</param>
/// <param name="RowCount">Строк ПОСЛЕ обработки — ровно столько отдаст <c>get_rows</c> и столько
/// попадёт в документ.</param>
/// <param name="RawRowCount">Строк до обработки. Две величины отдаются РАЗДЕЛЬНО (#592): одна
/// величина без имени, чему она равна, уже стоила внешнему анализу неверного вывода — сорок восемь
/// строк источника против сорока четырёх в выборке, и ни одно поле о разнице не говорило.</param>
/// <param name="Filtered">У источника задан фильтр строк — объяснение разницы двух чисел.</param>
/// <param name="RowsHash">Отпечаток строк ПОСЛЕ обработки (issue #598) — отвечает на вопрос
/// «менялись ли данные». Работа с комплектом итеративна («поправил реестр — перепроверь»), а
/// кабельный журнал выгружался за сессию не менее четырёх раз при двух реальных правках. Для
/// пропуска повторной выдачи передавайте в <c>get_rows</c> <c>pageHash</c> той страницы, которую
/// перечитываете, — этот отпечаток сквозной и окна не знает.</param>
/// <param name="RowsError">Строки прочитать не удалось: файл набора недоступен, системная
/// консолидация исчезла и т.п. Тогда <paramref name="RowCount"/> и <paramref name="RowsHash"/>
/// пусты, а описание источника всё равно приходит — метаданные и признак устаревания читаются без
/// строк, и терять их из-за недоступного блоба незачем.</param>
/// <param name="Warning">Оговорка к данным (issue #661): строки прочитаны успешно, но часть данных
/// внутри них неизвестна — «Не собрано документов: 9 из 12. Количество листов и дата генерации
/// известны только у собранных».
///
/// Поле из того же ряда, что <c>truncated</c> у страницы строк, <paramref name="Stale"/> и
/// <c>origin</c>: «вот чего эти данные не знают». В интерфейсе оговорка показывается у строки
/// источника, а внешнему агенту срез — вся доступная картина: не увидев её, он либо сообщит «листов
/// нет», либо молча посчитает по неполным данным.
///
/// С <paramref name="RowsError"/> НЕ сливается: там строк нет вовсе, здесь они есть и верны — в
/// одном поле агент перестал бы различать «данных нет» и «данные неполны».</param>
public record SourceDetail(
    Guid Id, Guid DatasetId, string DatasetName, string Name,
    DataOrigin Origin, int? RowCount, int RawRowCount, bool Filtered,
    bool Stale, string? StaleReason,
    DateTimeOffset UpdatedAt, string? RowsHash, string? RowsError,
    IReadOnlyList<ColumnInfo> Columns, SheetAnchor? Sheet, string? Warning = null);

public record ColumnInfo(string Name, IReadOnlyList<string> SampleValues);

/// <summary>Привязка табличного источника к листам исходного PDF — колонка «Файлы / листы» в отчёте
/// о расхождениях: без неё найденное расхождение невозможно проверить глазами.</summary>
public record SheetAnchor(string? Code, string? Name, IReadOnlyList<int> Pages);

/// <summary>
/// Страница строк источника ПОСЛЕ всей обработки (фильтр/вычисляемые колонки/сортировка).
///
/// Адрес значения — <c>(SourceId, порядковый номер строки, ключ колонки)</c>: порядок массива значим,
/// <see cref="Offset"/> задаёт смещение нумерации. Оборачивать каждую ячейку метаданными намеренно НЕ
/// стали — это забетонировало бы форму находки раньше, чем подсистема сверки её определит.
/// </summary>
/// <param name="Truncated">За этой страницей есть ещё строки. КРИТИЧНО для корректности: агент, молча
/// получивший часть таблицы, выдаст неверную сверку — тихое усечение здесь худший вид отказа.</param>
/// <param name="RowsHash">Отпечаток ВСЕХ строк источника после обработки (issue #598) — тот же, что
/// отдаёт <see cref="SourceDetail"/>. Отвечает на вопрос «менялись ли данные вообще».</param>
/// <param name="PageHash">Отпечаток ЭТОЙ страницы: он и передаётся обратно в <c>ifNoneMatch</c>.
/// Отдельный от <paramref name="RowsHash"/>, потому что «не изменилось» относится к запрошенному
/// окну: со сквозным отпечатком запрос следующей страницы с прежним хешем возвращал бы
/// <c>unchanged</c> с пустыми строками, и обход «пока truncated» не двигался бы с места.</param>
/// <param name="Unchanged">Страница совпала с переданным <c>ifNoneMatch</c>: сама таблица не
/// прикладывается, у вызывающего она уже есть. Остальные поля при этом заполнены как обычно, чтобы
/// «не изменилось» не приходилось отличать от «пусто».</param>
public record RowsPage(
    Guid SourceId, int Offset, int Limit, int TotalRows, bool Truncated,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows,
    string RowsHash = "", string PageHash = "", bool Unchanged = false)
{
    /// <inheritdoc cref="SnapshotContract.Version" />
    public int ContractVersion => SnapshotContract.Version;
}
