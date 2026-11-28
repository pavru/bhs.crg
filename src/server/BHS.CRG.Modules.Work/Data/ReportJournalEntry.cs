using BHS.CRG.Modules.Data;

namespace BHS.CRG.Modules.Work.Data;

/// <summary>
/// Виды записей журнала отчёта (change <c>work-14-report-journal</c>). Каталог ОТКРЫТ: приёмка, возврат и правка
/// менеджером (<c>work-16</c>) допишут свои виды тем change, который их приносит. Коды заведения, правки и удаления —
/// те же, что у вида протокола <see cref="ReportChangeKind" />: у записи журнала и записи протокола один номер, и вид
/// у них один.
/// </summary>
public static class ReportJournalKinds
{
    public const string Created = "created";

    public const string Updated = "updated";

    public const string Deleted = "deleted";

    /// <summary>Подан человеком — сотрудником или менеджером за сотрудника (<c>work-15</c>).</summary>
    public const string Submitted = "submitted";

    /// <summary>Подан автоматически по концу окна, от имени системы (<c>work-15</c>, ТЗ WORK-41).</summary>
    public const string AutoSubmitted = "auto-submitted";

    public const string FieldReceipt = "field-receipt";

    /// <summary>Принят менеджером (<c>work-16</c>, ТЗ WORK-13).</summary>
    public const string Accepted = "accepted";

    /// <summary>Возвращён на правку; что исправить — в <see cref="ReportJournalEntry.Comment" /> (<c>work-16</c>).</summary>
    public const string Returned = "returned";

    public const string CrewApproved = "crew-approved";

    public const string Corrected = "corrected";

    public const string AcceptedChanged = "accepted-changed";

    /// <summary>
    /// Правка менеджером поданного отчёта адресом ввода за сотрудника (<c>work-16</c>, ТЗ WORK-14) — запасной ход:
    /// протокол пишет её обычной правкой, а журнал называет отдельно, и по этой записи отчёт несёт пометку.
    /// </summary>
    public const string ManagerEdited = "manager-edited";

    /// <summary>
    /// «Ввёл и принял один человек» (<c>work-16</c>, ТЗ WORK-31.1) — отдельная запись рядом с приёмкой: отчёт она не
    /// меняет, записи протокола у неё нет.
    /// </summary>
    public const string SamePerson = "same-person";

    /// <summary>
    /// Уведомить о возврате некого: у сотрудника нет учётной записи (<c>work-16</c>, ТЗ WORK-56). Журнал говорит это, а
    /// не молчит; записи протокола нет.
    /// </summary>
    public const string NotifyNone = "notify-none";

    /// <summary>Виды, которые бывают «правкой после подачи» (ТЗ WORK-11): правки содержимого, а не переходы статуса.</summary>
    public static bool IsEdit(string kind) => kind is Updated or ManagerEdited or AcceptedChanged;
}

/// <summary>
/// Запись журнала отчёта (ТЗ WORK-11, WORK-12, WORK-31, WORK-51; change <c>work-14-report-journal</c>): что
/// изменилось в отчёте — поле, прежнее и новое значение, — кто ввёл, за кого, когда по часам устройства и когда по
/// часам сервера. Этим журналом менеджер и заказчик потом проверяют первичку.
///
/// <para><b>Только дописывается</b> (<see cref="IAppendOnly" />): правку и удаление загруженной записи отвергает
/// контекст, запись мимо отслеживания (<c>ExecuteUpdate</c>, сырой SQL) — перепись <c>WorkJournalWritePathTests</c>,
/// второе место записи — <c>WorkJournalInventoryTests</c>. Не правится никем, в том числе администратором.</para>
///
/// <para><b>Пишется одним сохранением с протоколом</b> (<see cref="ReportChange" />) и в одной транзакции с правкой
/// отчёта: нет записи — нет и правки (ТЗ WORK-51). Журнал ядра для этого не годится — он пишется после действия, без
/// общей транзакции.</para>
///
/// <para><b>Изолированная запись</b> (<see cref="IIsolatedRecord" />): журнал — часть отчёта, и читается он только
/// файлом чтения отчёта с охватом читателя.</para>
///
/// <para><b>Ссылка на отчёт — без внешнего ключа</b>: удалённый черновик оставляет в журнале запись
/// <see cref="ReportJournalKinds.Deleted" /> с тем, что было удалено.</para>
/// </summary>
public sealed class ReportJournalEntry : IIsolatedRecord, IAppendOnly
{
    private ReportJournalEntry() { }

