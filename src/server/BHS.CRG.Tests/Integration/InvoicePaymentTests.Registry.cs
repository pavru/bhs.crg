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
/// Оплата счёта в реестре: учётные месяцы, деньги по ним и расшифровка строки (C5, issue #1082; G4,
/// issue #1097). Тот же класс, что <c>InvoicePaymentTests.cs</c>, — отдельным файлом по занятию.
/// </summary>
public partial class InvoicePaymentTests
{
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

        // «Сумма» под отбором по учётному периоду — деньги названного месяца, в клетке и в итоге
        // (G4, #1097); «Сумма к оплате» рядом остаётся счётом целиком, и это сказано под её итогом.
        async Task<JsonElement> MoneyAsync(params object[] conditions) =>
            await admin.GetFromJsonAsync<JsonElement>(
                "/api/tables/costs.invoices?columns=Номер,СуммаПоОтбору,Итого&totals=СуммаПоОтбору,Итого&filter=" +
                Uri.EscapeDataString(JsonSerializer.Serialize(new
                {
                    type = "group", logic = "and",
                    children = conditions.Prepend(new { type = "condition", column = "Номер", op = "eq", value = number }),
                })));
        static JsonElement Total(JsonElement table, string key) => table.GetProperty("totals").GetProperty(key);

        var inNow = await MoneyAsync(new { type = "condition", column = "УчётныйПериод", op = "eq", value = now });
        Assert.Equal(40_000m, Assert.Single(inNow.GetProperty("rows").EnumerateArray()).GetProperty("СуммаПоОтбору").GetDecimal());
        Assert.Equal(40_000m, Total(inNow, "СуммаПоОтбору").GetProperty("sum").GetDecimal());
        Assert.Equal(100_000m, Total(inNow, "Итого").GetProperty("sum").GetDecimal());
        Assert.Equal("только периоды, названные отбором", Column(inNow, "СуммаПоОтбору").GetProperty("note").GetString());
        Assert.Equal("только периоды, названные отбором", Total(inNow, "СуммаПоОтбору").GetProperty("note").GetString());
        Assert.Equal("счета целиком, а не деньги названного периода", Total(inNow, "Итого").GetProperty("note").GetString());

        // «Период прошлый И период этот» — счета, разведённые на оба месяца; названы оба, счёт целиком.
        var split = await MoneyAsync(
            new { type = "condition", column = "УчётныйПериод", op = "eq", value = was },
            new { type = "condition", column = "УчётныйПериод", op = "eq", value = now });
        Assert.Equal(100_000m, Total(split, "СуммаПоОтбору").GetProperty("sum").GetDecimal());

        // Отбор и по дате счёта, и по учётному периоду: «Сумма» сужена учётным периодом, и оговорки
        // «по дате счёта» под ней нет — она спорила бы с подписью о сужении.
        var twoAxes = await MoneyAsync(
            new { type = "condition", column = "Дата", op = "between", values = new[] { "2000-01-01", "2100-01-01" } },
            new { type = "condition", column = "УчётныйПериод", op = "eq", value = now });
        Assert.Equal("только периоды, названные отбором", Total(twoAxes, "СуммаПоОтбору").GetProperty("note").GetString());
        Assert.Equal("счета целиком, а не деньги названного периода", Total(twoAxes, "Итого").GetProperty("note").GetString());

        // Оба месяца названы — счёт целиком; месяц и объект вместе — доля объекта В ЭТОМ месяце.
        var inBoth = await MoneyAsync(new { type = "condition", column = "УчётныйПериод", op = "in", values = new[] { was, now } });
        Assert.Equal(100_000m, Total(inBoth, "СуммаПоОтбору").GetProperty("sum").GetDecimal());

        var onA = new { type = "condition", column = "ОбъектыРазноски", op = "contains", value = "Оплата А " };
        var mine = await MoneyAsync(onA, new { type = "condition", column = "УчётныйПериод", op = "eq", value = now });
        Assert.Equal(40_000m, Total(mine, "СуммаПоОтбору").GetProperty("sum").GetDecimal());
        Assert.StartsWith("доля: ", Column(mine, "СуммаПоОтбору").GetProperty("note").GetString());
        Assert.EndsWith("; только периоды, названные отбором", Column(mine, "СуммаПоОтбору").GetProperty("note").GetString());

