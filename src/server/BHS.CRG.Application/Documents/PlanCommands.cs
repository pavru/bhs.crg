using BHS.CRG.Domain.Catalog;
using MediatR;

namespace BHS.CRG.Application.Documents;

/// <summary>Строка плана в контракте API: тип и сколько документов этого типа планируется.</summary>
public record PlanRow(Guid DocumentTypeId, int PlannedCount);

/// <summary>Строка плана с фактом — как её показывает панель плана комплекта.</summary>
public record PlanRowWithActual(Guid DocumentTypeId, string TypeName, int PlannedCount, int ActualCount);

public record GetDocumentSetPlanQuery(Guid SetId) : IRequest<IReadOnlyList<PlanRowWithActual>>;

/// <summary>
/// Замена плана ЦЕЛИКОМ, как реквизиты документа: клиент присылает то, что должно остаться.
/// Точечные добавить/убрать пришлось бы согласовывать с порядком строк на экране — а порядок там
/// не хранится, он берётся из типов.
/// </summary>
public record ReplaceDocumentSetPlanCommand(Guid SetId, IReadOnlyList<PlanRow> Rows) : IRequest;

/// <summary>
/// Готовность уровня и его непосредственных детей (issue #796). Одним ответом — по той же причине,
/// что у сводки проблем (#454): проценты нужны на пунктах, ведущих вниз, и запрос-на-ребёнка
/// превратил бы раздел с десятью комплектами в десять обращений.
/// </summary>
public record GetPlanSummaryQuery(CatalogScope Scope, Guid? ScopeId) : IRequest<PlanSummary>;