    public Guid Id { get; private set; }

    public Guid ReportId { get; private set; }

    /// <summary>Идентификатор изменения от клиента (ТЗ MOB-15) — тот же, что у записи протокола.</summary>
    public Guid ChangeId { get; private set; }

    /// <summary>Номер в серверной последовательности <c>work.report_seq</c> — тот же, что у записи протокола.</summary>
    public long Seq { get; private set; }

    /// <summary>Вид действия — код из открытого каталога <see cref="ReportJournalKinds" />.</summary>
    public string Kind { get; private set; } = "";

    /// <summary>Автор ввода — учётная запись запроса; <c>null</c> — действие системы (автоподача, имя «Система»).</summary>
    public Guid? AuthorId { get; private set; }

    /// <summary>Имя автора снимком: учётную запись могут переименовать или удалить (ТЗ WORK-11).</summary>
    public string AuthorName { get; private set; } = "";

    /// <summary>Исполнитель работ — сотрудник отчёта (ТЗ WORK-31).</summary>
    public Guid ExecutorEmployeeId { get; private set; }

    /// <summary>Имя исполнителя снимком.</summary>
    public string ExecutorName { get; private set; } = "";

    /// <summary>
    /// Автор вводит за другого (ТЗ WORK-31): сотрудник учётной записи автора — не сотрудник отчёта. «Вводит Иванов
    /// за Петрова».
    /// </summary>
    public bool EnteredFor { get; private set; }

    /// <summary>Время действия по часам устройства (ТЗ MOB-17) — заявление; <c>null</c> — устройство не назвало.</summary>
    public DateTimeOffset? DeviceTime { get; private set; }

    /// <summary>Время доставки на сервер — факт.</summary>
    public DateTimeOffset ServerTime { get; private set; }

    /// <summary>Правка после подачи (ТЗ WORK-11): время действия позже момента подачи отчёта.</summary>
    public bool AfterSubmission { get; private set; }

    /// <summary>
    /// Что изменилось — JSON-массив <c>[{ line, field, before, after }]</c>: только изменённые поля; <c>line</c> —
    /// идентификатор строки, у шапки — <c>null</c>; сведения строки — по ключам (<c>details.&lt;ключ&gt;</c>).
    /// </summary>
    public string Changes { get; private set; } = "[]";

    /// <summary>
    /// Слова человека к действию (<c>work-16</c>): у возврата — что исправить, у «уведомить некого» — почему. Хранится
    /// в журнале, а не в отчёте: следующий возврат затёр бы поле отчёта, а свидетельство обязано остаться.
    /// </summary>
    public string? Comment { get; private set; }

    /// <summary>Собрать запись. Зовёт только место записи — <see cref="ReportJournal" /> (перепись).</summary>
    internal static ReportJournalEntry Of(
        Guid reportId, Guid employeeId, string kind, Guid changeId, long seq, ReportJournalActor author, string executorName,
        DateTimeOffset? deviceTime, DateTimeOffset serverTime, bool afterSubmission, string changes, string? comment) => new()
    {
        Id = Guid.NewGuid(), ReportId = reportId, ChangeId = changeId, Seq = seq, Kind = kind,
        AuthorId = author.UserId, AuthorName = author.Name, ExecutorEmployeeId = employeeId,
        ExecutorName = executorName,
        // Система за сотрудника не вводит: автоподача — её действие, а не ввод (work-15).
        EnteredFor = kind != ReportJournalKinds.CrewApproved && author.UserId is not null && author.EmployeeId != employeeId,
        // К UTC — как у протокола: колонка timestamptz принимает только нулевое смещение.
        DeviceTime = deviceTime?.ToUniversalTime(), ServerTime = serverTime.ToUniversalTime(),
        AfterSubmission = afterSubmission, Changes = changes, Comment = comment,
    };
}

/// <summary>
/// Кто действует: учётная запись, её имя на момент действия и её сотрудник — по нему журнал отличает «вводит за
/// сотрудника» от своей правки (ТЗ WORK-31). <c>EmployeeId</c> — <c>null</c>, если учётная запись с сотрудником не
/// связана.
/// </summary>
public sealed record ReportJournalActor(Guid? UserId, string Name, Guid? EmployeeId);
