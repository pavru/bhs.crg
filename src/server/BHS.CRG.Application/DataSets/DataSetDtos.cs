using BHS.CRG.Domain.DataSets;

namespace BHS.CRG.Application.DataSets;

// ── Output DTOs (JSON shapes consumed by the SPA) ───────────────────────────────

public record DataSetSourceDto(
    Guid Id, Guid FileId, string Name, string SheetOrPath, string? ColumnExpressions,
    string CachedSchema, int CachedRowCount,
    object? RowFilter, object? ComputedColumns, object? SortSpec,
    IReadOnlyList<string>? Tags,
    /// <summary>Данные источника разошлись со своим происхождением. Признак и его причина едут
    /// вместе (issue #815): «да/нет» отвечает на вопрос «показывать ли метку», а причина — на
    /// вопрос «что человеку сказать», и вычислить одно из другого нельзя ни в одну сторону.</summary>
    bool RecognitionStale = false,
    Guid? MaterializeTypeId = null, Dictionary<string, string>? MaterializeMapping = null,
    /// <summary>Сколько привязок ссылается на источник (issue #417) — чтобы удаление не было вслепую.
    /// null = «не считали» (ответ одиночной мутации): UI не должен показывать из-за этого ложный ноль,
    /// актуальное число приезжает со списком.</summary>
    int? BindingCount = null,
    /// <summary>Живая оговорка системного источника (issue #626): считается на чтении вместе с
    /// числом строк, не хранится — она про сегодняшнее состояние данных, а не про определение.</summary>
    string? Warning = null,
    /// <summary>Правило выбора варианта union'а по строке (issue #716); null — материализация
    /// статична, один вариант на все строки.</summary>
    MaterializeDiscriminatorConfig? MaterializeDiscriminator = null,
    /// <summary>Колонка с Ид существующего документа (issue #725): непустая = строка целиком
    /// становится ссылкой на документ, маппинг при этом пуст. Null — обычная сборка из колонок.</summary>
    string? MaterializeByIdColumn = null,
    /// <summary>Откуда взялись значения (см. <see cref="DataOrigin" />). В списке выбора источника
    /// это единственное место, где видно, что за «PDF» стоит распознавание: формат файла отвечает на
    /// другой вопрос — чем файл был, а не как из него получили значения.</summary>
    DataOrigin Origin = DataOrigin.Parsed,
    /// <summary>Почему данные устарели; null — не устарели. Текст пишет клиент: он у каждой точки
    /// показа свой (у поля документа — без глагола, в списке источников — с действием).</summary>
    DataSetStaleReason? StaleReason = null);

/// <summary>
/// Материализованный предпросмотр источника: строки, развёрнутые в объекты формы типа (issue #19).
///
/// <paramref name="Variants"/> — ключ варианта union'а для каждой показанной строки (issue #716),
/// null-элемент = правила нет; <paramref name="Skipped"/> — строки, которым варианта не досталось.
///
/// Пропущенные перечисляются ПОИМЁННО, а не числом: предпросмотр для того и открывают — понять,
/// какие именно документы не доехали и почему. Сводку числом даёт генерация, ей список ни к чему.
/// </summary>
public record MaterializePreviewDto(
    Guid? TypeId, int TotalRows, IReadOnlyList<Dictionary<string, object?>> Rows, string? Error,
    IReadOnlyList<string?>? Variants = null,
    IReadOnlyList<MaterializeSkippedRowDto>? Skipped = null);

/// <summary>Строка, не попавшая в материализацию: её номер, значение колонки-признака и причина.</summary>
public record MaterializeSkippedRowDto(int RowNumber, string? Value, string ReasonCode, string Reason);

public record DataSetFileDto(
    Guid Id, string Name, string Format, string Scope, Guid? ScopeId,
    IReadOnlyList<DataSetSourceDto> Sources, DateTimeOffset CreatedAt,
    string? PreprocessingProfile = null,
    /// <summary>Профили распознавания набора: {вид: id профиля} (issue #412); null — все встроенные.</summary>
    IReadOnlyDictionary<string, Guid>? RecognitionProfiles = null);

public record BindingFileDto(Guid Id, string Name, string Format, string Scope, Guid? ScopeId);

