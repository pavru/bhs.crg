namespace BHS.CRG.Modules.Costs.Data;

/// <summary>
/// Часть разноски строки счёта (задача F1 этапа 2, issue #1085, ТЗ COST-10, COST-11, COST-13): сколько
/// из строки ушло на стройку и раздел.
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

    public Guid LineId { get; private set; }

    /// <summary>
    /// Порядок части в строке. Хранится, потому что на него опирается арифметика: копейки округления
    /// и расхождение с суммой к оплате уходят в ПОСЛЕДНЮЮ часть (ТЗ COST-13), и «последняя» обязана
    /// быть одной и той же на каждом чтении.
    /// </summary>
    public int Ordinal { get; private set; }

    public Guid ConstructionId { get; private set; }

    /// <summary>Раздел стройки — необязателен (ТЗ COST-10).</summary>
    public Guid? SectionId { get; private set; }

    /// <summary>Количество части — у строки, разносимой по количеству.</summary>
    public decimal? Quantity { get; private set; }

    /// <summary>Сумма части — ТОЛЬКО у строки без количества. У строки с количеством сумма считается.</summary>
    public decimal? Amount { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static InvoiceAllocation Create(Guid invoiceId, Guid lineId) => new()
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
        ConstructionId = values.ConstructionId;
        SectionId = values.SectionId;
        Quantity = values.Quantity;
        Amount = values.Amount;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public AllocationValues Snapshot() => new(ConstructionId, SectionId, Quantity, Amount);
}

/// <summary>Значения части — разобранные, ровно то, что ложится в колонки.</summary>
public sealed record AllocationValues(Guid ConstructionId, Guid? SectionId, decimal? Quantity, decimal? Amount);
