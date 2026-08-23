using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.DataSets;

public class DataSetSource : Entity
{
    public Guid FileId { get; private set; }
    /// <summary>
    /// Откуда взялись строки этого источника (issue #807 и далее). Вычисляется по маркеру в
    /// <see cref="SheetOrPath" />, а не хранится колонкой: колонка была бы копией признака, который
    /// и так однозначно следует из маркера, и разъехалась бы с ним при первом же переименовании.
    ///
    /// Свойство источника, а не чья-то частная классификация: до этого правило жило приватным
    /// методом в сервисе снимков и вторым вызовом в файловом сервисе — то есть двумя копиями, и
    /// третьим потребителем (интерфейсом, показывающим человеку, что значение распознано) стало бы
    /// три.
    /// </summary>
    public DataOrigin Origin =>
        SystemDataSets.IsSystemMarker(SheetOrPath) ? DataOrigin.System
        : PdfProfiles.IsRecognitionMarker(SheetOrPath) ? DataOrigin.Recognized
        : DataOrigin.Parsed;

    /// <summary>Display name: sheet name, XML group name, JSON key, or "default".</summary>
    public string Name { get; private set; } = null!;
    /// <summary>Internal locator: sheet name, XPath (/root/items), JSON path ($.key), "default".</summary>
    public string SheetOrPath { get; private set; } = null!;
    /// <summary>
    /// Для XML (опционально): JSON-массив явных относительных колонок вида
    /// [{"name":"Артикул","expr":"@id"}]. Вычисляются относительно узла строки (SheetOrPath).
    /// Null/пусто — авто-определение колонок по дочерним элементам/атрибутам (легаси-режим).
    /// </summary>
    public string? ColumnExpressions { get; private set; }
    /// <summary>JSON-кэш колонок: [{name, sampleValues[]}]. Заполняется при загрузке файла.</summary>
    public string CachedSchema { get; private set; } = "[]";
    public int CachedRowCount { get; private set; }
    /// <summary>
    /// JSON-массив полных распознанных строк (только для PDF — распознавание через vision-LLM
    /// дорого/недетерминированно, в отличие от остальных форматов не перепарсивается на каждый
    /// вызов). Null — ещё не распознавали. См. DataSetRowLoader.LoadRowsAsync.
    /// </summary>
    public string? CachedData { get; private set; }
    /// <summary>JSON-массив кодов функциональных тэгов источника (scope Dataset — TagRegistry).</summary>
    public string? Tags { get; private set; }

    /// <summary>
    /// Обработка (Filter/Transformation/Sort) — своя, независимая от других источников.
    /// Применение шаблона обработки (<see cref="DataSetProcessingTemplate"/>) копирует его
    /// значения сюда единожды (как и применение шаблона маппинга к DataSetBinding) — дальше
    /// правки шаблона на уже применившие его источники не влияют. JSON: FilterDef /
    /// ComputedColumnDef[] / SortColumnDef[] соответственно.
    /// </summary>
    public string? RowFilter { get; private set; }
    public string? ComputedColumns { get; private set; }
    public string? SortSpec { get; private set; }

    /// <summary>
    /// Почему данные источника устарели, или null — данные соответствуют своему происхождению.
    /// Сбрасывается свежим <see cref="UpdateCache"/> (перераспознали/перечитали). Пока причина есть,
    /// генерация опирается на устаревшие данные, а пользователю показывается «Перераспознать».
    /// </summary>
    public DataSetStaleReason? StaleReason { get; private set; }

    /// <summary>Устарели ли данные — то же, что «причина есть». Существует отдельно от
    /// <see cref="StaleReason"/> потому, что подавляющему большинству читателей (фильтры, агрегаты,
    /// признак в списке) нужен ответ «да/нет», и заставлять каждого сравнивать с null — значит
    /// разводить это сравнение по кодовой базе.</summary>
    public bool RecognitionStale => StaleReason is not null;

    /// <summary>
    /// Материализация (issue #19): ID типа документа (Composite или Document, различаем по Kind),
    /// в сущности которого источник разворачивает свои строки. Null — материализация не настроена
    /// (источник используется по-старому: маппинг на привязке). Строки материализуются ПОСЛЕ всех
    /// обработок (Filter/Transformation/Sort) — одна сущность типа на строку.
    /// </summary>
    public Guid? MaterializeTypeId { get; private set; }

    /// <summary>
    /// Маппинг колонок → поля материализуемого типа: JSON { "ключПоля": "Колонка" | "@@ref:…" | "@@file:…" }
    /// (та же форма и кодирование, что у <see cref="DataSetBinding.Mapping"/>). Значим только вместе с
    /// <see cref="MaterializeTypeId"/>.
    /// </summary>
    public string? MaterializeMapping { get; private set; }