/// <param name="Origin">
/// Откуда взялись значения источника. Нужен в точке ПОТРЕБЛЕНИЯ: человек, заполняющий документ,
/// видит у поля значок «из источника данных» и не знает, что источник — распознанный скан, то есть
/// значение прочитала модель и его стоит сверить с оригиналом. Считает сервер: правило маркеров
/// живёт в домене, и копия его на клиенте разъехалась бы — тот же урок, что у бейджа «не участвует».
/// </param>
public record BindingSourceDto(
    Guid Id, string Name, string SheetOrPath, string CachedSchema, int CachedRowCount, BindingFileDto? File,
    Guid? MaterializeTypeId = null, Dictionary<string, string>? MaterializeMapping = null,
    DataOrigin Origin = DataOrigin.Parsed,
    /// <summary>Данные источника разошлись со своим файлом (issue #815). Едет ЗДЕСЬ, а не только в
    /// <see cref="DataSetSourceDto"/>: точка потребления видит источник исключительно через привязку,
    /// и признака, оставшегося в широком DTO, для неё всё равно что нет.</summary>
    bool RecognitionStale = false,
    /// <summary>Почему данные устарели; null — не устарели. Текст пишет клиент: он у каждой точки
    /// показа свой (у поля документа — без глагола, в списке источников — с действием).</summary>
    DataSetStaleReason? StaleReason = null,
    /// <summary>Сколько привязок ссылается на источник — подтверждение перед перераспознаванием
    /// обязано сказать, что данные обновятся не только в этом документе.</summary>
    int? BindingCount = null,
    /// <summary>Что именно перезапустит «Перераспознать» (см. <see cref="RecognizeScope"/>). Считает
    /// сервер: правило живёт в реестре PDF-профилей, и копия его на клиенте разъехалась бы — а цена
    /// расхождения здесь не косметическая, это минуты работы модели и перезапись чужих данных.</summary>
    RecognizeScope RecognizeScope = RecognizeScope.None,
    /// <summary>Вычисляемые колонки источника (Transformation). В <c>CachedSchema</c> их нет — они
    /// считаются на чтении, — а редактор маппинга обязан предлагать их наравне с колонками файла
    /// (issue #49). Не приезжая сюда, они молча выпадали из списка: комментарий в редакторе на них
    /// рассчитывает, а поле всегда было пустым.</summary>
    object? ComputedColumns = null);

/// <summary>
/// Область действия «Перераспознать» для конкретного источника (issue #815).
///
/// Нужна интерфейсу, чтобы не обещать невыполнимого и не умалчивать о масштабе: у парсерного
/// источника действия нет вовсе, у табличной проекции оно точечное, а у проекций ГОСТ-альбома
/// запускает распознавание ВСЕГО набора — и человек, жмущий кнопку в своём документе, должен об
/// этом знать до, а не после.
/// </summary>
public enum RecognizeScope
{
    /// <summary>Перераспознать нельзя: источник не из PDF либо не наполняется распознаванием.</summary>
    None,

    /// <summary>Перезапустится только этот источник (табличная проекция документа, счёт).</summary>
    Source,

    /// <summary>Перезапустится распознавание всего набора — все его проекции разом.</summary>
    File,
}

/// <summary>Привязка — только Mapping. Filter/Transformation/Sort живут на DataSetSource.
/// Владелец — единый <see cref="OwnerId"/> (DomainObject: документ или запись общих данных).</summary>
public record DataSetBindingDto(
    Guid Id, Guid OwnerId, Guid SourceId, string? TargetFieldKey,
    Dictionary<string, string> Mapping, BindingSourceDto? Source);

/// <summary>Сколько держателей ключа поля перенесено при переименовании (issue #737).</summary>
/// <param name="Bindings">Привязок (целевое поле или ключ маппинга).</param>
/// <param name="Templates">Шаблонов привязок типа.</param>
public record BindingKeyMigrationResult(int Bindings, int Templates);

