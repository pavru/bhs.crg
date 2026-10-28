using BHS.CRG.Application.Activity;

namespace BHS.CRG.Tests.Activity;

/// <summary>
/// Граница сессии правки (ТЗ CORE-28.2): каждое условие отдельно, границы окна — ровно на пороге.
///
/// <para>Правило одно на весь продукт и живёт в одном месте, <see cref="ActivitySession" />, поэтому и
/// проверяется без базы: служба журнала только спрашивает его, и ошибка в границе здесь — ошибка
/// каждого ряда на экране.</para>
/// </summary>
public class ActivitySessionTests
{
    private const string Edit = "costs.invoice.edit";
    private static readonly Guid Ivan = Guid.NewGuid();
    private static readonly DateTimeOffset Noon = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    // Пояс, отличный от UTC: полночь по нему и по UTC — разные моменты, и путать их правилу нельзя.
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.CreateCustomTimeZone(
        "test+03", TimeSpan.FromHours(3), "UTC+3", "UTC+3");

    private static ActivitySessionTail Previous(DateTimeOffset at, DateTimeOffset? started = null,
        Guid? actor = null, string? group = Edit) =>
        new(actor ?? Ivan, group, at, started ?? at);

    [Fact]
    public void Подряд_тот_же_автор_и_группа_продолжают_сессию() =>
        Assert.True(ActivitySession.Continues(Ivan, Edit, Noon.AddMinutes(3), Previous(Noon), Moscow));

    [Fact]
    public void Пауза_ровно_десять_минут_ещё_продолжает_а_дольше_рвёт()
    {
        Assert.True(ActivitySession.Continues(Ivan, Edit, Noon.AddMinutes(10), Previous(Noon), Moscow));
        Assert.False(ActivitySession.Continues(Ivan, Edit, Noon.AddMinutes(10).AddTicks(10), Previous(Noon), Moscow));
    }

    [Fact]
    public void Сессия_ровно_в_час_ещё_продолжается_а_дольше_рвётся()
    {
        var started = Noon.AddMinutes(-55);
        Assert.True(ActivitySession.Continues(Ivan, Edit, Noon.AddMinutes(5), Previous(Noon, started), Moscow));
        Assert.False(ActivitySession.Continues(Ivan, Edit, Noon.AddMinutes(5).AddTicks(10), Previous(Noon, started), Moscow));
    }

    [Fact]
    public void Полночь_по_поясу_компании_рвёт_сессию_а_полночь_UTC_нет()
    {
        // 20:58 и 21:02 UTC — по поясу +3 это 23:58 и 00:02 следующего дня.
        var beforeMidnight = new DateTimeOffset(2026, 10, 28, 20, 58, 0, TimeSpan.Zero);
        Assert.False(ActivitySession.Continues(Ivan, Edit, beforeMidnight.AddMinutes(4), Previous(beforeMidnight), Moscow));

        // 23:58 и 00:02 UTC — по поясу +3 оба в одних сутках, 02:58 и 03:02.
        var utcMidnight = new DateTimeOffset(2026, 10, 28, 23, 58, 0, TimeSpan.Zero);
        Assert.True(ActivitySession.Continues(Ivan, Edit, utcMidnight.AddMinutes(4), Previous(utcMidnight), Moscow));
    }

    [Fact]
    public void Другой_автор_рвёт_сессию() =>
        Assert.False(ActivitySession.Continues(Guid.NewGuid(), Edit, Noon.AddMinutes(1), Previous(Noon), Moscow));

    [Fact]
    public void Запись_без_автора_сессию_не_продолжает() =>
        Assert.False(ActivitySession.Continues(null, Edit, Noon.AddMinutes(1), Previous(Noon), Moscow));

    [Fact]
    public void Предыдущая_запись_без_автора_сессию_не_начинает() =>
        Assert.False(ActivitySession.Continues(null, Edit, Noon.AddMinutes(1),
            new ActivitySessionTail(null, Edit, Noon, Noon), Moscow));

    [Fact]
    public void Другая_группа_или_вид_без_свёртки_рвут_сессию()
    {
        Assert.False(ActivitySession.Continues(Ivan, "costs.waybill.edit", Noon.AddMinutes(1), Previous(Noon), Moscow));
        Assert.False(ActivitySession.Continues(Ivan, Edit, Noon.AddMinutes(1), Previous(Noon, group: null), Moscow));
        Assert.False(ActivitySession.Continues(Ivan, null, Noon.AddMinutes(1), Previous(Noon), Moscow));
    }

    [Fact]
    public void Без_предыдущей_записи_сессия_новая() =>
        Assert.False(ActivitySession.Continues(Ivan, Edit, Noon, null, Moscow));

    /// <summary>«Было» и «стало» ряда (ТЗ CORE-28.3): первое и последнее непустое либо объединение перечней.</summary>
    [Fact]
    public void Слияние_ряда_по_правилу_вида()
    {
        var span = new ActivityFold(Edit, FoldRule.Span, FoldRule.Span);
        Assert.Equal(("3 строки", "7 строк"),
            ActivityFold.Merge(span, [(null, null), ("3 строки", "4 строки"), ("4 строки", "7 строк"), ("7 строк", null)]));

        var union = new ActivityFold(Edit, FoldRule.Union, FoldRule.Union);
        Assert.Equal(((string?)null, "номер, дата, поставщик"),
            ActivityFold.Merge(union, [(null, "номер, дата"), (null, "дата"), (null, "поставщик, номер")]));
    }

    /// <summary>
    /// Вид без объявления, у которого ряд всё же длиннее одной записи (свёртку сняли позже), — первое
    /// и последнее значение: ряд обязан показать записи, а не спрятать их.
    /// </summary>
    [Fact]
    public void Слияние_без_объявления_берёт_первое_и_последнее() =>
        Assert.Equal(("было", "стало"), ActivityFold.Merge(null, [("было", "между"), ("между", "стало")]));
}
