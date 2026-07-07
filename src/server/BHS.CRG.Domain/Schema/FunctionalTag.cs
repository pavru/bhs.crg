namespace BHS.CRG.Domain.Schema;

/// <summary>
/// Функциональные тэги — единственный санкционированный мост между пользовательской
/// конфигурацией (поля/типы) и hard-coded функционалом. Любой новый функционал,
/// зависящий от пользовательской схемы, должен находить поля/типы по этим тэгам,
/// а не по именам.
///
/// Уровень поля (в схеме: fields[].tags) и уровень типа (в схеме: tags).
/// Реестр с метаданными — <see cref="BHS.CRG.Application.Schema.TagRegistry"/>.
/// </summary>
public static class FunctionalTag
{
    // ── Тэги поля: метаданные генерации (автозаполнение) ────────────────────────
    /// <summary>Количество страниц сгенерированного PDF.</summary>
    public const string DocPageCount = "doc.pageCount";
    /// <summary>Дата генерации (YYYY-MM-DD).</summary>
    public const string DocGeneratedAt = "doc.generatedAt";
    /// <summary>Имя пользователя, запустившего генерацию.</summary>
    public const string DocGeneratedBy = "doc.generatedBy";
    /// <summary>Поле-файл с загруженной печатной формой (триггер извлечения метаданных).</summary>
    public const string DocPrintForm = "doc.printForm";

    /// <summary>Номер документа (для отображения/реестров).</summary>
    public const string DocNumber = "doc.number";

    // ── Тэги поля: документы качества ───────────────────────────────────────────
    /// <summary>Поле идентичности материала (артикул/наименование). Может быть несколько (по приоритету).</summary>
    public const string MaterialIdentity = "material.identity";
    /// <summary>Целевое поле, в которое подмешивается документ, подтверждающий качество.</summary>
    public const string MaterialQualityDocLink = "material.qualityDocLink";

    /// <summary>Дата окончания срока действия документа качества (для отсева просроченных при подборе).</summary>
    public const string QualityValidUntil = "quality.validUntil";

    /// <summary>Производитель — для группировки библиотеки и оценки релевантности подбора.</summary>
    public const string QualityManufacturer = "quality.manufacturer";

    // ── Тэги типа ───────────────────────────────────────────────────────────────
    /// <summary>Тип документа является «документом качества» (база для подтипов).</summary>
    public const string TypeQualityDocument = "type.qualityDocument";
    /// <summary>Тип документа относится к проектной документации (ГОСТ Р 21.101-2020).</summary>
    public const string TypeProjectDocumentation = "type.projectDocumentation";

    // ── Тэги набора данных (структура PDF-источника) ────────────────────────────
    /// <summary>PDF содержит обложку (первая страница пропускается при распознавании).</summary>
    public const string DatasetHasCover = "dataset.hasCover";
    /// <summary>PDF содержит титульный лист — источник реквизитов (скалярный профиль).</summary>
    public const string DatasetHasTitlePage = "dataset.hasTitlePage";
    /// <summary>Каждая страница PDF содержит основную надпись — распознаётся построчно (реестр листов).</summary>
    public const string DatasetHasTitleBlock = "dataset.hasTitleBlock";

    // ── Тэги документа ГОСТ-профиля (тип таблицы внутри распознанного документа) ──
    /// <summary>Документ — спецификация/ведомость материалов и/или оборудования (таблица, распознаётся и выгружается).</summary>
    public const string GostDocSpecification = "gostDoc.specification";
    /// <summary>Документ — кабельный журнал (таблица, распознаётся и выгружается).</summary>
    public const string GostDocCableJournal = "gostDoc.cableJournal";
}
