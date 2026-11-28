using BHS.CRG.Modules.Access;

namespace BHS.CRG.Modules.Exchange;

/// <summary>
/// Контракт «Плановые работы» (change <c>plan-23-planned-works-contract</c>, ТЗ CORE-34, PLAN-9.1, PLAN-23):
/// сметные объёмы, распределение по разделам, материалы, нормы чел.-ч и стоимость позиций перечня стройки —
/// для модулей, которым схема <c>plan</c> закрыта (заявки и «план против факта» <c>costs</c>, «по смете —
/// фактически» и подсказка материалов <c>work</c>).
///
/// <para><b>План — только действующих редакций</b> всех смет стройки и только сопоставленных строк (ТЗ
/// PLAN-5.2, PLAN-4): прежняя редакция рядом с новой удвоила бы объём. Несопоставленные строки названы
/// числом (<see cref="PlannedWorksAnswer.Unmapped" />), а не пропущены молча.</para>
///
/// <para><b>Каждая часть — под своим правом</b> (<see cref="PlannedWorksAnswer" />): снабженец не видит цен,
/// менеджер — денег, монтажник — количеств. Узкие права — права контракта (ТЗ AUTH-8.1): модуль
/// «Планирование» они не открывают.</para>
/// </summary>
[ModuleContract("plan", "Плановые работы")]
public interface IPlannedWorks
{
    /// <summary>
    /// План стройки в правах <paramref name="reader" />. Системе — отказ целиком
    /// (<see cref="ContractAnswer{T}.SystemRefused" />); человеку — ответ, где каждая часть либо пришла, либо
    /// «скрыта правами» с кодом права.
    /// </summary>
    Task<ContractAnswer<PlannedWorksAnswer>> ReadAsync(
        ModuleReader reader, PlannedWorksQuery query, CancellationToken ct = default);

    /// <summary>Номенклатура по позициям действующих смет стройки, проверенной адаптером Work.</summary>
    Task<IReadOnlyList<FieldPlannedHint>> ReadFieldHintsAsync(AssignedFieldSite site, CancellationToken ct = default);

    /// <summary>Зафиксированная ревизия источника узких полевых подсказок.</summary>
    Task<string> ReadFieldRevisionAsync(CancellationToken ct = default);
}

public sealed record FieldPlannedHint(Guid PlanItemId, Guid NomenclatureId);

/// <summary>Что спрашивают: стройка — обязательно, раздел и позиции перечня — сужают ответ.</summary>
/// <param name="SectionId">
/// Раздел стройки: каждое число ответа — доля этого раздела (части строки — их количеством, правило раздела
/// ЛСР и смета на разделе — целиком). Строки без доли на разделе в ответ не входят.
/// </param>
/// <param name="PlanItemIds">
/// Позиции перечня: ответ — только о них. Материалы строк-материалов (у них позиции нет) при этом отборе в
/// ответ не входят. <c>null</c> — все позиции.
/// </param>
public sealed record PlannedWorksQuery(
    Guid ConstructionId, Guid? SectionId = null, IReadOnlyCollection<Guid>? PlanItemIds = null);

/// <summary>
/// Ответ «Плановых работ» — частями, каждая своим правом (ТЗ PLAN-23):
/// <list type="bullet">
///   <item><see cref="Volumes" /> — <c>plan.estimate.read</c>;</item>
///   <item><see cref="Cost" /> — <c>plan.estimate.cost</c>;</item>
///   <item><see cref="Materials" /> — <c>plan.estimate.materials</c> или <c>plan.estimate.read</c>;</item>
///   <item><see cref="Labor" /> — <c>plan.labor.read</c> или <c>plan.estimate.read</c>;</item>
///   <item><see cref="Hint" /> — <c>plan.estimate.hint</c> или <c>plan.estimate.read</c>.</item>
/// </list>
/// «Скрыто» у части, которую открывает и узкое право, называет узкое: его и выдают роли без смет.
/// Денег нет нигде, кроме <see cref="Cost" />.
/// </summary>
/// <param name="Unmapped">
/// Несопоставленных строк-работ в действующих редакциях смет стройки: в объёмы они не вошли. Считается по
/// всей стройке и без отбора — позиции у такой строки нет. Приходит всегда — число без денег и
/// наименований.
/// </param>
/// <param name="Undecided">
/// Строк (работ и материалов) без решения о разделе, которых отбор по разделу не взял: их объём, материалы и
/// стоимость не лежат ни на одном разделе, и ответ раздела без них неполон (ТЗ PLAN-5.1). Без отбора по разделу —
/// 0: такие строки — в «стройке в целом» (<see cref="PlannedItem.Undistributed" />). Если не пришла ни одна
/// часть — тоже 0: неполным нечему быть.
/// </param>
public sealed record PlannedWorksAnswer(
    ContractAnswer<PlannedVolumes> Volumes,
    ContractAnswer<IReadOnlyList<PlannedCost>> Cost,
    ContractAnswer<PlannedMaterials> Materials,
    ContractAnswer<IReadOnlyList<PlannedLabor>> Labor,
    ContractAnswer<IReadOnlyList<PlannedHint>> Hint,
    int Unmapped,
    int Undecided);

