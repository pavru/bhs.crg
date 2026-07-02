using BHS.CRG.Domain.Schema;

namespace BHS.CRG.Application.Schema;

public enum TagScope { Field, Type, Dataset }

/// <summary>
/// Описание функционального тэга для UI и валидации.
/// <paramref name="AppliesTo"/>: для Field — допустимые типы поля (SchemaField.type);
/// для Type — допустимые виды типа ("Document"/"Composite"); для Dataset не используется
/// (пустой = любой формат источника).
/// </summary>
public record TagDefinition(
    string Code,
    string Label,
    string Description,
    TagScope Scope,
    string[] AppliesTo,
    bool Multiple);

/// <summary>Реестр функциональных тэгов — единый источник правды (см. <see cref="FunctionalTag"/>).</summary>
public static class TagRegistry
{
    public static readonly IReadOnlyList<TagDefinition> All =
    [
        // ── Field: метаданные генерации ──
        new(FunctionalTag.DocPageCount, "Кол-во страниц (PDF)",
            "Автозаполняется числом страниц после генерации/загрузки печатной формы.",
            TagScope.Field, ["number", "string", "text"], Multiple: false),
        new(FunctionalTag.DocGeneratedAt, "Дата генерации",
            "Автозаполняется датой генерации документа.",
            TagScope.Field, ["date", "string", "text"], Multiple: false),
        new(FunctionalTag.DocGeneratedBy, "Сгенерировал",
            "Автозаполняется именем пользователя, запустившего генерацию.",
            TagScope.Field, ["string", "text"], Multiple: false),
        new(FunctionalTag.DocPrintForm, "Печатная форма (файл)",
            "Поле-файл: при загрузке система извлекает метаданные (кол-во страниц и т.п.).",
            TagScope.Field, ["file"], Multiple: false),
        new(FunctionalTag.DocNumber, "Номер документа",
            "Номер документа — показывается в списках (напр. в библиотеке документов качества).",
            TagScope.Field, ["string", "text"], Multiple: false),

        // ── Field: документы качества ──
        new(FunctionalTag.MaterialIdentity, "Идентичность материала",
            "Поле для сопоставления материала с документом качества (артикул, наименование). Можно отметить несколько — порядок задаёт приоритет.",
            TagScope.Field, ["string", "text"], Multiple: true),
        new(FunctionalTag.MaterialQualityDocLink, "Ссылка на документ качества",
            "Целевое поле, в которое подмешивается привязанный документ, подтверждающий качество.",
            TagScope.Field, ["complex"], Multiple: false),
        new(FunctionalTag.QualityValidUntil, "Срок действия (до)",
            "Дата окончания действия документа качества. Просроченные документы исключаются при подборе сертификата к материалу.",
            TagScope.Field, ["date"], Multiple: false),
        new(FunctionalTag.QualityManufacturer, "Производитель",
            "Поле производителя. Используется для группировки библиотеки и оценки релевантности при подборе к материалу.",
            TagScope.Field, ["string", "text"], Multiple: false),

        // ── Type ──
        new(FunctionalTag.TypeQualityDocument, "Документ качества",
            "Тип документа считается «документом качества» (для библиотеки и распознавания). Наследуется подтипами.",
            TagScope.Type, ["Document"], Multiple: false),
        new(FunctionalTag.TypeProjectDocumentation, "Проектная документация",
            "Тип документа относится к проектной документации (ГОСТ Р 21.101-2020).",
            TagScope.Type, ["Document"], Multiple: false),

        // ── Dataset: структура PDF-источника ──
        new(FunctionalTag.DatasetHasCover, "Имеет обложку",
            "PDF-источник содержит обложку (первая страница пропускается при распознавании основных надписей).",
            TagScope.Dataset, [], Multiple: false),
        new(FunctionalTag.DatasetHasTitlePage, "Имеет титульный лист",
            "PDF-источник содержит титульный лист — реквизиты распознаются с него (скалярный профиль).",
            TagScope.Dataset, [], Multiple: false),
        new(FunctionalTag.DatasetHasTitleBlock, "Имеет основную надпись",
            "Каждая страница PDF содержит основную надпись (штамп) по ГОСТ Р 21.101-2020 — распознаётся построчно в реестр листов.",
            TagScope.Dataset, [], Multiple: false),
    ];

    public static TagDefinition? Find(string code) => All.FirstOrDefault(t => t.Code == code);
}
