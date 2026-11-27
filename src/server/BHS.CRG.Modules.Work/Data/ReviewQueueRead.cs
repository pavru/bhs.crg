using BHS.CRG.Modules.Work.Submission;

namespace BHS.CRG.Modules.Work.Data;

/// <summary>Отбор очереди приёмки; не названное — не отбирает.</summary>
public sealed record ReviewQueueFilter(
    Guid? ConstructionId, Guid? EmployeeId, DateOnly? From, DateOnly? To, ReviewMark? Mark)
{
    /// <summary>Отбор назван хоть чем-то — пустота тогда «отбор исключил всё», а не «ждущих нет».</summary>
    public bool Any => ConstructionId is not null || EmployeeId is not null || From is not null || To is not null
                       || Mark is not null;
}

/// <summary>
/// Отчёт страницы очереди, как его прочёл файл чтения: версия строки, число строк, коды пометок и число недогруженных
/// фото (<c>work-17</c>) — им пометка <c>photos-pending</c> называет, сколько ждут.
/// </summary>
public sealed record ReviewQueueItem(
    Report Report, string Version, int Lines, IReadOnlyList<string> Marks, int PhotosPending, int MissingFields,
    RequiredGaps Required);

/// <summary>Страница очереди из файла чтения.</summary>
public sealed record ReviewQueueRead(
    IReadOnlyList<ReviewQueueItem> Items, int Total, IReadOnlyDictionary<string, int> Counts,
    IReadOnlyList<Guid> PendingEmployeeIds);