/// <summary>Объёмы позиций и то, по чему они посчитаны.</summary>
/// <param name="Basis">
/// Сметы стройки с действующей редакцией — «план по: смета „…“, ред. N». При отборе по разделу — без смет,
/// составленных на другие разделы: в план этого раздела они не входят.
/// </param>
public sealed record PlannedVolumes(IReadOnlyList<PlannedItem> Items, IReadOnlyList<PlannedBasis> Basis);

/// <summary>Позиция перечня в плане.</summary>
/// <param name="UnitId">Единица классификатора — единица позиции перечня, в ней и объём.</param>
/// <param name="Volume">Объём в единице классификатора — сумма строк (пересчитанных коэффициентом).</param>
/// <param name="Distribution">
/// По разделам стройки; <c>SectionId: null</c> — «стройка в целом»: явное решение, остаток частей строки и
/// нераспределённое. Сумма — <see cref="Volume" />.
/// </param>
/// <param name="Undistributed">
/// Сколько из «стройки в целом» решения о разделе ещё не имеет (ТЗ PLAN-5.1). При отборе по разделу — 0:
/// нераспределённое не лежит ни на одном разделе.
/// </param>
/// <param name="WithoutVolume">
/// Строк позиции, у которых в смете нет объёма: в <see cref="Volume" /> они не вошли, и объём без них неполон.
/// Неизвестное — не ноль: 0 здесь значит «все строки с объёмом».
/// </param>
public sealed record PlannedItem(
    Guid PlanItemId, Guid UnitId, decimal Volume, IReadOnlyList<PlannedShare> Distribution, decimal Undistributed,
    int WithoutVolume)
{
    /// <summary>Есть хотя бы одна строка с числовым объёмом в выбранном охвате, в том числе с явным нулём.</summary>
    public bool HasVolume { get; init; } = true;
}

/// <summary>Объём позиции на разделе стройки; <c>null</c> — «стройка в целом».</summary>
public sealed record PlannedShare(Guid? SectionId, decimal Volume);

/// <summary>Смета и её действующая редакция, по которой посчитан план.</summary>
public sealed record PlannedBasis(Guid EstimateId, string EstimateName, Guid RevisionId, int Revision);

/// <summary>
/// Стоимость позиции — себестоимость сметы (ТЗ PLAN-11, COST-21).
/// </summary>
/// <param name="PlanItemId">Позиция перечня; <c>null</c> — строки-материалы сметы, у них позиции нет.</param>
/// <param name="Total">
/// Сумма стоимости строк. <c>null</c> — хоть одна строка стоимости не несёт: частичная сумма читалась бы как
/// полная, а ноль — как «бесплатно».
/// </param>
public sealed record PlannedCost(Guid? PlanItemId, decimal? Total, IReadOnlyList<PlannedMaterialCost> Materials);

/// <summary>Сметная стоимость материала позиции по номенклатуре.</summary>
/// <param name="Amount">
/// <c>null</c> — смета стоимости ресурса не несёт (так бывает у базисно-индексного расчёта), и это не ноль.
/// </param>
public sealed record PlannedMaterialCost(Guid NomenclatureId, decimal? Amount, ResourceTaxBasis AmountBasis = ResourceTaxBasis.Unknown)
{
    /// <summary>Стоимости в смете нет — показывать «нет в смете», а не ноль.</summary>
    public bool NotInEstimate => Amount is null;
}

/// <summary>Материалы позиций с количеством, без цен.</summary>
/// <param name="Unmatched">
/// Материалов строк ответа без позиции номенклатуры: в <see cref="Lines" /> их нет. Без числа «в смете нет»
/// и «не сопоставлено» были бы неотличимы.
/// </param>
public sealed record PlannedMaterials(IReadOnlyList<PlannedMaterial> Lines, int Unmatched);

/// <summary>Материал позиции: сколько на её объём.</summary>
/// <param name="PlanItemId">Позиция перечня; <c>null</c> — материал строки-материала сметы.</param>
/// <param name="Unit">Единица из сметы. Количества складываются внутри одной непустой единицы. Без неё строки идут отдельно.</param>
public sealed record PlannedMaterial(Guid? PlanItemId, Guid NomenclatureId, decimal Quantity, string? Unit);

/// <summary>Норма трудозатрат позиции — без денег.</summary>
/// <param name="HoursPerUnit">
/// Чел.-ч на единицу классификатора: сумма чел.-ч строк на сумму их объёма. <c>null</c> — смета норм не
/// несёт, и это не ноль.
/// </param>
public sealed record PlannedLabor(Guid PlanItemId, decimal? HoursPerUnit)
{
    public const string Permission = "plan.labor.read";
    public const string EstimateReadPermission = "plan.estimate.read";
}

/// <summary>Позиции номенклатуры материалов позиции — без количеств и цен.</summary>
/// <param name="PlanItemId">Позиция перечня; <c>null</c> — материалы строк-материалов сметы.</param>
public sealed record PlannedHint(Guid? PlanItemId, IReadOnlyList<Guid> NomenclatureIds)
{
    public const string HintPermission = "plan.estimate.hint";
    public const string ReadPermission = "plan.estimate.read";

    public static ContractAnswer<IReadOnlyList<PlannedHint>>? Refusal(ModuleReader reader) =>
        ContractAnswer.Refusal<IReadOnlyList<PlannedHint>>(reader, HintPermission, ReadPermission);
}
