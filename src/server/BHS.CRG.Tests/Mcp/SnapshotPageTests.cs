using BHS.CRG.Application.DataSnapshots;

namespace BHS.CRG.Tests.Mcp;

/// <summary>
/// Нарезка страниц (issue #576). Главное здесь — <c>truncated</c>: агент, молча получивший часть
/// списка, выдаст уверенный и неверный вывод, потому что недочитанные позиции неотличимы от
/// отсутствующих.
/// </summary>
public class SnapshotPageTests
{
    private static readonly string[] Ten =
        [.. Enumerable.Range(1, 10).Select(i => $"позиция {i}")];

    [Fact]
    public void FirstPage_ReportsTruncation_AndTotal()
    {
        var page = SnapshotPage<string>.Of(Ten, offset: 0, limit: 3, maxLimit: 100);

        Assert.Equal(["позиция 1", "позиция 2", "позиция 3"], page.Items);
        Assert.Equal(10, page.Total);
        Assert.True(page.Truncated);
    }

    [Fact]
    public void LastPage_IsNotTruncated()
    {
        var page = SnapshotPage<string>.Of(Ten, offset: 8, limit: 5, maxLimit: 100);

        Assert.Equal(["позиция 9", "позиция 10"], page.Items);
        Assert.Equal(8, page.Offset);
        Assert.False(page.Truncated);
    }

    /// <summary>Потолок существует, чтобы вызывающий не обошёл его сам: limit=10000 вернул бы ровно
    /// тот ответ, из-за которого страничность и появилась.</summary>
    [Fact]
    public void Limit_IsCappedByMaximum()
    {
        var page = SnapshotPage<string>.Of(Ten, offset: 0, limit: 10_000, maxLimit: 4);

        Assert.Equal(4, page.Limit);
        Assert.Equal(4, page.Items.Count);
        Assert.True(page.Truncated);
    }

    /// <summary>
    /// Агент листает вслепую, поэтому смещение за концом — не ошибка, а «дальше пусто». Исключение
    /// здесь заставляло бы его гадать, кончился список или сломался вызов.
    /// </summary>
    [Theory]
    [InlineData(50)]
    [InlineData(-5)]
    public void OutOfRangeOffset_IsClamped_NotRejected(int offset)
    {
        var page = SnapshotPage<string>.Of(Ten, offset, limit: 3, maxLimit: 100);

        Assert.Equal(10, page.Total);
        Assert.False(offset > 0 && page.Items.Count > 0);
        Assert.InRange(page.Offset, 0, 10);
    }

    [Fact]
    public void EmptySource_IsNotTruncated()
    {
        var page = SnapshotPage<string>.Of([], offset: 0, limit: 25, maxLimit: 100);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
        Assert.False(page.Truncated);
    }

    /// <summary>Обход страницами обязан покрыть список целиком и без дублей — иначе «полный обход»
    /// агента полным не будет.</summary>
    [Fact]
    public void PagingThrough_CoversEverythingOnce()
    {
        var seen = new List<string>();
        var offset = 0;
        while (true)
        {
            var page = SnapshotPage<string>.Of(Ten, offset, limit: 3, maxLimit: 100);
            seen.AddRange(page.Items);
            if (!page.Truncated) break;
            offset += page.Items.Count;
        }

        Assert.Equal(Ten, seen);
    }
}
