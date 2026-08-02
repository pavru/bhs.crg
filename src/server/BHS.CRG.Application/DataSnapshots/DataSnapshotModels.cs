namespace BHS.CRG.Application.DataSnapshots;

/// <summary>
/// Снимок данных набора для ВНЕШНЕГО потребителя (issue #415) — прежде всего для агента, который
/// проводит сверку документов на непротиворечивость. Формы намеренно отличаются от внутренних DTO:
/// здесь важны не поля редактора, а то, без чего внешний анализ становится НЕВЕРНЫМ, — полнота
/// выборки, свежесть и происхождение данных.
/// </summary>

/// <summary>Откуда взялись строки источника. Ключевое различие для доменного правила пользователя
/// «истина в xml, а docx/pdf генерируются»: без него агент примет распознанную vision-моделью
/// таблицу за первоисточник и построит сверку на менее достоверных данных.</summary>
public enum DataOrigin
{
    /// <summary>Детерминированный парсинг структурированного файла (XML/CSV/XLSX/JSON).</summary>
    Parsed = 1,

    /// <summary>Извлечено распознаванием (vision-LLM) — данные вероятностные.</summary>
    Recognized = 2,

    /// <summary>Консолидация данных самой системы (документы комплекта и т.п.) — строки собраны
    /// запросом к БД на момент чтения, поэтому детерминированы и никогда не устаревают.</summary>
    System = 3,
}

/// <param name="Stale">Данные могли устареть относительно текущего содержимого файла.</param>
public record DatasetSummary(
    Guid Id, string Name, string Format, string Scope, Guid? ScopeId,
    int SourceCount, bool Stale);

public record DatasetDetail(
    Guid Id, string Name, string Format, string Scope, Guid? ScopeId,
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
public record SourceSummary(
    Guid Id, string Name, DataOrigin Origin, int RawRowCount, bool Filtered, bool Stale,
    IReadOnlyList<string> Columns, SheetAnchor? Sheet);

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
public record SourceDetail(
    Guid Id, Guid DatasetId, string DatasetName, string Name,
    DataOrigin Origin, int RowCount, int RawRowCount, bool Filtered,
    bool Stale, string? StaleReason,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ColumnInfo> Columns, SheetAnchor? Sheet);

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
public record RowsPage(
    Guid SourceId, int Offset, int Limit, int TotalRows, bool Truncated,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows)
{
    /// <inheritdoc cref="SnapshotContract.Version" />
    public int ContractVersion => SnapshotContract.Version;
}
