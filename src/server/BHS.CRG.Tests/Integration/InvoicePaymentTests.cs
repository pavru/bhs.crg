using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Periods;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Contour = BHS.CRG.Domain.Periods.PeriodContour;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Оплата счёта на живой базе (задача C5, issue #1082): предпросмотр против записанного, отказы,
/// запирание закрытым периодом по всем путям записи и учётные даты при правке оплаченного счёта.
///
/// <para>Чистая арифметика расклада — в <c>PaymentPostingTests</c>; здесь то, что без базы, прав и
/// службы закрытия периода не проверить.</para>
/// </summary>
[Collection("Integration")]
public class InvoicePaymentTests(InvoiceLineHost host) : InvoiceLineTestBase(host), IDisposable
{
    /// <summary>Закрытия — состояние всего экземпляра: соседнему классу на этой базе их оставлять нельзя.</summary>
    public void Dispose()
    {
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
            .ExecuteSqlRaw("TRUNCATE TABLE period_closures");
    }

    // ── Предпросмотр и запись ─────────────────────────────────────────────────

    /// <summary>
    /// Главный сторож задачи: предпросмотр совпадает с записанным. Стройка А закрыта, платёж задним
    /// числом — её доля уходит в первый открытый день, доля стройки Б остаётся на дате платежа; и то, что
    /// показал предпросмотр, равно тому, что легло в базу и что отдаёт чтение расклада.
    /// </summary>
    [Fact]
    public async Task Предпросмотр_совпадает_с_записанным_и_переносит_только_закрытую_стройку()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, a, b) = await TwoSitesAsync(admin);
        await CloseAsync(a, today.AddDays(-10));
        var paidOn = today.AddDays(-15);

        var preview = await PreviewAsync(admin, invoice, paidOn);
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("refusal").ValueKind);
        var rows = preview.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);

        // Переносимая — первой, с причиной словами сервера.
        Assert.Equal(a, rows[0].GetProperty("constructionId").GetGuid());
        Assert.True(rows[0].GetProperty("moved").GetBoolean());
        Assert.Equal(Iso(today.AddDays(-9)), rows[0].GetProperty("accountingOn").GetString());
        Assert.Equal(40_000m, rows[0].GetProperty("amount").GetDecimal());
        Assert.Contains($"закрыто по {today.AddDays(-10):dd.MM.yyyy}", rows[0].GetProperty("note").GetString());

        Assert.Equal(b, rows[1].GetProperty("constructionId").GetGuid());
        Assert.False(rows[1].GetProperty("moved").GetBoolean());
        Assert.Equal(Iso(paidOn), rows[1].GetProperty("accountingOn").GetString());

        var paid = await PayAsync(admin, invoice, paidOn, preview, "п/п № 45");
        var payment = paid.GetProperty("payment");
        Assert.True(payment.GetProperty("paid").GetBoolean());
        Assert.Equal(Iso(paidOn), payment.GetProperty("paidOn").GetString());
        Assert.Equal("п/п № 45", payment.GetProperty("document").GetString());
        Assert.Equal(JsonValueKind.Null, payment.GetProperty("lockedBy").ValueKind);
        Assert.Equal(
            new[] { paidOn, today.AddDays(-9) }.Select(d => d.ToString("MM.yyyy")).Distinct(),
            payment.GetProperty("periods").EnumerateArray().Select(p => p.GetString()));

        // Записанное — в базе…
        var stored = await DatesAsync(invoice);
        Assert.Equal(today.AddDays(-9), stored[a]);
        Assert.Equal(paidOn, stored[b]);

        // …и в чтении расклада: строка в строку с предпросмотром.
        var posted = await admin.GetFromJsonAsync<JsonElement>($"/api/costs/invoices/{invoice}/paid");
        Assert.Equal(
            rows.Select(r => (r.GetProperty("constructionId").GetGuid(), r.GetProperty("accountingOn").GetString())),
            posted.GetProperty("rows").EnumerateArray()
                .Select(r => (r.GetProperty("constructionId").GetGuid(), r.GetProperty("accountingOn").GetString())));
    }

    /// <summary>
    /// Журнал ядра читают без права на счета: событие оплаты называет дату, документ и перенос — и ни
    /// одной суммы.
    /// </summary>
    [Fact]
    public async Task Событие_оплаты_в_журнале_без_сумм()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, a, _) = await TwoSitesAsync(admin);
        await CloseAsync(a, today.AddDays(-10));

        await PayAsync(admin, invoice, today.AddDays(-15), await PreviewAsync(admin, invoice, today.AddDays(-15)), "п/п № 7");

        using var scope = host.Services.CreateScope();
        var record = (await scope.ServiceProvider.GetRequiredService<IActivityLog>().ReadAsync(0, 50, "costs.invoice.paid"))
            .Single(r => r.TargetId == invoice.ToString());
        Assert.Contains($"оплачен {today.AddDays(-15):dd.MM.yyyy}", record.After);
        Assert.Contains("п/п № 7", record.After);
        Assert.Contains($"учётная дата перенесена", record.After);
        foreach (var money in new[] { "₽", "руб", "40000", "40 000", "60000", "60 000", "100000", "100 000" })
            Assert.DoesNotContain(money, record.After);
    }

    [Fact]
    public async Task Отказы_оплаты_называют_причину_уже_в_предпросмотре()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (supplierUser, _) = await SignInAsync("Supplier");
        var today = await TodayAsync();

        // Счёт без суммы.
        var bare = await CreateAsync(admin, complete: true);
        var preview = await PreviewAsync(admin, bare, today);
        Assert.Contains("не указана сумма", preview.GetProperty("refusal").GetString());
        Assert.Equal(0, preview.GetProperty("rows").GetArrayLength());
        var refused = await PayRawAsync(admin, bare, today, "any");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("не указана сумма", await ErrorAsync(refused));

        // Строки не бьются с суммой (решение владельца 04.10.2026): отказ называет расхождение числом.
        var (invoice, _, _) = await TwoSitesAsync(admin, total: 100_500m);
        preview = await PreviewAsync(admin, invoice, today);
        Assert.Contains("расходится", preview.GetProperty("refusal").GetString());
        Assert.Contains("на 500", preview.GetProperty("refusal").GetString());
        Assert.Contains("расходится", (await ReadAsync(admin, invoice)).GetProperty("payment").GetProperty("refusal").GetString());

        // Дата в будущем — отказ даты, а не счёта.
        var (good, _, _) = await TwoSitesAsync(admin);
        preview = await PreviewAsync(admin, good, today.AddDays(1));
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("refusal").ValueKind);
        Assert.Contains("в будущем", preview.GetProperty("dateRefusal").GetString());

        // Без отметки увиденного расклада оплату не записать.
        var blind = await admin.PostAsJsonAsync($"/api/costs/invoices/{good}/paid", new { paidOn = Iso(today) });
        Assert.Equal(HttpStatusCode.BadRequest, blind.StatusCode);
        Assert.Contains("seen", await ErrorAsync(blind));

        // Право оплаты — своё: снабженец, который вводит счета, оплату не отмечает.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await supplierUser.PostAsJsonAsync($"/api/costs/invoices/{good}/paid/preview", new { })).StatusCode);

        // Дата раньше даты счёта — разрешена (предоплата).
        var early = new DateOnly(2026, 9, 1);
        await PayAsync(admin, good, early, await PreviewAsync(admin, good, early), null);
    }

    /// <summary>
    /// Между предпросмотром и оплатой закрыли период — записать молча другой расклад, чем обещан, нельзя.
    /// То же ловит и правка разноски: отметка расклада несёт её версию.
    /// </summary>
    [Fact]
    public async Task Расклад_изменившийся_после_предпросмотра_оплату_не_записывает()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, a, _) = await TwoSitesAsync(admin);
        var paidOn = today.AddDays(-15);

        var seen = await PreviewAsync(admin, invoice, paidOn);
        await CloseAsync(a, today.AddDays(-10));

        var stale = await PayRawAsync(admin, invoice, paidOn, seen.GetProperty("stamp").GetString()!);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("расклад оплаты изменился", await ErrorAsync(stale));
        Assert.False((await ReadAsync(admin, invoice)).GetProperty("payment").GetProperty("paid").GetBoolean());

        await PayAsync(admin, invoice, paidOn, await PreviewAsync(admin, invoice, paidOn), null);
    }

    // ── Запирание ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Закрытый период запирает оплаченный счёт ЦЕЛИКОМ и по всем путям записи: шапка, метки, строки,
    /// переходы состояния, обе разноски, платёжный документ и отмена оплаты. Каждый путь отвечает одним
    /// отказом и называет, чем счёт заперт.
    /// </summary>
    [Fact]
    public async Task Закрытый_период_запирает_оплаченный_счёт_по_всем_путям_записи()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, a, _) = await TwoSitesAsync(admin);
        var paidOn = today.AddDays(-15);
        await PayAsync(admin, invoice, paidOn, await PreviewAsync(admin, invoice, paidOn), null);

        var view = await ReadAsync(admin, invoice);
        var line = LineId(view, 1);
        var stamp = view.GetProperty("allocation").GetProperty("stamp").GetString();

        // Закрыта только стройка А — а заперт весь счёт, вместе с долей открытой стройки Б.
        await CloseAsync(a, today.AddDays(-10));
        view = await ReadAsync(admin, invoice);
        Assert.Contains("у стройки", view.GetProperty("payment").GetProperty("lockedBy").GetString());

        var paths = new Dictionary<string, Func<Task<HttpResponseMessage>>>
        {
            ["шапка"] = async () => await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
                new { requisites = await RequisitesWithAsync(admin, invoice, "Назначение", "поправили") }),
            ["метки"] = () => admin.PostAsJsonAsync($"/api/costs/invoices/{invoice}/confirmed", new { fields = new[] { "Номер" } }),
            ["строки"] = () => admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}/lines", new { lines = Array.Empty<object>() }),
            ["разобран"] = () => admin.PostAsync($"/api/costs/invoices/{invoice}/parsed", null),
            ["черновик"] = () => admin.PostAsync($"/api/costs/invoices/{invoice}/draft", null),
            ["разноска строки"] = () => AllocateRawAsync(admin, invoice, line, []),
            ["матрица"] = () => admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}/allocation",
                new { stamp, lines = Array.Empty<object>() }),
            ["платёжный документ"] = () => admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}/paid", new { document = "п/п" }),
            ["отмена оплаты"] = () => admin.PostAsJsonAsync($"/api/costs/invoices/{invoice}/unpaid", new { reason = "ошиблись" }),
        };

        foreach (var (name, send) in paths)
        {
            var response = await send();
            Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{name}: {(int)response.StatusCode}");
            Assert.Contains("заперт", await ErrorAsync(response));
        }

        // Ничего не изменилось.
        Assert.Equal(view.GetProperty("updatedAt").GetString(),
            (await ReadAsync(admin, invoice)).GetProperty("updatedAt").GetString());

        // Скан: приложить можно, заменить нельзя — замена удалила бы документ закрытого периода.
        await OkAsync(await ScanAsync(admin, invoice, "первый.pdf"));
        var replace = await ScanAsync(admin, invoice, "второй.pdf");
        Assert.Equal(HttpStatusCode.Conflict, replace.StatusCode);
        Assert.Contains("Заменить скан нельзя", await ErrorAsync(replace));

        // Закрытие отменили — счёт снова правится, и оплату можно отменить.
        await ReopenAsync(a, today.AddDays(-10));
        await OkAsync(await paths["отмена оплаты"]());
    }

    /// <summary>
    /// Оплаченный счёт открытого периода правится — и учётные даты перекладываются одним правилом на оба
    /// пути разноски: та же строка и та же цель дату сохраняют, новая цель получает дату по правилу
    /// оплаты, неразнесённое уходит в остаток по компании.
    /// </summary>
    [Fact]
    public async Task Правка_разноски_оплаченного_счёта_перекладывает_учётные_даты()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, a, b) = await TwoSitesAsync(admin);
        var (c, _) = await SiteAsync("Оплата В");
        var paidOn = today.AddDays(-15);
        await PayAsync(admin, invoice, paidOn, await PreviewAsync(admin, invoice, paidOn), null);

        // Стройку В закрыли уже после оплаты: счёта она не касается, пока на неё ничего не легло.
        await CloseAsync(c, today.AddDays(-5));
        var view = await ReadAsync(admin, invoice);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("payment").GetProperty("lockedBy").ValueKind);

        // Построчно: первая строка делится между А (как было) и В (новая цель в закрытом периоде).
        await AllocateAsync(admin, invoice, LineId(view, 1), [Part(a, quantity: 60), Part(c, quantity: 40)]);
        var dates = await DatesAsync(invoice);
        Assert.Equal(paidOn, dates[a]);
        Assert.Equal(today.AddDays(-4), dates[c]);
        Assert.Equal(paidOn, dates[b]);

        // Сняли разноску второй строки: её деньги — остаток по компании, с датой платежа.
        await AllocateAsync(admin, invoice, LineId(view, 2), []);
        Assert.Equal(paidOn, await RemainderAsync(invoice));
        var posted = await admin.GetFromJsonAsync<JsonElement>($"/api/costs/invoices/{invoice}/paid");
        var rest = posted.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("kind").GetString() == "remainder");
        Assert.Equal(60_000m, rest.GetProperty("amount").GetDecimal());

        // Вернули — остатка нет, и даты у него нет.
        await AllocateAsync(admin, invoice, LineId(view, 2), [Part(b, quantity: 50)]);
        Assert.Null(await RemainderAsync(invoice));

        // Оплаченный счёт обязан остаться сведённым: строка, после которой сумма не бьётся, — отказ.
        var broken = await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}/lines",
            new { lines = new object[] { Line(cable, 100, 400, id: LineId(view, 1)) } });
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.Contains("Сначала отмените оплату", await ErrorAsync(broken));
        Assert.Equal(2, (await ReadAsync(admin, invoice)).GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public async Task Отмена_оплаты_требует_причину_и_стирает_учётные_даты()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, _, _) = await TwoSitesAsync(admin);
        await PayAsync(admin, invoice, today, await PreviewAsync(admin, invoice, today), "п/п № 1");

        // Платёжный документ правится без отмены; дата — нет: второй раз оплатить нельзя.
        var described = await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}/paid", new { document = "п/п № 2" });
        await OkAsync(described);
        var again = await PayRawAsync(admin, invoice, today.AddDays(-1), "any");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("уже оплачен", await ErrorAsync(again));

        var silent = await admin.PostAsJsonAsync($"/api/costs/invoices/{invoice}/unpaid", new { });
        Assert.Equal(HttpStatusCode.BadRequest, silent.StatusCode);
        Assert.Contains("Причина", await ErrorAsync(silent));

        var cancelled = await admin.PostAsJsonAsync($"/api/costs/invoices/{invoice}/unpaid", new { reason = "не тот счёт" });
        await OkAsync(cancelled);
        var payment = (await cancelled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("payment");
        Assert.False(payment.GetProperty("paid").GetBoolean());
        Assert.Equal(0, payment.GetProperty("periods").GetArrayLength());
        Assert.Empty(await DatesAsync(invoice));

        using var scope = host.Services.CreateScope();
        var record = (await scope.ServiceProvider.GetRequiredService<IActivityLog>().ReadAsync(0, 50, "costs.invoice.unpaid"))
            .Single(r => r.TargetId == invoice.ToString());
        Assert.Contains("не тот счёт", record.After);
    }

    /// <summary>
    /// Правило «оплаченный счёт обязан остаться сведённым» спрашивают с правки ДЕНЕГ, а не с любой
    /// (ревью PR #1191). Счёт, переставший сходиться не по вине правки (сузили допуск, починили данные),
    /// обязан принимать платёжный документ и скан: отказ «после этой правки сумма расходится» про
    /// правку, которая сумм не касалась, был бы неправдой, а выхода из него не было бы.
    /// </summary>
    [Fact]
    public async Task Правка_не_тронувшая_деньги_сведённости_не_проверяет()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, a, _) = await TwoSitesAsync(admin);
        await PayAsync(admin, invoice, today, await PreviewAsync(admin, invoice, today), null);

        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CostsDbContext>().Database
                .ExecuteSqlInterpolatedAsync($"UPDATE costs.invoices SET total = 100500 WHERE id = {invoice}");

        await OkAsync(await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}/paid", new { document = "п/п № 9" }));
        await OkAsync(await ScanAsync(admin, invoice, "счёт.pdf"));

        // А правка денег — по-прежнему отказ, и названа причина.
        var view = await ReadAsync(admin, invoice);
        var touched = await admin.PutAsJsonAsync(
            $"/api/costs/invoices/{invoice}/lines/{LineId(view, 1)}/allocation",
            new { parts = new object[] { Part(a, quantity: 60) } });
        Assert.Equal(HttpStatusCode.BadRequest, touched.StatusCode);
        Assert.Contains("Сначала отмените оплату", await ErrorAsync(touched));
    }

    /// <summary>
    /// Реестр показывает то же, что форма: учётные месяцы счёта и деньги каждого (ТЗ COST-16,
    /// COST-20.1). Счёт на две стройки, одна закрыта по конец прошлого месяца: её 40 000 входят в
    /// затраты этого месяца, 60 000 второй — прошлого.
    /// </summary>
    [Fact]
    public async Task Реестр_называет_учётные_месяцы_счёта_и_деньги_каждого()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, a, _) = await TwoSitesAsync(admin);

        var current = new DateOnly(today.Year, today.Month, 1);
        var paidOn = current.AddDays(-1);
        await CloseAsync(a, paidOn);
        var paid = await PayAsync(admin, invoice, paidOn, await PreviewAsync(admin, invoice, paidOn), null);

        string was = $"{paidOn:MM.yyyy}", now = $"{current:MM.yyyy}";
        var ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        string Money(decimal amount) => amount.ToString("N2", ru);
        var number = paid.GetProperty("requisites").GetProperty("Номер").GetString()!;

        async Task<JsonElement> TableAsync(HttpClient client, params object[] conditions)
        {
            var filter = JsonSerializer.Serialize(new
            {
                type = "group", logic = "and",
                children = conditions.Prepend(new { type = "condition", column = "Номер", op = "eq", value = number }),
            });
            var response = await client.GetAsync(
                $"/api/tables/costs.invoices?columns=Номер,УчётныйПериод,СуммыПоПериодам&filter={Uri.EscapeDataString(filter)}");
            await OkAsync(response);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        static JsonElement Column(JsonElement table, string key) =>
            table.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("key").GetString() == key);

        // Без отбора по периоду — оба месяца, по возрастанию, и те же, что называет форма счёта.
        var whole = await TableAsync(admin);
        var row = Assert.Single(whole.GetProperty("rows").EnumerateArray());
        Assert.Equal([was, now], row.GetProperty("УчётныйПериод").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal(
            paid.GetProperty("payment").GetProperty("periods").EnumerateArray().Select(m => m.GetString()),
            row.GetProperty("УчётныйПериод").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal($"{Money(60_000m)} ({was}) + {Money(40_000m)} ({now})", row.GetProperty("СуммыПоПериодам").GetString());
        Assert.Equal(JsonValueKind.Null, Column(whole, "СуммыПоПериодам").GetProperty("note").ValueKind);

        // Отбор называет месяц: счёт находится по ЛЮБОМУ из своих месяцев, а суммы — только названного.
        var named = await TableAsync(admin, new { type = "condition", column = "УчётныйПериод", op = "eq", value = now });
        row = Assert.Single(named.GetProperty("rows").EnumerateArray());
        Assert.Equal($"{Money(40_000m)} ({now})", row.GetProperty("СуммыПоПериодам").GetString());
        Assert.Equal([was, now], row.GetProperty("УчётныйПериод").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal("только периоды, названные отбором", Column(named, "СуммыПоПериодам").GetProperty("note").GetString());
        Assert.Single((await TableAsync(admin,
            new { type = "condition", column = "УчётныйПериод", op = "eq", value = was })).GetProperty("rows").EnumerateArray());

        // Месяц, в который деньги счёта не вошли, — счёта под отбором нет.
        Assert.Empty((await TableAsync(admin,
            new { type = "condition", column = "УчётныйПериод", op = "eq", value = "01.1999" })).GetProperty("rows").EnumerateArray());

        // Под отбором по объекту суммы — только доли на названную стройку: сентябрьские 60 000 второй
        // стройки к ней не относятся (ревью PR #1192). Сам «Учётный период» — факт о счёте, он не сужается.
        var byObject = await TableAsync(admin, new { type = "condition", column = "ОбъектыРазноски", op = "contains", value = "Оплата А " });
        row = Assert.Single(byObject.GetProperty("rows").EnumerateArray());
        Assert.Equal($"{Money(40_000m)} ({now})", row.GetProperty("СуммыПоПериодам").GetString());
        Assert.Equal([was, now], row.GetProperty("УчётныйПериод").EnumerateArray().Select(m => m.GetString()));
        Assert.StartsWith("доля: ", Column(byObject, "СуммыПоПериодам").GetProperty("note").GetString());

        // Итог под отбором по учётному периоду — счета целиком, и это сказано под ним.
        var totalled = await admin.GetFromJsonAsync<JsonElement>("/api/tables/costs.invoices?columns=Номер&totals=Итого&filter=" +
            Uri.EscapeDataString(JsonSerializer.Serialize(new { type = "condition", column = "УчётныйПериод", op = "eq", value = now })));
        Assert.Equal("счета целиком, а не деньги названного периода",
            totalled.GetProperty("totals").GetProperty("Итого").GetProperty("note").GetString());

        // «Пусто» и отрицание идут тем же объединением долей и остатка, что и «равно».
        Assert.Empty((await TableAsync(admin, new { type = "condition", column = "УчётныйПериод", op = "is_empty" }))
            .GetProperty("rows").EnumerateArray());
        Assert.Empty((await TableAsync(admin, new { type = "condition", column = "УчётныйПериод", op = "neq", value = was }))
            .GetProperty("rows").EnumerateArray());

        // Отмена оплаты — счёт в реестре остаётся, а месяцев и сумм у него больше нет.
        await OkAsync(await admin.PostAsJsonAsync($"/api/costs/invoices/{invoice}/unpaid", new { reason = "проверка реестра" }));
        row = Assert.Single((await TableAsync(admin)).GetProperty("rows").EnumerateArray());
        Assert.Empty(row.GetProperty("УчётныйПериод").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("СуммыПоПериодам").ValueKind);
    }

    /// <summary>
    /// Сортировка по «Учётному периоду» — по времени, а не по названию месяца: «09.2026» раньше
    /// «01.2027», хотя по алфавиту наоборот. И месяц ОСТАТКА участвует в отборе наравне с месяцами долей:
    /// остаток лежит в самом счёте, и условие по нему — второй подзапрос того же объединения.
    /// </summary>
    [Fact]
    public async Task Учётный_период_сортируется_по_времени_и_видит_месяц_остатка()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (early, _, _) = await TwoSitesAsync(admin);
        var (late, _, _) = await TwoSitesAsync(admin);
        foreach (var invoice in new[] { early, late })
            await PayAsync(admin, invoice, today, await PreviewAsync(admin, invoice, today), null);

        // Даты — прямо в базу: сентябрь этого года и январь следующего через оплату не получить.
        DateOnly september = new(today.Year, 9, 10), january = new(today.Year + 1, 1, 10), june = new(today.Year, 6, 5);
        using (var scope = host.Services.CreateScope())
        {
            var costs = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
            await costs.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE costs.invoice_allocations SET accounting_on = {september} WHERE invoice_id = {early}");
            await costs.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE costs.invoice_allocations SET accounting_on = {january} WHERE invoice_id = {late}");
            await costs.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE costs.invoices SET remainder_accounting_on = {june} WHERE id = {late}");
        }

        string Number(JsonElement view) => view.GetProperty("requisites").GetProperty("Номер").GetString()!;
        string first = Number(await ReadAsync(admin, early)), second = Number(await ReadAsync(admin, late));

        async Task<string[]> NumbersAsync(string sort, object? condition = null)
        {
            var own = new { type = "condition", column = "Номер", op = "in", values = new[] { first, second } };
            var filter = JsonSerializer.Serialize(new
            {
                type = "group", logic = "and", children = condition is null ? [own] : new[] { own, condition },
            });
            var response = await admin.GetAsync(
                $"/api/tables/costs.invoices?columns=Номер,УчётныйПериод&sort={Uri.EscapeDataString(sort)}&filter={Uri.EscapeDataString(filter)}");
            await OkAsync(response);
            return [.. (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray()
                .Select(r => r.GetProperty("Номер").GetString()!)];
        }

        // У «позднего» счёта самый ранний месяц — июнь (остаток): по возрастанию он первый.
        Assert.Equal([second, first], await NumbersAsync("УчётныйПериод"));
        Assert.Equal([first, second], await NumbersAsync("УчётныйПериод:desc"));

        // Отбор по месяцу остатка находит счёт, хотя ни одна доля в июнь не легла.
        Assert.Equal([second], await NumbersAsync("Номер",
            new { type = "condition", column = "УчётныйПериод", op = "eq", value = $"{june:MM.yyyy}" }));

        // Без остатка порядок решают доли: сентябрь этого года раньше января следующего.
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CostsDbContext>().Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE costs.invoices SET remainder_accounting_on = NULL WHERE id = {late}");
        Assert.Equal([first, second], await NumbersAsync("УчётныйПериод"));
        Assert.Equal([second, first], await NumbersAsync("УчётныйПериод:desc"));
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    /// <summary>Счёт на 100 000: строка 40 000 на стройку А и строка 60 000 на стройку Б.</summary>
    private async Task<(Guid Invoice, Guid A, Guid B)> TwoSitesAsync(HttpClient client, decimal total = 100_000m)
    {
        var invoice = await CreateAsync(client, complete: true);
        var view = await LinesAsync(client, invoice, [Line(cable, 100, 400), Line(conduit, 50, 1200)]);
        var (a, _) = await SiteAsync("Оплата А");
        var (b, _) = await SiteAsync("Оплата Б");

        await AllocateAsync(client, invoice, LineId(view, 1), [Part(a, quantity: 100)]);
        await AllocateAsync(client, invoice, LineId(view, 2), [Part(b, quantity: 50)]);
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(client, invoice, "Итого", total) }));

        return (invoice, a, b);
    }

    private static async Task<JsonElement> PreviewAsync(HttpClient client, Guid invoice, DateOnly paidOn)
    {
        var response = await client.PostAsJsonAsync($"/api/costs/invoices/{invoice}/paid/preview", new { paidOn = Iso(paidOn) });
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> PayRawAsync(HttpClient client, Guid invoice, DateOnly paidOn, string seen, string? document = null) =>
        client.PostAsJsonAsync($"/api/costs/invoices/{invoice}/paid", new { paidOn = Iso(paidOn), document, seen });

    private static async Task<JsonElement> PayAsync(
        HttpClient client, Guid invoice, DateOnly paidOn, JsonElement preview, string? document)
    {
        var response = await PayRawAsync(client, invoice, paidOn, preview.GetProperty("stamp").GetString()!, document);
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> ScanAsync(HttpClient client, Guid invoice, string name)
    {
        var file = new ByteArrayContent("%PDF-1.4 скан"u8.ToArray());
        file.Headers.ContentType = new("application/pdf");
        return client.PostAsync($"/api/costs/invoices/{invoice}/scan", new MultipartFormDataContent { { file, "file", name } });
    }

    /// <summary>Учётные даты долей счёта по стройкам — как лежат в базе.</summary>
    private async Task<Dictionary<Guid, DateOnly>> DatesAsync(Guid invoice)
    {
        using var scope = host.Services.CreateScope();
        var parts = await scope.ServiceProvider.GetRequiredService<CostsDbContext>().InvoiceAllocations.AsNoTracking()
            .Where(p => p.InvoiceId == invoice).ToListAsync();

        // У оплаченного счёта дата есть у КАЖДОЙ доли, у неоплаченного — ни у одной.
        Assert.True(parts.All(p => p.AccountingOn is not null) || parts.All(p => p.AccountingOn is null));
        return parts.Where(p => p.AccountingOn is not null)
            .ToDictionary(p => p.ConstructionId!.Value, p => p.AccountingOn!.Value);
    }

    private async Task<DateOnly?> RemainderAsync(Guid invoice)
    {
        using var scope = host.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<CostsDbContext>().Invoices.AsNoTracking()
            .SingleAsync(i => i.Id == invoice)).RemainderAccountingOn;
    }

    private async Task<DateOnly> TodayAsync()
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPeriodClosures>().TodayAsync();
    }

    private async Task CloseAsync(Guid site, DateOnly through)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPeriodClosures>()
            .CloseAsync(new ClosePeriod(Contour.Construction(site), through.AddDays(-30), through, null, null));
    }

    private async Task ReopenAsync(Guid site, DateOnly seen)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPeriodClosures>()
            .ReopenAsync(new ReopenPeriod(Contour.Construction(site), seen, "для теста"));
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd");

    private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;
}
