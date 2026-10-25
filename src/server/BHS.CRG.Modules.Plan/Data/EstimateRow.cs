namespace BHS.CRG.Modules.Plan.Data;

/// <summary>
/// Строка редакции сметы — класс C: колонки-скелет плюс сметное наполнение по схеме типа
/// <c>ПозицияСметы</c> (change <c>plan-06-estimate-model</c>, ТЗ PLAN-3, PLAN-4, TYPE-16, CORE-15).
///
/// <para><b>Колонками — всё, на что опирается код</b>: позиция перечня, вид работы, объём и единица —
/// план, «Ход работ» и график; раздел ЛСР, шифр и номер — сопоставление и печать; стоимость единицы и
/// итог — КП, итоги и контракт. Положи мы деньги полями схемы, их читал бы JSON, а поле заказчика без
/// пометки «деньги» утекало бы к тому, кому себестоимость не открыта (см. <c>docs/DEV_NOTES.md</c>).</para>
///
/// <para><b>Сметное наполнение</b> (<see cref="Data" />) — расценка, коэффициенты, поля заказчика —
/// отдаётся целиком только с правом <c>plan.estimate.cost</c>: без права ответ не несёт ни одного его
/// поля (ТЗ PLAN-11, PLAN-23).</para>
///
/// <para><b>После импорта строка не правится</b>, кроме полей сопоставления (вид работы, единица,
/// пересчёт объёма, позиция перечня) — их заполняет сопоставление (<c>plan-08</c>). Строка без вида
/// работы или позиции перечня — «не сопоставлена» (<see cref="Unmatched" />) и в план не входит
/// (ТЗ PLAN-4, <see cref="PlanRows" />).</para>
/// </summary>
public sealed class EstimateRow
{
    public const int SectionCodeLength = 100;
    public const int SectionNameLength = 500;
    public const int NumberLength = 50;
    public const int RateCodeLength = 200;
    public const int UnitLength = 100;

    private EstimateRow() { }

    public Guid Id { get; private set; }

    public Guid RevisionId { get; private set; }

    /// <summary>Порядок строки в файле сметы — он же ключ страниц при чтении строк.</summary>
    public int Order { get; private set; }

    /// <summary>Раздел ЛСР, как в файле: код («1») и наименование («Электромонтажные работы»).</summary>
    public string? LsrSectionCode { get; private set; }

    public string? LsrSectionName { get; private set; }

    /// <summary>Номер позиции в разделе, как в файле.</summary>
    public string? NumberInSection { get; private set; }

