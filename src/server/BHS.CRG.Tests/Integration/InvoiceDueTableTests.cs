using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Хост счетов с ПОДСТАВНЫМ «сегодня»: порт часов заменён, остальное — как у <see cref="InvoiceLineHost" />
/// (та же база, тот же состав модулей). Заменяется именно порт, а не часы приложения: по часам
/// приложения живёт и срок жизни входа, и сдвинутые на годы они увели бы тест не туда.
/// </summary>
public sealed class InvoiceClockHost : InvoiceLineHost
{
    public TestClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IModuleClock>();
            services.AddSingleton<IModuleClock>(Clock);
        });
    }

    public sealed class TestClock : IModuleClock
    {
        public DateOnly Today { get; set; }

        public Task<DateOnly> TodayAsync(CancellationToken ct = default) => Task.FromResult(Today);
    }
}

/// <summary>
/// «Осталось дней» и «Просрочен» в таблице счетов (ТЗ COST-9.1, CORE-33; задача G1c, issue #1090):
/// считаются от сегодня, не хранятся, и по ним работают отбор и сортировка — одинаково у запроса к базе
/// и у набора данных в памяти.
///
/// <para>«Сегодня» здесь — 15 января 2031 года, нарочно далеко от настоящего: возьми служба строк часы
/// сервера вместо порта, ни один срок из посева просроченным бы не оказался, и тест это покажет.</para>
///
/// <para>⚠️ Фабрик у класса две: своя и унаследованная от <see cref="InvoiceLineTestBase" />, которая
/// объявляет <see cref="InvoiceLineHost" />. Вторую xUnit создаёт и освобождает, но приложение в ней не
/// стартует — фабрика поднимает его первым обращением, а к ней никто не обращается. Лечится базовым
/// классом, обобщённым по типу хоста; не сделано, чтобы не перекраивать оснастку всех проверок строк
/// счёта ради одного класса.</para>
/// </summary>
public sealed class InvoiceDueTableTests(InvoiceClockHost host)
    : ModuleTableSeededTests(host), IClassFixture<InvoiceClockHost>
{
    private static readonly DateOnly Today = new(2031, 1, 15);

    private const string DaysLeft = "ДнейДоСрока";
    private const string Overdue = "СрокПросрочен";

    [Fact]
    public async Task Срок_считается_от_сегодня_и_только_у_счёта_который_ждёт_оплаты()
    {
        host.Clock.Today = Today;
        var (client, user) = await SignInAsync("Supplier");
        var tag = await SeedAsync(client,
            (Today.AddDays(-1), "Unpaid", "Draft"),    // 1: срок вчера — просрочен на день
            (Today, "Unpaid", "Draft"),                // 2: срок сегодня — ещё не просрочен
            (Today.AddDays(1), "Unpaid", "Parsed"),    // 3: срок завтра
            (Today.AddDays(-1), "Unpaid", "Parsed"),   // 4: разобран, но не оплачен — просрочен
            (Today.AddDays(-1), "Paid", "Draft"),      // 5: оплачен — срока больше нет
            (Today.AddDays(-1), "Unpaid", "Rejected"), // 6: отклонён, «не платим» — оплаты не ждёт
            (null, "Unpaid", "Draft"));                // 7: срок не определён

        var rows = await RowsAsync(client, $"columns=Номер,{DaysLeft},{Overdue}&sort=Номер&filter={Own(tag)}");

        Assert.Equal(
            [
                ($"{tag}-1", -1m, true), ($"{tag}-2", 0m, false), ($"{tag}-3", 1m, false), ($"{tag}-4", -1m, true),
                ($"{tag}-5", null, false), ($"{tag}-6", null, false), ($"{tag}-7", null, false),
            ],
            rows.Select(r => (
                r.GetProperty("Номер").GetString()!,
                r.GetProperty(DaysLeft).ValueKind == JsonValueKind.Null ? (decimal?)null : r.GetProperty(DaysLeft).GetDecimal(),
                r.GetProperty(Overdue).GetBoolean())));

        // Отбор — в запросе к базе и в памяти (набор данных на той же таблице) — одни и те же строки.
        var memory = await MemoryRowsAsync(user);
        async Task Same(string condition, params int[] expected)
        {
            var filter = Filter(tag, condition);
            var want = expected.Select(n => $"{tag}-{n}").ToList();
            Assert.Equal(want, await SqlAsync(client, filter));
            Assert.Equal(want, Numbers(DataSetRowFilterExecutor.Apply(filter, [.. memory.Rows], "пара", memory.Types)));
        }

        await Same($$"""{"type":"condition","column":"{{Overdue}}","op":"eq","value":"true"}""", 1, 4);
        await Same($$"""{"type":"condition","column":"{{Overdue}}","op":"neq","value":"true"}""", 2, 3, 5, 6, 7);
        await Same($$"""{"type":"condition","column":"{{DaysLeft}}","op":"lte","value":"0"}""", 1, 2, 4);
        await Same($$"""{"type":"condition","column":"{{DaysLeft}}","op":"between","values":["0","1"]}""", 2, 3);
        await Same($$"""{"type":"condition","column":"{{DaysLeft}}","op":"is_null"}""", 5, 6, 7);

        // Сортировка: самые просроченные сверху, счета без срока — в конце в обоих направлениях.
        async Task<string[]> Order(string sort) => [.. (await RowsAsync(client,
            $"columns=Номер&sort={sort},Номер&filter={Own(tag)}")).Select(r => r.GetProperty("Номер").GetString()![^1..])];

        Assert.Equal(["1", "4", "2", "3", "5", "6", "7"], await Order(DaysLeft));
        Assert.Equal(["3", "2", "1", "4", "5", "6", "7"], await Order($"{DaysLeft}:desc"));
        Assert.Equal(["1", "4", "2", "3", "5", "6", "7"], await Order($"{Overdue}:desc"));
    }

    /// <summary>
    /// «Просрочен» не хранится нигде: наступил следующий день — признак сменился, а в счёт никто не
    /// писал. Храни мы его значением, он был бы верен ровно до полуночи.
    /// </summary>
    [Fact]
    public async Task Наступил_следующий_день_счёт_просрочен_и_в_него_никто_не_писал()
    {
        host.Clock.Today = Today;
        var (client, _) = await SignInAsync("Supplier");
        var tag = await SeedAsync(client, (Today, "Unpaid", "Draft"));
        var query = $"columns=Номер,{DaysLeft},{Overdue}&totals={DaysLeft}&filter={Own(tag)}";

        var today = Assert.Single(await RowsAsync(client, query));
        Assert.False(today.GetProperty(Overdue).GetBoolean());
        Assert.Equal(0m, today.GetProperty(DaysLeft).GetDecimal());
        var written = await UpdatedAtAsync(tag);

        host.Clock.Today = Today.AddDays(1);

        var tomorrow = Assert.Single(await RowsAsync(client, query));
        Assert.True(tomorrow.GetProperty(Overdue).GetBoolean());
        Assert.Equal(-1m, tomorrow.GetProperty(DaysLeft).GetDecimal());
        Assert.Equal(written, await UpdatedAtAsync(tag));
    }

    // ── Помощники ───────────────────────────────────────────────────────────────

    /// <summary>Счета со сроком, оплатой и состоянием; номера — «{метка}-1…N». Отдаёт метку.</summary>
    private async Task<string> SeedAsync(HttpClient client, params (DateOnly? Due, string Payment, string State)[] rows)
    {
        var tag = $"С{Guid.NewGuid().ToString("N")[..6]}";
        using var scope = host.Services.CreateScope();
        var costs = scope.ServiceProvider.GetRequiredService<CostsDbContext>();

        for (var i = 0; i < rows.Length; i++)
        {
            var id = await CreateAsync(client);
            var (due, payment, state) = rows[i];
            var number = $"{tag}-{i + 1}";
            await costs.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE costs.invoices SET number = {number}, due_date = {due}, payment = {payment}, state = {state},
                    paid_on = CASE WHEN {payment} = 'Paid' THEN DATE '2026-09-01' END
                WHERE id = {id}
                """);
        }
        return tag;
    }

    private async Task<DateTimeOffset> UpdatedAtAsync(string tag)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CostsDbContext>().Invoices.AsNoTracking()
            .Where(i => i.Number!.StartsWith(tag)).Select(i => i.UpdatedAt).SingleAsync();
    }

    private static string Filter(string tag, string? condition = null) =>
        $$"""{"type":"group","logic":"and","children":[{"type":"condition","column":"Номер","op":"starts_with","value":"{{tag}}"}{{(condition is null ? "" : "," + condition)}}]}""";

    private static string Own(string tag) => Uri.EscapeDataString(Filter(tag));

    private static async Task<List<JsonElement>> RowsAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/tables/{Address}?{query}");
        await OkAsync(response);
        var table = await response.Content.ReadFromJsonAsync<JsonElement>();
        return [.. table.GetProperty("rows").EnumerateArray()];
    }
}
