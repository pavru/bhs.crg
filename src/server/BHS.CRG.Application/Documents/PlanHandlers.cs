using BHS.CRG.Application.Common;
using BHS.CRG.Application.Reconciliation;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using MediatR;

namespace BHS.CRG.Application.Documents;

/// <summary>
/// План по документам: чтение и замена плана комплекта, сводка готовности по уровням (issue #796).
/// </summary>
public class PlanHandlers(
    IRepository<DocumentSetPlanItem> plans,
    IRepository<DocumentSet> sets,
    IRepository<DocumentType> types,
    IDomainObjectRepository objects,
    IScopeSubtree subtree,
    IScopeChildren scopeChildren,
    IMediator mediator) :
    IRequestHandler<GetDocumentSetPlanQuery, IReadOnlyList<PlanRowWithActual>>,
    IRequestHandler<ReplaceDocumentSetPlanCommand>,
    IRequestHandler<GetPlanSummaryQuery, PlanSummary>
{
    public async Task<IReadOnlyList<PlanRowWithActual>> Handle(GetDocumentSetPlanQuery q, CancellationToken ct)
    {
        // Комплекта нет — это НЕ «комплект без плана». Иначе после устаревшей навигации клиент
        // получил бы пустую форму плана, заполнил её и упёрся в 404 уже на сохранении.
        _ = await sets.GetByIdAsync(q.SetId, ct) ?? throw new NotFoundException();

        var rows = await plans.FindAsync(p => p.DocumentSetId == q.SetId, ct);
        if (rows.Count == 0) return [];

        var actual = await objects.CountReadyDocumentsByTypeAsync([q.SetId], ct);
        var byId = (await types.GetAllAsync(ct)).ToDictionary(t => t.Id, t => t.Name);

        return [.. rows
            .Select(r => new PlanRowWithActual(
                r.DocumentTypeId,
                byId.GetValueOrDefault(r.DocumentTypeId, "Тип удалён"),
                r.PlannedCount,
                actual.GetValueOrDefault((q.SetId, r.DocumentTypeId))))
            .OrderBy(r => r.TypeName, StringComparer.CurrentCulture)];
    }

    public async Task Handle(ReplaceDocumentSetPlanCommand cmd, CancellationToken ct)
    {
        _ = await sets.GetByIdAsync(cmd.SetId, ct) ?? throw new NotFoundException();

        // Тип в строке обязан существовать: план ссылается на него, и «план на несуществующий тип»
        // это тихо неисполнимая позиция, из-за которой процент никогда не дойдёт до ста.
        var known = (await types.GetAllAsync(ct)).Select(t => t.Id).ToHashSet();
        foreach (var row in cmd.Rows)
        {
            if (!known.Contains(row.DocumentTypeId))
                throw new InvalidRequestException("В плане указан тип документа, которого нет.");
            if (row.PlannedCount < 1)
                throw new InvalidRequestException("Планируемое количество — от 1. Чтобы убрать позицию, удалите строку.");
        }

        if (cmd.Rows.Select(r => r.DocumentTypeId).Distinct().Count() != cmd.Rows.Count)
            throw new InvalidRequestException("Тип в плане повторяется — на тип приходится одна строка.");

        foreach (var existing in await plans.FindAsync(p => p.DocumentSetId == cmd.SetId, ct))
            plans.Remove(existing);
        foreach (var row in cmd.Rows)
            await plans.AddAsync(DocumentSetPlanItem.Create(cmd.SetId, row.DocumentTypeId, row.PlannedCount), ct);

        await plans.SaveChangesAsync(ct);
    }

    public async Task<PlanSummary> Handle(GetPlanSummaryQuery q, CancellationToken ct)
    {
        var children = await scopeChildren.ChildrenOfAsync(q.Scope, q.ScopeId, ct);

        // Неразобранное берём ОДНОЙ сводкой на весь ответ, а не по запросу на уровень. Причин две.
        //
        // Цена: подсчёт проблем уровня перебирает определения сверки и замечания всего поддерева —
        // спрашивать его отдельно у себя и у каждого ребёнка значило бы для стройки с десятью
        // разделами одиннадцать таких обходов на КАЖДУЮ отрисовку шапки. Ровно от этого уводит
        // пакетный CountReadyDocumentsByTypeAsync парой строк ниже, и завести здесь то, от чего там
        // уходили, было бы странно.
        //
        // Правда: сводка знает счётчик ребёнка независимо от того, есть ли у ребёнка план. Считай
        // мы его внутри ProgressAsync, уровень без плана возвращал бы ноль — и System, складывая
        // детей, показал бы «100 %» рядом с бейджем «7 не разобрано».
        var problems = await mediator.Send(new GetProblemSummaryQuery(q.Scope, q.ScopeId), ct);
        var needsByChild = problems.Children.ToDictionary(c => c.ScopeId, c => c.NeedsAttention);

        var all = new List<PlanProgressOf>();
        foreach (var (childScope, childId) in children)
            all.Add(new PlanProgressOf(childId,
                await ProgressAsync(childScope, childId, needsByChild.GetValueOrDefault(childId), ct)));

        // Уровни без плана в разбивку не попадают: рисовать там нечего, а «0 %» соврал бы.
        var childProgress = all.Where(c => c.Progress.HasPlan).ToList();

        // У System своего уровня нет — он сумма всех строек. Спуск по поддереву для него намеренно
        // пуст («все комплекты базы» — не поддерево), поэтому складываем детей: иначе верхний
        // уровень всегда показывал бы «плана нет» при полностью расписанных стройках. Счётчик
        // разбора при этом берём у сводки, а не из слагаемых: у неё он есть и по бесплановым детям.
        if (q.Scope == CatalogScope.System || q.ScopeId is not { } selfId)
            return new PlanSummary(Sum(all.Select(c => c.Progress), problems.NeedsAttention), childProgress);

        return new PlanSummary(await ProgressAsync(q.Scope, selfId, problems.NeedsAttention, ct), childProgress);
    }

    private static PlanProgress Sum(IEnumerable<PlanProgress> parts, int needsAttention)
    {
        var list = parts.ToList();
        return new PlanProgress(
            list.Sum(p => p.Planned),
            list.Sum(p => p.Ready),
            needsAttention,
            list.Sum(p => p.SetsWithoutPlan));
    }

    /// <summary>
    /// Готовность уровня: план и факт по ВСЕМ комплектам под ним.
    ///
    /// Считается на лету, без кэша и предподсчёта: масштаб приложения это позволяет (так же
    /// устроены счётчики проблем), а сохранённый процент разошёлся бы с фактом при первой же
    /// генерации документа, о которой забыли пересчитать.
    /// </summary>
    private async Task<PlanProgress> ProgressAsync(
        CatalogScope scope, Guid scopeId, int needsAttention, CancellationToken ct)
    {
        var setIds = await subtree.SetIdsUnderAsync(scope, scopeId, ct);
        if (setIds.Count == 0) return new PlanProgress(0, 0, needsAttention, 0);

        var rows = await plans.FindAsync(p => setIds.Contains(p.DocumentSetId), ct);
        var setsWithPlan = rows.Select(r => r.DocumentSetId).ToHashSet();

        // Комплекты без плана в проценте не участвуют, но и не молчат: их число уходит наверх,
        // иначе «стройка на 100 %» означала бы «единственный комплект с планом закрыт», а про
        // девять остальных экран не сказал бы ничего.
        var withoutPlan = setIds.Count(id => !setsWithPlan.Contains(id));
        if (rows.Count == 0) return new PlanProgress(0, 0, needsAttention, withoutPlan);

        var actual = await objects.CountReadyDocumentsByTypeAsync(setsWithPlan, ct);
        var planned = rows.Sum(r => r.PlannedCount);
        var ready = PlanMath.Ready(rows.Select(r =>
            (r.PlannedCount, actual.GetValueOrDefault((r.DocumentSetId, r.DocumentTypeId)))));

        return new PlanProgress(planned, ready, needsAttention, withoutPlan);
    }
}
