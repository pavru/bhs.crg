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
/// <para>⚠️ Чего здесь НЕТ: «отклонён». Оно приедет со своим переходом, и объявленное заранее
/// действие означало бы фильтр на экране журнала, по которому никогда ничего не находится. «Разобран» приехал задачей C2 (issue #1078), правка разноски — задачей F1 (issue #1085),
/// каждое вместе с адресом, который его записывает.</para>
/// </summary>
public sealed class InvoiceActions : IModuleActivityActions
{
    /// <summary>
    /// Право, без которого запись о счёте в журнале не видна (H1, issue #1104). Журнал читают по праву
    /// ядра, а запись называет счёт и поставщика: «кому и когда платим» — те же данные счёта, только
    /// без суммы. Одного открытого модуля здесь мало: его права делят данные, и человек с одним правом
    /// на справочник статей видел бы в журнале оплаты.
    ///
    /// <para>⚠️ Статьи вне строек права НЕ называют нарочно: справочник открыт и лежит в общей таблице,
    /// название статьи видно и без модуля. Им хватает открытого модуля.</para>
    /// </summary>
    private const string InvoiceRead = "costs.invoice.read";

    public static readonly ModuleActivityAction Created = new("costs.invoice.created", "Счёт заведён", InvoiceRead);

    public static readonly ModuleActivityAction Changed = new("costs.invoice.changed", "Счёт изменён", InvoiceRead);

    /// <summary>
    /// «Всё верно» — отдельное действие, а не часть правки: человек ничего не менял, он подтвердил
    /// распознанное. Слить их значило бы потерять единственный след того, что проверку кто-то делал.
    /// </summary>
    public static readonly ModuleActivityAction Confirmed =
        new("costs.invoice.confirmed", "Распознанные поля счёта подтверждены", InvoiceRead);

    public static readonly ModuleActivityAction ScanAttached =
        new("costs.invoice.scanned", "К счёту приложен скан", InvoiceRead);

    /// <summary>
    /// Правка строк — отдельно от правки счёта (C2, issue #1078): строки меняют вставкой из буфера
    /// сразу десятками, и в журнале это должно читаться как одно действие с числом строк, а не
    /// растворяться в «счёт изменён».
    /// </summary>
    public static readonly ModuleActivityAction LinesChanged =
        new("costs.invoice.lines", "Строки счёта изменены", InvoiceRead);

    /// <summary>
    /// Выбор позиций запомнен как соответствия поставщика (issue #1079, ТЗ COST-7.1). Отдельным действием,
    /// а не внутри «строки изменены»: запомненное меняет поведение СЛЕДУЮЩИХ счетов этого поставщика, и
    /// найти в журнале, когда и чьим сохранением строка стала подставляться не туда, надо уметь.
    /// </summary>
    public static readonly ModuleActivityAction MatchesRemembered =
        new("costs.match.remembered", "Соответствия наименований запомнены", InvoiceRead);

    /// <summary>
    /// Соответствие направлено на другую позицию из списка соответствий — «было → стало». По записи на
    /// правку, а не сводкой: правка из списка — явное действие человека над одной записью, и найти, кто
    /// и когда перенаправил строку поставщика, надо уметь.
    /// </summary>
    public static readonly ModuleActivityAction MatchPointed =
        new("costs.match.pointed", "Соответствие наименований направлено на другую позицию", InvoiceRead);

    /// <summary>Соответствие забыто: следующие счета поставщика такую строку не узнают.</summary>
    public static readonly ModuleActivityAction MatchForgotten =
        new("costs.match.forgotten", "Соответствие наименований забыто", InvoiceRead);

    /// <summary>
    /// Все соответствия поставщика забыты разом — одной записью с числом: сотня записей «забыто» о
    /// одном нажатии засорила бы журнал и ничего бы не добавила.
    /// </summary>
    public static readonly ModuleActivityAction MatchesForgotten =
        new("costs.match.forgotten_all", "Все соответствия наименований поставщика забыты", InvoiceRead);

    /// <summary>«Разобран»: человек сверил счёт с бумагой (ТЗ COST-9).</summary>
    public static readonly ModuleActivityAction Parsed = new("costs.invoice.parsed", "Счёт разобран", InvoiceRead);

