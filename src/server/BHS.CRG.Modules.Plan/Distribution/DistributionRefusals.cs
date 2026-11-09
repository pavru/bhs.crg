using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;

namespace BHS.CRG.Modules.Plan.Distribution;

/// <summary>
/// Строка, у которой части не сходятся (change <c>plan-10-distribution</c>, ТЗ COST-13): сетка подсвечивает её по
/// <see cref="RowId" />, а текст называет сумму и объём в обеих единицах.
/// </summary>
/// <param name="Code"><c>over</c> — части больше объёма; <c>no-volume</c> — у строки нет объёма, только правилом;
/// <c>not-positive</c> — часть не больше нуля; <c>too-small</c> — часть меньше шестого знака единицы строки;
/// <c>not-matched</c> — единица плана у несопоставленной строки; <c>section-twice</c> — раздел назван дважды.</param>
/// <param name="Sum">Сумма частей в единице запроса.</param>
/// <param name="Volume">Объём строки в единице запроса; <c>null</c> — объёма нет.</param>
/// <param name="Unit"><c>row</c> или <c>plan</c> — единица запроса.</param>
/// <param name="Factor">Коэффициент строки в единицу плана — им ответ называет числа в обеих единицах.</param>
public sealed record BalanceError(
    Guid RowId, string Number, string Name, string Code, decimal Sum, decimal? Volume, PartUnit Unit, decimal? Factor,
    string Text)
{
    /// <summary>Число запроса в единице строки и в единице плана.</summary>
    public (decimal Row, decimal? Plan) Both(decimal quantity) => Unit == PartUnit.Row
        ? (quantity, DistributionMath.ToPlan(quantity, Factor))
        : (Factor is > 0m ? Math.Round(quantity / Factor.Value, DistributionMath.Decimals, MidpointRounding.ToEven) : quantity, quantity);
}

/// <summary>
/// Части строк набора не сходятся — 400 со всеми нарушившими строками (ТЗ COST-13): не записывается ничего, ни
/// одна строка набора.
/// </summary>
public sealed class DistributionBalanceException(IReadOnlyList<BalanceError> errors) : InvalidRequestException(
    "Распределение не записано — ни одна строка: " + string.Join("; ", errors.Select(e => e.Text)) + ".")
{
    public IReadOnlyList<BalanceError> Errors { get; } = errors;
}

/// <summary>
/// Смета составлена на раздел стройки (<see cref="Estimate.SectionId" />): её строки целиком в этом разделе, и
/// распределять их не по чему — 400 с названием раздела.
/// </summary>
public sealed class EstimateOnSectionException(Guid sectionId, string? sectionName) : InvalidRequestException(
    $"Смета составлена на раздел «{sectionName ?? "удалённый раздел"}» — все её строки в нём, и распределять их по " +
    "разделам не нужно.")
{
    public const string Code = "estimate-section";
    public Guid SectionId { get; } = sectionId;
    public string? SectionName { get; } = sectionName;
}

/// <summary>
/// Распределение тем временем изменили — 409 с текущей версией, кем и когда (ТЗ PLAN-5.1): экран пишет «изменила
/// Петрова О. (10:42) — перечитайте», и набранное в окне остаётся.
/// </summary>
public sealed class DistributionChangedException(string version, string? changedBy, DateTimeOffset? changedAt)
    : ConflictException(
        "Распределение редакции тем временем изменено" + (changedBy is null ? "" : $" — {changedBy}") + ", и эта правка " +
        "не записана. Перечитайте распределение и повторите: вы видели прежнее состояние.")
{
    public const string Code = "distribution-changed";
    public string Version { get; } = version;
    public string? ChangedBy { get; } = changedBy;
    public DateTimeOffset? ChangedAt { get; } = changedAt;
}

/// <summary>
/// Запись распределения проиграла одновременной — версия заголовка разошлась уже при сохранении. Переводит в
/// <see cref="DistributionChangedException" /> звавший: ему видно, какую редакцию перечитать.
/// </summary>
public sealed class DistributionRaceException(Exception inner) : ConflictException(
    "Распределение редакции тем временем изменено, и эта правка не записана. Перечитайте распределение и повторите.",
    inner);
