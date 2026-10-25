using System.Text.Json.Serialization;

namespace BHS.CRG.Modules.Plan.Endpoints;

/// <summary>Новая смета, которую заведёт импорт (решение В-12 (а)).</summary>
public sealed record NewEstimateDto(string Name, Guid? SectionId, string Kind);

/// <summary>Куда ляжет редакция: в существующую смету или в новую — ровно одно из двух.</summary>
public sealed record EstimateImportTargetDto(Guid? EstimateId, string? EstimateName, NewEstimateDto? NewEstimate);

/// <summary>Почему файл не разобран: <c>code</c> — причина разбора (<c>UnsupportedVersion</c>, <c>NoTotals</c>…)
/// или <c>Failed</c> — сбой, не связанный с файлом.</summary>
public sealed record EstimateImportRefusalDto(string Code, string Text);

/// <summary>Позиция файла, которая не разобрана, — где и почему (ТЗ PLAN-7).</summary>
public sealed record UnparsedPositionDto(string Place, int Line, string Reason);

/// <summary>Итог раздела или сметы — только с <c>plan.estimate.cost</c>.</summary>
/// <param name="EstimateTotal">«ВСЕГО по смете» с лимитированными затратами, как его записала программа;
/// у досчитанной сметы его нет.</param>
public sealed record ImportCostDto(decimal? Total, decimal? EstimateTotal = null);

/// <param name="Rows">Строк будущей редакции из раздела — разобранные позиции.</param>
public sealed record ImportChapterDto(
    int Index, string Caption, int Rows,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ImportCostDto? Cost);

/// <summary>Сводка будущей редакции (ТЗ PLAN-7): разделы, строки, итог.</summary>
/// <param name="Method"><c>bim</c>, <c>rim</c>, <c>rm</c> или <c>unknown</c>.</param>
/// <param name="Inactive">Из строк — неактивных позиций: в итог сметы они не входят.</param>
public sealed record ImportSummaryDto(
    string? LsrNumber, string? LsrName, string Method, IReadOnlyList<ImportChapterDto> Chapters, int Rows, int Inactive,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ImportCostDto? Cost);

/// <summary>
/// Импорт в списке импортов стройки — без отчёта: по списку несостоявшийся импорт находят и отменяют (он
/// держит стройку и раздел новой сметы, design, Risks).
/// </summary>
/// <param name="State">Как у отчёта: <c>parsing</c>, <c>parsed</c>, <c>rejected</c> или <c>committed</c>.</param>
/// <param name="RefusalCode">Причина отказа — у <c>rejected</c>.</param>
public sealed record EstimateImportItemDto(
    Guid Id, EstimateImportTargetDto Target, string FileName, string State, string? Kind, string? RefusalCode,
    Guid? RevisionId, DateTimeOffset CreatedAt, string? CreatedBy, string Version);

/// <summary>
/// Отчёт импорта (ТЗ PLAN-7): состояние, вид документа и версия схемы, «разобрано N из M», не разобранное с
/// причиной и сводка будущей редакции.
/// </summary>
/// <param name="State"><c>parsing</c>, <c>parsed</c>, <c>rejected</c> или <c>committed</c>.</param>
/// <param name="Kind"><c>lsr</c>, <c>vor</c>, <c>ka</c>; <c>null</c> — файл ещё не открыт или не открылся.</param>
/// <param name="Progress">Ход разбора словами очереди, пока он идёт.</param>
/// <param name="Parsed">Разобрано позиций — у ЛСР.</param>
/// <param name="InFile">Позиций в файле — у ЛСР.</param>
/// <param name="Marks">Пометки отчёта: <c>notEstimate</c> — это не ЛСР, приложите к смете; <c>partial</c> —
/// разобрано не всё; <c>otherCalculation</c> — номер ЛСР не совпадает с прежней редакцией сметы;
/// <c>computedTotals</c> — итоги посчитаны системой; <c>interrupted</c> — разбор прерван.</param>
/// <param name="Hidden">Что скрыто правами; <c>null</c> — ничего.</param>
public sealed record EstimateImportDto(
    Guid Id, Guid ConstructionId, EstimateImportTargetDto Target, string FileName, string State, string? Kind,
    string? KindName, string? ProgramVersion, string? SchemaVersion, string? Progress, EstimateImportRefusalDto? Refusal,
    int? Parsed, int? InFile, IReadOnlyList<UnparsedPositionDto> Unparsed, ImportSummaryDto? Summary,
    IReadOnlyList<PlanMark> Marks, Guid? RevisionId, DateTimeOffset CreatedAt, string? CreatedBy, string Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<HiddenPart>? Hidden);

/// <summary>Создание редакции из импорта: <c>acceptPartial</c> — согласие на редакцию без не разобранных
/// позиций (ТЗ PLAN-7); без него импорт с не разобранными отвергается 409 с числами.</summary>
public sealed record EstimateImportCommitRequest(bool AcceptPartial = false);

/// <summary>Строка редакции, оставшаяся несопоставленной при создании, — и почему.</summary>
public sealed record UnmatchedImportRowDto(int Row, string Reason);

/// <summary>
/// Редакция создана: отчёт импорта (теперь <c>committed</c>), смета с её редакциями и созданная редакция.
/// </summary>
/// <param name="EstimateCreated">Импорт завёл новую смету.</param>
/// <param name="Repeated">Редакция создана раньше — тем же импортом: ответ называет её, второй нет.</param>
/// <param name="Unmatched">Строки, которым подстановка дала вид работы, но позиция перечня не заведена.</param>
public sealed record EstimateImportCommitDto(
    EstimateImportDto Import, EstimateDto Estimate, Guid RevisionId, int RevisionNumber, bool EstimateCreated,
    bool Repeated, IReadOnlyList<UnmatchedImportRowDto> Unmatched);
