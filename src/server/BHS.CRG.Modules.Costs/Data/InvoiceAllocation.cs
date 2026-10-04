namespace BHS.CRG.Modules.Costs.Data;

/// <summary>
/// Часть разноски строки счёта (задача F1 этапа 2, issue #1085, ТЗ COST-10, COST-11, COST-13): сколько
/// из строки ушло на стройку и раздел — или, с F3 (issue #1087), на статью вне строек.
///
/// <para><b>Разносится строка, а не счёт</b> (ТЗ COST-11): в одном счёте позиции идут на разные объекты,
/// и разноска «по документу» вынуждала бы делить пропорционально и врать в мелочах.</para>
///
/// <para><b>Хранится ТО, ЧТО ВВЁЛ ЧЕЛОВЕК, — и только это.</b> У строки с количеством это количество
/// части, а сумма её считается (<see cref="AllocationMath" />) — количество проверяемо на объекте, сумма
/// из него следует. У строки без количества (доставка, услуги) это сумма части. Храни мы посчитанную
/// сумму рядом с количеством, у неё стало бы два источника: правка цены строки меняла бы одно и не
/// меняла другое, и расходились бы они молча — ровно те копейки, ради которых задача заводилась.</para>
///
/// <para>⚠️ <b>Ссылки на стройку и раздел — идентификаторами, без внешнего ключа</b>, как поставщик у
/// счёта: стройки живут в схеме ядра. Удалённую стройку показывает чтение
/// (<c>AllocationPartView.TargetLost</c>), а «разобран» с такой частью не проходит.</para>
/// </summary>
public sealed class InvoiceAllocation
{
    /// <summary>Для EF.</summary>
    private InvoiceAllocation() { }

    public Guid Id { get; private set; }

    /// <summary>
    /// Счёт — рядом со строкой, хотя выводится из неё: разноску читают счётом целиком, и без этой
    /// колонки каждое чтение шло бы через строки.
    /// </summary>
    public Guid InvoiceId { get; private set; }

    /// <summary>
    /// Строка счёта; <c>null</c> — часть <b>счёта целиком</b> (задача F2, issue #1086, ТЗ COST-11): счёт
    /// без строк разносится суммой на сумму к оплате. Такая часть хранит только сумму, и живёт она, пока
    /// строк нет: появились строки — разноска пересчитывается по ним, а части счёта уходят.
    /// </summary>
    public Guid? LineId { get; private set; }

    /// <summary>
    /// Порядок части в строке. Хранится, потому что на него опирается арифметика: копейки округления
    /// и расхождение с суммой к оплате уходят в ПОСЛЕДНЮЮ часть (ТЗ COST-13), и «последняя» обязана
    /// быть одной и той же на каждом чтении.
    /// </summary>
    public int Ordinal { get; private set; }

    /// <summary>Стройка; <c>null</c> — часть легла на статью вне строек (<see cref="ArticleId" />).</summary>
    public Guid? ConstructionId { get; private set; }

    /// <summary>Раздел стройки — необязателен (ТЗ COST-10), и только у части на стройку.</summary>
    public Guid? SectionId { get; private set; }

    /// <summary>
    /// Статья вне строек — «Склад», «Общие расходы» (задача F3, issue #1087, ТЗ COST-10.1): запись
    /// справочника модуля в общей таблице ядра, тоже идентификатором без внешнего ключа.
    ///
    /// <para>⚠️ Цель части — стройка ИЛИ статья, <b>ровно одно</b>, и держит это ограничение базы, а не
    /// один разбор: часть с обеими целями легла бы в затраты дважды, а без цели — никуда.</para>
    /// </summary>
    public Guid? ArticleId { get; private set; }

    /// <summary>Куда легла часть — одним значением: по нему части сравниваются и группируются.</summary>
    public AllocationTarget Target => new(ConstructionId, SectionId, ArticleId);

    /// <summary>Количество части — у строки, разносимой по количеству.</summary>
    public decimal? Quantity { get; private set; }

    /// <summary>Сумма части — ТОЛЬКО у строки без количества. У строки с количеством сумма считается.</summary>
    public decimal? Amount { get; private set; }

    /// <summary>
    /// Учётная дата доли — день, которым её деньги входят в затраты (C5, issue #1082, ТЗ COST-16): дата
    /// платежа, если период её стройки открыт, иначе первый открытый день этой стройки. Пусто у
    /// неоплаченного счёта.
    ///
    /// <para><b>Хранится, а не считается на чтении:</b> посчитанная от сегодняшних границ, она менялась
    /// бы с каждым закрытием и отменой закрытия, и отчёт за сентябрь, снятый вчера, не сошёлся бы с
    /// сегодняшним.</para>
    ///
    /// <para>⚠️ <see cref="UpdatedAt" /> она НЕ двигает: это отметка версии РАЗНОСКИ, по ней матрица
    /// отличает свежий набор от устаревшего, а разноску оплата не меняет.</para>
    /// </summary>
    public DateOnly? AccountingOn { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Положить учётную дату — её считает <c>PaymentPosting</c>, одним местом на все пути.</summary>
    public void Post(DateOnly? accountingOn) => AccountingOn = accountingOn;

    public static InvoiceAllocation Create(Guid invoiceId, Guid? lineId) => new()
    {
        Id = Guid.CreateVersion7(),
        InvoiceId = invoiceId,
        LineId = lineId,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Положить часть разом — части приходят набором строки, как и сами строки.</summary>
    public void Apply(int ordinal, AllocationValues values)
    {
        Ordinal = ordinal;
        ConstructionId = values.Target.ConstructionId;
        SectionId = values.Target.SectionId;
        ArticleId = values.Target.ArticleId;
        Quantity = values.Quantity;
        Amount = values.Amount;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public AllocationValues Snapshot() => new(Target, Quantity, Amount);
}

/// <summary>Значения части — разобранные, ровно то, что ложится в колонки.</summary>
public sealed record AllocationValues(AllocationTarget Target, decimal? Quantity, decimal? Amount);

/// <summary>
/// Куда ложится часть разноски: стройка (и, необязательно, её раздел) ИЛИ статья вне строек (задача F3,
/// issue #1087, ТЗ COST-10.1). Ровно одно из двух — это держат разбор (<c>InvoiceAllocations.Values</c>)
/// и ограничение базы.
///
/// <para>Статья — не «ещё одна стройка»: «Склад» стройкой не заводится, иначе он всплыл бы в назначениях
/// монтажников, сметах и комплектах документов (ТЗ COST-10.1).</para>
/// </summary>
public readonly record struct AllocationTarget(Guid? ConstructionId, Guid? SectionId, Guid? ArticleId)
{
    public static AllocationTarget Site(Guid construction, Guid? section = null) => new(construction, section, null);

    public static AllocationTarget Article(Guid article) => new(null, null, article);
}
