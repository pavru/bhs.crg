using BHS.CRG.Application.Documents;

namespace BHS.CRG.Tests.Documents;

/// <summary>
/// Арифметика готовности по плану (issue #796) — вся спорная часть фичи, и проверяется она без базы.
/// </summary>
public class PlanMathTests
{
    // ── Закрыто планом ────────────────────────────────────────────────────────

    [Fact]
    public void Ready_CountsEachTypeUpToItsPlan()
        => Assert.Equal(5, PlanMath.Ready([(3, 3), (4, 2)]));

    /// <summary>
    /// Документы сверх плана процент не надувают. Без <c>min</c> пять актов при плане в три давали
    /// бы 167 % по одной строке и вытягивали бы весь комплект за незакрытые соседние позиции.
    /// </summary>
    [Fact]
    public void Ready_DoesNotCountDocumentsBeyondThePlan()
        => Assert.Equal(3, PlanMath.Ready([(3, 5)]));

    [Fact]
    public void Ready_OfEmptyPlanIsZero()
        => Assert.Equal(0, PlanMath.Ready([]));

    // ── Процент ───────────────────────────────────────────────────────────────

    /// <summary>Плана нет — процента нет. «0 %» соврал бы: там, где не планировали, ничего и не должно.</summary>
    [Fact]
    public void Percent_IsNullWithoutPlan()
        => Assert.Null(PlanMath.Percent(planned: 0, ready: 0, needsAttention: 0));

    [Fact]
    public void Percent_IsHundredOnlyWhenPlanClosedAndNothingToReview()
        => Assert.Equal(100, PlanMath.Percent(planned: 10, ready: 10, needsAttention: 0));

    /// <summary>
    /// Сверка — гейт финала, а не множитель: план закрыт, но есть неразобранное — 99 %, и рядом
    /// маркер. Дробить процент долей сверки нельзя: количество документов и качество данных —
    /// разные природы, и смешанная цифра не значит ничего.
    /// </summary>
    [Fact]
    public void Percent_CapsAt99WhenReviewPending()
        => Assert.Equal(99, PlanMath.Percent(planned: 10, ready: 10, needsAttention: 1));

    /// <summary>Округление вниз: 99,6 % — это ещё не «сто», и показать сто было бы прямой ложью.</summary>
    [Fact]
    public void Percent_RoundsDown()
        => Assert.Equal(99, PlanMath.Percent(planned: 250, ready: 249, needsAttention: 0));

    [Theory]
    [InlineData(10, 0, 0)]
    [InlineData(10, 5, 50)]
    [InlineData(3, 1, 33)]
    public void Percent_IsShareOfClosedPositions(int planned, int ready, int expected)
        => Assert.Equal(expected, PlanMath.Percent(planned, ready, needsAttention: 0));

    /// <summary>
    /// Готовых больше запланированного процент не ломает. Само по себе такое приходит не из
    /// <see cref="PlanMath.Ready"/> (там уже <c>min</c>), а из сложения уровней — и «117 %» на
    /// шапке стройки был бы худшим способом об этом узнать.
    /// </summary>
    [Fact]
    public void Percent_NeverExceedsHundred()
        => Assert.Equal(100, PlanMath.Percent(planned: 6, ready: 7, needsAttention: 0));

    [Fact]
    public void Percent_NeverGoesBelowZero()
        => Assert.Equal(0, PlanMath.Percent(planned: 6, ready: -3, needsAttention: 0));
}