    /// <summary>Шифр расценки: «ГЭСНм08-02-399-01».</summary>
    public string? RateCode { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Объём расценки КАК В ФАЙЛЕ — в её единице («100 м»); пересчёт — <see cref="Volume" />.</summary>
    public decimal? SourceVolume { get; private set; }

    public string? SourceUnit { get; private set; }

    /// <summary>Вид работы классификатора — запись ядра; <c>null</c> — строка не сопоставлена.</summary>
    public Guid? WorkTypeId { get; private set; }

    /// <summary>Единица классификатора — запись ядра.</summary>
    public Guid? UnitId { get; private set; }

    /// <summary>Множитель пересчёта объёма из единицы расценки в единицу классификатора (D8 зонтика).</summary>
    public decimal? Factor { get; private set; }

    /// <summary>Объём в единице классификатора — то, что суммирует план стройки.</summary>
    public decimal? Volume { get; private set; }

    /// <summary>Позиция перечня работ стройки — порт ядра <c>IModulePlanItems</c>; <c>null</c> — не сопоставлена.</summary>
    public Guid? PlanItemId { get; private set; }

    /// <summary>
    /// Пометка «запомнено» (<c>plan-08</c>, D8 зонтика): запомненное сопоставление шифра, которое подставило
    /// вид работы, единицу и коэффициент; <c>null</c> — сопоставлял человек либо строка не сопоставлена.
    /// Без внешнего ключа нарочно, как у строки счёта: забытое сопоставление строку не меняет, а что с ним
    /// стало, пометка вычисляет на чтении.
    /// </summary>
    public Guid? RateMatchId { get; private set; }

    /// <summary>Стоимость единицы — себестоимость, под <c>plan.estimate.cost</c>.</summary>
    public decimal? UnitCost { get; private set; }

    /// <summary>Итог по строке — себестоимость, под <c>plan.estimate.cost</c>.</summary>
    public decimal? Total { get; private set; }

    /// <summary>Сметное наполнение по схеме <c>ПозицияСметы</c> — JSON-объект, под <c>plan.estimate.cost</c>.</summary>
    public string Data { get; private set; } = "{}";

    /// <summary>
    /// Строка не сопоставлена: нет вида работы или позиции перечня. Такая строка видна в смете, но в
    /// план, «Ход работ» и график не входит (ТЗ PLAN-4) — это правило одно и для чтения строк, и для
    /// <see cref="PlanRows" />.
    /// </summary>
    public bool Unmatched => WorkTypeId is null || PlanItemId is null;

    /// <summary>
    /// Сопоставить строку (<c>plan-08</c>, ТЗ PLAN-6.1): вид работы, единица классификатора, коэффициент и
    /// позиция перечня. Объём плана — объём файла × коэффициент; объём файла остаётся как в ЛСР — смета
    /// свидетельство расчёта, а не плана.
    /// </summary>
    /// <param name="rateMatchId">Запомненное, которым строка сопоставлена; <c>null</c> — выбор человека.</param>
    internal void Match(Guid workTypeId, Guid unitId, decimal factor, Guid planItemId, Guid? rateMatchId)
    {
        WorkTypeId = workTypeId;
        UnitId = unitId;
        Factor = factor;
        Volume = SourceVolume * factor;
        PlanItemId = planItemId;
        RateMatchId = rateMatchId;
    }

    /// <summary>Снять сопоставление: строка снова «не сопоставлена» и из плана уходит (ТЗ PLAN-4).</summary>
    internal void Unmatch()
    {
        WorkTypeId = null;
        UnitId = null;
        Factor = null;
        Volume = null;
        PlanItemId = null;
        RateMatchId = null;
    }

    /// <summary>Сопоставлена ли строка ровно так — повтор того же выбора ничего не меняет.</summary>
    internal bool MatchedAs(Guid workTypeId, Guid unitId, decimal factor) =>
        WorkTypeId == workTypeId && UnitId == unitId && Factor == factor && PlanItemId is not null;

    /// <summary>
    /// Новая строка редакции. Звать её — дело одного места: службы записи редакции
    /// (<see cref="EstimateRevisionWriter" />), где сметное наполнение проходит охрану записи.
    /// </summary>
    internal static EstimateRow CreateEstimateRow(Guid revisionId, int order, EstimateRowDraft draft, string data) =>
        new()
        {
            Id = Guid.NewGuid(),
            RevisionId = revisionId,
            Order = order,
            LsrSectionCode = PlanText.Clip(draft.LsrSectionCode, SectionCodeLength),
            LsrSectionName = PlanText.Clip(draft.LsrSectionName, SectionNameLength),
            NumberInSection = PlanText.Clip(draft.NumberInSection, NumberLength),
            RateCode = PlanText.Clip(draft.RateCode, RateCodeLength),
            Name = draft.Name,
            SourceVolume = draft.SourceVolume,
            SourceUnit = PlanText.Clip(draft.SourceUnit, UnitLength),
            WorkTypeId = draft.WorkTypeId,
            UnitId = draft.UnitId,
            Factor = draft.Factor,
            Volume = draft.Volume,
            PlanItemId = draft.PlanItemId,
            RateMatchId = draft.RateMatchId,
            UnitCost = draft.UnitCost,
            Total = draft.Total,
            Data = data,
        };
}
