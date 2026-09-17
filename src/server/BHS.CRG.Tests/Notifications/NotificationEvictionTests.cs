using BHS.CRG.Infrastructure.Notifications;

namespace BHS.CRG.Tests.Notifications;

/// <summary>
/// Когда вытеснение уведомлений отмечается в журнале (issue #919). Ошибиться можно в обе стороны:
/// молчать о потоке — значит снова принять обрезанную историю за начало событий; шуметь о ротации —
/// значит строка на каждую публикацию при полной корзине.
/// </summary>
public class NotificationEvictionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Вытеснение_годовой_давности_это_ротация()
        => Assert.False(NotificationEviction.IsFlow(Now.AddYears(-1), Now));

    [Fact]
    public void Вытеснение_вчерашнего_это_поток()
        => Assert.True(NotificationEviction.IsFlow(Now.AddDays(-1), Now));

    [Fact]
    public void Граница_окна_уже_ротация()
        => Assert.False(NotificationEviction.IsFlow(Now - NotificationEviction.FlowWindow, Now));

    [Fact]
    public void Ротация_не_предупреждает_никогда()
        => Assert.False(NotificationEviction.ShouldWarn(flow: false, lastWarnedAt: null, Now));

    [Fact]
    public void Первый_поток_предупреждает()
        => Assert.True(NotificationEviction.ShouldWarn(flow: true, lastWarnedAt: null, Now));

    [Fact]
    public void Поток_не_предупреждает_чаще_раза_в_час()
    {
        Assert.False(NotificationEviction.ShouldWarn(true, Now.AddMinutes(-59), Now));
        Assert.True(NotificationEviction.ShouldWarn(true, Now - NotificationEviction.WarnEvery, Now));
    }
}
