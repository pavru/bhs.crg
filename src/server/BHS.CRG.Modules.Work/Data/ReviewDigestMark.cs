using BHS.CRG.Modules.Data;

namespace BHS.CRG.Modules.Work.Data;

/// <summary>
/// Отметка «эта подача отчёта вошла в сводку менеджерам» (ТЗ WORK-56; change <c>work-16-review</c>). Ключ — отчёт и
/// момент подачи: повторно поданный возвращённый несёт новый момент и снова ждёт сводки, а прежняя отметка остаётся.
///
/// <para><b>Отдельной таблицей, а не колонкой отчёта</b>: запись колонки сдвинула бы версию строки отчёта
/// (<c>xmin</c>), и правка по только что прочитанной версии получила бы 409 «отчёт изменили» без видимой правки.</para>
///
/// <para><b>Изолированная запись</b> (<see cref="IIsolatedRecord" />): называет отчёты, и читается только файлом
/// чтения отчёта. Только дописывается (<see cref="IAppendOnly" />).</para>
/// </summary>
public sealed class ReviewDigestMark : IIsolatedRecord, IAppendOnly
{
    private ReviewDigestMark() { }

    public Guid ReportId { get; private set; }

    /// <summary>Момент подачи, вошедшей в сводку, — тот же, что у отчёта на момент сводки.</summary>
    public DateTimeOffset SubmittedAt { get; private set; }

    /// <summary>Когда сводка опубликована.</summary>
    public DateTimeOffset DigestedAt { get; private set; }

    public static ReviewDigestMark Of(Guid reportId, DateTimeOffset submittedAt, DateTimeOffset digestedAt) => new()
    {
        ReportId = reportId, SubmittedAt = submittedAt.ToUniversalTime(), DigestedAt = digestedAt.ToUniversalTime(),
    };
}
