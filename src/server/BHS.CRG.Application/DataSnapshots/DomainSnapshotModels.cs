using System.Text.Json;

namespace BHS.CRG.Application.DataSnapshots;

/// <summary>
/// Снимок ДОМЕНА для внешнего потребителя (issue #419) — в дополнение к снимку наборов данных (#415).
/// Наборы отвечают на вопрос «что в файлах», домен — «что об этом знает сама система»: какие документы
/// заведены, с какими реквизитами, какие документы качества привязаны.
///
/// Для сверки это разные источники истины, и агенту нужны оба.
/// </summary>

public record ConstructionSummary(
    Guid Id, string Name, int SectionCount, int SetCount, int DocumentCount);

public record ConstructionDetail(
    Guid Id, string Name, IReadOnlyList<SectionInfo> Sections);

public record SectionInfo(Guid Id, string Name, IReadOnlyList<DocumentSetInfo> Sets);

public record DocumentSetInfo(Guid Id, string Name, int DocumentCount);

public record DocumentSetDetail(
    Guid Id, string Name,
    Guid SectionId, string SectionName,
    Guid ConstructionId, string ConstructionName,
    IReadOnlyList<DocumentSummary> Documents);

/// <param name="Status">Черновик / Готов и т.п. — состояние документа в комплекте.</param>
public record DocumentSummary(
    Guid Id, string Name, Guid TypeId, string TypeCode, string TypeName, string Status);

/// <param name="Requisites">Реквизиты сырым JSON. Ключи объясняет схема типа — см.
/// <see cref="DocumentTypeSchemaInfo"/>: слабо-типизированный блоб компенсируется schema-as-resource,
/// а не попыткой заранее развернуть его в фиксированную форму.</param>
/// <param name="RefsResolved">Развёрнуты ли ссылки на каталог, наследование и перечисления (#421).
/// Флаг обязателен: две формы реквизитов выглядят одинаково по типу и по-разному по смыслу, и молчаливое
/// различие привело бы к выводам о «незаполненных» полях, которые на деле унаследованы.</param>
/// <param name="TableFields">Табличные поля типа (issue #591). Их значений в <paramref name="Requisites"/>
/// НЕТ — строки подмешивает генерация из набора данных, — и по прежнему ответу «таблицы нет» было
/// неотличимо от «таблица придёт из набора». Отличие не косметическое: агент прочитал отсутствие
/// ключа «Материалы» как пустой реестр, хотя в нём 151 позиция, и выпустил ошибочное замечание.</param>
public record DocumentDetail(
    Guid Id, string Name, Guid TypeId, string TypeCode, string TypeName, string Status,
    Guid? SetId, string? SetName,
    JsonElement Requisites, bool RefsResolved,
    IReadOnlyList<DocumentTableField> TableFields);

/// <summary>
/// Табличное поле документа: заглушка вместо строк плюс адрес, по которому строки лежат (#591).
///
/// Она же — единственная обратная ссылка «документ → источник данных»: без неё нечем понять, из
/// какой из трёх распознанных таблиц кабельного журнала собран этот документ.
/// </summary>
/// <param name="BoundToDataset">Привязан ли к полю источник. <c>false</c> означает ровно «таблица
/// пуста»: строкам взяться неоткуда.</param>
/// <param name="RowCount">Сколько строк подмешается — уже ПОСЛЕ фильтра источника, то есть столько,
/// сколько попадёт в PDF. Null, если привязки нет.</param>
public record DocumentTableField(
    string Key, string? Title, bool BoundToDataset,
    Guid? SourceId, string? SourceName, Guid? DatasetId, string? DatasetName, int? RowCount);

/// <summary>Запись каталога (общие данные): организация, лицо, объект строительства и т.п.</summary>
/// <param name="Scope">Уровень видимости: System / Construction / Section / Set.</param>
public record CatalogEntrySummary(
    Guid Id, string Name, Guid TypeId, string TypeCode, string TypeName,
    string Scope, Guid? ScopeId);

/// <param name="Data">Данные записи как хранятся. Вложенные ссылки остаются ссылками — их
/// <c>entryId</c> самодостаточен, и агент проходит цепочку тем же инструментом.</param>
public record CatalogEntryDetail(
    Guid Id, string Name, Guid TypeId, string TypeCode, string TypeName,
    string Scope, Guid? ScopeId, JsonElement Data);

/// <param name="HasScan">Есть ли прикреплённый скан — сам файл через MCP не отдаётся.</param>
public record QualityDocumentSummary(
    Guid Id, string Name, Guid TypeId, string TypeName,
    string Scope, Guid? ScopeId, string Source, bool HasScan,
    JsonElement Requisites);

/// <param name="Schema">Схема типа сырым JSON: описывает ключи, типы и заголовки полей — без неё
/// реквизиты документа для внешнего читателя не интерпретируемы.</param>
public record DocumentTypeSchemaInfo(
    Guid Id, string Code, string Name, string Kind, Guid? ParentId, JsonElement Schema);

/// <summary>
/// Действующая связь «материал → документ качества» для комплекта (issue #423).
/// </summary>
/// <param name="MaterialKey">Составной ключ идентичности материала: нормализованные значения ВСЕХ
/// полей с тэгом «Идентификатор» через « | », пустое поле — пустой слот (issue #582). Он же
/// связывает карту со строками наборов данных.</param>
/// <param name="Scope">Уровень, с которого связь пришла. Провенанс обязателен: связь может быть
/// заведена на System и неожиданно действовать на конкретном комплекте, и без уровня «почему тут
/// этот сертификат» непроверяемо.</param>
public record MaterialQualityLinkInfo(
    string MaterialKey,
    Guid QualityDocumentId, string QualityDocumentName, string QualityDocumentTypeName,
    string Scope, Guid? ScopeId);
