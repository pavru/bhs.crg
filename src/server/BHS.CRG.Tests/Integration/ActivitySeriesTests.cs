using System.Diagnostics;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Activity;
using BHS.CRG.Infrastructure.Activity;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Серии записей журнала (ТЗ CORE-28.2): сессия правки определяется при записи, журнал читается
/// рядами, ряд раскрывается в записи, копия переносит ключ.
///
/// <para>Служба журнала собирается здесь настоящая, с настоящей базой, — подменены только автор,
/// часы и каталог видов. Автор и часы — потому что граница сессии зависит от них, а в запросе их не
/// задать. Каталог — потому что ни один вид продукта до PR 2 свёртку не объявляет, и проверять
/// сессию на видах, которые не сворачиваются, было бы нечем.</para>
/// </summary>
[Collection("Integration")]
public class ActivitySeriesTests(IntegrationTestFixture fixture, ITestOutputHelper output) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string Group = "core.probe.edit";

    private static readonly ActivityAction Head =
        new("core.probe.head", "Шапка изменена", new ActivityFold(Group, FoldRule.Union, FoldRule.Union));

    private static readonly ActivityAction Lines =
        new("core.probe.lines", "Строки изменены", new ActivityFold(Group, FoldRule.Span, FoldRule.Span));

    private static readonly ActivityAction Marked = new("core.probe.marked", "Отмечен разобранным");

    private static readonly ActivityActor Ivan = new(Guid.NewGuid(), "Иван");
    private static readonly ActivityActor Petr = new(Guid.NewGuid(), "Пётр");

    // Полдень по UTC: и в поясе сервера, и в поясе компании до полуночи далеко — полночь проверяет
    // модульный тест правила, здесь она только мешала бы.
    private readonly Clock _clock = new(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
    private readonly string _invoice = Guid.NewGuid().ToString();

    // ── Сессия при записи ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Подряд_идущие_правки_одного_человека_одна_серия_и_прежние_записи_не_меняются()
    {
        await WriteAsync(Ivan, Head, after: "номер");
        var first = Assert.Single(await RecordsAsync());

        for (var i = 0; i < 4; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(3));
            await WriteAsync(Ivan, Head, after: "дата");
        }

        var records = await RecordsAsync();
        Assert.Equal(5, records.Count);
        Assert.All(records, r => Assert.Equal(first.SessionId, r.SessionId));
        Assert.Single(records, r => r.SeriesHead);
        Assert.Equivalent(first, records[0], strict: true);   // первая запись как была — ни поля не тронуто
    }

    [Fact]
    public async Task Чередование_видов_одной_группы_одна_сессия_и_две_серии()
    {
        foreach (var action in new[] { Head, Lines, Head, Lines })
        {
            await WriteAsync(Ivan, action);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        var records = await RecordsAsync();
        Assert.Single(records.Select(r => r.SessionId).Distinct());
        Assert.Equal([Head.Code, Lines.Code], records.Where(r => r.SeriesHead).Select(r => r.Action));
    }

    [Fact]
    public async Task Запись_другого_автора_рвёт_сессию_с_обеих_сторон()
    {
        await WriteAsync(Ivan, Head);
        await WriteAsync(Petr, Head);
        await WriteAsync(Ivan, Head);

        Assert.Equal(3, (await RecordsAsync()).Select(r => r.SessionId).Distinct().Count());
    }

    [Fact]
    public async Task Вид_вне_группы_стоит_отдельно_и_рвёт_сессию()
    {
        await WriteAsync(Ivan, Head);
        await WriteAsync(Ivan, Head);
        await WriteAsync(Ivan, Marked);
        await WriteAsync(Ivan, Head);

        var records = await RecordsAsync();
        Assert.Equal(3, records.Select(r => r.SessionId).Distinct().Count());
        var marked = Assert.Single(records, r => r.Action == Marked.Code);
        Assert.Equal(marked.Id, marked.SessionId);
    }

    [Fact]
    public async Task Пауза_дольше_десяти_минут_и_сессия_дольше_часа_рвут_её()
    {
        await WriteAsync(Ivan, Head);
        _clock.Advance(TimeSpan.FromMinutes(11));
        await WriteAsync(Ivan, Head);                              // пауза — новая сессия

        for (var i = 0; i < 7; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(9));               // 63 минуты от начала второй
            await WriteAsync(Ivan, Head);
        }

        var sessions = (await RecordsAsync()).GroupBy(r => r.SessionId).Select(g => g.Count()).ToList();
        Assert.Equal([1, 7, 1], sessions);
    }

    [Fact]
    public async Task Запись_без_автора_или_без_цели_одиночна()
    {
        await WriteAsync(ActivityActor.System, Head);
        await WriteAsync(ActivityActor.System, Head);
        await WriteAsync(Ivan, Head, target: null);
        await WriteAsync(Ivan, Head, target: null);

        var records = await ExportAsync();
        Assert.Equal(4, records.Count);
        Assert.All(records, r => Assert.True(r.SeriesHead && r.SessionId == r.Id));
    }

    /// <summary>
    /// Две записи одной цели одновременно (ТЗ CORE-28.2): ни одна не продолжает сессию через запись
    /// другого автора. Без замка цели обе прочитали бы одну «предыдущую» и склеились бы через чужую.
    ///
    /// Время — настоящее: под общими часами записи совпали бы по времени, и «предыдущую» решала бы
    /// добивка по идентификатору, а не порядок записи.
    /// </summary>
    [Fact]
    public async Task Одновременные_записи_двух_авторов_не_склеиваются_через_чужую()
    {
        const int Each = 25;
        var took = new List<TimeSpan>();

        await Task.WhenAll(Writer(Ivan), Writer(Petr));

        var records = await ExportAsync();
        Assert.Equal(2 * Each, records.Count);
        foreach (var session in records.GroupBy(r => r.SessionId))
            Assert.Single(session.Select(r => r.ActorId).Distinct());

        // Сессия непрерывна: запись, продолжившая её, идёт сразу за записью той же сессии.
        for (var i = 1; i < records.Count; i++)
            if (records[i].SessionId != records[i].Id)
                Assert.Equal(records[i].SessionId, records[i - 1].SessionId);

        lock (took)
            output.WriteLine($"Запись под замком цели: среднее {took.Average(t => t.TotalMilliseconds):F1} мс, " +
                $"наибольшее {took.Max(t => t.TotalMilliseconds):F1} мс на {took.Count} записях в два потока.");

        async Task Writer(ActivityActor who)
        {
            for (var i = 0; i < Each; i++)
            {
                using var scope = fixture.Services.CreateScope();
                var journal = Journal(scope, who, TimeProvider.System);
                var watch = Stopwatch.StartNew();
                await journal.RecordAsync(Head, _invoice, "Счёт № 14", after: "номер");
                lock (took) took.Add(watch.Elapsed);
            }
        }
    }

    // ── Чтение рядами ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Серия_из_девяти_записей_один_ряд_с_интервалом_и_объединённым_перечнем()
    {
        var started = _clock.GetUtcNow();
        var last = started;
        string[] fields = ["номер", "дата", "поставщик"];
        for (var i = 0; i < 9; i++)
        {
            if (i > 0) _clock.Advance(TimeSpan.FromMinutes(2));
            last = _clock.GetUtcNow();
            await WriteAsync(Ivan, Head, after: fields[i % 3]);
        }

        var row = Assert.Single(await RowsAsync());
        Assert.Equal(9, row.Count);
        Assert.Equal(started, row.OccurredAt);
        Assert.Equal(last, row.LastAt);
        Assert.Equal("номер, дата, поставщик", row.After);
        Assert.Null(row.FirstTargetLabel);

        // Раскрытие — те же девять записей, каждая со своим временем и значением.
        var series = await SeriesAsync(row.SessionId, Head.Code);
        Assert.Equal(9, series.Count);
        Assert.Equal("номер", series[0].After);
        Assert.Equal(last, series[^1].OccurredAt);
    }

    [Fact]
    public async Task Ряд_называет_цель_последним_именем_и_прежним()
    {
        await WriteAsync(Ivan, Head, label: "Счёт № 14");
        await WriteAsync(Ivan, Head, label: "Счёт № 41");

        var row = Assert.Single(await RowsAsync());
        Assert.Equal("Счёт № 41", row.TargetLabel);
        Assert.Equal("Счёт № 14", row.FirstTargetLabel);
    }

    [Fact]
    public async Task Вторая_страница_не_едет_пока_серия_на_первой_растёт()
    {
        for (var i = 0; i < 4; i++)
        {
            await WriteAsync(Ivan, Marked, target: Guid.NewGuid().ToString());
            _clock.Advance(TimeSpan.FromMinutes(1));
        }
        await WriteAsync(Ivan, Head);                              // самая свежая — на первой странице

        var before = await RowsAsync(skip: 2, take: 2);
        for (var i = 0; i < 3; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            await WriteAsync(Ivan, Head);
        }

        Assert.Equal(before.Select(r => r.Id), (await RowsAsync(skip: 2, take: 2)).Select(r => r.Id));
        Assert.Equal(5, await CountAsync());
    }

    [Fact]
    public async Task Отбор_по_действию_отдаёт_ряды_вида_не_склеивая_разные_сессии()
    {
        await WriteAsync(Ivan, Head);
        await WriteAsync(Ivan, Lines, after: "3 строки");
        await WriteAsync(Ivan, Lines, after: "5 строк");
        _clock.Advance(TimeSpan.FromMinutes(30));
        await WriteAsync(Ivan, Lines, after: "6 строк");

        var rows = await RowsAsync(action: Lines.Code);
        Assert.Equal([1, 2], rows.Select(r => r.Count));         // новые первыми
        Assert.Equal("5 строк", rows[1].After);
        Assert.Equal(2, await CountAsync(Lines.Code));
    }

    [Fact]
    public async Task Скрытая_серия_не_раскрывается_как_и_несуществующая()
    {
        await WriteAsync(Ivan, Head);
        await WriteAsync(Ivan, Head);
        var session = Assert.Single(await RowsAsync()).SessionId;

        var hidden = ActivityVisibility.Of(["core"], [Head.Code]);
        using var scope = fixture.Services.CreateScope();
        var journal = Journal(scope, Ivan, _clock);
        Assert.Empty(await journal.SeriesAsync(session, Head.Code, hidden));
        Assert.Empty(await journal.SeriesAsync(Guid.NewGuid(), Head.Code, ActivityVisibility.Whole));
        Assert.Empty(await journal.ReadAsync(0, 50, hidden));
        Assert.Equal(0, await journal.CountAsync(hidden));
    }

    // ── Копия ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Копия, снятая посреди серии, на чистом экземпляре даёт те же ряды, а на работающем — без
    /// дублей (ТЗ CORE-28.2, CORE-26). Путь тот же, что у восстановления: выгрузка и приём службой.
    /// </summary>
    [Fact]
    public async Task Копия_посреди_серии_переносит_ряды_без_дублей()
    {
        await WriteAsync(Ivan, Head, after: "номер");
        await WriteAsync(Ivan, Lines, after: "3 строки");
        await WriteAsync(Ivan, Head, after: "дата");
        var rows = await RowsAsync();
        var backup = await ExportAsync();

        await fixture.ResetDatabaseAsync();
        Assert.Equal(backup.Count, await ImportAsync(backup));
        Assert.Equal(rows, await RowsAsync());

        Assert.Equal(0, await ImportAsync(backup));
        Assert.Equal(rows, await RowsAsync());
    }

    /// <summary>
    /// Копия старого формата — без сессии: каждая запись ложится одиночным рядом, без отказа. Путь
    /// тот же, что у восстановления копии (<c>ActivityRecord.Create</c> с <c>sessionId: null</c>).
    /// </summary>
    [Fact]
    public async Task Копия_старого_формата_читается_одиночными_рядами()
    {
        var legacy = Enumerable.Range(0, 3).Select(i => ActivityRecord.Create(
            Head.Code, Ivan.Id, Ivan.Name, _invoice, "Счёт № 14", after: "номер",
            id: Guid.NewGuid(), occurredAt: _clock.GetUtcNow().AddMinutes(i),
            sessionId: null, seriesHead: false)).ToList();

        Assert.Equal(3, await ImportAsync(legacy));

        var rows = await RowsAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(1, r.Count));
    }

    // ── Помощники ────────────────────────────────────────────────────────────────────────────

    /// <param name="target">Цель записи; по умолчанию — счёт теста, <c>null</c> — запись без цели.</param>
    private async Task WriteAsync(ActivityActor who, ActivityAction action, string? after = null,
        string? label = "Счёт № 14", string? target = "")
    {
        using var scope = fixture.Services.CreateScope();
        await Journal(scope, who, _clock).RecordAsync(action, target == "" ? _invoice : target, label, after: after);
        _clock.Advance(TimeSpan.FromSeconds(1));
    }

    private async Task<IReadOnlyList<ActivityRecord>> ExportAsync()
    {
        using var scope = fixture.Services.CreateScope();
        return await Journal(scope, Ivan, _clock).ExportAsync();
    }

    // Записи цели по порядку записи.
    private async Task<List<ActivityRecord>> RecordsAsync() =>
        [.. (await ExportAsync()).Where(r => r.TargetId == _invoice)];

    private async Task<IReadOnlyList<ActivityRow>> RowsAsync(int skip = 0, int take = 50, string? action = null)
    {
        using var scope = fixture.Services.CreateScope();
        return await Journal(scope, Ivan, _clock).ReadAsync(skip, take, ActivityVisibility.Whole, action);
    }

    private async Task<int> CountAsync(string? action = null)
    {
        using var scope = fixture.Services.CreateScope();
        return await Journal(scope, Ivan, _clock).CountAsync(ActivityVisibility.Whole, action);
    }

    private async Task<IReadOnlyList<ActivityRecord>> SeriesAsync(Guid session, string action)
    {
        using var scope = fixture.Services.CreateScope();
        return await Journal(scope, Ivan, _clock).SeriesAsync(session, action, ActivityVisibility.Whole);
    }

    private async Task<int> ImportAsync(IReadOnlyList<ActivityRecord> records)
    {
        using var scope = fixture.Services.CreateScope();
        return await Journal(scope, Ivan, _clock).ImportAsync(records);
    }

    private static ActivityLog Journal(IServiceScope scope, ActivityActor who, TimeProvider clock) =>
        new(scope.ServiceProvider.GetRequiredService<AppDbContext>(), new Fixed(who), new Probes(),
            scope.ServiceProvider.GetRequiredService<IAppSettingsStore>(), clock);

    private sealed class Fixed(ActivityActor who) : IActivityActor
    {
        public ActivityActor Current => who;
    }

    private sealed class Probes : IActivityActionLookup
    {
        public ActivityAction? Find(string code) => new[] { Head, Lines, Marked }.FirstOrDefault(a => a.Code == code);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
