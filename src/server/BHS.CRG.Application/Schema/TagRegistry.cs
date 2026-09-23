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

/// <summary>
/// Числовой параметр тэга (issue #583) — запись в схеме «код:параметр», напр. <c>identity:1</c>.
/// Наличие описания и означает «у этого тэга есть параметр»: редактор схем показывает поле ввода
/// только тем тэгам, что его объявили, а разбирается запись всегда одинаково
/// (<see cref="Domain.Schema.TagCode" />).
/// </summary>
/// <param name="Label">Короткая подпись поля ввода (в строку рядом с тэгом).</param>
/// <param name="Description">Что означает номер — подсказка при наведении.</param>
public record TagParameter(string Label, string Description);

/// <summary>Кто объявил тэг. Модуль назван своим кодом; тэги ядра — <see cref="Core" />.</summary>
public static class TagOwners
{
    /// <summary>
    /// Владелец тэгов, которые читает само ядро. Не модуль: выключить ядро нельзя, поэтому такой
    /// тэг предлагается всегда.
    ///
    /// Значение выбрано так, чтобы не совпасть ни с одним кодом модуля (<c>id</c>, <c>work</c>,
    /// <c>plan</c>, <c>costs</c>, <c>ozhr</c>) — иначе «ядро» и «модуль по имени core» стали бы
    /// неразличимы, и тэг ядра пропал бы вместе с выключённым однофамильцем.
    /// </summary>
    public const string Core = "core";
}

/// <param name="Owner">
/// Код модуля-владельца либо <see cref="TagOwners.Core" /> (ТЗ TYPE-22, issue #959). Владелец —
/// тот, чей код читает тэг: выключен модуль — тэг не предлагается, потому что прочитать его
/// некому.
///
/// ⚠️ Пусто быть не может: тэг без владельца невозможно ни выключить, ни объяснить, и первым же
/// вопросом о нём станет «а кто это читает?». Сторож в тестах требует непустого владельца у
/// каждой записи реестра.
/// </param>
/// <param name="Group">
/// Группа для редактора схем. Пусто — тэг идёт под названием владельца.
/// </param>
public record TagDefinition(
    string Code,
    string Label,
    string Description,
    TagScope Scope,
    string[] AppliesTo,
    bool Multiple,
    TagRestriction? Restriction = null,
    TagParameter? Parameter = null,
    string Owner = TagOwners.Core,
    string? Group = null);

/// <summary>
/// Тэги ЯДРА — те, что читает общий код (см. <see cref="FunctionalTag"/>). Модули добавляют свои
/// через <c>IAppModule.Tags</c>; собранный реестр включённых — <see cref="TagCatalog" />.
///
/// ⚠️ Напрямую этот список читают только сборка каталога и её тесты. Всем остальным нужен
/// <see cref="TagCatalog" />: он знает, какие модули включены, а статический список — нет, и
/// обращение к нему мимо каталога вернуло бы тэги выключенного модуля (TYPE-22).
/// </summary>
public static class TagRegistry
{
    public static readonly IReadOnlyList<TagDefinition> Core =
    [
        // ── Field: реквизиты документа ──
        // Метаданные генерации (кол-во страниц, дата и автор публикации, печатная форма) и тэги
        // документов качества переехали к модулю исполнительной документации (issue #959):
        // читает их его код, и на экземпляре без него предлагать их незачем.
        new(FunctionalTag.DocNumber, "Номер документа",
            "Номер документа — показывается в списках (напр. в библиотеке документов качества).",
            TagScope.Field, ["string", "text"], Multiple: false),
        new(FunctionalTag.DocDate, "Дата документа",
            "Дата документа, заполняемая пользователем. В отличие от даты генерации не меняется при повторной генерации PDF; попадает в консолидации (напр. в реестр документов комплекта).",
            TagScope.Field, ["date", "string", "text"], Multiple: false),

        // ── Field: идентификатор объекта / документы качества ──
        new(FunctionalTag.Identity, "Идентификатор",
            "Поле-идентификатор объекта (артикул, наименование…) — по нему строка сопоставляется с существующим объектом каталога (paste, источники данных) и материал — с документом качества. Отмеченных полей может быть несколько: ключ склеивается из ВСЕХ них, поэтому объект опознаётся всеми полями сразу, а не любым из них.",
            TagScope.Field, ["string", "text"], Multiple: true,
            Parameter: new("№", "Номер компонента в составном ключе. Задаёт порядок склейки — менять его "
                + "нельзя без последствий: ключи всех объектов изменятся разом и заведённые связки "
                + "перестанут находиться. Поля без номера идут после нумерованных, в порядке схемы.")),
        // ── Type ──
        new(FunctionalTag.TypeProjectDocumentation, "Проектная документация",
            "Тип документа относится к проектной документации (ГОСТ Р 21.101-2020).",
            TagScope.Type, ["Document"], Multiple: false),
        new(FunctionalTag.TypeUnion, "Выбор одного (union)",
            "Составной тип-«выбор»: пользователь заполняет РОВНО ОДНО из полей типа (напр. «Список» ИЛИ «Ссылка на документ»). В форме показывается переключатель варианта, а не все поля сразу.",
            TagScope.Type, ["Composite"], Multiple: false),

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

}
