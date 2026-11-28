using BHS.CRG.Modules.Data;

namespace BHS.CRG.Modules.Work.Data;

/// <summary>Что случилось с отчётом.</summary>
public enum ReportChangeKind
{
    Created,
    Updated,
    Deleted,

    /// <summary>Подан — человеком или автоподачей (<c>work-15</c>): курсор отдаёт телефону новый статус.</summary>
    Submitted,

    /// <summary>Принят менеджером (<c>work-16</c>).</summary>
    Accepted,

    /// <summary>Возвращён на правку с комментарием (<c>work-16</c>): телефон узнаёт о возврате курсором.</summary>
    Returned,
    Corrected,
    FieldReceipt,
}

/// <summary>
/// Запись протокола изменений отчёта (ТЗ MOB-15, MOB-16, MOB-17; change <c>work-13-report-model</c>) — узкая, без
/// содержимого: «применён ли этот <c>changeId</c>», «что изменилось после номера N» и «когда по часам устройства и
/// когда по часам сервера». Содержимое правки пишет журнал отчёта (<c>work-14</c>).
///
/// <para><b>Только дописывается</b> (<see cref="IAppendOnly" />): протокол — свидетельство для повтора. Правленая
/// задним числом запись ответила бы повтору не тем, что было применено.</para>
///
/// <para><b>Ключ — номер последовательности</b> <c>work.report_seq</c>, общей для всех отчётов: курсор
/// <c>since</c> читает протокол по нему, и время устройства на порядок не влияет (ТЗ MOB-16) — часы телефона
/// уходят назад, а номер растёт.</para>
///
/// <para><b>Ссылка на отчёт — без внешнего ключа</b>: удалённый черновик остаётся в протоколе записью
/// <see cref="ReportChangeKind.Deleted" />, и по ней курсор называет его клиенту удалённым.</para>
/// </summary>
public sealed class ReportChange : IIsolatedRecord, IAppendOnly
{
    private ReportChange() { }

    /// <summary>Номер в серверной последовательности — он же ключ записи.</summary>
    public long Seq { get; private set; }

    public Guid ReportId { get; private set; }

    /// <summary>
    /// Сотрудник отчёта — копией: после удаления черновика отчёта нет, а курсор «свои изменения» обязан знать, чьи
    /// они. Держащей ссылкой не служит — сотрудника держит сам отчёт.
    /// </summary>
    public Guid EmployeeId { get; private set; }

    /// <summary>Стройка отчёта на момент изменения — для курсора по стройкам (ввод за сотрудника, <c>work-16</c>).</summary>
    public Guid ConstructionId { get; private set; }

    /// <summary>Идентификатор изменения от клиента (ТЗ MOB-15): повтор с ним же не применяется второй раз.</summary>
    public Guid ChangeId { get; private set; }

    public ReportChangeKind Kind { get; private set; }

    /// <summary>
    /// Время действия по часам устройства (ТЗ MOB-17) — заявление, не факт; <c>null</c> — устройство не назвало.
    /// Расхождение с <see cref="ServerTime" /> не ведёт к отказу.
    /// </summary>
    public DateTimeOffset? DeviceTime { get; private set; }

    /// <summary>Время получения сервером — факт.</summary>
    public DateTimeOffset ServerTime { get; private set; }

    /// <summary>
    /// Версия отчёта после изменения — та, что ушла в ответе: свидетельство «какую версию получил клиент». Повтор
    /// применённой правки отвечает ТЕКУЩЕЙ версией вместе с текущим телом — версия отсюда при более поздней правке
    /// разошлась бы с телом (ревью PR #1976).
    /// </summary>
    public string? Version { get; private set; }

    public static ReportChange Of(
        long seq, Report report, Guid changeId, ReportChangeKind kind, DateTimeOffset? deviceTime, DateTimeOffset serverTime,
        string? version) => new()
    {
        Seq = seq, ReportId = report.Id, EmployeeId = report.EmployeeId, ConstructionId = report.ConstructionId,
        // К UTC: телефон присылает время со своим поясом («+03:00»), а колонка timestamptz принимает только нулевое
        // смещение — без приведения правка с местным временем отвергалась бы ошибкой сервера.
        ChangeId = changeId, Kind = kind, DeviceTime = deviceTime?.ToUniversalTime(), ServerTime = serverTime.ToUniversalTime(),
        Version = version,
    };

    /// <summary>
    /// Вид кодом колонки и ответа: <c>created</c> | <c>updated</c> | <c>deleted</c> | <c>submitted</c> | <c>accepted</c> |
    /// <c>returned</c>.
    /// </summary>
    public static string KindCode(ReportChangeKind kind) => kind.ToString().ToLowerInvariant();
}
