using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Periods;
using BHS.CRG.Domain.Periods;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Диалог закрытия периода: что войдёт в период и что не завершено (E1b, issue #1099; ТЗ CORE-35).
/// Тот же класс, что <c>InvoicePaymentTests.cs</c>, — отдельным файлом по занятию.
/// </summary>
public partial class InvoicePaymentTests
{
    private static object Closing(DateOnly from, DateOnly through, string? report = null, Guid? site = null) => new
    {
        contour = site is null ? "company" : "construction",
        constructionId = site,
        from = Iso(from),
        through = Iso(through),
        ifMatch = "none",
        report,
    };

    private static async Task<JsonElement> ClosingPreviewAsync(HttpClient client, DateOnly from, DateOnly through, Guid? site = null)
    {
        var response = await client.PostAsJsonAsync("/api/periods/close/preview", Closing(from, through, site: site));
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// Оплаченному счёту — неразнесённый остаток в 500 ₽: сумма к оплате прямо в базе. Через адреса так
    /// не сделать: счёт, у которого строки не бьются с суммой, к оплате не допускается.
    /// </summary>
    private async Task LeaveRemainderAsync(Guid invoice, DateOnly on)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CostsDbContext>().Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE costs.invoices SET total = 100500, remainder_accounting_on = {on} WHERE id = {invoice}");
    }

    /// <summary>Строка перечня числом и суммой; строки нет — ноль: модуль не называет то, чего нет.</summary>
    private static (int Count, decimal? Amount) Line(JsonElement preview, string group, string key)
    {
        var line = Assert.Single(preview.GetProperty("sections").EnumerateArray()).GetProperty(group).EnumerateArray()
            .FirstOrDefault(l => l.GetProperty("key").GetString() == key);
        if (line.ValueKind == JsonValueKind.Undefined) return (0, 0);
        return (line.GetProperty("count").GetInt32(),
            line.GetProperty("amount").ValueKind == JsonValueKind.Null ? null : line.GetProperty("amount").GetDecimal());
    }

    /// <summary>Счёт на две стройки — разобранный (или нет) и оплаченный этим днём.</summary>
    private async Task<(Guid Invoice, Guid A)> PaidAsync(HttpClient client, DateOnly on, bool parsed = true)
    {
        var (invoice, a, _) = await TwoSitesAsync(client);
        if (parsed) await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));
        await PayAsync(client, invoice, on, await PreviewAsync(client, invoice, on), null);
        return (invoice, a);
    }

    /// <summary>
    /// Главный сторож задачи: перечень диалога совпадает с записанным. Что показал предпросмотр, то и
    /// лежит в записи закрытия (её отдаёт «История»), и из того же перечня — текст журнала. Закрыть без
    /// отпечатка или с устаревшим нельзя: иначе диалог — украшение.
    ///
    /// <para>Числа сверяются ПРИРОСТОМ: база у класса общая, и оплаченные счета соседних тестов в
    /// период попадают тоже.</para>
    /// </summary>
    [Fact]
    public async Task Перечень_диалога_закрытия_совпадает_с_записанным()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (from, through) = (today.AddDays(-30), today.AddDays(-1));

        var before = await ClosingPreviewAsync(admin, from, through);

        // Три оплаченных счёта по 100 000: разобранный и разнесённый; разобранный с остатком в 500 ₽;
        // неразобранный. Не завершены два последних.
        await PaidAsync(admin, through);
        var (partly, _) = await PaidAsync(admin, through);
        await LeaveRemainderAsync(partly, through);
        await PaidAsync(admin, through, parsed: false);

        var preview = await ClosingPreviewAsync(admin, from, through);
        var section = Assert.Single(preview.GetProperty("sections").EnumerateArray());
        Assert.Equal("costs", section.GetProperty("module").GetString());
        Assert.Equal("Счета и накладные", section.GetProperty("title").GetString());
        Assert.Equal(CostsClosingReport.DateRule, section.GetProperty("dateRule").GetString());
        Assert.False(section.GetProperty("amountsHidden").GetBoolean());
        Assert.Equal("Оплачены в периоде, но не разнесены или не разобраны",
            section.GetProperty("unfinished")[0].GetProperty("text").GetString());

        (int Count, decimal? Amount) Grown(string group, string key) =>
            (Line(preview, group, key).Count - Line(before, group, key).Count,
             Line(preview, group, key).Amount - Line(before, group, key).Amount);
        Assert.Equal((2, 200_500m), Grown("unfinished", "unsettled"));
        Assert.Equal((3, 300_500m), Grown("frozen", "entering"));

        // Без отпечатка — отказ словами; с устаревшим — «данные изменились», и период не закрыт.
        var blind = await admin.PostAsJsonAsync("/api/periods/close", Closing(from, through));
        Assert.Equal(HttpStatusCode.BadRequest, blind.StatusCode);
        Assert.Contains("перечень, который вы видели", await ErrorAsync(blind));

        await PaidAsync(admin, through);
        var stale = await admin.PostAsJsonAsync("/api/periods/close", Closing(from, through, preview.GetProperty("stamp").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("данные изменились", await ErrorAsync(stale));
        Assert.Equal(JsonValueKind.Null,
            (await admin.GetFromJsonAsync<JsonElement>("/api/periods")).GetProperty("company").GetProperty("closedThrough").ValueKind);

        // Посмотрели заново — закрылось, и в записи ровно то, что показал диалог.
        var seen = await ClosingPreviewAsync(admin, from, through);
        Assert.Equal(Line(preview, "frozen", "entering").Count + 1, Line(seen, "frozen", "entering").Count);
        await OkAsync(await admin.PostAsJsonAsync("/api/periods/close", Closing(from, through, seen.GetProperty("stamp").GetString())));

        var record = (await admin.GetFromJsonAsync<JsonElement>("/api/periods/history?take=1")).EnumerateArray().First();
        Assert.Equal(seen.GetProperty("sections").GetRawText(), record.GetProperty("report").GetRawText());

        // Журнал ядра — из того же перечня: число незавершённых документов есть, рублей нет.
        using var scope = host.Services.CreateScope();
        var closed = await scope.ServiceProvider.GetRequiredService<IActivityLog>().LastAsync(ActivityActions.PeriodClosed);
        var counted = Assert.Single(seen.GetProperty("sections").EnumerateArray()).GetProperty("unfinished")[0].GetProperty("counted").GetString();
        Assert.Equal(
            $"закрыто по {through:dd.MM.yyyy}. Не завершено: Счета и накладные — оплачены в периоде, но не разнесены или не разобраны: {counted}",
            closed!.After);

        // В базе перечень лежит С СУММАМИ — их режут на выдаче, а не при записи.
        var stored = await scope.ServiceProvider.GetRequiredService<BHS.CRG.Infrastructure.Persistence.AppDbContext>()
            .PeriodClosures.AsNoTracking().OrderByDescending(r => r.At).FirstAsync();
        Assert.Equal(Line(seen, "frozen", "entering").Amount,
            ClosingReport.FromJson(stored.Report)!.Sections[0].Frozen.Single(l => l.Key == "entering").Amount);
    }

    /// <summary>
    /// Диалог — экран ядра, и право закрывать период сумм счетов не открывает (путь H1): менеджер
    /// проекта видит числа документов, но не рубли — ни в предпросмотре, ни в «Истории». Отпечаток при
    /// этом тот же, что у того, кому суммы видны: закрывают они одно и то же.
    /// </summary>
    [Fact]
    public async Task Суммы_в_диалоге_закрытия_видит_только_право_на_отчёты_по_затратам()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (manager, _) = await SignInAsync("ProjectManager");
        var today = await TodayAsync();
        var (from, through) = (today.AddDays(-30), today.AddDays(-1));
        await PaidAsync(admin, through, parsed: false);

        var full = await ClosingPreviewAsync(admin, from, through);
        var cut = await ClosingPreviewAsync(manager, from, through);
        Assert.Equal(full.GetProperty("stamp").GetString(), cut.GetProperty("stamp").GetString());

        Assert.True(Assert.Single(cut.GetProperty("sections").EnumerateArray()).GetProperty("amountsHidden").GetBoolean());
        foreach (var (group, key) in new[] { ("unfinished", "unsettled"), ("frozen", "entering") })
        {
            Assert.NotNull(Line(full, group, key).Amount);
            Assert.Equal(Line(full, group, key).Count, Line(cut, group, key).Count);
            Assert.Null(Line(cut, group, key).Amount);
        }

        await OkAsync(await manager.PostAsJsonAsync("/api/periods/close", Closing(from, through, cut.GetProperty("stamp").GetString())));

        static JsonElement Entering(JsonElement record) =>
            record.GetProperty("report")[0].GetProperty("frozen").EnumerateArray().Single(l => l.GetProperty("key").GetString() == "entering");
        var mine = (await manager.GetFromJsonAsync<JsonElement>("/api/periods/history?take=1")).EnumerateArray().First();
        Assert.Equal(JsonValueKind.Null, Entering(mine).GetProperty("amount").ValueKind);
        Assert.True(mine.GetProperty("report")[0].GetProperty("amountsHidden").GetBoolean());
        var theirs = (await admin.GetFromJsonAsync<JsonElement>("/api/periods/history?take=1")).EnumerateArray().First();
        Assert.Equal(Line(full, "frozen", "entering").Amount, Entering(theirs).GetProperty("amount").GetDecimal());
    }

    /// <summary>
    /// Считаются только дни, которые закрытие закрывает впервые, и только оплаченное: неоплаченный счёт
    /// не принадлежит ни одному периоду, а деньги уже закрытых дней второй раз в перечень не идут. У
    /// стройки — её доли, и остатка у неё нет: он лежит на компании.
    /// </summary>
    [Fact]
    public async Task В_перечень_входят_только_впервые_закрываемые_дни_и_только_оплаченное()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (start, early, late) = (today.AddDays(-40), today.AddDays(-20), today.AddDays(-2));

        var (first, a) = await PaidAsync(admin, early);
        await LeaveRemainderAsync(first, early);

        // Стройка А — новая, счёт у неё один: её доля 40 000, а остаток в 500 ₽ — не её.
        var site = await ClosingPreviewAsync(admin, start, early, a);
        Assert.Equal((1, 40_000m), Line(site, "frozen", "entering"));
        Assert.Empty(Assert.Single(site.GetProperty("sections").EnumerateArray()).GetProperty("unfinished").EnumerateArray());

        // Неоплаченный счёт перечня не меняет.
        var byEarly = await ClosingPreviewAsync(admin, start, early);
        var byLate = await ClosingPreviewAsync(admin, start, late);
        await TwoSitesAsync(admin);
        Assert.Equal(byEarly.GetProperty("stamp").GetString(), (await ClosingPreviewAsync(admin, start, early)).GetProperty("stamp").GetString());

        // Компанию закрыли по день первой оплаты — следующее закрытие считает только дни после него.
        await OkAsync(await admin.PostAsJsonAsync("/api/periods/close", Closing(start, early, byEarly.GetProperty("stamp").GetString())));
        var next = await admin.PostAsJsonAsync("/api/periods/close/preview",
            new { contour = "company", from = Iso(early.AddDays(1)), through = Iso(late) });
        await OkAsync(next);
        var rest = await next.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Line(byLate, "frozen", "entering").Count - Line(byEarly, "frozen", "entering").Count, Line(rest, "frozen", "entering").Count);
        Assert.Equal(Line(byLate, "frozen", "entering").Amount - Line(byEarly, "frozen", "entering").Amount, Line(rest, "frozen", "entering").Amount);
    }

    /// <summary>
    /// «Впервые» — не «с границы компании по дату» (ревью PR #1201). Стройка, закрытая своим закрытием
    /// дальше компании, свои дни уже держит: закрывая компанию, её долю второй раз не называют — ни
    /// деньгами, ни числом запираемых счетов. И «не разнесён» — только остаток закрываемых дней: счёт с
    /// долями в периоде и остатком, перенесённым за него, здесь завершён.
    /// </summary>
    [Fact]
    public async Task Уже_закрытые_дни_стройки_и_перенесённый_остаток_в_перечень_не_идут()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (from, through, paidOn) = (today.AddDays(-30), today.AddDays(-1), today.AddDays(-5));

        var before = await ClosingPreviewAsync(admin, from, through);
        // Сколько счетов запрётся: строка «Запрутся целиком» есть, только когда число другое.
        static int Locked(JsonElement preview) =>
            Line(preview, "frozen", "locked") is { Count: > 0 } locked ? locked.Count : Line(preview, "frozen", "entering").Count;
        async Task<(int Count, decimal? Amount, int Unsettled, int Locked)> GrownAsync()
        {
            var now = await ClosingPreviewAsync(admin, from, through);
            return (Line(now, "frozen", "entering").Count - Line(before, "frozen", "entering").Count,
                Line(now, "frozen", "entering").Amount - Line(before, "frozen", "entering").Amount,
                Line(now, "unfinished", "unsettled").Count - Line(before, "unfinished", "unsettled").Count,
                Locked(now) - Locked(before));
        }

        // Счёт на 100 000: 40 000 на стройку А и 60 000 на Б, оплачен в периоде; остаток в 500 ₽ — уже
        // за периодом. В период входят доли, а незавершённым счёт не назван.
        var (invoice, a, b) = await TwoSitesAsync(admin);
        await OkAsync(await admin.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));
        await PayAsync(admin, invoice, paidOn, await PreviewAsync(admin, invoice, paidOn), null);
        await LeaveRemainderAsync(invoice, today);
        Assert.Equal((1, 100_000m, 0, 1), await GrownAsync());

        // А остаток в самих закрываемых днях — «не разнесён».
        await LeaveRemainderAsync(invoice, paidOn);
        Assert.Equal((1, 100_500m, 1, 1), await GrownAsync());
        await LeaveRemainderAsync(invoice, today);

        // Стройку А закрыли своим закрытием по день оплаты: её доля уже заперта, компания закроет впервые
        // только долю Б. Счёт всё ещё запирается — долей Б.
        await CloseAsync(a, paidOn);
        Assert.Equal((1, 60_000m, 0, 1), await GrownAsync());

        // Закрыта и Б — от счёта в закрываемых впервые днях не осталось ничего.
        await CloseAsync(b, paidOn);
        Assert.Equal((0, 0m, 0, 0), await GrownAsync());
    }

    /// <summary>
    /// Ссылка строки «вошедшие в период» ведёт в реестр, и под ней реестр называет ТО ЖЕ число счетов и
    /// ту же сумму — иначе ссылка хуже её отсутствия. Поэтому она есть только там, где это верно: когда
    /// закрываемые впервые дни — целые учётные месяцы. У первого закрытия (начала нет), у неполного
    /// месяца и при стройке, закрытой дальше компании, ссылки нет.
    /// </summary>
    [Fact]
    public async Task Ссылка_строки_ведёт_в_реестр_с_тем_же_числом_и_только_за_целые_месяцы()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        // Месяц — давний: соседние тесты класса платят днями «сегодня минус N», и их счета сюда не
        // попадают. Сверке с реестром они не мешают, но среди них есть счёт без денег — а с ним ссылки нет.
        var month = new DateOnly(today.Year, today.Month, 1).AddMonths(-4);
        var (before, last) = (month.AddDays(-1), month.AddMonths(1).AddDays(-1));

        static string? Link(JsonElement preview) =>
            Assert.Single(preview.GetProperty("sections").EnumerateArray()).GetProperty("frozen").EnumerateArray()
                .Where(l => l.GetProperty("key").GetString() == "entering")
                .Select(l => l.GetProperty("link").GetString()).FirstOrDefault();
        async Task<JsonElement> NextAsync(DateOnly through, Guid? site = null)
        {
            var response = await admin.PostAsJsonAsync("/api/periods/close/preview", new
            {
                contour = site is null ? "company" : "construction", constructionId = site,
                from = Iso(month), through = Iso(through),
            });
            await OkAsync(response);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        // Первое закрытие компании — по конец предыдущего месяца. Начала у него нет: закрывается всё, что
        // было раньше, и отбора «все месяцы по этот» у реестра нет.
        await PaidAsync(admin, before);
        var first = await ClosingPreviewAsync(admin, before.AddDays(-30), before);
        Assert.Equal(1, Math.Sign(Line(first, "frozen", "entering").Count));
        Assert.Null(Link(first));
        await OkAsync(await admin.PostAsJsonAsync("/api/periods/close",
            Closing(before.AddDays(-30), before, first.GetProperty("stamp").GetString())));

        // Счёт на две стройки оплачен в этом месяце: 40 000 на А и 60 000 на Б.
        var (invoice, a, b) = await TwoSitesAsync(admin);
        await OkAsync(await admin.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));
        var paidOn = month.AddDays(2);
        await PayAsync(admin, invoice, paidOn, await PreviewAsync(admin, invoice, paidOn), null);

        async Task SameInRegistryAsync(JsonElement preview)
        {
            var link = Link(preview);
            Assert.NotNull(link);
            Assert.StartsWith(CostsClosingReport.Registry + "#filter=", link);
            var filter = link[(link.IndexOf('=') + 1)..];
            var registry = await admin.GetFromJsonAsync<JsonElement>(
                $"/api/tables/costs.invoices?columns=Номер,СуммаПоОтбору&totals=СуммаПоОтбору&limit=1&filter={filter}");
            var line = Line(preview, "frozen", "entering");
            Assert.Equal(line.Count, registry.GetProperty("count").GetInt32());
            Assert.Equal(line.Amount, registry.GetProperty("totals").GetProperty("СуммаПоОтбору").GetProperty("sum").GetDecimal());
        }

        // Целый месяц — ссылка есть, и реестр под ней согласен: и у компании, и у стройки (её доля).
        await SameInRegistryAsync(await NextAsync(last));
        var site = await NextAsync(last, a);
        Assert.Equal((1, 40_000m), Line(site, "frozen", "entering"));
        await SameInRegistryAsync(site);

        // Неполный месяц: реестр отбирает по месяцу и показал бы больше, чем строка.
        Assert.Null(Link(await NextAsync(last.AddDays(-1))));

        // Стройка Б закрыта дальше компании: её долю строка уже не считает, а реестр показал бы.
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IPeriodClosures>()
                .CloseAsync(new ClosePeriod(PeriodContour.Construction(b), month, paidOn, before, null));
        var ahead = await NextAsync(last);
        Assert.Equal(1, Math.Sign(Line(ahead, "frozen", "entering").Count));
        Assert.Null(Link(ahead));

        // Закрытие стройки отменили — ссылка вернулась.
        await ReopenAsync(b, paidOn);
        await SameInRegistryAsync(await NextAsync(last));

        // У строк второго счёта стёрли цену: его доли в периоде есть, а денег в них нет. Реестр под
        // отбором периода счёт покажет, строка его не считает — число разошлось бы, и ссылки нет, хотя
        // первый счёт в период по-прежнему входит.
        var (blank, _, _) = await TwoSitesAsync(admin);
        await OkAsync(await admin.PostAsync($"/api/costs/invoices/{blank}/parsed", null));
        await PayAsync(admin, blank, paidOn, await PreviewAsync(admin, blank, paidOn), null);
        var both = await NextAsync(last);
        await SameInRegistryAsync(both);
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CostsDbContext>().Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE costs.invoice_lines SET amount = NULL WHERE invoice_id = {blank}");
        var moneyless = await NextAsync(last);
        Assert.Equal(Line(both, "frozen", "entering").Count - 1, Line(moneyless, "frozen", "entering").Count);
        Assert.Equal(1, Math.Sign(Line(moneyless, "frozen", "entering").Count));
        Assert.Equal(Line(both, "frozen", "entering").Count, Line(moneyless, "frozen", "locked").Count);
        Assert.Null(Link(moneyless));
    }

    /// <summary>
    /// У закрытия СТРОЙКИ реестр под ссылкой отбирает счёт двумя независимыми условиями: «есть доля на
    /// стройку» и «есть учётная дата в этих месяцах» — хоть бы и у чужой доли (ревью PR #1202). Счёт,
    /// чья доля на эту стройку легла в другой месяц, реестр перечислит, а строка не считает: число
    /// разошлось бы — ссылки нет. У соседней стройки, где расхождения нет, ссылка есть и сходится.
    /// </summary>
    [Fact]
    public async Task У_стройки_ссылки_нет_когда_реестр_перечислит_счёт_с_её_долей_в_другом_месяце()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var month = new DateOnly(today.Year, today.Month, 1).AddMonths(-4);
        var (last, next) = (month.AddMonths(1).AddDays(-1), month.AddMonths(1));
        var nextLast = next.AddMonths(1).AddDays(-1);

        async Task CloseSiteAsync(Guid site, DateOnly from, DateOnly through, DateOnly? seen)
        {
            using var scope = host.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IPeriodClosures>()
                .CloseAsync(new ClosePeriod(PeriodContour.Construction(site), from, through, seen, null));
        }
        async Task<JsonElement> NextMonthAsync(Guid site)
        {
            var response = await admin.PostAsJsonAsync("/api/periods/close/preview",
                new { contour = "construction", constructionId = site, from = Iso(next), through = Iso(nextLast) });
            await OkAsync(response);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        static JsonElement Entering(JsonElement preview) =>
            Assert.Single(preview.GetProperty("sections").EnumerateArray()).GetProperty("frozen").EnumerateArray()
                .Single(l => l.GetProperty("key").GetString() == "entering");

        // Стройка А закрыта по конец месяца; счёт на А и Б оплачен в этом месяце — доля А (40 000) уходит
        // в первый открытый день, в следующий месяц, а доля Б (60 000) остаётся в месяце оплаты.
        var (split, a, b) = await TwoSitesAsync(admin);
        await OkAsync(await admin.PostAsync($"/api/costs/invoices/{split}/parsed", null));
        await CloseSiteAsync(a, month, last, null);
        await PayAsync(admin, split, month.AddDays(2), await PreviewAsync(admin, split, month.AddDays(2)), null);
        await CloseSiteAsync(b, month, last, null);

        // Второй счёт — целиком на Б и уже в следующем месяце.
        var whole = await CreateAsync(admin, complete: true);
        var lines = await LinesAsync(admin, whole, [Line(cable, 100, 400), Line(conduit, 50, 1200)]);
        await AllocateAsync(admin, whole, LineId(lines, 1), [Part(b, quantity: 100)]);
        await AllocateAsync(admin, whole, LineId(lines, 2), [Part(b, quantity: 50)]);
        await OkAsync(await admin.PutAsJsonAsync($"/api/costs/invoices/{whole}",
            new { requisites = await RequisitesWithAsync(admin, whole, "Итого", 100_000m) }));
        await OkAsync(await admin.PostAsync($"/api/costs/invoices/{whole}/parsed", null));
        await PayAsync(admin, whole, next.AddDays(2), await PreviewAsync(admin, whole, next.AddDays(2)), null);

        // Закрывают следующий месяц у Б: в него вошёл один счёт, а реестр под «объект Б + этот месяц»
        // перечислил бы два — у первого доля Б есть, и дата в этом месяце есть (у доли А).
        var ofB = Entering(await NextMonthAsync(b));
        Assert.Equal((1, 100_000m), (ofB.GetProperty("count").GetInt32(), ofB.GetProperty("amount").GetDecimal()));
        Assert.Equal(JsonValueKind.Null, ofB.GetProperty("link").ValueKind);

        // У А в том же месяце — её перенесённая доля, и реестр согласен: ссылка есть.
        var ofA = Entering(await NextMonthAsync(a));
        Assert.Equal((1, 40_000m), (ofA.GetProperty("count").GetInt32(), ofA.GetProperty("amount").GetDecimal()));
        var link = ofA.GetProperty("link").GetString()!;
        var registry = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/tables/costs.invoices?columns=Номер,СуммаПоОтбору&totals=СуммаПоОтбору&limit=1&filter={link[(link.IndexOf('=') + 1)..]}");
        Assert.Equal(1, registry.GetProperty("count").GetInt32());
        Assert.Equal(40_000m, registry.GetProperty("totals").GetProperty("СуммаПоОтбору").GetProperty("sum").GetDecimal());

        // «История» ссылок не несёт: они собраны по названиям и данным на момент закрытия.
        var shown = await NextMonthAsync(a);
        await OkAsync(await admin.PostAsJsonAsync("/api/periods/close", new
        {
            contour = "construction", constructionId = a, from = Iso(next), through = Iso(nextLast),
            ifMatch = Iso(last), report = shown.GetProperty("stamp").GetString(),
        }));
        var record = (await admin.GetFromJsonAsync<JsonElement>("/api/periods/history?take=1")).EnumerateArray().First();
        Assert.Equal(JsonValueKind.Null,
            record.GetProperty("report")[0].GetProperty("frozen").EnumerateArray()
                .Single(l => l.GetProperty("key").GetString() == "entering").GetProperty("link").ValueKind);
    }

    /// <summary>
    /// Предпросмотр отказывает теми же словами, что закрытие: стройки нет — «не найдена» сразу, а не
    /// перечень с открытой кнопкой и отказ по её нажатию.
    /// </summary>
    [Fact]
    public async Task Перечень_по_несуществующей_стройке_отказ_как_у_закрытия()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();

        var response = await admin.PostAsJsonAsync("/api/periods/close/preview",
            Closing(today.AddDays(-30), today.AddDays(-1), site: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Стройка не найдена", await ErrorAsync(response));
    }
}
