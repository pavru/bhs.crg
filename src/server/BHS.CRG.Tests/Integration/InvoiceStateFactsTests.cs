using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Periods;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Факты для индикатора состояния счёта (change <c>costs-invoice-screen-02-state-facts</c>, COST-9.1 и
/// COST-6.4): срок в ответе счёта, «чему мешает» у пунктов готовности, род отказа оплаты и автор последней
/// записи.
///
/// <para><b>Сторож — согласие источников.</b> Индикатор не пересчитывает правила на клиенте, поэтому каждое
/// новое поле проверяется против того, с чем оно обязано совпадать: срок — со строкой реестра, «мешает
/// оплате» — с родом отказа и с фактическим отказом <c>POST …/paid</c>.</para>
/// </summary>
[Collection("Integration")]
public class InvoiceStateFactsTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    // ── Срок (COST-9.1) ───────────────────────────────────────────────────────

    /// <summary>
    /// «Осталось дней» и «просрочен» в ответе счёта — те же числа, что строка реестра: одно правило и одно
    /// «сегодня» в поясе компании. У оплаченного «осталось» пусто, «просрочен» не бывает.
    /// </summary>
    [Fact]
    public async Task Срок_в_ответе_счёта_тот_же_что_в_реестре()
    {
        var (client, _) = await SignInAsync("Admin");
        var today = await TodayAsync();

        var ahead = await DueAsync(client, today.AddDays(5));
        var late = await DueAsync(client, today.AddDays(-1));
        var paid = await DueAsync(client, today.AddDays(-3));
        await PayAsync(client, paid, today);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices");
        JsonElement Row(Guid id) => list.EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == id);

        foreach (var id in new[] { ahead, late, paid })
        {
            var due = (await ReadAsync(client, id)).GetProperty("due");
            Assert.Equal(Row(id).GetProperty("daysLeft").GetRawText(), due.GetProperty("daysLeft").GetRawText());
            Assert.Equal(Row(id).GetProperty("overdue").GetBoolean(), due.GetProperty("overdue").GetBoolean());
        }

        var view = (await ReadAsync(client, ahead)).GetProperty("due");
        Assert.Equal(5, view.GetProperty("daysLeft").GetInt32());
        Assert.False(view.GetProperty("overdue").GetBoolean());
        Assert.True((await ReadAsync(client, late)).GetProperty("due").GetProperty("overdue").GetBoolean());
        view = (await ReadAsync(client, paid)).GetProperty("due");
        Assert.Equal(JsonValueKind.Null, view.GetProperty("daysLeft").ValueKind);
        Assert.False(view.GetProperty("overdue").GetBoolean());
    }

    // ── Готовность и род отказа (COST-6.4) ────────────────────────────────────

    /// <summary>
    /// Расхождение — один пункт: <c>total</c> мешает и «Разобран», и оплате; род отказа оплаты —
    /// <c>discrepancy</c>; сама оплата отказывает той же причиной. Три источника согласованы.
    /// </summary>
    [Fact]
    public async Task Расхождение_мешает_оплате_одним_пунктом_и_тем_же_родом_что_отказ()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await AllocatedLinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m, rate: 20)]);
        await TotalAsync(client, invoice, 999m);

        var view = await ReadAsync(client, invoice);
        var gap = Assert.Single(Gaps(view));
        Assert.Equal("total", gap.GetProperty("kind").GetString());
        Assert.Equal([InvoiceReadiness.Parsed, InvoiceReadiness.Payment], Blocks(gap));
        Assert.Equal(PaymentRefusal.Discrepancy, Kind(view));

        var refused = await PayRawAsync(client, invoice, await TodayAsync());
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains(view.GetProperty("payment").GetProperty("refusal").GetString()!, await refused.Content.ReadAsStringAsync());
    }

    /// <summary>Строки без позиции мешают «Разобран», но не оплате: оплатить такой счёт можно.</summary>
    [Fact]
    public async Task Строки_без_позиции_мешают_только_разбору()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await LinesAsync(client, invoice, [Line(null, quantity: 1, price: 100m, rate: 20, text: "Хомут")]);
        await TotalAsync(client, invoice, 100m);

        var view = await ReadAsync(client, invoice);
        var gaps = Gaps(view);
        Assert.Contains(gaps, g => g.GetProperty("kind").GetString() == "nomenclature");
        Assert.All(gaps, g => Assert.Equal([InvoiceReadiness.Parsed], Blocks(g)));
        Assert.Equal(JsonValueKind.Null, view.GetProperty("payment").GetProperty("refusalKind").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("payment").GetProperty("refusal").ValueKind);

        // Оплаченный черновик — вопрос об оплате не стоит: ни один пункт её не называет.
        await PayAsync(client, invoice, await TodayAsync());
        view = await ReadAsync(client, invoice);
        Assert.NotEmpty(Gaps(view));
        Assert.All(Gaps(view), g => Assert.DoesNotContain(InvoiceReadiness.Payment, Blocks(g)));
        Assert.Equal(JsonValueKind.Null, view.GetProperty("payment").GetProperty("refusalKind").ValueKind);
    }

    /// <summary>
    /// Разобранный без суммы: перечня готовности нет, причину отказа называет одна оплата — родом
    /// <c>amount</c>. Сумма стёрта в базе, а не правкой: обычная правка вернула бы счёт в черновик, когда
    /// сумма станет обязательной (change 01), и тест проверял бы не то.
    /// </summary>
    [Fact]
    public async Task Разобранный_без_суммы_назван_одной_оплатой()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await AllocatedLinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m, rate: 20)]);
        await TotalAsync(client, invoice, 100m);
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));

        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CostsDbContext>().Database
                .ExecuteSqlAsync($"UPDATE costs.invoices SET total = NULL WHERE id = {invoice}");

        var view = await ReadAsync(client, invoice);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("readiness").GetProperty("gaps").ValueKind);
        Assert.Equal(PaymentRefusal.Amount, Kind(view));
        Assert.Equal(HttpStatusCode.BadRequest, (await PayRawAsync(client, invoice, await TodayAsync())).StatusCode);
    }

    /// <summary>Отклонённый: род — <c>rejected</c>, тем же порядком, что отказ записи оплаты.</summary>
    [Fact]
    public async Task Отклонённый_назван_своим_родом()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await TotalAsync(client, invoice, 100m);
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/rejected", null));

        var view = await ReadAsync(client, invoice);
        Assert.Equal(PaymentRefusal.Rejected, Kind(view));
        Assert.Equal("счёт отклонён", view.GetProperty("payment").GetProperty("refusal").GetString());
    }

    // ── Автор последней записи (COST-6.4, Э4) ─────────────────────────────────

    /// <summary>
    /// Б сохранил шапку счёта, открытого у А: А видит род <c>user</c>, имя Б и «не вы», а Б — «это вы».
    /// Имя — снимком: переименованный Б назван прежним именем.
    /// </summary>
    [Fact]
    public async Task Сосед_сохранил_шапку_и_назван_по_имени_на_момент_записи()
    {
        var (a, _) = await SignInAsync("Admin");
        var (b, bId) = await SignInAsync("Admin");
        var invoice = await CreateAsync(a);
        var name = await NameAsync(bId);

        var saved = await b.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(b, invoice, "Назначение", "Кабель на склад") });
        await OkAsync(saved);
        var written = await saved.Content.ReadFromJsonAsync<JsonElement>();

        var seen = await ReadAsync(a, invoice);
        var author = seen.GetProperty("updatedBy");
        Assert.Equal(InvoiceAuthor.User, author.GetProperty("via").GetString());
        Assert.Equal(name, author.GetProperty("name").GetString());
        Assert.False(author.GetProperty("mine").GetBoolean());
        // Время — записи Б. С точностью до миллисекунды: ответ записи несёт тики .NET, а база хранит микросекунды.
        Assert.InRange(
            (written.GetProperty("updatedAt").GetDateTimeOffset() - seen.GetProperty("updatedAt").GetDateTimeOffset()).Duration(),
            TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        Assert.True((await ReadAsync(b, invoice)).GetProperty("updatedBy").GetProperty("mine").GetBoolean());

        await RenameAsync(bId, "Петров Пётр");
        Assert.Equal(name, (await ReadAsync(a, invoice)).GetProperty("updatedBy").GetProperty("name").GetString());
    }

    /// <summary>
    /// Переход, который ничего не пишет (<c>POST …/draft</c> у черновика), не двигает ни версию, ни автора:
    /// иначе пустая запись дала бы соседу ложное «изменили».
    /// </summary>
    [Fact]
    public async Task Пустой_переход_не_меняет_ни_версию_ни_автора()
    {
        var (a, _) = await SignInAsync("Admin");
        var (b, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(a);
        var before = await ReadAsync(a, invoice);

        await ReadAsync(b, invoice);
        await OkAsync(await b.PostAsync($"/api/costs/invoices/{invoice}/draft", null));

        var after = await ReadAsync(a, invoice);
        Assert.Equal(before.GetProperty("version").GetString(), after.GetProperty("version").GetString());
        Assert.Equal(before.GetProperty("updatedBy").GetRawText(), after.GetProperty("updatedBy").GetRawText());
        Assert.True(after.GetProperty("updatedBy").GetProperty("mine").GetBoolean());
    }

    /// <summary>Правка строк — тоже запись счёта: автор — тот, кто сохранил строки.</summary>
    [Fact]
    public async Task Правка_строк_ставит_автора()
    {
        var (a, _) = await SignInAsync("Admin");
        var (b, bId) = await SignInAsync("Admin");
        var invoice = await CreateAsync(a);

        await ReadAsync(b, invoice);
        await LinesAsync(b, invoice, [Line(cable, quantity: 1, price: 100m, rate: 20)]);

        var author = (await ReadAsync(a, invoice)).GetProperty("updatedBy");
        Assert.Equal(await NameAsync(bId), author.GetProperty("name").GetString());
        Assert.False(author.GetProperty("mine").GetBoolean());
    }

    /// <summary>Счёт, записанный до колонок автора, — автор неизвестен: <c>updatedBy</c> пуст, а не «никто».</summary>
    [Fact]
    public async Task Автор_неизвестен_пусто()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CostsDbContext>().Database.ExecuteSqlAsync(
                $"UPDATE costs.invoices SET updated_by = NULL, updated_by_name = NULL, updated_via = NULL WHERE id = {invoice}");

        Assert.Equal(JsonValueKind.Null, (await ReadAsync(client, invoice)).GetProperty("updatedBy").ValueKind);
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    private static List<JsonElement> Gaps(JsonElement view) =>
        [.. view.GetProperty("readiness").GetProperty("gaps").EnumerateArray()];

    private static string[] Blocks(JsonElement gap) =>
        [.. gap.GetProperty("blocks").EnumerateArray().Select(b => b.GetString()!)];

    private static string? Kind(JsonElement view) =>
        view.GetProperty("payment").GetProperty("refusalKind").GetString();

    private static async Task TotalAsync(HttpClient client, Guid invoice, decimal total) =>
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(client, invoice, "Итого", total) }));

    /// <summary>Неоплаченный счёт с суммой 100 и сроком «оплатить до».</summary>
    private static async Task<Guid> DueAsync(HttpClient client, DateOnly due)
    {
        var invoice = await CreateAsync(client, complete: true);
        await TotalAsync(client, invoice, 100m);
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(client, invoice, "Срок", Iso(due)) }));
        return invoice;
    }

    private static Task<HttpResponseMessage> PayRawAsync(HttpClient client, Guid invoice, DateOnly paidOn, string seen = "-") =>
        client.PostAsJsonAsync($"/api/costs/invoices/{invoice}/paid", new { paidOn = Iso(paidOn), seen });

    private static async Task PayAsync(HttpClient client, Guid invoice, DateOnly paidOn)
    {
        var preview = await client.PostAsJsonAsync($"/api/costs/invoices/{invoice}/paid/preview", new { paidOn = Iso(paidOn) });
        await OkAsync(preview);
        var stamp = (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stamp").GetString()!;
        await OkAsync(await PayRawAsync(client, invoice, paidOn, stamp));
    }

    private async Task<DateOnly> TodayAsync()
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPeriodClosures>().TodayAsync();
    }

    /// <summary>Имя, каким пользователя называет журнал: отображаемое, а без него — почта.</summary>
    private async Task<string> NameAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(id.ToString());
        return string.IsNullOrWhiteSpace(user!.DisplayName) ? user.Email! : user.DisplayName;
    }

    private async Task RenameAsync(Guid id, string name)
    {
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(id.ToString());
        user!.DisplayName = name;
        Assert.True((await users.UpdateAsync(user)).Succeeded);
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd");
}
