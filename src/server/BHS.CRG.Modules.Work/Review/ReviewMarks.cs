using System.Linq.Expressions;
using BHS.CRG.Modules.Work.Data;

namespace BHS.CRG.Modules.Work.Review;

/// <summary>
/// Из чего считается пометка: журнал отчёта, назначения и учётная запись читающего. Наборы даёт файл чтения
/// (<see cref="ReportReads" />): каталог их не открывает сам — изолированная запись читается только там.
/// </summary>
/// <param name="Journal">Журнал отчётов (<c>work-14</c>).</param>
/// <param name="Assignments">Назначения (<c>work-11</c>).</param>
/// <param name="Viewer">Учётная запись читающего; <c>null</c> — система.</param>
public sealed record ReviewMarkSources(
    IQueryable<ReportJournalEntry> Journal, IQueryable<Assignment> Assignments, Guid? Viewer);

/// <summary>Пометка в ответе: код для отбора и счётчика, текст — для человека.</summary>
public sealed record ReviewMarkDto(string Code, string Text);

/// <summary>
/// Пометка очереди приёмки (ТЗ WORK-28; change <c>work-16-review</c>).
/// </summary>
/// <param name="Code">Постоянный код: им клиент отбирает и читает счётчик. Текст может поменяться, код — нет.</param>
/// <param name="Text">Текст для человека.</param>
/// <param name="Source">Из каких данных пометка считается. Код без источника не регистрируется: пометку, за которой
/// нет данных, нельзя отличить от «таких отчётов нет».</param>
/// <param name="Has">Предикат отчёта для запроса — им считаются и пометки строки, и счётчик по всему отбору.
/// <c>null</c> — данных ещё нет, и пометка честно равна нулю: источник названо в <paramref name="Source" />.</param>
public sealed record ReviewMark(
    string Code, string Text, string Source, Func<ReviewMarkSources, Expression<Func<Report, bool>>>? Has)
{
    public ReviewMarkDto Dto => new(Code, Text);
}

/// <summary>
/// Каталог пометок очереди приёмки (ТЗ WORK-28, MOB-21, WORK-34; change <c>work-16-review</c>) — один, на сервере, и
/// открыт для дописывания: <c>crew-mismatch</c>, <c>accepted-changed</c> и <c>fields-missing</c> добавят <c>work-18</c>,
/// <c>work-19</c> и <c>work-24</c>.
///
/// <para><b>Пометка считается запросом, а не на клиенте.</b> Счётчики очереди — по всему отбору, а не по странице:
/// предикат одной пометки даёт и её строки, и её число. Клиент знает только коды (<c>WORK_MARK_CODES</c> в
/// <c>src/client/src/shared/api/work.ts</c>; сверяет <c>ReviewMarkCatalogTests</c>).</para>
///
/// <para><b>Пометки — о текущей подаче.</b> Правка после подачи считается с момента ПОСЛЕДНЕЙ подачи: исправления по
/// возврату — не подозрение (<c>work-15</c>, PR #1987). Правка менеджером и «ввёл и принял один человек» — факты отчёта,
/// а не подачи: они остаются.</para>
/// </summary>
public static class ReviewMarks
{
    public const string EditedAfterSubmit = "edited-after-submit";
    public const string SamePerson = "same-person";
    public const string ManagerEdit = "manager-edit";
    public const string SiteNotAssigned = "site-not-assigned";
    public const string PhotosPending = "photos-pending";
    public const string LateDelivery = "late-delivery";

    /// <summary>Все пометки — в порядке, в котором их показывает очередь.</summary>
    public static IReadOnlyList<ReviewMark> All { get; } =
    [
        new(EditedAfterSubmit, "правка после подачи",
            "журнал отчёта (work-14): правка исполнителем после последней подачи",
            // «После последней подачи» — по номеру журнала, а не по часам: время подачи и правки бывает временем
            // устройства, а номер — порядок записи на сервере.
            s => r => s.Journal.Any(e => e.ReportId == r.Id && e.Kind == ReportJournalKinds.Updated && e.AfterSubmission
                                         && !s.Journal.Any(x => x.ReportId == r.Id && x.Seq > e.Seq
                                                                && (x.Kind == ReportJournalKinds.Submitted
                                                                    || x.Kind == ReportJournalKinds.AutoSubmitted)))),
        // «Ввёл и принял один человек» (WORK-31.1): у поданного — то же правило, что ReportViews.AcceptWouldMark (ввёл
        // читающий), у принятого — запись журнала, сделанная приёмкой.
        new(SamePerson, "ввёл и принял один человек",
            "автор ввода отчёта (work-13) и журнал отчёта: запись «ввёл и принял один человек»",
            s => r => (r.Status == ReportStatus.Submitted && s.Viewer != null && r.EnteredByUserId == s.Viewer)
                      || s.Journal.Any(e => e.ReportId == r.Id && e.Kind == ReportJournalKinds.SamePerson)),
        new(ManagerEdit, "правка менеджером",
            "журнал отчёта: правка строк менеджером (work-16)",
            s => r => s.Journal.Any(e => e.ReportId == r.Id && e.Kind == ReportJournalKinds.ManagerEdited)),
        // То же правило, что assignedOnDate ответа отчёта (ReportViews): назначение любого вида на дату отчёта.
        new(SiteNotAssigned, "стройка не назначена на дату",
            "назначения (work-11): у сотрудника нет назначения на стройку отчёта на его дату",
            s => r => !s.Assignments.Any(a => a.EmployeeId == r.EmployeeId && a.ConstructionId == r.ConstructionId
                                              && a.DateFrom <= r.Date && (a.DateTo == null || r.Date <= a.DateTo))),
        new(PhotosPending, "фото ещё догружаются",
            "фото отчёта — work-17; до него фото нет вовсе, и догружать нечего", null),
        new(LateDelivery, "поздняя доставка",
            "доставка отчёта с телефона — этап 5 (WORK-58); в вебе отчёт доставляется сразу", null),
    ];

    /// <summary>Пометка по коду; неизвестный код — <c>null</c>.</summary>
    public static ReviewMark? Of(string code) => All.FirstOrDefault(m => m.Code == code);
}
