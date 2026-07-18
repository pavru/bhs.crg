using BHS.CRG.Domain.Schema;

namespace BHS.CRG.Application.Schema;

public enum TagScope { Field, Type, Dataset, GostDocument }

/// <summary>
/// Описание функционального тэга для UI и валидации.
/// <paramref name="AppliesTo"/>: для Field — допустимые типы поля (SchemaField.type);
/// для Type — допустимые виды типа ("Document"/"Composite"); для Dataset не используется
/// (пустой = любой формат источника).
/// </summary>
/// <summary>
/// Внутреннее ограничение назначения тэга (issue #258) — пользователь им не управляет. Сейчас одно
/// поле: <paramref name="MaxBearers"/> — глобальный максимум РАЗЛИЧНЫХ носителей тэга по всем типам
/// (носитель: тип для Type-тэга, пара тип+поле для Field-тэга; считается по СОБСТВЕННЫМ схемам).
/// Сам record — точка расширения (взаимоисключения/обязательность добавляются позже, не ломая контракт).
/// </summary>
public record TagRestriction(int? MaxBearers);

public record TagDefinition(
    string Code,
    string Label,
    string Description,
    TagScope Scope,
    string[] AppliesTo,
    bool Multiple,
    TagRestriction? Restriction = null);

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

        // ── Field: идентификатор объекта / документы качества ──
        new(FunctionalTag.Identity, "Идентификатор",
            "Поле-идентификатор объекта (артикул, наименование…) — по нему строка сопоставляется с существующим объектом каталога (paste, источники данных) и материал — с документом качества. Можно отметить несколько: порядок задаёт приоритет и порядок компонентов составного ключа.",
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

        // ── Type: профиль уровня (issue #258) — ровно один тип на уровень (MaxBearers=1) ──
        new(FunctionalTag.ProfileConstruction, "Профиль стройки",
            "Составной тип — профиль уровня «Стройка». Его поля доступны во всех документах стройки в шаблоне: data.уровень.стройка.*. Может быть только один такой тип.",
            TagScope.Type, ["Composite"], Multiple: false, Restriction: new(MaxBearers: 1)),
        new(FunctionalTag.ProfileSection, "Профиль раздела",
            "Составной тип — профиль уровня «Раздел». Его поля доступны во всех документах раздела в шаблоне: data.уровень.раздел.*. Может быть только один такой тип.",
            TagScope.Type, ["Composite"], Multiple: false, Restriction: new(MaxBearers: 1)),
        new(FunctionalTag.ProfileSet, "Профиль комплекта",
            "Составной тип — профиль уровня «Комплект». Его поля доступны во всех документах комплекта в шаблоне: data.уровень.комплект.*. Может быть только один такой тип.",
            TagScope.Type, ["Composite"], Multiple: false, Restriction: new(MaxBearers: 1)),

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

        // ── GostDocument: тип таблицы внутри распознанного документа ГОСТ-профиля ──
        new(FunctionalTag.GostDocSpecification, "Спецификация / ведомость",
            "Документ — спецификация или ведомость материалов и/или оборудования. Таблица распознаётся и доступна к выгрузке (CSV/XLS/XLSX).",
            TagScope.GostDocument, [], Multiple: false),
        new(FunctionalTag.GostDocCableJournal, "Кабельный журнал",
            "Документ — кабельный журнал. Таблица распознаётся и доступна к выгрузке (CSV/XLS/XLSX).",
            TagScope.GostDocument, [], Multiple: false),
    ];

    public static TagDefinition? Find(string code) => All.FirstOrDefault(t => t.Code == code);
}