/// <summary>Шаблон маппинга (для типа документа). Filter/Transformation/Sort — см. DataSetProcessingTemplateDto.</summary>
public record DataSetBindingTemplateDto(
    Guid Id, Guid DocumentTypeId, string Name, string? TargetFieldKey,
    Dictionary<string, string> ColumnMappings,
    int SortOrder, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Переиспользуемый рецепт источника (Extraction + Filter/Transformation/Sort) — не привязан к типу документа.</summary>
public record DataSetProcessingTemplateDto(
    Guid Id, string Name, string? SheetOrPath, string? ColumnExpressions,
    object? RowFilter, object? ComputedColumns, object? SortSpec,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record BindingPreviewDto(
    Guid BindingId, string SourceName, string FileName, string Mode,
    string? TargetFieldKey, int TotalRows, object Data, string? Error);

public record SourcePreviewDto(
    IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows, int TotalRows);

/// <summary>Готовый файл выгрузки табличного источника (CSV/XLS/XLSX) — байты + имя + content-type.</summary>
public record SourceExportDto(byte[] Content, string FileName, string ContentType);

/// <summary>
/// Предпросмотр XPath/JSONPath-выражения (row-selector или колонки) в builder'е — без сохранения
/// источника. rowSelector — куда встать (context); expr — что вычислить относительно найденных
/// узлов (null — предпросмотр самого rowSelector: сколько узлов найдено + их авто-колонки).
/// </summary>
public record ExpressionPreviewDto(int MatchCount, IReadOnlyList<string> Samples);

/// <summary>Original blob stream + metadata for file download.</summary>
public record FileDownloadDto(Stream Stream, string ContentType, string FileName);

/// <summary>Вид группы в единой постраничной группировке ГОСТ-профиля (Document=0 — обязателен для
/// толерантной миграции старого формата, см. Infrastructure GostGroupingData).</summary>
public enum GostGroupKind
{
    Document = 0,
    Cover = 1,
    TitlePage = 2,
}

/// <summary>
/// Текущая группировка ВСЕХ страниц источника ГОСТ-профиля — для ручного редактора разбиения:
/// обложка/титул/документы как группы с <see cref="GostGroupKind"/>. PageCount — общее число
/// страниц исходного PDF (в т.ч. не вошедших ни в одну группу — допустимо, см. GetPagesAsync).
/// </summary>
public record GostGroupingDto(IReadOnlyList<GostGroupingGroupDto> Groups, bool ManuallyEdited, int PageCount);

/// <summary>Одна группа страниц. Для документа Code/Name как в реестре; для обложки/титула — null.
/// PageIndices — 0-based индексы исходного PDF. Tags — функциональные тэги документа (тип таблицы).</summary>
/// <param name="ProfileId">Привязанный профиль распознавания (issue #410); null — привязки нет.</param>
/// <param name="PagesWithoutAnswer">
/// Индексы листов группы, по которым движок не ответил (issue #803). Отдельно от пустых полей: лист
/// без штампа даёт пустые поля законно, и пометив его наравне с неотвеченным, интерфейс кричал бы на
/// каждом графическом листе — а признак, который горит всегда, перестают замечать.
/// </param>
public record GostGroupingGroupDto(
    GostGroupKind Kind, string? Code, string? Name, IReadOnlyList<int> PageIndices,
    IReadOnlyList<string>? Tags = null, Guid? ProfileId = null,
    IReadOnlyList<int>? PagesWithoutAnswer = null);

// ── Input DTOs (assembled by the HTTP layer, free of ASP.NET types) ─────────────

public record UploadFileInput(
    byte[] Bytes, string FileName, string? ContentType, string? Name, string Scope, string? ScopeId);

public record ReplaceFileInput(byte[] Bytes, string FileName, string? ContentType, string? Name);

/// <summary>Системный набор (issue #580): создаётся без файла — сырьё берётся из данных системы.</summary>
public record CreateSystemFileInput(string Scope, string? ScopeId, string? Name);

/// <summary>Явная относительная колонка XML-источника: имя + XPath-выражение относительно строки.</summary>
public record ColumnExprDto(string Name, string Expr);

public record CreateSourceInput(string Name, string SheetOrPath, IReadOnlyList<ColumnExprDto>? ColumnExpressions);

public record UpdateSourceInput(string Name, string SheetOrPath, IReadOnlyList<ColumnExprDto>? ColumnExpressions);

/// <summary>
/// Ручное создание PDF-источника: без SheetOrPath/ColumnExpressions (Extraction для PDF —
/// распознавание, а не XPath/JSONPath-builder, см. RecognizePdfSourceAsync). Tags — коды
/// функциональных тэгов (scope Dataset), напр. dataset.hasTitleBlock — применимы только к
/// профилю "gost-titleblock". Profile — "gost-titleblock" (по умолчанию, один источник,
/// реестр по страницам) или "invoice" (счёт на оплату — создаёт пару источников
/// шапка+товары, см. PdfProfiles в Infrastructure).
/// </summary>
public record CreatePdfSourceInput(string Name, IReadOnlyList<string>? Tags, string? Profile = null);

/// <summary>План распознавания источника: Background=true — операция долгая (GOST-набор), её ставят в
/// фоновую задачу; false — короткая (счёт/legacy), выполняется синхронно. Title — заголовок для
/// индикатора задач. null-результат метода = источник не найден. Кидает 409/400 при пред-валидации.</summary>
public record RecognizePlan(bool Background, string Title);

/// <summary>
/// Новая группировка ВСЕХ страниц — целиком заменяет предыдущую (ручную или автоматическую).
/// Группы всех видов (обложка/титул/документы). Пересекающиеся PageIndices между группами — ошибка
/// (400); страница может не входить ни в одну группу (тогда выпадает из реестров — допустимо).
/// </summary>
public record ApplyGroupingInput(IReadOnlyList<GostGroupingGroupDto> Groups);

/// <summary>Лёгкая правка обработки источника — не трогает файл/кэш схемы (в отличие от Update/CreateSourceInput).</summary>
public record SetSourceProcessingInput(object? RowFilter, object? ComputedColumns, object? SortSpec);

public record CreateProcessingTemplateInput(
    string Name, string? SheetOrPath, IReadOnlyList<ColumnExprDto>? ColumnExpressions,
    object? RowFilter, object? ComputedColumns, object? SortSpec);

public record UpdateProcessingTemplateInput(
    string Name, string? SheetOrPath, IReadOnlyList<ColumnExprDto>? ColumnExpressions,
    object? RowFilter, object? ComputedColumns, object? SortSpec);

/// <summary>Владелец — единый DomainObject (документ или запись общих данных).</summary>
public record CreateBindingInput(
    Guid OwnerId, Guid SourceId, string? TargetFieldKey, Dictionary<string, string>? Mapping);

public record UpdateBindingInput(string? TargetFieldKey, Dictionary<string, string>? Mapping);

public record CreateTemplateInput(
    string Name, string? TargetFieldKey, Dictionary<string, string>? ColumnMappings);

public record UpdateTemplateInput(
    string Name, string? TargetFieldKey, Dictionary<string, string>? ColumnMappings, int? SortOrder);
