using BHS.CRG.Modules.Data;

namespace BHS.CRG.Modules.Work.Data;

/// <summary>
/// Недоставленная сводка о поданных (ТЗ WORK-56; change <c>work-16-review</c>): публикация менеджеру
/// <see cref="UserId" /> упала, и эта подача отчёта уйдёт ему следующим тактом — ему одному. Отметка подачи
/// (<see cref="ReviewDigestMark" />) при этом ставится сразу: менеджеры, кому сводка ушла, второй раз её не получают.
///
/// <para>Строка удаляется, когда сводка дошла, отчёт решён или подан заново (тогда подачу несёт новая сводка), либо
/// менеджер больше не менеджер этой стройки.</para>
///
/// <para><b>Изолированная запись</b> (<see cref="IIsolatedRecord" />): называет отчёты и читается только файлом чтения
/// отчёта.</para>
/// </summary>
public sealed class ReviewDigestRetry : IIsolatedRecord
{
    private ReviewDigestRetry() { }

    /// <summary>Учётная запись менеджера, которому сводка не ушла.</summary>
    public Guid UserId { get; private set; }

    public Guid ReportId { get; private set; }

    /// <summary>Момент подачи, о которой сводка, — тот же, что у отметки.</summary>
    public DateTimeOffset SubmittedAt { get; private set; }

    public static ReviewDigestRetry Of(Guid userId, Guid reportId, DateTimeOffset submittedAt) => new()
    {
        UserId = userId, ReportId = reportId, SubmittedAt = submittedAt.ToUniversalTime(),
    };
}
