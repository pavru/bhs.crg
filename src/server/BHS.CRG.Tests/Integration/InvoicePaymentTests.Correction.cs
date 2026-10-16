using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Activity;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Tables;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Исправление счёта закрытого периода (E2, issue #1100, PR 2): адрес исправления через связку записи —
/// «требует исправления», порядок проверок, права, «не хуже, чем было», версия, книга и отметка
/// предпросмотра. Сценарии S21 (а)–(г′) пятого прогона — на живой базе (S29 и S32 — со сменой допуска,
/// в <c>InvoicePaymentTests.Tolerance.cs</c>).
///
/// <para>Счёт везде — <c>TwoSitesAsync</c>: строка 1 — 100 × 400 на стройку А, строка 2 — 50 × 1200 на
/// стройку Б, оплата 15 дней назад, А закрыта по «10 дней назад». Числа книги — проводкой
/// (<c>InvoiceBookPostingTests</c>); здесь — что они доходят до базы через адрес.</para>
/// </summary>
public partial class InvoicePaymentTests
{
    private const string ToleranceKey = "costs.allocation.tolerance";

    /// <summary>
    /// Главный путь: предпросмотр показывает день и строки книги и не пишет ничего; запись кладёт версию,
    /// книгу и журнал и двигает версию формы. Счёт с книгой и после отмены закрытия правится только
    /// исправлением.
    /// </summary>
    [Fact]
    public async Task Исправление_пишет_версию_книгу_и_журнал_а_предпросмотр_ничего()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, a, b) = await LockedAsync(admin, today);
        var before = await ReadAsync(admin, invoice);

        // Цена строки 1 — 410 вместо 400: стройке А +1000 в закрытом месяце.
        var body = Correction(before, "опечатка в цене", lines => Price(lines[0], 410m), total: 101_000m);

        var preview = await CorrectionPreviewAsync(admin, invoice, before, body);
        Assert.Equal(1, preview.GetProperty("version").GetInt32());
        Assert.Equal(Iso(today), preview.GetProperty("day").GetString());
        var records = preview.GetProperty("records").EnumerateArray().ToList();
        Assert.Equal(["opening", "correction"], records.Select(r => r.GetProperty("kind").GetString()));
        Assert.Equal([(a, 40_000m), (b, 60_000m)], Lines(records[0]).Select(l => (l.Site!.Value, l.Gross)).OrderBy(l => l.Gross));
        Assert.All(Lines(records[0]), l => Assert.Equal(Iso(today.AddDays(-15)), l.Day));
        // Закрытый день А не правится: разница ложится днём исправления.
        Assert.Equal([(a, Iso(today), 1_000m)], Lines(records[1]).Select(l => (l.Site!.Value, l.Day, l.Gross)));

        // Предпросмотр не записал ничего — ни счёта, ни книги, ни версии.
        Assert.Equal(before.GetProperty("version").GetString(), (await ReadAsync(admin, invoice)).GetProperty("version").GetString());
        Assert.Equal((0, 0), await BookAsync(invoice));

        var corrected = await CorrectAsync(admin, invoice, before, body with { Stamp = preview.GetProperty("stamp").GetString() });
        Assert.NotEqual(before.GetProperty("version").GetString(), corrected.GetProperty("version").GetString());
        Assert.Equal(41_000m, corrected.GetProperty("lines")[0].GetProperty("amount").GetDecimal());
        Assert.Equal((2, 1), await BookAsync(invoice));
        // Учётный день доли А — прежний: та же строка, та же стройка (п. 0.3).
        Assert.Equal(today.AddDays(-15), (await DatesAsync(invoice))[a]);

        // Журнал: номер версии и причина, ни одной суммы.
        using (var scope = host.Services.CreateScope())
        {
            var record = (await scope.ServiceProvider.GetRequiredService<IActivityLog>()
                    .ReadAsync(0, 50, ActivityVisibility.Whole, "costs.invoice.corrected"))
                .Single(r => r.TargetId == invoice.ToString());
            Assert.Equal("версия 1", record.Before);
            Assert.Equal("опечатка в цене", record.After);
        }

        // Закрытие отменили — а счёт с книгой по-прежнему правится только исправлением (spec п. 1.6).
        await ReopenAsync(a, today.AddDays(-10));
        var view = await ReadAsync(admin, invoice);
        Assert.Equal("у него есть исправления закрытого периода", view.GetProperty("payment").GetProperty("lockedBy").GetString());
        var plain = await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(admin, invoice, "Назначение", "поправили") });
        Assert.Equal(HttpStatusCode.Conflict, plain.StatusCode);
        Assert.Contains("только исправлением", await ErrorAsync(plain));

        // Скан такому счёту — как запертому: приложить можно, заменить нельзя.
        await OkAsync(await ScanAsync(admin, invoice, "первый.pdf"));
        Assert.Equal(HttpStatusCode.Conflict, (await ScanAsync(admin, invoice, "второй.pdf")).StatusCode);

        // Второе исправление продолжает книгу и нумерацию версий; открытия больше нет.
        view = await ReadAsync(admin, invoice);
        var again = Correction(view, "вернули цену", lines => Price(lines[0], 400m), total: 100_000m);
        var second = await CorrectionPreviewAsync(admin, invoice, view, again);
        Assert.Equal(2, second.GetProperty("version").GetInt32());
        Assert.Equal(["correction"], second.GetProperty("records").EnumerateArray().Select(r => r.GetProperty("kind").GetString()));
        await CorrectAsync(admin, invoice, view, again with { Stamp = second.GetProperty("stamp").GetString() });
        Assert.Equal((3, 2), await BookAsync(invoice));
    }

    /// <summary>
    /// Порядок проверок (spec п. 1.4): «исправление не нужно» — раньше права исправления, право — раньше
    /// версии формы. Без права на обычном счёте человек слышит «правьте как обычно» (S21 (а)).
    /// </summary>
    [Fact]
    public async Task Исправление_отказывает_в_названном_порядке()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (supplier, _) = await SignInAsync("Supplier");
        var today = await TodayAsync();

        // Неоплаченный — «исправление не нужно», и тому, у кого права исправления нет, тоже.
        var (open, _, _) = await TwoSitesAsync(admin);
        var openView = await ReadAsync(admin, open);
        foreach (var who in new[] { admin, supplier })
        {
            var needless = await CorrectionRawAsync(who, open, "preview", openView.GetProperty("version").GetString(),
                Correction(openView, "проверка"));
            Assert.Equal(HttpStatusCode.Conflict, needless.StatusCode);
            Assert.Contains("Правьте его как обычно", await ErrorAsync(needless));
        }

        var (invoice, _, _) = await LockedAsync(admin, today);
        var view = await ReadAsync(admin, invoice);
        var body = Correction(view, "проверка");

        // S21 (а): правка счетов есть, права исправления нет.
        var forbidden = await CorrectionRawAsync(supplier, invoice, "preview", view.GetProperty("version").GetString(), body);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Contains("Исправлять счета закрытого периода", await ErrorAsync(forbidden));

        // Версия формы — устаревшая и не названная.
        var stale = (ulong.Parse(view.GetProperty("version").GetString()!) - 1).ToString();
        var outdated = await CorrectionRawAsync(admin, invoice, "preview", stale, body);
        Assert.Equal(HttpStatusCode.Conflict, outdated.StatusCode);
        Assert.Contains("тем временем изменили", await ErrorAsync(outdated));
        var unnamed = await CorrectionRawAsync(admin, invoice, "preview", SeenInvoiceVersion.Omit, body);
        Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);
        Assert.Contains("If-Match", await ErrorAsync(unnamed));

        // Запись без причины и без отметки предпросмотра — отказ до связки.
        Assert.Equal(HttpStatusCode.BadRequest, (await CorrectionRawAsync(admin, invoice, "",
            view.GetProperty("version").GetString(), Correction(view, " "))).StatusCode);
        var blind = await CorrectionRawAsync(admin, invoice, "", view.GetProperty("version").GetString(), body);
        Assert.Equal(HttpStatusCode.BadRequest, blind.StatusCode);
        Assert.Contains("stamp", await ErrorAsync(blind));

        Assert.Equal((0, 0), await BookAsync(invoice));
    }

    /// <summary>
    /// S21 (в), (г), (г′): перенос доли без права на разноску — 403; правка цены с той же разноской —
    /// право на разноску не нужно; сумма, разошедшаяся сильнее, чем до исправления, — 400 «не хуже».
    /// </summary>
    [Fact]
    public async Task Право_на_разноску_и_не_хуже_чем_было()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (corrector, _) = await SignInAsync(await RoleAsync(
            "costs.invoice.read", CostsInvoiceEdit, "costs.invoice.correct"));
        var today = await TodayAsync();
        var (invoice, _, b) = await LockedAsync(admin, today);
        var view = await ReadAsync(admin, invoice);
        var version = view.GetProperty("version").GetString();

        // (в) Доля строки 1 — на Б вместо А: разноска меняется, права на неё нет.
        var moved = await CorrectionRawAsync(corrector, invoice, "preview", version,
            Correction(view, "не та стройка", lines => lines[0]["parts"] = new object[] { Part(b, quantity: 100) }));
        Assert.Equal(HttpStatusCode.Forbidden, moved.StatusCode);
        Assert.Contains("costs.allocation.edit", await ErrorAsync(moved));

        // (г′) Цена 410, а к оплате осталось 100 000: расхождение 1000 при допуске 1,00 — хуже, чем было.
        var worse = await CorrectionRawAsync(corrector, invoice, "preview", version,
            Correction(view, "опечатка", lines => Price(lines[0], 410m)));
        Assert.Equal(HttpStatusCode.BadRequest, worse.StatusCode);
        Assert.Contains("хуже, чем он был", await ErrorAsync(worse));

        // Новый пробел — строка 2 не разнесена: тоже хуже.
        var unallocated = await CorrectionRawAsync(admin, invoice, "preview", version,
            Correction(view, "сняли разноску", lines => lines[1]["parts"] = Array.Empty<object>()));
        Assert.Equal(HttpStatusCode.BadRequest, unallocated.StatusCode);
        Assert.Contains("строка 2 разнесена по стройкам не полностью", await ErrorAsync(unallocated));

        // (г) Цена 410 и к оплате 101 000, разноска та же: права на разноску не нужно.
        var body = Correction(view, "опечатка", lines => Price(lines[0], 410m), total: 101_000m);
        var preview = await CorrectionPreviewAsync(corrector, invoice, view, body);
        await CorrectAsync(corrector, invoice, view, body with { Stamp = preview.GetProperty("stamp").GetString() });
        Assert.Equal((2, 1), await BookAsync(invoice));
    }

    /// <summary>
    /// Исправление не трогает состояние счёта (ревью PR #1364): разобранный остаётся разобранным, даже если
    /// разноска при нынешнем допуске не сходится, — вернуть его в черновик значило бы запереть там навсегда.
    /// А стереть у разобранного обязательное поле «не хуже» не даёт.
    /// </summary>
    [Fact]
    public async Task Исправление_разобранного_оставляет_его_разобранным()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, _, _) = await LockedAsync(admin, today, total: 100_000.80m, parsed: true);
        try
        {
            await ToleranceAsync(admin, "0.50");
            var view = await ReadAsync(admin, invoice);
            Assert.False(view.GetProperty("allocation").GetProperty("allocated").GetBoolean());

            var body = Correction(view, "опечатка в наименовании", lines => lines[0]["supplierText"] = "Кабель ВВГнг");
            var preview = await CorrectionPreviewAsync(admin, invoice, view, body);
            var corrected = await CorrectAsync(admin, invoice, view, body with { Stamp = preview.GetProperty("stamp").GetString() });
            Assert.Equal("Разобран", corrected.GetProperty("requisites").GetProperty("Состояние").GetString());

            var blank = Correction(corrected, "стёрли номер");
            blank.Requisites["Номер"] = null;
            var refused = await CorrectionRawAsync(admin, invoice, "preview", corrected.GetProperty("version").GetString(), blank);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("не заполнено обязательное поле", await ErrorAsync(refused));
        }
        finally
        {
            await ToleranceAsync(admin, null);
        }
    }

    /// <summary>
    /// Счёт без строк, разнесённый суммой: поднятая сумма к оплате при той же разноске оставила бы
    /// неразнесённое, которого не было, — «не хуже» обязано это увидеть, хотя расхождения строк у такого
    /// счёта нет вовсе (ревью PR #1364).
    /// </summary>
    [Fact]
    public async Task Неразнесённое_у_счёта_без_строк_не_хуже_не_пропускает()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (site, _) = await SiteAsync("Без строк");
        var invoice = await CreateAsync(admin, complete: true);
        await OkAsync(await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(admin, invoice, "Итого", 1_000m) }));
        var view = await ReadAsync(admin, invoice);
        await OkAsync(await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}/allocation", new
        {
            lines = Array.Empty<object>(),
            document = new object[] { Part(site, amount: 1_000m) },
            stamp = view.GetProperty("allocation").GetProperty("stamp").GetString(),
        }));
        var paidOn = today.AddDays(-15);
        await PayAsync(admin, invoice, paidOn, await PreviewAsync(admin, invoice, paidOn), null);
        await CloseAsync(site, today.AddDays(-10));

        view = await ReadAsync(admin, invoice);
        var body = Correction(view, "сумма к оплате", total: 1_200m);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/costs/invoices/{invoice}/correction/preview")
        {
            Content = JsonContent.Create(new
            {
                reason = body.Reason, requisites = body.Requisites, lines = body.Lines,
                document = new object[] { Part(site, amount: 1_000m) },
            }),
        };
        request.Headers.TryAddWithoutValidation(SeenInvoiceVersion.Header, view.GetProperty("version").GetString());
        var refused = await admin.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("счёт без строк разнесён не полностью", await ErrorAsync(refused));
    }

    /// <summary>
    /// Закрытие отменили, книга осталась: ссылка на удалённую запись у такого счёта — не «период закрыт»
    /// (закрытия нет), а своё слово, и в отбор «можно исправить» обычной правкой он не идёт (ревью PR #1364).
    /// </summary>
    [Fact]
    public async Task Потеря_у_исправлявшегося_счёта_названа_своим_словом()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, position, site) = await InvoiceWithPositionAsync(admin, "Позиция исправленного");
        var paidOn = today.AddDays(-15);
        await PayAsync(admin, invoice, paidOn, await PreviewAsync(admin, invoice, paidOn), null);
        await CloseAsync(site, today.AddDays(-10));

        var view = await ReadAsync(admin, invoice);
        var body = Correction(view, "опечатка", lines => lines[0]["supplierText"] = "Кабель");
        var preview = await CorrectionPreviewAsync(admin, invoice, view, body);
        await CorrectAsync(admin, invoice, view, body with { Stamp = preview.GetProperty("stamp").GetString() });

        await ForgetAsync(position);
        await ReopenAsync(site, today.AddDays(-10));

        var corrected = InvoiceTable.LostWords[(int)LostMark.Corrected];
        Assert.Equal(corrected, await TroubleCellAsync(admin, LostColumn, invoice));
        Assert.False(Has(await TroubleTableAsync(admin, LostColumn, InvoiceTable.TroubleFixable), invoice));
        Assert.True(Has(await TroubleTableAsync(admin, LostColumn, corrected), invoice));
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    private const string CostsInvoiceEdit = "costs.invoice.edit";

    /// <summary>Тело исправления, собранное из ответа чтения, — с правкой строк и суммой к оплате.</summary>
    private sealed record CorrectionBody(
        string Reason, Dictionary<string, object?> Requisites, List<Dictionary<string, object?>> Lines, string? Stamp = null);

    private static CorrectionBody Correction(
        JsonElement view, string reason, Action<List<Dictionary<string, object?>>>? edit = null, decimal? total = null)
    {
        var requisites = JsonSerializer.Deserialize<Dictionary<string, object?>>(view.GetProperty("requisites").GetRawText())!;
        if (total is { } sum) requisites["Итого"] = sum;

        var lines = view.GetProperty("lines").EnumerateArray().Select(l =>
        {
            var mode = l.GetProperty("allocation").GetProperty("mode").GetString();
            return new Dictionary<string, object?>
            {
                ["id"] = l.GetProperty("id").GetGuid().ToString(),
                ["nomenclature"] = l.GetProperty("nomenclatureId").ValueKind == JsonValueKind.Null
                    ? null : Reference(l.GetProperty("nomenclatureId").GetGuid()),
                ["supplierText"] = l.GetProperty("supplierText").GetString(),
                ["unit"] = l.GetProperty("unit").GetString(),
                ["quantity"] = l.GetProperty("quantity").Clone(),
                ["price"] = l.GetProperty("price").Clone(),
                ["amount"] = l.GetProperty("amount").Clone(),
                ["parts"] = l.GetProperty("allocation").GetProperty("parts").EnumerateArray().Select(p => new Dictionary<string, object?>
                {
                    ["construction"] = p.GetProperty("constructionId").Clone(),
                    ["section"] = p.GetProperty("sectionId").Clone(),
                    ["article"] = p.GetProperty("articleId").Clone(),
                    ["quantity"] = mode == "quantity" ? p.GetProperty("quantity").Clone() : null,
                    ["amount"] = mode == "amount" ? p.GetProperty("amount").Clone() : null,
                }).ToArray(),
            };
        }).ToList();

        edit?.Invoke(lines);
        return new CorrectionBody(reason, requisites, lines);
    }

    /// <summary>Новая цена строки — сумма пересчитается по цене.</summary>
    private static void Price(Dictionary<string, object?> line, decimal price)
    {
        line["price"] = price;
        line["amount"] = null;
    }

    private static object Payload(CorrectionBody body) =>
        new { reason = body.Reason, requisites = body.Requisites, lines = body.Lines, document = Array.Empty<object>(), stamp = body.Stamp };

    /// <param name="what"><c>preview</c> — предпросмотр; пусто — запись.</param>
    private static Task<HttpResponseMessage> CorrectionRawAsync(
        HttpClient client, Guid invoice, string what, string? version, CorrectionBody body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/costs/invoices/{invoice}/correction{(what.Length > 0 ? "/" + what : "")}")
        {
            Content = JsonContent.Create(Payload(body)),
        };
        // Версию называет сам тест — и пропуск тоже: молча не названную обработчик клиента подставил бы свежей.
        request.Headers.TryAddWithoutValidation(SeenInvoiceVersion.Header,
            version == SeenInvoiceVersion.Omit ? SeenInvoiceVersion.Omit : $"\"{version}\"");
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> CorrectionPreviewAsync(HttpClient client, Guid invoice, JsonElement view, CorrectionBody body)
    {
        var response = await CorrectionRawAsync(client, invoice, "preview", view.GetProperty("version").GetString(), body);
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> CorrectAsync(HttpClient client, Guid invoice, JsonElement view, CorrectionBody body)
    {
        var response = await CorrectionRawAsync(client, invoice, "", view.GetProperty("version").GetString(), body);
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<(Guid? Site, string Day, decimal Gross)> Lines(JsonElement record) =>
    [
        .. record.GetProperty("lines").EnumerateArray().Select(l => (
            l.GetProperty("constructionId").ValueKind == JsonValueKind.Null ? (Guid?)null : l.GetProperty("constructionId").GetGuid(),
            l.GetProperty("day").GetString()!, l.GetProperty("gross").GetDecimal())),
    ];

    /// <summary>Оплачен 15 дней назад, стройка А закрыта по «10 дней назад».</summary>
    private async Task<(Guid Invoice, Guid A, Guid B)> LockedAsync(
        HttpClient admin, DateOnly today, decimal total = 100_000m, bool parsed = false)
    {
        var (invoice, a, b) = await TwoSitesAsync(admin, total);
        if (parsed) await OkAsync(await admin.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));
        var paidOn = today.AddDays(-15);
        await PayAsync(admin, invoice, paidOn, await PreviewAsync(admin, invoice, paidOn), null);
        await CloseAsync(a, today.AddDays(-10));
        return (invoice, a, b);
    }

    /// <summary>Записей книги у счёта и версий — как лежат в базе.</summary>
    private async Task<(int Entries, int Versions)> BookAsync(Guid invoice)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        return (await db.InvoiceBookEntries.CountAsync(e => e.InvoiceId == invoice),
            await db.InvoiceVersions.CountAsync(v => v.InvoiceId == invoice));
    }

    /// <summary>Допуск расхождения — состояние всего экземпляра: тест обязан вернуть его (<c>null</c> — умолчание).</summary>
    private static async Task ToleranceAsync(HttpClient admin, string? value) =>
        await OkAsync(await admin.PutAsJsonAsync("/api/settings/modules/costs",
            new { values = new Dictionary<string, string?> { [ToleranceKey] = value } }));

    /// <summary>Своя роль с названными правами — системной «исправляет, но не разносит» нет.</summary>
    private async Task<string> RoleAsync(params string[] permissions)
    {
        var name = $"Correct_{Guid.NewGuid():N}";
        using var scope = host.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var role = new IdentityRole<Guid>(name);
        Assert.True((await roles.CreateAsync(role)).Succeeded);
        foreach (var permission in permissions)
            Assert.True((await roles.AddClaimAsync(role, new Claim(RoleSynchronizer.PermissionClaim, permission))).Succeeded);
        return name;
    }
}