    /// <summary>
    /// Возврат в черновик. Пишется и когда человек решил сам, и когда правка строк сняла последнюю
    /// позицию: во втором случае это единственный след того, почему счёт перестал быть разобранным.
    /// </summary>
    public static readonly ModuleActivityAction Draft =
        new("costs.invoice.draft", "Счёт возвращён в черновик", InvoiceRead);

    /// <summary>
    /// Правка разноски строки (ТЗ COST-15): пишется с ПРЕЖНИМ и НОВЫМ распределением — разноску правят
    /// и после «разобран», и без прежнего распределения запись «разноска изменена» не отвечала бы на
    /// единственный вопрос, ради которого её читают: с какого объекта и на какой перенесли затраты.
    ///
    /// <para>⚠️ Распределение названо строкой, целью и количеством — <b>без рублей</b> (issue #1190): журнал
    /// ядра читают по <c>core.audit.read</c>, без права на счета. СКОЛЬКО перенесли, показывает сам счёт,
    /// под своим правом. Правка одних сумм описывается теми же целями — и «стало» говорит об этом словами
    /// («изменены суммы долей»), а не числом.</para>
    /// </summary>
    public static readonly ModuleActivityAction AllocationChanged =
        new("costs.invoice.allocation", "Разноска счёта изменена", InvoiceRead);

    /// <summary>
    /// Отметка оплаты (C5, issue #1082). ⚠️ Сумм событие не несёт: журнал ядра читают по
    /// <c>core.audit.read</c>, без права на счета, — названы дата, платёжный документ и перенос учётной даты.
    /// </summary>
    public static readonly ModuleActivityAction Paid = new("costs.invoice.paid", "Счёт оплачен", InvoiceRead);

    /// <summary>Отмена ошибочной отметки — с причиной: версий оплата не создаёт, и другого следа нет.</summary>
    public static readonly ModuleActivityAction Unpaid = new("costs.invoice.unpaid", "Оплата счёта отменена", InvoiceRead);

    public static readonly ModuleActivityAction PaymentDescribed =
        new("costs.invoice.paydoc", "Платёжный документ счёта изменён", InvoiceRead);

    // Справочник статей вне строек (F3, issue #1087): статья меняет то, куда попадут новые затраты, и
    // «кто убрал «Склад»» — вопрос, на который журнал обязан отвечать.
    public static readonly ModuleActivityAction ArticleCreated =
        new("costs.article.created", "Статья вне строек заведена");

    public static readonly ModuleActivityAction ArticleRenamed =
        new("costs.article.renamed", "Статья вне строек переименована");

    public static readonly ModuleActivityAction ArticleDeleted =
        new("costs.article.deleted", "Статья вне строек убрана");

    // Архив статьи (issue #1185): «кто и когда убрал статью из выбора» больше спросить не у кого —
    // поля «кто» у записи нет нарочно.
    public static readonly ModuleActivityAction ArticleArchived =
        new("costs.article.archived", "Статья вне строек отправлена в архив");

    public static readonly ModuleActivityAction ArticleUnarchived =
        new("costs.article.unarchived", "Статья вне строек возвращена из архива");

    // Распознавание скана легло в счёт (issue #1077). Отдельным действием, а не «изменён»: правку
    // сделал не человек, и по журналу это должно быть видно — «кто вписал номер» иначе вело бы к тому,
    // кто нажал кнопку.
    public static readonly ModuleActivityAction Recognized =
        new("costs.invoice.recognized", "Счёт заполнен из скана", InvoiceRead);

    // Организация заведена из скана (issue #1077). Запись — справочника ЯДРА, а создание записи ядро
    // само не журналирует: без этой строки не узнать ни кто её завёл, ни по какому счёту. Закрыта
    // правом чтения счетов: запись называет счёт и поставщика — это уже данные счёта.
    public static readonly ModuleActivityAction OrganizationCreated =
        new("costs.organization.created", "Организация заведена из скана счёта", InvoiceRead);

    public IReadOnlyList<ModuleActivityAction> Actions =>
        [Created, Changed, Confirmed, ScanAttached, Recognized, OrganizationCreated, LinesChanged, MatchesRemembered, MatchPointed, MatchForgotten, MatchesForgotten, Parsed, Draft, AllocationChanged,
         Paid, Unpaid, PaymentDescribed, ArticleCreated, ArticleRenamed, ArticleDeleted,
         ArticleArchived, ArticleUnarchived];
}