    /// <summary>
    /// Дискриминатор варианта union'а (issue #716): по какому признаку строки выбирается вариант,
    /// в который её материализовать. JSON:
    /// <code>{"column":"ТипКод","kind":"docTypeCode"|"docId","rules":{"АОСР":["&lt;guid типа&gt;"],…}}</code>
    /// Null — материализация статична: ровно один вариант на все строки (прежнее поведение).
    ///
    /// <para><b>Почему отдельной колонкой, а не служебным ключом внутри
    /// <see cref="MaterializeMapping"/>.</b> Маппинг читают циклом четыре потребителя — применение
    /// при генерации, подстановка значений по умолчанию, предпросмотр и редактор маппинга. Ключ,
    /// который «не поле», каждому из них пришлось бы объяснять отдельно, и первый же забывший
    /// получил бы вариант в качестве поля.</para>
    ///
    /// <para><b>Что становится можно.</b> При заданном дискриминаторе <see cref="MaterializeMapping"/>
    /// несёт НЕСКОЛЬКО ключей-вариантов — по одному на каждый настроенный вариант union'а. Без
    /// дискриминатора ключ по-прежнему ровно один: старый формат — это один ключ и null здесь,
    /// то есть обратная совместимость байт-в-байт.</para>
    /// </summary>
    public string? MaterializeDiscriminator { get; private set; }

    /// <summary>
    /// Режим «существующий документ по Ид» (issue #725): имя колонки, несущей идентификатор уже
    /// существующего документа. Строка источника целиком становится ссылкой
    /// <c>{"$ref":"instance","instanceId":…}</c>, а не объектом, собранным из колонок; живые данные
    /// подставляет второй проход <c>EntityResolver</c>. Null — материализация обычная (сборка).
    ///
    /// <para><b>Почему отдельной колонкой.</b> Та же причина, что у <see cref="MaterializeDiscriminator"/>:
    /// «вся строка» — не поле типа, и служебный ключ внутри <see cref="MaterializeMapping"/> пришлось
    /// бы объяснять всем, кто читает маппинг циклом (генерация, предпросмотр привязки, предпросмотр
    /// материализации, редактор маппинга). Первый забывший получил бы «всю строку» в качестве поля.</para>
    ///
    /// <para>Применим только к типу-документу: у составного типа экземпляров-документов нет, ссылаться
    /// не на что. Вместе с непустым <see cref="MaterializeMapping"/> не сохраняется — это две разные
    /// настройки одного и того же (см. <c>MaterializeConfigValidator</c>).</para>
    /// </summary>
    public string? MaterializeByIdColumn { get; private set; }

    public DataSetFile File { get; private set; } = null!;
    private readonly List<DataSetBinding> _bindings = [];
    public IReadOnlyList<DataSetBinding> Bindings => _bindings.AsReadOnly();

    private DataSetSource() { }

    internal static DataSetSource Create(Guid fileId, string name, string sheetOrPath,
        string cachedSchema, int cachedRowCount, string? columnExpressions = null, string? cachedData = null)
        => new()
        {
            FileId = fileId,
            Name = name,
            SheetOrPath = sheetOrPath,
            CachedSchema = cachedSchema,
            CachedRowCount = cachedRowCount,
            ColumnExpressions = columnExpressions,
            CachedData = cachedData,
        };

    /// <summary>
    /// Восстановление из резервной копии (issue #833) — со ВСЕМ разбором, включая кэш данных.
    ///
    /// Кэш переносится не для скорости: восстановление не запускает разборщики и не распознаёт
    /// сканы заново, поэтому источник без кэша приехал бы пустым — с файлом в хранилище и без
    /// единой строки. Это и есть главная причина, по которой копия с проектными данными тяжелее
    /// конфигурационной.
    /// </summary>
    public static DataSetSource Restore(Guid id, Guid fileId, string name, string sheetOrPath,
        string? columnExpressions, string cachedSchema, int cachedRowCount, string? cachedData,
        string? tags, string? rowFilter, string? computedColumns, string? sortSpec,
        DataSetStaleReason? staleReason, Guid? materializeTypeId, string? materializeMapping,
        string? materializeDiscriminator, string? materializeByIdColumn,
        DateTimeOffset createdAt, DateTimeOffset updatedAt)
        => new()
        {
            Id = id, FileId = fileId, Name = name, SheetOrPath = sheetOrPath,
            ColumnExpressions = columnExpressions, CachedSchema = cachedSchema,
            CachedRowCount = cachedRowCount, CachedData = cachedData, Tags = tags,
            RowFilter = rowFilter, ComputedColumns = computedColumns, SortSpec = sortSpec,
            StaleReason = staleReason, MaterializeTypeId = materializeTypeId,
            MaterializeMapping = materializeMapping, MaterializeDiscriminator = materializeDiscriminator,
            MaterializeByIdColumn = materializeByIdColumn,
            CreatedAt = createdAt, UpdatedAt = updatedAt,
        };

