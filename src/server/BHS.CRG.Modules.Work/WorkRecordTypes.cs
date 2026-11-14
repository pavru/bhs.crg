namespace BHS.CRG.Modules.Work;

/// <summary>
/// Типы, которые заводит модуль «Учёт работ» (ТЗ TYPE-7, TYPE-10, TYPE-11, CORE-19.2, CORE-20.2; change
/// <c>work-13-report-model</c>): шапка отчёта монтажника и сведения строки работы.
///
/// <para><b>Колонки — системными полями с замком.</b> Отчёт и строка лежат в таблицах модуля колонками
/// (<c>ModuleStorage.ModuleTable</c>), и тип показывает их в редакторе полями модуля: администратор видит, из чего
/// состоит запись, а печатная форма и тэги находят поля по ключам. Значения кладёт код модуля (<c>Locked</c>), и
/// охрана записи не даёт положить их в JSON сведений рядом с колонкой.</para>
///
/// <para>⚠️ <b>Стройки и раздела среди полей нет, и это не пропуск</b>: стройка — сущность ядра, а не запись типа,
/// и полю-ссылке целиться не во что (вид <c>entity-ref</c> не построен, TYPE-7). Обе остаются колонками отчёта и
/// строки и объявлены ссылками модуля (<see cref="WorkReferences" />).</para>
///
/// <para>⚠️ <b>Код сведений — <see cref="LineDetailsCode" />, без точки</b>, хотя в ТЗ (TYPE-7) тип назван
/// «СтрокаРаботы.Сведения». Код типа — имя модуля его блоков в Typst (<c>Код.имя</c>, #773), а имя с точкой Typst не
/// принимает: блоки такого типа шаблонам были бы недоступны (<c>TypstPreambleBuilder.IsTypstIdentifier</c>).</para>
///
/// <para><b>Пропуск, а не отказ старта</b> (<c>SkipWhenBlocked</c>): цели полей — справочники, которые ядро заводит
/// не на всякой установке («Сотрудник» производен от «Персоны», «ВидРаботы» — от единиц измерения). На чистой
/// установке их нет, и тип заводится на том запуске, где цели появились.</para>
/// </summary>
public static class WorkRecordTypes
{
    /// <summary>Код шапки отчёта. Постоянен: им адресуются печатные блоки (ТЗ TYPE-6).</summary>
    public const string ReportCode = "ОтчётМонтажника";

    /// <summary>Код сведений строки работы — см. оговорку о точке в описании класса.</summary>
    public const string LineDetailsCode = "СтрокаРаботыСведения";

    // Коды типов ЯДРА (CoreRecordTypes) — строкой: модуль ядро не видит, а проекция разрешает код в тип сама и
    // называет в журнале запуска, если типа нет.
    private const string EmployeeType = "Сотрудник";
    private const string WorkTypeType = "ВидРаботы";
    private const string WorkVariantType = "ВариантРаботы";
    private const string UnitType = "ЕдиницаИзмерения";

    private const string Group = "Учёт работ";

    /// <summary>
    /// Отчёт монтажника — закрытый тип (ТЗ CORE-19.2, TYPE-7): схему шапки задаёт модуль, администратору остаются
    /// подписи полей и печатная форма отчёта и табеля. Своё поле в шапку не добавить — отказ «тип закрыт модулем».
    /// </summary>
    public static readonly ModuleRecordType Report = new(
        ReportCode,
        "Отчёт монтажника",
        ModuleSchemaLevel.Closed,
        [
            new("Сотрудник", "Сотрудник", "complex", Target: EmployeeType),
            new("Дата", "Дата", "date"),
            new("Смена", "Смена", "string"),
            new("Часы", "Часы", "number"),
            new("Состояние", "Состояние", "string"),
            new("Примечание", "Примечание", "text"),
        ],
        Group: Group,
        Storage: ModuleStorage.ModuleTable,
        SkipWhenBlocked: true);

    /// <summary>
    /// Сведения строки работы — расширяемый составной тип (ТЗ CORE-19.1, TYPE-7): поля модуля заперты, заказчик
    /// добавляет свои — испытания, условия, поля заказчика — только необязательными: прежние строки обязательное
    /// поле не заполнят. Значения своих полей лежат в JSON <c>details</c> строки и проходят охрану записи.
    /// </summary>
    public static readonly ModuleRecordType LineDetails = new(
        LineDetailsCode,
        "Строка работы: сведения",
        ModuleSchemaLevel.Extendable,
        [
            new("ВидРаботы", "Вид работы", "complex", Target: WorkTypeType),
            new("Вариант", "Вариант", "complex", Target: WorkVariantType),
            new("Объём", "Объём", "number"),
            new("Единица", "Ед. изм.", "complex", Target: UnitType),
            new("Место", "Место работ", "string"),
            new("Комментарий", "Комментарий", "text"),
            new("Бригада", "Бригадная работа", "boolean"),
        ],
        Group: Group,
        Storage: ModuleStorage.ModuleTable,
        SkipWhenBlocked: true,
        Kind: ModuleTypeKind.Composite);

    /// <summary>Всё, что объявляет модуль. Порядок значения не имеет — ядро проецирует каждый.</summary>
    public static readonly IReadOnlyList<ModuleRecordType> All = [Report, LineDetails];
}
