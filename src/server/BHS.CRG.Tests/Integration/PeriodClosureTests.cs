using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Periods;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Periods;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Contour = BHS.CRG.Domain.Periods.PeriodContour;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Служба закрытия периода на живой базе (ТЗ CORE-35; задача E1a, issue #1081): адреса и права,
/// порт модулей, замок «запись против закрытия» на двух настоящих соединениях, защита записей и
/// резервная копия.
///
/// <para>Чистые правила границ — в <c>PeriodLedgerTests</c>; здесь то, что без базы и без второго
/// соединения не проверить.</para>
/// </summary>
[Collection("Integration")]
public class PeriodClosureTests(InvoiceLineHost host) : InvoiceLineTestBase(host), IDisposable
{
    /// <summary>
    /// Закрытия — состояние всего экземпляра: оставленное соседнему классу на этой базе, оно закрыло
    /// бы ему месяц. Убираем ПОСЛЕ каждого теста, прямым SQL: служба стирать не умеет нарочно.
    /// </summary>
    public void Dispose()
    {
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
            .ExecuteSqlRaw("TRUNCATE TABLE period_closures");
    }

    // ── Адреса, права, порт ───────────────────────────────────────────────────

    /// <summary>
    /// Граница от закрытия до экрана и до порта модулей: у стройки без своих закрытий — граница
    /// компании, и читает её вошедший БЕЗ права закрывать.
    /// </summary>
    [Fact]
    public async Task Закрытие_компании_видно_всем_вошедшим_и_порту_модулей()
    {
        var (accountant, _) = await SignInAsync("Accountant");
        var (installer, _) = await SignInAsync("Installer");
        var (site, _) = await SiteAsync("Под период");
        var through = (await TodayAsync()).AddDays(-10);

        var refused = await installer.PostAsJsonAsync("/api/periods/close", CloseBody(through.AddDays(-30), through, "none"));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        await ClosedAsync(accountant, through.AddDays(-30), through, "none");

        var view = await installer.GetFromJsonAsync<JsonElement>("/api/periods");
        Assert.Equal(Iso(through), view.GetProperty("company").GetProperty("closedThrough").GetString());
        Assert.Equal(Iso(through.AddDays(1)), view.GetProperty("company").GetProperty("expectedFrom").GetString());

        var row = view.GetProperty("constructions").EnumerateArray()
            .Single(c => c.GetProperty("constructionId").GetGuid() == site);
        Assert.Equal(Iso(through), row.GetProperty("closedThrough").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("ownClosedThrough").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("reopenable").ValueKind);

        using var scope = host.Services.CreateScope();
        var boundaries = await scope.ServiceProvider.GetRequiredService<IModulePeriods>().BoundariesAsync();
        Assert.Equal(through, boundaries.ClosedThrough(new Modules.Ports.PeriodContour.Company()));
        Assert.True(boundaries.IsClosed(through, new Modules.Ports.PeriodContour.Construction(site)));
        Assert.Equal(through.AddDays(1),
            boundaries.AccountingDate(through.AddDays(-3), new Modules.Ports.PeriodContour.Construction(site)));

        Assert.Equal(HttpStatusCode.Forbidden, (await installer.GetAsync("/api/periods/history")).StatusCode);
        var history = await accountant.GetFromJsonAsync<JsonElement>("/api/periods/history");
        Assert.Equal("Close", history[0].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Отказы_закрытия_называют_причину()
    {
        var (accountant, _) = await SignInAsync("Accountant");
        var today = await TodayAsync();
        var through = today.AddDays(-10);

        // Без увиденной границы «закрыть» подтверждает человек, а закрывается состояние, которого он не видел.
        var blind = await accountant.PostAsJsonAsync("/api/periods/close",
            new { contour = "company", from = Iso(through.AddDays(-5)), through = Iso(through) });
        Assert.Equal(HttpStatusCode.BadRequest, blind.StatusCode);
        Assert.Contains("ifMatch", await ErrorAsync(blind));

        var future = await accountant.PostAsJsonAsync("/api/periods/close", CloseBody(through, today, "none"));
        Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);
        Assert.Contains("только прошедшие дни", await ErrorAsync(future));

        await ClosedAsync(accountant, through.AddDays(-5), through, "none");

        // Вторая вкладка всё ещё видит «не закрыто ничего».
        var stale = await accountant.PostAsJsonAsync("/api/periods/close",
            CloseBody(through.AddDays(1), through.AddDays(2), "none"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("тем временем изменили", await ErrorAsync(stale));

        var gap = await accountant.PostAsJsonAsync("/api/periods/close",
            CloseBody(through.AddDays(2), through.AddDays(3), Iso(through)));
        Assert.Equal(HttpStatusCode.BadRequest, gap.StatusCode);
        Assert.Contains($"начинается {through.AddDays(1):dd.MM.yyyy}", await ErrorAsync(gap));

        var nowhere = await accountant.PostAsJsonAsync("/api/periods/close",
            CloseBody(through.AddDays(1), through.AddDays(2), Iso(through), Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, nowhere.StatusCode);
    }

    [Fact]
    public async Task Отмена_закрытия_требует_причину_и_возвращает_прежнюю_границу()
    {
        var (accountant, _) = await SignInAsync("Accountant");
        var (site, _) = await SiteAsync("Под отмену");
        var through = (await TodayAsync()).AddDays(-20);

        await ClosedAsync(accountant, through.AddDays(-5), through, "none");
        // Стройка закрывается дальше компании — и начало её периода считается от границы компании.
        await ClosedAsync(accountant, through.AddDays(1), through.AddDays(5), Iso(through), site);

        var silent = await accountant.PostAsJsonAsync("/api/periods/reopen",
            new { contour = "construction", constructionId = site, ifMatch = Iso(through.AddDays(5)) });
        Assert.Equal(HttpStatusCode.BadRequest, silent.StatusCode);
        Assert.Contains("причину", await ErrorAsync(silent));

        await OkAsync(await accountant.PostAsJsonAsync("/api/periods/reopen",
            new { contour = "construction", constructionId = site, ifMatch = Iso(through.AddDays(5)), reason = "не тот месяц" }));

        var view = await accountant.GetFromJsonAsync<JsonElement>("/api/periods");
        var row = view.GetProperty("constructions").EnumerateArray()
            .Single(c => c.GetProperty("constructionId").GetGuid() == site);
        Assert.Equal(Iso(through), row.GetProperty("closedThrough").GetString());

        // Больше у стройки отменять нечего: она закрыта закрытием компании.
        var nothing = await accountant.PostAsJsonAsync("/api/periods/reopen",
            new { contour = "construction", constructionId = site, ifMatch = Iso(through), reason = "ещё раз" });
        Assert.Equal(HttpStatusCode.Conflict, nothing.StatusCode);

        await OkAsync(await accountant.PostAsJsonAsync("/api/periods/reopen",
            new { contour = "company", ifMatch = Iso(through), reason = "закрыли по ошибке" }));
        view = await accountant.GetFromJsonAsync<JsonElement>("/api/periods");
        Assert.Equal(JsonValueKind.Null, view.GetProperty("company").GetProperty("closedThrough").ValueKind);
    }

    /// <summary>
    /// Событие в журнале ядра называет контур и даты — и ни одного числа модулей (H1): журнал читают
    /// по <c>core.audit.read</c>, а суммы счетов закрыты правом модуля.
    /// </summary>
    [Fact]
    public async Task Журнал_называет_контур_и_даты_без_чисел_модулей()
    {
        var through = (await TodayAsync()).AddDays(-10);
        using var scope = host.Services.CreateScope();
        var closures = scope.ServiceProvider.GetRequiredService<IPeriodClosures>();
        var journal = scope.ServiceProvider.GetRequiredService<IActivityLog>();

        var before = await journal.CountAsync(ActivityVisibility.Whole, ActivityActions.PeriodClosed.Code);
        await closures.CloseAsync(new ClosePeriod(Contour.Company, through.AddDays(-30), through, null, null));
        await closures.ReopenAsync(new ReopenPeriod(Contour.Company, through, "закрыли не тот месяц"));

        var closed = await journal.LastAsync(ActivityActions.PeriodClosed);
        Assert.NotNull(closed);
        Assert.Equal("Компания", closed.TargetLabel);
        Assert.Equal("не закрыто ничего", closed.Before);
        Assert.Equal($"закрыто по {through:dd.MM.yyyy}", closed.After);

        // Сколько закрытий — столько и событий: журнал пишется в той же транзакции.
        Assert.Equal(before + 1, await journal.CountAsync(ActivityVisibility.Whole, ActivityActions.PeriodClosed.Code));

        var reopened = await journal.LastAsync(ActivityActions.PeriodReopened);
        Assert.NotNull(reopened);
        Assert.Equal($"не закрыто ничего. Причина: закрыли не тот месяц", reopened.After);
    }

    // ── Защита записей ────────────────────────────────────────────────────────

    [Fact]
    public async Task Запись_о_закрытии_нельзя_ни_поправить_ни_удалить()
    {
        var through = (await TodayAsync()).AddDays(-10);
        using var scope = host.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<IPeriodClosures>()
            .CloseAsync(new ClosePeriod(Contour.Company, through.AddDays(-30), through, null, null));
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.Attach(row).State = EntityState.Modified;
        var onEdit = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("изменению и удалению не подлежат", onEdit.Message);
        Assert.Contains("закрытие периода", onEdit.Message);

        db.Entry(row).State = EntityState.Deleted;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    // ── Замок «запись против закрытия» ────────────────────────────────────────

    /// <summary>
    /// Два настоящих соединения: модуль пишет через связку и держит разделяемый замок до конца
    /// транзакции, закрытие ждёт его — и, не дождавшись, отвечает отказом, а не тишиной. Когда запись
    /// зафиксирована, закрытие проходит.
    /// </summary>
    [Fact]
    public async Task Закрытие_ждёт_запись_модуля_и_отказывает_не_дождавшись()
    {
        var through = (await TodayAsync()).AddDays(-10);
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        using var moduleScope = host.Services.CreateScope();
        var costs = moduleScope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var periods = moduleScope.ServiceProvider.GetRequiredService<IModulePeriods>();

        var writing = costs.InOpenPeriodAsync(periods, async boundaries =>
        {
            // Модуль проверил: период открыт. Пока он не зафиксировал запись, закрыть его нельзя.
            Assert.False(boundaries.IsClosed(through, new Modules.Ports.PeriodContour.Company()));
            entered.SetResult();
            await release.Task;
        });
        await entered.Task;

        using (var scope = host.Services.CreateScope())
        {
            var refusal = await Assert.ThrowsAsync<ConflictException>(() =>
                scope.ServiceProvider.GetRequiredService<IPeriodClosures>()
                    .CloseAsync(new ClosePeriod(Contour.Company, through.AddDays(-30), through, null, null)));
            Assert.Contains("идёт запись", refusal.Message);
        }

        release.SetResult();
        await writing;
        Assert.Null(costs.Database.CurrentTransaction);

        using (var scope = host.Services.CreateScope())
        {
            var closures = scope.ServiceProvider.GetRequiredService<IPeriodClosures>();
            await closures.CloseAsync(new ClosePeriod(Contour.Company, through.AddDays(-30), through, null, null));
            Assert.Equal(through, (await closures.LedgerAsync()).Company);
        }

        // Следующая запись видит закрытие: снимок читается ПОСЛЕ замка.
        var seen = await costs.InOpenPeriodAsync(periods, boundaries =>
            Task.FromResult(boundaries.IsClosed(through, new Modules.Ports.PeriodContour.Company())));
        Assert.True(seen);
    }

    /// <summary>
    /// Вне транзакции замок снялся бы тем же запросом — и вызов выглядел бы защитой, не будучи ею.
    /// Поэтому он отказывает.
    /// </summary>
    [Fact]
    public async Task Замок_записи_вне_транзакции_отказывает()
    {
        using var scope = host.Services.CreateScope();
        var costs = scope.ServiceProvider.GetRequiredService<CostsDbContext>();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => OpenPeriodWrite.HoldAsync(costs));
        Assert.Contains("только внутри транзакции", refusal.Message);

        // В открытой транзакции связка использует её и НЕ фиксирует: замок живёт до конца чужой.
        await using var tx = await costs.Database.BeginTransactionAsync();
        await costs.InOpenPeriodAsync(scope.ServiceProvider.GetRequiredService<IModulePeriods>(), _ => Task.CompletedTask);
        Assert.NotNull(costs.Database.CurrentTransaction);
        Assert.Equal(1, await HeldSharedAsync(costs));
    }

    // ── Резервная копия ───────────────────────────────────────────────────────

    /// <summary>
    /// Копия несёт закрытия, восстановление дописывает недостающие — и поверх экземпляра с более
    /// поздним закрытием не «открывает» период: граница остаётся позднейшей.
    /// </summary>
    [Fact]
    public async Task Копия_переносит_закрытия_и_не_открывает_закрытое_позже()
    {
        var through = (await TodayAsync()).AddDays(-20);
        byte[] zip;

        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPeriodClosures>()
                .CloseAsync(new ClosePeriod(Contour.Company, through.AddDays(-30), through, null, null));
            var (stream, _) = await scope.ServiceProvider.GetRequiredService<BackupService>().ExportAsync(BackupScope.Full);
            await using (stream)
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                zip = buffer.ToArray();
            }
        }

        // Экземпляр без закрытий: копия возвращает границу.
        Dispose();
        using (var scope = host.Services.CreateScope())
        {
            var report = await scope.ServiceProvider.GetRequiredService<BackupService>().ImportAsync(new MemoryStream(zip));
            Assert.True(report.Success, string.Join("; ", report.Warnings));
            Assert.Equal(through, (await scope.ServiceProvider.GetRequiredService<IPeriodClosures>().LedgerAsync()).Company);
        }

        // Экземпляр ушёл дальше копии: восстановление не откатывает границу и не падает на повторе.
        using (var scope = host.Services.CreateScope())
        {
            var closures = scope.ServiceProvider.GetRequiredService<IPeriodClosures>();
            await closures.CloseAsync(new ClosePeriod(Contour.Company, through.AddDays(1), through.AddDays(5), through, null));
        }
        using (var scope = host.Services.CreateScope())
        {
            var report = await scope.ServiceProvider.GetRequiredService<BackupService>().ImportAsync(new MemoryStream(zip));
            Assert.True(report.Success, string.Join("; ", report.Warnings));
            var closures = scope.ServiceProvider.GetRequiredService<IPeriodClosures>();
            Assert.Equal(through.AddDays(5), (await closures.LedgerAsync()).Company);
            Assert.Equal(2, (await closures.ExportAsync()).Count);
        }
    }

    /// <summary>
    /// Восстановление сдвигает границу — значит, идёт под тем же замком, что и закрытие: запись
    /// модуля, проверившая «период открыт», не должна оказаться в периоде, закрытом копией посреди
    /// неё. И повтор записи внутри самой копии — пропуск, а не падение.
    /// </summary>
    [Fact]
    public async Task Записи_из_копии_ждут_запись_модуля_и_не_падают_на_повторе()
    {
        var through = (await TodayAsync()).AddDays(-10);
        var closure = PeriodClosure.Close(Contour.Company, through.AddDays(-30), through, null, "Из копии", null,
            DateTimeOffset.UtcNow, ClosingReport.Empty);
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        using var moduleScope = host.Services.CreateScope();
        var costs = moduleScope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var writing = costs.InOpenPeriodAsync(moduleScope.ServiceProvider.GetRequiredService<IModulePeriods>(),
            async _ =>
            {
                entered.SetResult();
                await release.Task;
            });
        await entered.Task;

        using (var scope = host.Services.CreateScope())
            await Assert.ThrowsAsync<ConflictException>(() =>
                scope.ServiceProvider.GetRequiredService<IPeriodClosures>().ImportAsync([closure]));

        release.SetResult();
        await writing;

        using (var scope = host.Services.CreateScope())
        {
            var closures = scope.ServiceProvider.GetRequiredService<IPeriodClosures>();
            Assert.Equal(1, await closures.ImportAsync([closure, closure]));
            Assert.Equal(0, await closures.ImportAsync([closure]));
            Assert.Equal(through, (await closures.LedgerAsync()).Company);
        }
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    private async Task<DateOnly> TodayAsync()
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPeriodClosures>().TodayAsync();
    }

    /// <param name="report">Отпечаток перечня. По умолчанию — выдуманный: отказам по границе и датам
    /// он не нужен, они случаются раньше сверки перечня.</param>
    private static object CloseBody(
        DateOnly from, DateOnly through, string ifMatch, Guid? site = null, string report = "не смотрел") => new
    {
        contour = site is null ? "company" : "construction",
        constructionId = site,
        from = Iso(from),
        through = Iso(through),
        ifMatch,
        report,
    };

    /// <summary>Закрыть так, как закрывает экран: посмотреть перечень и назвать его отпечаток.</summary>
    private static async Task ClosedAsync(HttpClient client, DateOnly from, DateOnly through, string ifMatch, Guid? site = null)
    {
        var preview = await client.PostAsJsonAsync("/api/periods/close/preview", CloseBody(from, through, ifMatch, site));
        await OkAsync(preview);
        var stamp = (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stamp").GetString()!;
        await OkAsync(await client.PostAsJsonAsync("/api/periods/close", CloseBody(from, through, ifMatch, site, stamp)));
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd");

    private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

    /// <summary>Сколько разделяемых замков записи в учёт держит это соединение.</summary>
    private static async Task<int> HeldSharedAsync(CostsDbContext db) =>
        await db.Database.SqlQueryRaw<int>(
            $"""
             SELECT count(*)::int AS "Value" FROM pg_locks
             WHERE locktype = 'advisory' AND granted AND mode = 'ShareLock'
               AND pid = pg_backend_pid() AND objid = {AdvisoryLockKeys.PeriodWrite}
             """).SingleAsync();
}
