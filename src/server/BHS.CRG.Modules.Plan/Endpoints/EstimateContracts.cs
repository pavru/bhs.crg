using System.Text.Json;
using System.Text.Json.Serialization;

namespace BHS.CRG.Modules.Plan.Endpoints;

/// <summary>
/// Носитель «скрыто правами» (D4, D17 зонтика <c>plan-00-epic</c>, ТЗ CORE-33): часть ответа, которой в
/// нём нет, потому что у читающего нет права. Закрытые поля не приходят вовсе — ни пустыми, ни нулём, —
/// и без носителя клиент не отличил бы «скрыто» от «нет данных».
/// </summary>
/// <param name="Code">Что скрыто: <c>cost</c> — себестоимость сметы.</param>
/// <param name="Permission">Право, которое это открывает.</param>
/// <param name="Name">То же право словами каталога прав — им экран называет причину, кода человеку не показывая.</param>
public sealed record HiddenPart(string Code, string Permission, string Name);

/// <summary>Пометка состояния с сервера (D17 зонтика): код для клиента и текст для человека.</summary>
public sealed record PlanMark(string Code, string Text);

public sealed record EstimateCreateRequest(Guid ConstructionId, Guid? SectionId, string? Name, string? Kind);

public sealed record EstimateUpdateRequest(string? Name, string? Kind);

public sealed record CurrentRevisionRequest(Guid RevisionId);

/// <summary>Почему редакция зафиксирована: из неё сделана производная (ТЗ PLAN-5).</summary>
public sealed record EstimateFrozenDto(DateTimeOffset At, string Code, string Label);

/// <summary>Деньги редакции — только с <c>plan.estimate.cost</c>: итог по её строкам.</summary>
public sealed record RevisionCostDto(decimal Total);

/// <summary>Редакция создана из неполного разбора: «частично N из M» (ТЗ PLAN-7, D4 <c>plan-31</c>).</summary>
public sealed record RevisionPartialDto(int Parsed, int InFile);

/// <param name="Rows">Строк в редакции.</param>
/// <param name="Unmatched">Из них строк-работ не сопоставлено — в план они не входят (ТЗ PLAN-4).
/// Строки-материалы сюда не входят никогда (D2 <c>plan-31</c>).</param>
/// <param name="MaterialsUnmatched">Материалов без позиции номенклатуры — и у строк-материалов (D4 <c>plan-31</c>).</param>
/// <param name="UnitChanged">Строк с пометкой <c>unit-changed</c>: шифр знаком, единица расценки другая.</param>
/// <param name="ImportId">Импорт, из которого редакция создана; <c>null</c> — создана не импортом. Сам отчёт
/// импорта — под <c>plan.estimate.edit</c>.</param>
/// <param name="Partial">Создана из неполного разбора; <c>null</c> — из полного или не импортом.</param>
public sealed record EstimateRevisionDto(
    Guid Id, int Number, bool Current, DateTimeOffset ImportedAt, string? ImportedBy, string? SourceFileName,
    string? SchemaVersion, int Rows, int Unmatched, int MaterialsUnmatched, int UnitChanged, Guid? ImportId,
    RevisionPartialDto? Partial, EstimateFrozenDto? Frozen, string Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RevisionCostDto? Cost);

/// <param name="Kind"><c>main</c> — основная, <c>additional</c> — дополнительная.</param>
/// <param name="Hidden">Что скрыто правами; <c>null</c> — ничего.</param>
public sealed record EstimateDto(
    Guid Id, Guid ConstructionId, Guid? SectionId, string Name, string Kind, string Version,
    Guid? CurrentRevisionId, int? CurrentRevisionNumber, IReadOnlyList<EstimateRevisionDto> Revisions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<HiddenPart>? Hidden);

/// <param name="Next">Курсор следующей страницы общего списка (без стройки); <c>null</c> — смет больше нет либо
/// список стройки, который приходит целиком.</param>
public sealed record EstimateListDto(
    IReadOnlyList<EstimateDto> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Next,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<HiddenPart>? Hidden);

public sealed record LsrSectionDto(string? Code, string? Name);

/// <summary>Себестоимость строки и её сметное наполнение — только с <c>plan.estimate.cost</c>.</summary>
public sealed record EstimateRowCostDto(decimal? UnitCost, decimal? Total, JsonElement Data);

/// <summary>Цена и стоимость материала — только с <c>plan.estimate.cost</c>.</summary>
public sealed record MaterialCostDto(decimal? UnitPrice, decimal? Amount);

public sealed record EstimateMaterialDto(
    Guid Id, int Order, Guid? NomenclatureId, string SourceName, string? SourceCode, string? SourceUnit,
    decimal Quantity, IReadOnlyList<PlanMark> Marks,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MaterialCostDto? Cost);

public sealed record EstimateLaborDto(decimal ManHours);

/// <param name="Number">Номер позиции в файле как есть («13», «13.1») — только показ; <c>null</c> у редакций до
/// <c>plan-31</c>, и тогда экран показывает номер в разделе.</param>
/// <param name="Subordinate">Подчинённая позиция файла.</param>
/// <param name="Kind"><c>work</c> — работа; <c>material</c> — материал без работы: вида работы не требует, в
/// «не сопоставлено» и в план не входит, сопоставляется его материал (D2 <c>plan-31</c>).</param>
public sealed record EstimateRowDto(
    Guid Id, int Order, LsrSectionDto? LsrSection, string? NumberInSection, string? Number, bool Subordinate,
    string Kind, string? RateCode, string Name,
    decimal? SourceVolume, string? SourceUnit, Guid? WorkTypeId, Guid? UnitId, decimal? Factor, decimal? Volume,
    Guid? PlanItemId, IReadOnlyList<PlanMark> Marks, IReadOnlyList<EstimateMaterialDto> Materials,
    IReadOnlyList<EstimateLaborDto> Labor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EstimateRowCostDto? Cost);

/// <param name="Next">Курсор следующей страницы; <c>null</c> — строк больше нет.</param>
public sealed record EstimateRowsPageDto(
    IReadOnlyList<EstimateRowDto> Items, string? Next,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<HiddenPart>? Hidden);
