using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Действия модуля в журнале (ТЗ COST-6 «журнал изменений», CORE-25, CORE-28).
///
/// <para>Журнал в продукте ОДИН — тот же, что у ядра, — поэтому изменения счёта видны на общем
/// экране журнала вперемешку с действиями ядра. Свой журнал у модуля расходился бы с общим в главном:
/// в том, что считается автором и что считается временем.</para>
///
/// <para>⚠️ Объявление и запись — ОДИН И ТОТ ЖЕ объект (поля-константы ниже), потому что на экран
/// журнала попадает название из объявления, а в самой записи лежит только код. Передай мы название
/// строкой при записи — правка названия не доехала бы до старых записей, а расхождение названий порт
/// отвергает.</para>
///
/// <para>⚠️ Чего здесь НЕТ: «разобран», «отклонён», «оплачен», «разнесён». Каждое приезжает со своей
/// задачей (C2, C5, F1), и объявленное заранее действие означало бы фильтр на экране журнала, по
/// которому никогда ничего не находится.</para>
/// </summary>
public sealed class InvoiceActions : IModuleActivityActions
{
    public static readonly ModuleActivityAction Created = new("costs.invoice.created", "Счёт заведён");

    public static readonly ModuleActivityAction Changed = new("costs.invoice.changed", "Счёт изменён");

    /// <summary>
    /// «Всё верно» — отдельное действие, а не часть правки: человек ничего не менял, он подтвердил
    /// распознанное. Слить их значило бы потерять единственный след того, что проверку кто-то делал.
    /// </summary>
    public static readonly ModuleActivityAction Confirmed =
        new("costs.invoice.confirmed", "Распознанные поля счёта подтверждены");

    public static readonly ModuleActivityAction ScanAttached =
        new("costs.invoice.scanned", "К счёту приложен скан");

    public IReadOnlyList<ModuleActivityAction> Actions => [Created, Changed, Confirmed, ScanAttached];
}
