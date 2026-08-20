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
    private readonly HashSet<int> pagesWithoutAnswer = [];

    /// <summary>Листов без ответа — для уведомления.</summary>
    public int FailedPages { get; private set; }

    /// <summary>
    /// ПЕРВАЯ причина отказа. Первая, а не последняя: она объясняет, с чего прогон посыпался, тогда
    /// как последняя чаще всего лишь следствие.
    /// </summary>
    public string? FirstReason { get; private set; }

    /// <summary>Прекращать ли прогон: движок молчит подряд слишком долго.</summary>
    public bool ShouldStop { get; private set; }

    /// <summary>
    /// Листы, по которым ответа не было, — чтобы признак дошёл до данных, а не только до уведомления
    /// (issue #803). Уведомление гаснет, а таблица с пустыми строками живёт в наборе и уезжает в
    /// реквизиты: без пометки на самих страницах «модель не ответила» становится неотличимо от
    /// «на листе нечего распознавать».
    /// </summary>
    public IReadOnlySet<int> PagesWithoutAnswer => pagesWithoutAnswer;

    /// <summary>Лист остался без ответа: движок промолчал (<paramref name="silent" />) либо отказал.</summary>
    /// <param name="pageIndex">Индекс листа, если он известен вызывающему, — для пометки на данных.</param>
    public void PageFailed(Exception ex, bool silent, int? pageIndex = null)
    {
        FailedPages++;
        FirstReason ??= ex.Message;
        if (pageIndex is { } idx) pagesWithoutAnswer.Add(idx);
        if (!silent) { silentInARow = 0; return; }
        if (++silentInARow >= silenceLimit) ShouldStop = true;
    }

    /// <inheritdoc cref="PageFailed(Exception, bool, int?)" />
    public void PageFailed(Exception ex, int? pageIndex = null)
        => PageFailed(ex, ex is RecognitionSilentException, pageIndex);

    /// <summary>
    /// Лист, до которого прогон не дошёл: работу прекратили раньше. В счётчик отказов он НЕ идёт —
    /// движок по нему не ошибался, — но на данных помечается так же: полей нет и взяться им неоткуда.
    /// </summary>
    public void MarkNotAttempted(int pageIndex)
    {
        if (pagesWithoutAnswer.Add(pageIndex)) NotAttemptedPages++;
    }

    /// <summary>
    /// Сколько листов остались необработанными из-за прекращения прогона. Отдельно от
    /// <see cref="FailedPages" /> и в тексте, и в счёте: движок по ним не ошибался, но и данных по
    /// ним нет — а сообщение, называющее только неотвеченные, на прогоне, прекращённом третьим
    /// листом из двухсот, говорило бы про три при ста девяноста семи пустых строках.
    /// </summary>
    public int NotAttemptedPages { get; private set; }

    /// <summary>Лист распознан — счёт молчаний подряд начинается заново.</summary>
    public void PageSucceeded() => silentInARow = 0;

    /// <summary>
    /// Чем объяснить прекращение прогона. Прогон при этом НЕ отменяется: распознанные листы
    /// сохраняются, иначе одна потеря данных сменилась бы другой — человек, у которого движок замолк
    /// на пятидесятом листе из двухсот, лишился бы и первых сорока девяти.
    /// </summary>
    public string StopReason => $"движок перестал отвечать: {silentInARow} листа подряд без ответа. {FirstReason}";
}
