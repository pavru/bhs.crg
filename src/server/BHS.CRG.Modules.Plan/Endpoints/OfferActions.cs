using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Plan.Endpoints;

/// <summary>
/// Действия над КП в журнале (change <c>plan-13-offers</c>, ТЗ CORE-28). Закрыты правом чтения КП: запись
/// называет КП и его суммы, а их открывает <c>plan.offer.read</c>.
/// </summary>
public sealed class OfferActions : IModuleActivityActions
{
    /// <summary>КП сделано из редакции — с ним редакция зафиксирована (ТЗ PLAN-5).</summary>
    public static readonly ModuleActivityAction Created =
        new("plan.offer.created", "КП сделано из редакции сметы", PlanModule.OfferRead);

    public static readonly ModuleActivityAction Changed =
        new("plan.offer.changed", "Шапка КП изменена", PlanModule.OfferRead);

    /// <summary>Наценки КП — с ними меняются цены продажи и итог: «откуда эта цена» ищут здесь.</summary>
    public static readonly ModuleActivityAction Markups =
        new("plan.offer.markups", "Наценки КП изменены", PlanModule.OfferRead);

    public static readonly ModuleActivityAction Deleted =
        new("plan.offer.deleted", "КП удалено", PlanModule.OfferRead);

    public IReadOnlyList<ModuleActivityAction> Actions => [Created, Changed, Markups, Deleted];
}
