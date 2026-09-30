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
/// <para>⚠️ Чего здесь НЕТ: «отклонён», «оплачен». Каждое приезжает со своей задачей (C5), и
/// объявленное заранее действие означало бы фильтр на экране журнала, по которому никогда ничего не
/// находится. «Разобран» приехал задачей C2 (issue #1078), правка разноски — задачей F1 (issue #1085),
/// каждое вместе с адресом, который его записывает.</para>
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

    /// <summary>
    /// Правка строк — отдельно от правки счёта (C2, issue #1078): строки меняют вставкой из буфера
    /// сразу десятками, и в журнале это должно читаться как одно действие с числом строк, а не
    /// растворяться в «счёт изменён».
    /// </summary>
    public static readonly ModuleActivityAction LinesChanged =
        new("costs.invoice.lines", "Строки счёта изменены");

    /// <summary>«Разобран»: человек сверил счёт с бумагой (ТЗ COST-9).</summary>
    public static readonly ModuleActivityAction Parsed = new("costs.invoice.parsed", "Счёт разобран");

    /// <summary>
    /// Возврат в черновик. Пишется и когда человек решил сам, и когда правка строк сняла последнюю
    /// позицию: во втором случае это единственный след того, почему счёт перестал быть разобранным.
    /// </summary>
    public static readonly ModuleActivityAction Draft =
        new("costs.invoice.draft", "Счёт возвращён в черновик");

    /// <summary>
    /// Правка разноски строки (ТЗ COST-15): пишется с ПРЕЖНИМ и НОВЫМ распределением — разноску правят
    /// и после «разобран», и без прежнего распределения запись «разноска изменена» не отвечала бы на
    /// единственный вопрос, ради которого её читают: откуда ушли деньги.
    /// </summary>
    public static readonly ModuleActivityAction AllocationChanged =
        new("costs.invoice.allocation", "Разноска счёта изменена");

    public IReadOnlyList<ModuleActivityAction> Actions =>
        [Created, Changed, Confirmed, ScanAttached, LinesChanged, Parsed, Draft, AllocationChanged];
}
