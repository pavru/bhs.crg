using BHS.CRG.Application.QualityDocs;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Что пошло не так на постраничном прогоне: сколько листов осталось без ответа, почему и не пора ли
/// прекращать (issue #802).
///
/// Отдельным объектом, потому что постраничных циклов в сервисе три — распознавание набора,
/// распознавание источника и точечное перераспознавание документа, — и счётчики до сих пор были
/// ровно у одного. Положив правило внутрь того цикла, где оно понадобилось, мы бы завели третью
/// копию поведения «отказ на странице» и оставили два цикла молчаливыми: то самое «закрыв дверь,
/// перечисли остальные», применённое к собственной правке.
/// </summary>
public class PageFailureTracker(int silenceLimit = PageFailureTracker.DefaultSilenceLimit)
{
    /// <summary>
    /// Сколько молчаний подряд считать приговором движку. Три, а не одно: молчание СТРАНИЧНОЕ —
    /// нечитаемый лист среди шестнадцати нормальных встречается и ничего не говорит о движке. Но
    /// движок, молчащий лист за листом, отвечать уже не начнёт, и на альбоме в двести листов это
    /// часы впустую.
    /// </summary>
    public const int DefaultSilenceLimit = 3;

    private int silentInARow;

    /// <summary>Листов без ответа — для уведомления.</summary>
    public int FailedPages { get; private set; }

    /// <summary>
    /// ПЕРВАЯ причина отказа. Первая, а не последняя: она объясняет, с чего прогон посыпался, тогда
    /// как последняя чаще всего лишь следствие.
    /// </summary>
    public string? FirstReason { get; private set; }

    /// <summary>Прекращать ли прогон: движок молчит подряд слишком долго.</summary>
    public bool ShouldStop { get; private set; }

    /// <summary>Лист остался без ответа: движок промолчал (<paramref name="silent" />) либо отказал.</summary>
    public void PageFailed(Exception ex, bool silent)
    {
        FailedPages++;
        FirstReason ??= ex.Message;
        if (!silent) { silentInARow = 0; return; }
        if (++silentInARow >= silenceLimit) ShouldStop = true;
    }

    /// <inheritdoc cref="PageFailed(Exception, bool)" />
    public void PageFailed(Exception ex) => PageFailed(ex, ex is RecognitionSilentException);

    /// <summary>Лист распознан — счёт молчаний подряд начинается заново.</summary>
    public void PageSucceeded() => silentInARow = 0;

    /// <summary>
    /// Чем объяснить прекращение прогона. Прогон при этом НЕ отменяется: распознанные листы
    /// сохраняются, иначе одна потеря данных сменилась бы другой — человек, у которого движок замолк
    /// на пятидесятом листе из двухсот, лишился бы и первых сорока девяти.
    /// </summary>
    public string StopReason => $"движок перестал отвечать: {silentInARow} листа подряд без ответа. {FirstReason}";
}