        // Счёт под отбором есть (сентябрьские деньги второй стройки), а доли стройки А в сентябре нет:
        // клетка пуста, а не «вся доля стройки» и не ноль.
        var foreign = await MoneyAsync(onA, new { type = "condition", column = "УчётныйПериод", op = "eq", value = was });
        Assert.Equal(JsonValueKind.Null,
            Assert.Single(foreign.GetProperty("rows").EnumerateArray()).GetProperty("СуммаПоОтбору").ValueKind);
        Assert.Equal(0, Total(foreign, "СуммаПоОтбору").GetProperty("count").GetInt32());

        // Ветки «любое» называют ПАРЫ: «(стройка А и прошлый месяц) или (вторая стройка и этот)» — ни
        // одной пары у счёта нет, хотя и обе стройки, и оба месяца у него есть (ревью PR #1195).
        object Pair(string site, string month) => new
        {
            type = "group", logic = "and",
            children = new object[]
            {
                new { type = "condition", column = "ОбъектыРазноски", op = "contains", value = site },
                new { type = "condition", column = "УчётныйПериод", op = "eq", value = month },
            },
        };
        var crossed = await MoneyAsync(new { type = "group", logic = "or", children = new[] { Pair("Оплата А ", was), Pair("Оплата Б ", now) } });
        Assert.Equal(JsonValueKind.Null,
            Assert.Single(crossed.GetProperty("rows").EnumerateArray()).GetProperty("СуммаПоОтбору").ValueKind);
        var straight = await MoneyAsync(new { type = "group", logic = "or", children = new[] { Pair("Оплата А ", now), Pair("Оплата Б ", was) } });
        Assert.Equal(100_000m, Total(straight, "СуммаПоОтбору").GetProperty("sum").GetDecimal());

        // Период назван НЕ каждой веткой — колонкой он не назван, но пара остаётся парой: под «(стройка А
        // и прошлый месяц) или (вторая стройка)» доля А — этого месяца, и в первую ветку она не входит.
        // «Сумма» и «Суммы по периодам» отвечают одно и то же: правило «названо» у них одно (ревью PR #1199).
        object half = new
        {
            type = "group", logic = "or",
            children = new[] { Pair("Оплата А ", was), new { type = "condition", column = "ОбъектыРазноски", op = "contains", value = "Оплата Б " } },
        };
        Assert.Equal(60_000m, Total(await MoneyAsync(half), "СуммаПоОтбору").GetProperty("sum").GetDecimal());
        Assert.Equal($"{Money(60_000m)} ({was})",
            Assert.Single((await TableAsync(admin, half)).GetProperty("rows").EnumerateArray()).GetProperty("СуммыПоПериодам").GetString());

        // «Пусто» и отрицание идут тем же объединением долей и остатка, что и «равно».
        Assert.Empty((await TableAsync(admin, new { type = "condition", column = "УчётныйПериод", op = "is_empty" }))
            .GetProperty("rows").EnumerateArray());
        Assert.Empty((await TableAsync(admin, new { type = "condition", column = "УчётныйПериод", op = "neq", value = was }))
            .GetProperty("rows").EnumerateArray());

        // ── Расшифровка строки: счёт «раскрывается» по стройкам в боковой панели (G4, #1097) ──
        async Task<JsonElement> OpenedAsync(params object[] conditions) =>
            (await admin.GetFromJsonAsync<JsonElement>($"/api/tables/costs.invoices?row={invoice}&filter=" +
                Uri.EscapeDataString(JsonSerializer.Serialize(new
                {
                    type = "group", logic = "and",
                    children = conditions.Prepend(new { type = "condition", column = "Номер", op = "eq", value = number }),
                })))).GetProperty("breakdown");
        static (string Object, decimal? Share, string? Month, bool Named)[] Parts(JsonElement breakdown) =>
        [
            .. breakdown.GetProperty("rows").EnumerateArray().Select(r => (
                r.GetProperty("values").GetProperty("Объект").GetString()!,
                r.GetProperty("values").GetProperty("Доля").ValueKind == JsonValueKind.Null
                    ? (decimal?)null : r.GetProperty("values").GetProperty("Доля").GetDecimal(),
                r.GetProperty("values").GetProperty("УчётныйМесяц").GetString(),
                r.GetProperty("named").GetBoolean())),
        ];

