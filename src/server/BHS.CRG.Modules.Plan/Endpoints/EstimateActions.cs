using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Plan.Endpoints;

/// <summary>
/// Действия над сметой в журнале (change <c>plan-06-estimate-model</c>, ТЗ CORE-28, PLAN-5.2). Каждое
/// закрыто правом чтения смет: запись называет смету и стройку, а журнал читают по праву ядра.
/// </summary>
public sealed class EstimateActions : IModuleActivityActions
{
    public static readonly ModuleActivityAction Created =
        new("plan.estimate.created", "Смета заведена", PlanModule.EstimateRead);

    public static readonly ModuleActivityAction Changed =
        new("plan.estimate.changed", "Смета переименована", PlanModule.EstimateRead);

    public static readonly ModuleActivityAction Deleted =
        new("plan.estimate.deleted", "Смета удалена", PlanModule.EstimateRead);

    /// <summary>
    /// Смена действующей редакции (ТЗ PLAN-5.2): с ней меняется план стройки, из которого другие модули
    /// берут плановые объёмы, — и это первое, что ищут, когда план «вдруг стал другим».
    /// </summary>
    public static readonly ModuleActivityAction CurrentRevision =
        new("plan.estimate.current", "Действующая редакция сметы сменена", PlanModule.EstimateRead);

    /// <summary>
    /// Редакция создана импортом (<c>plan-07</c>, ТЗ PLAN-6, CORE-28): «редакция N, строк M, не разобрано K» —
    /// частичный импорт виден в журнале, а не только в отчёте, который удалится вместе с редакцией.
    /// </summary>
    public static readonly ModuleActivityAction Imported =
        new("plan.estimate.imported", "Смета импортирована", PlanModule.EstimateRead);

    public static readonly ModuleActivityAction RevisionDeleted =
        new("plan.revision.deleted", "Редакция сметы удалена", PlanModule.EstimateRead);

    /// <summary>
    /// Строки редакции сопоставлены с видом работы — по одной или разделом ЛСР (<c>plan-08</c>, ТЗ PLAN-6.1): с
    /// этим меняется план стройки, и «откуда в плане эта работа» ищут здесь.
    /// </summary>
    public static readonly ModuleActivityAction Matched =
        new("plan.revision.matched", "Строки сметы сопоставлены", PlanModule.EstimateRead);

    public static readonly ModuleActivityAction Unmatched =
        new("plan.revision.unmatched", "Сопоставление строки сметы снято", PlanModule.EstimateRead);

    /// <summary>Запомненное сопоставление шифра перенацелено из списка (<c>plan-08</c>).</summary>
    public static readonly ModuleActivityAction RateMatchPointed =
        new("plan.ratematch.pointed", "Запомненное сопоставление шифра перенацелено", PlanModule.EstimateRead);

    public static readonly ModuleActivityAction RateMatchForgotten =
        new("plan.ratematch.forgotten", "Запомненное сопоставление шифра забыто", PlanModule.EstimateRead);

    public IReadOnlyList<ModuleActivityAction> Actions =>
        [Created, Changed, Deleted, CurrentRevision, Imported, RevisionDeleted, Matched, Unmatched, RateMatchPointed, RateMatchForgotten];

    internal static string Label(Data.Estimate estimate) => $"Смета «{estimate.Name}»";
}
