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

    /// <summary>Всё, что объявляет модуль.</summary>
    public static readonly IReadOnlyList<ModuleRecordType> All = [EstimateRow];
}