        // Без сужающего отбора — счёт целиком: две стройки, каждая своим месяцем; «в отборе» нет ни у кого.
        var open = await OpenedAsync();
        Assert.Equal("Разноска", open.GetProperty("title").GetString());
        Assert.False(open.GetProperty("narrowed").GetBoolean());
        var parts = Parts(open);
        Assert.Equal([(40_000m, now, false), (60_000m, was, false)], parts.Select(p => (p.Share!.Value, p.Month!, p.Named)));
        Assert.StartsWith("Оплата А ", parts[0].Object);
        var sumOf = Assert.Single(open.GetProperty("totals").EnumerateArray());
        Assert.Equal(100_000m, sumOf.GetProperty("whole").GetDecimal());
        Assert.Equal(JsonValueKind.Null, sumOf.GetProperty("named").ValueKind);
        Assert.Equal(JsonValueKind.Null, open.GetProperty("note").ValueKind);

        // Под отбором блок по-прежнему показывает счёт целиком, а названное помечено — и его сумма равна
        // клетке «Сумма» (сверяет ядро: расхождение было бы отказом, а не двумя цифрами).
        var narrowedTo = await OpenedAsync(new { type = "condition", column = "УчётныйПериод", op = "eq", value = now });
        Assert.True(narrowedTo.GetProperty("narrowed").GetBoolean());
        Assert.Equal([true, false], Parts(narrowedTo).Select(p => p.Named));
        Assert.Equal(40_000m, Assert.Single(narrowedTo.GetProperty("totals").EnumerateArray()).GetProperty("named").GetDecimal());
        Assert.Equal([false, true], Parts(await OpenedAsync(
            new { type = "condition", column = "ОбъектыРазноски", op = "contains", value = "Оплата Б " })).Select(p => p.Named));
        // Счёт под отбором есть, а названной пары у него нет: помеченных строк нет, «в отборе» — пусто.
        var none = await OpenedAsync(onA, new { type = "condition", column = "УчётныйПериод", op = "eq", value = was });
        Assert.All(Parts(none), p => Assert.False(p.Named));
        Assert.Equal(JsonValueKind.Null, Assert.Single(none.GetProperty("totals").EnumerateArray()).GetProperty("named").ValueKind);

        // Страница расшифровок не несёт: она — только у одной строки.
        Assert.Equal(JsonValueKind.Null, (await TableAsync(admin)).GetProperty("breakdown").ValueKind);

        // Отмена оплаты — счёт в реестре остаётся, а месяцев и сумм у него больше нет.
        await OkAsync(await admin.PostAsJsonAsync($"/api/costs/invoices/{invoice}/unpaid", new { reason = "проверка реестра" }));
        row = Assert.Single((await TableAsync(admin)).GetProperty("rows").EnumerateArray());
        Assert.Empty(row.GetProperty("УчётныйПериод").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("СуммыПоПериодам").ValueKind);

