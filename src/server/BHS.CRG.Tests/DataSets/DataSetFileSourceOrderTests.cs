using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;

namespace BHS.CRG.Tests.DataSets;

/// <summary>
/// Порядок источников — свойство набора (issue #1149): <see cref="DataSetFile.Sources" /> отдаёт их
/// по времени создания, в каком бы порядке они ни попали в память.
///
/// Здесь правило проверяется без базы — и потому не зависит от того, как она сегодня читает
/// таблицу. Интеграционный сторож (<c>DataSetSourceOrderTests</c>) воспроизводит сам дефект, но
/// держится на плане запроса; этот держится только на коде.
/// </summary>
public class DataSetFileSourceOrderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static DataSetFile File() =>
        DataSetFile.Create("счёт.pdf", DataSetFormat.Pdf, "b/p", CatalogScope.System, null);

    /// <summary>Источник «из базы» — с заданными идентификатором и временем создания.</summary>
    private static DataSetSource Stored(DataSetFile file, string name, DateTimeOffset createdAt, Guid? id = null) =>
        DataSetSource.Restore(id ?? Guid.NewGuid(), file.Id, name, "default", null, "[]", 0, null, null, null, null,
            null, null, null, null, null, null, createdAt, createdAt);

    [Fact]
    public void Sources_AreInCreationOrder_HoweverTheyWereLoaded()
    {
        var file = File();
        var first = Stored(file, "Шапка", T0);
        var second = Stored(file, "Товары", T0.AddMinutes(1));
        var third = Stored(file, "Акты", T0.AddMinutes(2));

        // Так источники и приходят из базы после правки первого: его строка переехала в конец кучи.
        file.ReplaceAllSources([second, third, first]);

        Assert.Equal([first, second, third], file.Sources);
    }

    [Fact]
    public void Sources_WithEqualCreationTime_AreOrderedById()
    {
        var file = File();
        var low = Stored(file, "Б", T0, Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var high = Stored(file, "А", T0, Guid.Parse("00000000-0000-0000-0000-000000000002"));

        file.ReplaceAllSources([high, low]);
        Assert.Equal([low, high], file.Sources);

        // И в обратном порядке загрузки — то же: между равными по времени решает не порядок входа.
        file.ReplaceAllSources([low, high]);
        Assert.Equal([low, high], file.Sources);
    }

    /// <summary>
    /// Часы сервера отстали — поправка времени, машина из снимка: последний источник набора создан
    /// «в будущем». Новый всё равно встаёт в конец, а не выше существующих.
    /// </summary>
    [Fact]
    public void AddSource_GoesLast_EvenWhenClockIsBehindExistingSources()
    {
        var file = File();
        var future = DateTimeOffset.UtcNow.AddDays(1);
        var existing = Stored(file, "Шапка", future);
        file.ReplaceAllSources([existing]);

        var added = file.AddSource("Товары", "default", "[]", 0);
        var next = file.AddSource("Акты", "default", "[]", 0);

        Assert.Equal([existing, added, next], file.Sources);
        // Сдвиг — не меньше микросекунды: мельче база не хранит, и после сохранения источники
        // совпали бы по времени.
        Assert.True(added.CreatedAt - existing.CreatedAt >= TimeSpan.FromMicroseconds(1));
        Assert.True(next.CreatedAt - added.CreatedAt >= TimeSpan.FromMicroseconds(1));
    }

    [Fact]
    public void AddSource_KeepsRealTime_WhenClockIsFine()
    {
        var file = File();
        file.ReplaceAllSources([Stored(file, "Шапка", DateTimeOffset.UtcNow.AddDays(-1))]);

        var before = DateTimeOffset.UtcNow;
        var added = file.AddSource("Товары", "default", "[]", 0);

        // Время создания подправляется только при отставших часах; в обычном случае оно настоящее.
        Assert.InRange(added.CreatedAt, before, DateTimeOffset.UtcNow);
    }
}
