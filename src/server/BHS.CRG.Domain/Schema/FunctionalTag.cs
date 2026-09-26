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
    /// <summary>Дата генерации (YYYY-MM-DD); пользователю показывается как «Дата публикации».</summary>
    public const string DocGeneratedAt = "doc.generatedAt";
    /// <summary>Имя пользователя, запустившего генерацию; пользователю — «Публикатор».</summary>
    public const string DocGeneratedBy = "doc.generatedBy";
    /// <summary>Поле-файл с загруженной печатной формой (триггер извлечения метаданных).</summary>
    public const string DocPrintForm = "doc.printForm";

    /// <summary>Номер документа (для отображения/реестров).</summary>
    public const string DocNumber = "doc.number";

    /// <summary>Дата документа — реквизит, заполняемый пользователем. Не путать с
    /// <see cref="DocGeneratedAt"/>: дата документа не меняется при повторной генерации PDF.</summary>
    public const string DocDate = "doc.date";

    // ── Тэги поля: идентификатор объекта / документы качества ───────────────────
    /// <summary>Поле-идентификатор объекта (артикул/наименование/...). Обобщён из «material.identity»
    /// (issue #183): применим к ЛЮБОМУ составному типу — участвует в резолве «строка→объект».
    /// Может быть несколько: порядок задаёт приоритет и порядок компонентов композитного ключа.</summary>
    public const string Identity = "identity";
    /// <summary>Legacy-алиас на <see cref="Identity"/> (код тэга был «material.identity» до #183).
    /// Оставлен на один релиз как страховка; новый код использует <see cref="Identity"/>.</summary>
    public const string MaterialIdentity = Identity;
    /// <summary>Целевое поле, в которое подмешивается документ, подтверждающий качество.</summary>
    public const string MaterialQualityDocLink = "material.qualityDocLink";

    /// <summary>Дата окончания срока действия документа качества (для отсева просроченных при подборе).</summary>
    public const string QualityValidUntil = "quality.validUntil";

    /// <summary>Производитель — для группировки библиотеки и оценки релевантности подбора.</summary>
    public const string QualityManufacturer = "quality.manufacturer";

    // ── Тэги поля: справочник сотрудников (ТЗ TYPE-21, CORE-7, issue #962) ──────
    //
    // Коды названы ТЗ; их читает и ядро, и будущий модуль учёта работ — потому владелец у всех
    // ядро: у выключаемого владельца тэг пропал бы из редактора схем вместе с ним, а выключить
    // ядро нельзя. Схему сотрудника наращивает заказчик (CORE-7.1), и по названиям полей код её
    // читать не вправе (CORE-7.3) — тэг здесь единственный мост.

    /// <summary>Табельный номер — им сотрудник адресуется в выгрузке для бухгалтерии (CORE-7).</summary>
    public const string EmployeePersonnelNumber = "employee.personnelNumber";
    /// <summary>Должность сотрудника.</summary>
    public const string EmployeePosition = "employee.position";
    /// <summary>Начало периода работы.</summary>
    public const string EmployeeEmployedFrom = "employee.employedFrom";
    /// <summary>Конец периода работы; пусто — работает.</summary>
    public const string EmployeeEmployedTo = "employee.employedTo";

    /// <summary>
    /// Связь сотрудника с учётной записью — необязательная (CORE-7).
    ///
    /// ⚠️ В ТЗ этого кода НЕТ: свойство названо в CORE-7 словами, а тэга под него TYPE-21 не
    /// завело. Заведено решением — иначе код искал бы поле по названию, что CORE-7.3 запрещает
    /// прямо. Значение — идентификатор учётной записи; ссылкой в базе не является и являться не
    /// должна: учётные записи удаляются жёстко и в резервную копию не идут, а сотрудник их
    /// переживает (в этом и причина существования справочника).
    /// </summary>
    public const string EmployeeAccount = "employee.account";

    /// <summary>
    /// Срок действия допуска, удостоверения, медосмотра (ТЗ CORE-7.2, TYPE-21).
    ///
    /// ⚠️ Не путать с <see cref="QualityValidUntil" /> и не заменять им: тот объявлен модулем
    /// исполнительной документации, и при выключенном модуле срок действия допусков сотрудника
    /// исчез бы из редактора схем вместе с ним. Читатели тоже разные — подбор сертификата к
    /// материалу против проверки допуска при назначении.
    /// </summary>
    public const string CertValidUntil = "cert.validUntil";

    /// <summary>
    /// Ссылка на вид работы классификатора (ТЗ TYPE-21, CORE-13, issue #963).
    ///
    /// ⚠️ Тэг, а не имя поля, — ровно ради CORE-13: позиция сметы, задача графика и строка отчёта
    /// обязаны ссылаться на вид работы, а не склеиваться по совпадению названия. Склейка по
    /// названию была главной ошибкой старой системы, только теперь она была бы в деньгах и сроках.
    /// </summary>
    public const string RefWorkType = "ref.workType";

    // ── Тэги поля: классификатор видов работ (ТЗ TYPE-21, CORE-8, issue #963) ───
    //
    // Читатель по ТЗ — модуль учёта работ (этап 2), а объявлены тэги здесь, у ядра, и это не
    // небрежность: поля с ними объявляет САМО ЯДРО (CoreRecordTypes.WorkType), а объявление ядра
    // не вправе ждать модуля. Отдай мы их модулю — при выключенном «work» классификатор не
    // поднялся бы вовсе, хотя он справочник ядра и переживает выключение любого модуля. Что
    // кода-читателя пока нет, сказано вслух в описаниях реестра — как у cert.validUntil.

    /// <summary>Вид работы требует указания места выполнения (ТЗ CORE-8, TYPE-9).</summary>
    public const string WorkRequiresLocation = "work.requiresLocation";

    /// <summary>Вид работы требует указания применённых материалов (ТЗ CORE-8, TYPE-9).</summary>
    public const string WorkRequiresMaterials = "work.requiresMaterials";

    /// <summary>
    /// Работа этого вида попадает в исполнительную документацию (ТЗ CORE-8, TYPE-9).
    ///
    /// ⚠️ Читателей по ТЗ двое — учёт работ и исполнительная документация, — а владелец у тэга
    /// один. Отсюда владелец «ядро»: тэг, отданный одному из двух читателей, пропадал бы из
    /// редактора схем при выключении чужого для него модуля.
    /// </summary>
    public const string WorkProducesId = "work.producesId";

    // ── Тэги типа ───────────────────────────────────────────────────────────────
    /// <summary>Тип документа является «документом качества» (база для подтипов).</summary>
    public const string TypeQualityDocument = "type.qualityDocument";
    /// <summary>Тип документа относится к проектной документации (ГОСТ Р 21.101-2020).</summary>
    public const string TypeProjectDocumentation = "type.projectDocumentation";
    /// <summary>Составной тип-«выбор» (union, issue #320): пользователь заполняет РОВНО ОДНО из полей
    /// типа (напр. «список» ИЛИ «ссылка на документ»). Значение — composite с единственным заполненным
    /// подполем; выбранный вариант определяется по заполненному ключу.</summary>
    public const string TypeUnion = "type.union";

    // ── Тэги типа: профиль уровня (issue #258) ──────────────────────────────────
    // Составной тип, помеченный тэгом, — «профиль» соответствующего уровня-контейнера: его поля
    // амбиентно попадают в шаблон всех документов уровня (data.уровень.стройка/раздел/комплект).
    // Ограничение MaxBearers=1 (TagRegistry): ровно один тип во всей системе может нести каждый тэг.
    /// <summary>Составной тип — профиль уровня «Стройка».</summary>
    public const string ProfileConstruction = "profile.construction";
    /// <summary>Составной тип — профиль уровня «Раздел».</summary>
    public const string ProfileSection = "profile.section";
    /// <summary>Составной тип — профиль уровня «Комплект».</summary>
    public const string ProfileSet = "profile.set";

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