        // В расшифровке неоплаченного счёта доли есть, месяцев нет, и это сказано.
        var unpaid = await OpenedAsync();
        Assert.Equal("счёт не оплачен — в затраты не вошёл", unpaid.GetProperty("note").GetString());
        Assert.All(Parts(unpaid), p => Assert.Null(p.Month));
        Assert.Equal(100_000m, Parts(unpaid).Sum(p => p.Share));
    }

    /// <summary>
    /// Неразнесённый остаток — своя строка расшифровки; счёт без разноски — одна строка на всю сумму.
    /// Иначе доли не сложились бы в счёт, и блок показывал бы меньше, чем стоит в «Сумме к оплате».
    /// </summary>
    [Fact]
    public async Task Расшифровка_называет_неразнесённый_остаток_и_счёт_без_разноски()
    {
        var (admin, _) = await SignInAsync("Admin");
        async Task<(string Object, decimal Share)[]> PartsAsync(Guid invoice) =>
        [
            .. (await admin.GetFromJsonAsync<JsonElement>($"/api/tables/costs.invoices?row={invoice}"))
                .GetProperty("breakdown").GetProperty("rows").EnumerateArray().Select(r => (
                    r.GetProperty("values").GetProperty("Объект").GetString()!,
                    r.GetProperty("values").GetProperty("Доля").GetDecimal())),
        ];

        // Строки на 100 000 разнесены целиком, а к оплате — 100 500: пятьсот не лежат ни на одном объекте.
        var (partly, _, _) = await TwoSitesAsync(admin, total: 100_500m);
        var parts = await PartsAsync(partly);
        Assert.Equal(3, parts.Length);
        Assert.Equal(("Не разнесено", 500m), parts[^1]);

        // Под отбором по объекту остаток в «Сумму» не идёт: он не лежит ни на одном объекте (ревью PR #1199).
        var onA = Uri.EscapeDataString(JsonSerializer.Serialize(
            new { type = "condition", column = "ОбъектыРазноски", op = "contains", value = "Оплата А " }));
        var share = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/tables/costs.invoices?row={partly}&columns=СуммаПоОтбору&totals=СуммаПоОтбору&filter={onA}");
        Assert.Equal(40_000m, Assert.Single(share.GetProperty("rows").EnumerateArray()).GetProperty("СуммаПоОтбору").GetDecimal());

        var bare = await CreateAsync(admin, complete: true);
        await OkAsync(await admin.PutAsJsonAsync($"/api/costs/invoices/{bare}",
            new { requisites = await RequisitesWithAsync(admin, bare, "Итого", 7_000m) }));
        Assert.Equal([("Счёт не разнесён", 7_000m)], await PartsAsync(bare));
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

        // Даты долей пережили отмену оплаты (правка базы; дату остатка стережёт ограничение таблицы, даты
        // долей — нет): неоплаченный счёт учётного
        // периода не называет и в деньги месяца не входит — этого держит не только стирание дат при
        // отмене, но и сам читатель денег (ревью PR #1199).
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CostsDbContext>().Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE costs.invoices SET payment = 'Unpaid', paid_on = NULL WHERE id = {early}");
        var open = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/tables/costs.invoices?row={early}&columns=УчётныйПериод,СуммыПоПериодам");
        var unpaid = Assert.Single(open.GetProperty("rows").EnumerateArray());
        Assert.Empty(unpaid.GetProperty("УчётныйПериод").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, unpaid.GetProperty("СуммыПоПериодам").ValueKind);
        Assert.All(open.GetProperty("breakdown").GetProperty("rows").EnumerateArray(),
            r => Assert.Equal(JsonValueKind.Null, r.GetProperty("values").GetProperty("УчётныйМесяц").ValueKind));

        // Отбор идёт по ЗАПИСАННЫМ датам и счёт найдёт — но денег месяца у него нет: клетка пуста.
        var stale = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/tables/costs.invoices?columns=Номер,СуммаПоОтбору&totals=СуммаПоОтбору&filter=" +
            Uri.EscapeDataString(JsonSerializer.Serialize(new
            {
                type = "group", logic = "and",
                children = new object[]
                {
                    new { type = "condition", column = "Номер", op = "eq", value = first },
                    new { type = "condition", column = "УчётныйПериод", op = "eq", value = $"{september:MM.yyyy}" },
                },
            })));
        Assert.All(stale.GetProperty("rows").EnumerateArray(),
            row => Assert.Equal(JsonValueKind.Null, row.GetProperty("СуммаПоОтбору").ValueKind));
        Assert.Equal(0, stale.GetProperty("totals").GetProperty("СуммаПоОтбору").GetProperty("count").GetInt32());
    }
}