    public void UpdateCache(string cachedSchema, int cachedRowCount, string? cachedData = null)
    {
        CachedSchema = cachedSchema;
        CachedRowCount = cachedRowCount;
        CachedData = cachedData;
        StaleReason = null; // свежий кэш соответствует текущему файлу
        TouchUpdatedAt();
    }

    /// <summary>Пометить источник устаревшим с указанием причины (см. <see cref="DataSetStaleReason"/>).
    /// Данные не трогаем, чтобы не терять их до перераспознавания: устаревшие строки лучше пустых,
    /// решение принимает человек.
    ///
    /// У уже помеченного источника причина меняется, только если новая ОБЪЯСНЯЕТ БОЛЬШЕ. Порядок не
    /// «кто первый»: события приходят в любой последовательности — привязали профиль, потом заменили
    /// файл, — и рассказывать человеку про смену профиля там, где подменили весь файл, значит
    /// занизить беду. Замена файла и неразобравшийся источник обесценивают ВСЁ, что из файла
    /// выведено; сдвиг границ и смена профиля — только часть.</summary>
    public void MarkRecognitionStale(DataSetStaleReason reason)
    {
        if (Weight(reason) <= Weight(StaleReason)) return;
        StaleReason = reason;
        TouchUpdatedAt();
    }

    private static int Weight(DataSetStaleReason? reason) => reason switch
    {
        null => 0,
        DataSetStaleReason.ProfileChanged => 1,
        DataSetStaleReason.TableBoundariesChanged => 2,
        DataSetStaleReason.NotParsedAgainstNewFile => 3,
        DataSetStaleReason.FileReplaced => 3,
        _ => 1,
    };

    /// <summary>Функциональные тэги источника (scope Dataset) — JSON-массив кодов или null.</summary>
    public void SetTags(string? tagsJson)
    {
        Tags = tagsJson;
        TouchUpdatedAt();
    }

    /// <summary>Лёгкое переименование источника (issue #43) — только имя, не трогает локатор/колонки/кэш.
    /// Применимо к любому источнику, включая PDF-проекции (у них UpdateDefinition недоступен).</summary>
    public void Rename(string name)
    {
        Name = name.Trim();
        TouchUpdatedAt();
    }

    /// <summary>Ручное редактирование источника пользователем (имя, локатор, колонки).</summary>
    public void UpdateDefinition(string name, string sheetOrPath, string? columnExpressions)
    {
        Name = name;
        SheetOrPath = sheetOrPath;
        ColumnExpressions = columnExpressions;
        TouchUpdatedAt();
    }

    /// <summary>Обработка (Filter/Transformation/Sort) — лёгкая правка, не трогает файл/кэш схемы.</summary>
    public void SetProcessing(string? rowFilter, string? computedColumns, string? sortSpec)
    {
        RowFilter = rowFilter;
        ComputedColumns = computedColumns;
        SortSpec = sortSpec;
        TouchUpdatedAt();
    }

    /// <summary>
    /// Настроить/снять материализацию источника в тип (issue #19). typeId=null снимает.
    ///
    /// <paramref name="discriminatorJson" /> (issue #716) — правило выбора варианта union'а по
    /// строке; null означает «один вариант на все строки». Задаётся ЦЕЛИКОМ вместе с маппингом:
    /// правила и маппинг связаны (правило варианта без маппинга бессмысленно), и раздельное
    /// сохранение оставляло бы источник в состоянии, которого валидатор не пропустил бы.
    ///
    /// <paramref name="byIdColumn" /> (issue #725) — колонка с идентификатором существующего
    /// документа; непустая означает режим «вся строка = ссылка», при котором маппинг пуст.
    /// </summary>
    public void SetMaterialization(Guid? typeId, string? mappingJson, string? discriminatorJson = null,
        string? byIdColumn = null)
    {
        MaterializeTypeId = typeId;
        MaterializeMapping = typeId is null ? null : (mappingJson ?? "{}");
        MaterializeDiscriminator = typeId is null ? null : discriminatorJson;
        // Имя колонки НЕ подрезаем: ключи строк — заголовки файла как есть (CSV-парсер подрезает
        // значения, не заголовки). Подрезанное «Ид » не совпало бы ни с одной строкой, и реестр
        // молча опустел бы с сообщением про пустую колонку. Дискриминатор (#716) хранит имя так же.
        MaterializeByIdColumn = typeId is null || string.IsNullOrWhiteSpace(byIdColumn) ? null : byIdColumn;
        TouchUpdatedAt();
    }
}
