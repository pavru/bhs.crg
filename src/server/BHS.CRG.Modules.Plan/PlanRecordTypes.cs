namespace BHS.CRG.Modules.Plan;

/// <summary>
/// Типы, которые заводит модуль «Планирование» (change <c>plan-06-estimate-model</c>, ТЗ TYPE-7, TYPE-16).
///
/// <para><b>Тэгов <c>estimate.*</c> модуль НЕ объявляет</b>, хотя пример в ТЗ (TYPE-16) их показывает
/// (<c>estimate.rate</c>, <c>estimate.unitCost</c>, <c>estimate.total</c>). Расценка, стоимость единицы и
/// итог — колонки строки сметы (<c>EstimateRow</c>): их читают код КП, итоги и контракт, и читали бы их
/// тэги из JSON — поле заказчика, переименованное или удалённое, меняло бы суммы молча (ТЗ CORE-15).</para>
/// </summary>
public static class PlanRecordTypes
{
    /// <summary>Код типа сметного наполнения строки. Постоянен: по нему модуль находит тип при записи.</summary>
    public const string EstimateRowCode = "ПозицияСметы";

    /// <summary>
    /// Сметное наполнение строки сметы — составной тип класса C, открытый (ТЗ TYPE-16, CORE-19): скелет
    /// строки и деньги — колонки модуля, а схемой описано то, что у заказчиков разное, — обоснование,
    /// нормативная база, коэффициенты, свои поля.
    ///
    /// <para><b>Полей модуль не объявляет вовсе</b> — схему ведёт администратор. Всё наполнение целиком
    /// отдаётся только с правом <c>plan.estimate.cost</c> (ТЗ PLAN-11): пометка денежных полей тэгом
    /// отвергнута, потому что поле, добавленное без тэга, утекло бы молча.</para>
    ///
    /// <para>Носитель — таблица модуля: данные лежат в строке сметы (<c>estimate_rows.data</c>), и общим
    /// путём объект этого типа не заводится (<c>TypeStorageRules</c>). Пишет их одно место —
    /// <c>EstimateRevisionWriter</c>, через охрану записи ядра.</para>
    /// </summary>
    public static readonly ModuleRecordType EstimateRow = new(
        EstimateRowCode,
        "Позиция сметы",
        ModuleSchemaLevel.Open,
        [],
        Group: "Планирование",
        Storage: ModuleStorage.ModuleTable,
        Kind: ModuleTypeKind.Composite);

    /// <summary>Код типа коммерческого предложения. Постоянен: по нему модуль находит тип при записи.</summary>
    public const string OfferCode = "КоммерческоеПредложение";

    /// <summary>Тэг наценки КП (ТЗ TYPE-17, таблица тэгов: владелец — <c>plan</c>).</summary>
    public const string OfferMarkupTag = "offer.markup";

    /// <summary>
    /// Тэги, которые вносит модуль (ТЗ TYPE-21, TYPE-22). Только <c>offer.markup</c>: его читает печать КП
    /// (<c>plan-15</c>), а у «Итого» тэга нет нарочно — <c>doc.total</c> принадлежит модулю счетов, и установка
    /// без счетов с ним не поднялась бы (неизвестный тэг останавливает старт).
    /// </summary>
    public static readonly IReadOnlyList<ModuleTag> Tags =
    [
        new(OfferMarkupTag, "Наценка коммерческого предложения",
            "Наценка предложения в процентах — та, что действует у строк, где своей наценки и наценки раздела ЛСР " +
            "нет. Значение кладёт код модуля «Планирование», руками поле не правится.",
            ModuleTagScope.Field, ["number"], Group: "Планирование"),
    ];

    /// <summary>
    /// Коммерческое предложение — запись модуля (ТЗ TYPE-17, PLAN-Q8, change <c>plan-13-offers</c>): строки,
    /// наценки и суммы — колонки и таблицы модуля, схемой описаны поля заказчика.
    ///
    /// <para><b>Уровень «расширяемый», а не «открытый»</b>, как в таблице ТЗ: на открытом уровне замков не держит
    /// ни охрана записи (<c>RecordWriteGuard</c> открытый тип не проверяет), ни редактор схемы — администратор
    /// удалил бы «Наценку» и «Итого», и печать КП потеряла бы поле. Поля заказчика добавляются, как у счёта.</para>
    ///
    /// <para><b>Оба системных поля заперты</b>: их значения кладёт расчёт (<c>OfferPricing</c>), а в данных
    /// записи они не лежат — охрана сверяет их проекцией из колонок (<c>OfferRequisites</c>).</para>
    /// </summary>
    public static readonly ModuleRecordType Offer = new(
        OfferCode,
        "Коммерческое предложение",
        ModuleSchemaLevel.Extendable,
        [
            new("Наценка", "Наценка, %", "number", [OfferMarkupTag]),
            new("Итого", "Итого к оплате", "number"),
        ],
        Group: "Планирование",
        Storage: ModuleStorage.ModuleTable);

    public const string ScheduleCode = "ГрафикСтройки";

    /// <summary>Шапка графика хранится в plan.schedules. Тип нужен для печатной формы записи.</summary>
    public static readonly ModuleRecordType Schedule = new(
        ScheduleCode, "График стройки", ModuleSchemaLevel.Closed, [],
        Group: "Планирование", Storage: ModuleStorage.ModuleTable);

    /// <summary>Всё, что объявляет модуль.</summary>
    public static readonly IReadOnlyList<ModuleRecordType> All = [EstimateRow, Offer, Schedule];
}
