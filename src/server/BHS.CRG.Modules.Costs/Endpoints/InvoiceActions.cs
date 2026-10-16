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
/// <para>Действие объявляется ВМЕСТЕ с адресом, который его записывает: объявленное заранее означало
/// бы фильтр на экране журнала, по которому никогда ничего не находится. Так приехали «разобран»,
/// «оплачен» и «отклонён» (issue #1284).</para>
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
        new("costs.invoice.scanned", "К счёту приложен файл", InvoiceRead);

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
    /// «Отклонён»: по счёту решили не платить (issue #1284). ⚠️ Сумм запись не несёт (issue #1190) —
    /// журнал ядра читают без права на счета; названы счёт, поставщик и прежнее состояние.
    /// </summary>
    public static readonly ModuleActivityAction Rejected =
        new("costs.invoice.rejected", "Счёт отклонён", InvoiceRead);

    /// <summary>
    /// Счёт удалён (issue #1282) — с тем же <c>targetId</c>, что у прежних записей счёта: по нему
    /// история удалённого счёта находится целиком. ⚠️ Сумм запись не несёт (issue #1190); названы
    /// номер, дата, поставщик и имя файла — больше опознать удалённое нечем.
    /// </summary>
    public static readonly ModuleActivityAction Deleted =
        new("costs.invoice.deleted", "Счёт удалён", InvoiceRead);

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

    /// <summary>
    /// Исправление счёта закрытого периода (E2, issue #1100): номер ушедшей версии и причина. ⚠️ Сумм
    /// нет, как у разноски (issue #1190): что было и что стало, показывают версия и книга счёта под правом
    /// на счета, а журнал ядра читают и без него.
    /// </summary>
    public static readonly ModuleActivityAction Corrected =
        new("costs.invoice.corrected", "Счёт исправлен", InvoiceRead);

    /// <summary>
    /// Смена допуска записала счёту книгу (E2, issue #1100, spec v3.3 п. 1.9): «было → стало» — допуск, а не
    /// суммы. Смена у ядра записана своим событием; это — у каждого затронутого счёта, чтобы его история
    /// отвечала, почему у него запись книги, которой никто из людей не делал.
    /// </summary>
    public static readonly ModuleActivityAction ToleranceAligned =
        new("costs.invoice.tolerance", "Книга счёта выровнена сменой допуска", InvoiceRead);

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
        new("costs.invoice.recognized", "Счёт заполнен из файла", InvoiceRead);

    // Организация заведена из скана (issue #1077). Запись — справочника ЯДРА, а создание записи ядро
    // само не журналирует: без этой строки не узнать ни кто её завёл, ни по какому счёту. Закрыта
    // правом чтения счетов: запись называет счёт и поставщика — это уже данные счёта.
    public static readonly ModuleActivityAction OrganizationCreated =
        new("costs.organization.created", "Организация заведена из файла счёта", InvoiceRead);

    /// <summary>
    /// Срок «оплатить до» вписан рукой (ТЗ COST-16: ручная правка срока — событие журнала; задача C4,
    /// issue #1080). Отдельно от «счёт изменён»: с этой записи правило срок больше не трогает, и найти,
    /// кто и когда снял счёт с правила, надо уметь.
    /// </summary>
    public static readonly ModuleActivityAction DueByHand =
        new("costs.invoice.due-by-hand", "Срок оплаты изменён вручную", InvoiceRead);

    /// <summary>«По правилу»: ручная правка срока снята, срок подставлен правилом.</summary>
    public static readonly ModuleActivityAction DueByRule =
        new("costs.invoice.due-by-rule", "Срок оплаты возвращён к правилу", InvoiceRead);

    /// <summary>
    /// Условия поставщика записаны или изменены (ТЗ COST-9.2; C4, issue #1080) — «было → стало» в днях.
    /// Заведённые счета от этого не меняются, и запись нужна, чтобы ответить, откуда у нового счёта
    /// такая отсрочка.
    /// </summary>
    public static readonly ModuleActivityAction TermsSet =
        new("costs.supplier-terms.set", "Условия поставщика изменены", InvoiceRead);

    /// <summary>Условия поставщика сняты: новые счета этого поставщика отсрочку от них не получают.</summary>
    public static readonly ModuleActivityAction TermsRemoved =
        new("costs.supplier-terms.removed", "Условия поставщика сняты", InvoiceRead);

    public IReadOnlyList<ModuleActivityAction> Actions =>
        [Created, Changed, Confirmed, DueByHand, DueByRule, ScanAttached, Recognized, OrganizationCreated, LinesChanged, MatchesRemembered, MatchPointed, MatchForgotten, MatchesForgotten, TermsSet, TermsRemoved, Parsed, Draft, Rejected, Deleted, AllocationChanged,
         Paid, Unpaid, PaymentDescribed, Corrected, ToleranceAligned, ArticleCreated, ArticleRenamed, ArticleDeleted,
         ArticleArchived, ArticleUnarchived];
}
