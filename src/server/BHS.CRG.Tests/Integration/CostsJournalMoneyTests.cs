using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Periods;
using BHS.CRG.Domain.Activity;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Modules.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Сторож правила «суммы модулей в журнал действий ядра не пишем» (issue #1190, решение владельца
/// 04.10.2026): журнал читают по <c>core.audit.read</c>, без права на счета, и сумма в записи — деньги
/// счёта, показанные мимо <c>costs.invoice.read</c>.
///
/// <para>Проверяется не одно событие, а ВСЕ действия модуля: счёт с приметными суммами проходит через
/// каждый адрес, который пишет в журнал, и ни в одной записи этих сумм нет. Нарушило правило событие
/// разноски — оно клало в «было / стало» рубли каждой доли.</para>
///
/// <para>⚠️ Перечень пройденных действий сверяется с объявленным (<see cref="InvoiceActions" />): новое
/// действие модуля, которого сценарий не касается, роняет тест — иначе оно осталось бы вне присмотра
/// молча, и сторож охранял бы только то, что было на день его написания.</para>
/// </summary>
[Collection("Integration")]
public class CostsJournalMoneyTests(InvoiceScanHost host) : InvoiceLineTestBase(host), IClassFixture<InvoiceScanHost>
{
    private const decimal Price = 1_234.5m;      // 7 м × 1 234,50 = 8 641,50; доли 3 м и 4 м — 3 703,50 и 4 938,00
    private const decimal Delivery = 3_777.25m;  // строка без количества: 2 777,25 + 1 000 → 1 777,25 + 2 000
    private const decimal Total = 12_418.75m;

    /// <summary>
    /// Рубли каждой суммы сценария — цена, суммы строк, доли, итог. Ищется ЦЕЛАЯ часть отдельным числом,
    /// с пробелом-разделителем или без: так находится и «2 777,25 ₽», и «2777.25», и округлённое «2777» —
    /// сумма без копеек и без знака рубля остаётся суммой.
    ///
    /// <para>«Отдельным числом» — потому что в названиях строек и статей сидит обрывок идентификатора, и
    /// «1000» внутри «e61000» суммой не является: слева и справа от найденного не должно быть ни буквы, ни
    /// цифры.</para>
    /// </summary>
    private static readonly (string Rubles, Regex Pattern)[] Money =
        [.. new[] { "8641", "3703", "4938", "3777", "2777", "1777", "1000", "2000", "12418", "1234" }
            .Select(rubles => (rubles, new Regex(
                @"(?<![\w])" + rubles[..^3] + @"[\s\u00A0\u202F]?" + rubles[^3..] + @"(?![\w])")))];

    [Fact]
    public async Task Ни_одно_событие_модуля_не_пишет_в_журнал_суммы()
    {
        var (client, _) = await SignInAsync("Admin");
        var (a, sections) = await SiteAsync("Журнал А", "2 эт.");
        var (b, _) = await SiteAsync("Журнал Б");

        // Статьи вне строек: заведена, переименована, убрана (занятую убрать нельзя — убирается вторая).
        var (article, _) = await ArticleAsync(client, "Склад");
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/articles/{article}",
            new { name = $"Склад новый {Guid.NewGuid().ToString()[..6]}" }));
        var (spare, _) = await ArticleAsync(client, "Лишняя");
        // Архив и возврат — тоже действия модуля (issue #1185): сценарий обязан пройти через каждое.
        await OkAsync(await client.PostAsync($"/api/costs/articles/{spare}/archive", null));
        await OkAsync(await client.PostAsync($"/api/costs/articles/{spare}/unarchive", null));
        await OkAsync(await client.DeleteAsync($"/api/costs/articles/{spare}"));

        // Счёт с меткой «распознано, не подтверждено» — чтобы «Всё верно» было что снимать.
        var created = await client.PostAsJsonAsync("/api/costs/invoices", new
        {
            requisites = new Dictionary<string, object?>
            {
                ["Номер"] = $"СЧ-{Guid.NewGuid().ToString()[..6]}",
                ["Дата"] = "2026-09-29",
                ["Поставщик"] = Reference(supplier),
                ["Плательщик"] = Reference(payer),
            },
            unconfirmed = new[] { "Номер" },
        });
        await OkAsync(created);
        var invoice = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await OkAsync(await client.PostAsJsonAsync($"/api/costs/invoices/{invoice}/confirmed", new { fields = new[] { "Номер" } }));

        var view = await LinesAsync(client, invoice,
        [
            Line(cable, quantity: 7, price: Price),
            new Dictionary<string, object?>
            {
                // Позиция — кабель, а не труба: её название попадёт в журнал соответствий, а в слове
                // «Труба» сторож ниже находит «руб».
                ["nomenclature"] = Reference(cable), ["supplierText"] = "Доставка", ["amount"] = Delivery,
            },
        ]);
        var (first, second) = (LineId(view, 1), LineId(view, 2));

        // Построчная разноска: количеством — на стройки, суммой — на стройку и статью.
        await AllocateAsync(client, invoice, first, [Part(a, quantity: 3, section: sections[0]), Part(b, quantity: 4)]);
        view = await AllocateAsync(client, invoice, second,
            [Part(a, amount: 2_777.25m), ArticlePart(article, amount: 1_000m)]);

        // Матрица: те же цели, у строки без количества сдвинуты ОДНИ СУММЫ. Описание без рублей от этого
        // не меняется — событие обязано быть всё равно, и о том, что изменились суммы, сказать словами.
        var before = await CountAsync(InvoiceActions.AllocationChanged, invoice);
        view = await MatrixAsync(client, invoice, view,
            (first, [Part(a, quantity: 3, section: sections[0]), Part(b, quantity: 4)]),
            (second, [Part(a, amount: 1_777.25m), ArticlePart(article, amount: 2_000m)]));
        Assert.Equal(before + 1, await CountAsync(InvoiceActions.AllocationChanged, invoice));

        var amounts = (await RecordsOfAsync(InvoiceActions.AllocationChanged, invoice)).First();
        Assert.Equal(amounts.Before + " (изменены суммы долей)", amounts.After);

        // Та же матрица ещё раз, без правок: раскладка не менялась — и записи нет.
        view = await MatrixAsync(client, invoice, view,
            (first, [Part(a, quantity: 3, section: sections[0]), Part(b, quantity: 4)]),
            (second, [Part(a, amount: 1_777.25m), ArticlePart(article, amount: 2_000m)]));
        Assert.Equal(before + 1, await CountAsync(InvoiceActions.AllocationChanged, invoice));

        // Построчно — то же: одни суммы, одна запись, слова вместо чисел.
        var stored = view.GetProperty("lines")[1].GetProperty("allocation").GetProperty("parts").EnumerateArray()
            .Select(part => part.GetProperty("id").GetGuid()).ToList();
        await AllocateAsync(client, invoice, second,
        [
            Part(a, amount: 2_777.25m, id: stored[0]),
            With(ArticlePart(article, amount: 1_000m), "id", stored[1]),
        ]);
        amounts = (await RecordsOfAsync(InvoiceActions.AllocationChanged, invoice)).First();
        Assert.Equal(amounts.Before + " (изменены суммы долей)", amounts.After);
        Assert.Equal(before + 2, await CountAsync(InvoiceActions.AllocationChanged, invoice));

        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(client, invoice, "Итого", Total) }));

        // Срок оплаты (C4, issue #1080): ручная правка и возврат к правилу — свои события журнала.
        // Отгрузка и отсрочка — чтобы правилу было из чего считать: без них «По правилу» отказывает.
        var due = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            (await ReadAsync(client, invoice)).GetProperty("requisites").GetRawText())!;
        due["ДатаОтгрузки"] = "2026-12-01";
        due["Отсрочка"] = 10;
        due["Срок"] = "2026-12-25";
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}", new { requisites = due }));
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/due/rule", null));

        var scan = new ByteArrayContent("%PDF-1.4 скан"u8.ToArray());
        scan.Headers.ContentType = new("application/pdf");
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/scan",
            new MultipartFormDataContent { { scan, "file", "счёт.pdf" } }));

        // Распознавание скана (issue #1077) читает со счёта именно суммы — и событие о нём обязано
        // назвать только счётные «полей, строк», а не прочитанное.
        await InvoiceScanPartiesTests.OrganizationsAsync(host);
        host.Recognition.On("%PDF-1.4 скан", () => Task.FromResult(new ModuleRecognitionResult(
            [CostsRecognitionProfiles.Total, CostsRecognitionProfiles.Supplier, CostsRecognitionProfiles.SupplierTaxId],
            new Dictionary<string, string?>
            {
                [CostsRecognitionProfiles.Total] = "12 418,75 руб.",
                [CostsRecognitionProfiles.Supplier] = "ООО «Журнал»",
                [CostsRecognitionProfiles.SupplierTaxId] = InvoiceScanPartiesTests.NextTaxId(),
            },
            [CostsRecognitionProfiles.LineAmount],
            [new Dictionary<string, string?> { [CostsRecognitionProfiles.LineAmount] = "8 641,50" }], null, "сценарий")));
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/recognition", null));
        for (var deadline = DateTime.UtcNow.AddSeconds(30); ; await Task.Delay(50))
        {
            var state = (await client.GetFromJsonAsync<JsonElement>($"/api/costs/invoices/{invoice}/recognition"))
                .GetProperty("state").GetString();
            if (state == "done") break;
            Assert.True(state == "running" && DateTime.UtcNow < deadline, $"Распознавание не дошло до исхода: {state}.");
        }

        // Организация из скана: событие называет ИНН и счёт — не сумму.
        var intake = await client.PostAsJsonAsync(
            $"/api/costs/invoices/{invoice}/recognition/parties/supplier/organization", new { });
        await OkAsync(intake);
        var organization = (await intake.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("created").GetGuid();

        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/draft", null));
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/rejected", null));
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/draft", null));

        // Оплата, правка платёжного документа и отмена.
        var today = await TodayAsync();
        var preview = await client.PostAsJsonAsync($"/api/costs/invoices/{invoice}/paid/preview", new { paidOn = Iso(today) });
        await OkAsync(preview);
        var seen = (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stamp").GetString();
        await OkAsync(await client.PostAsJsonAsync($"/api/costs/invoices/{invoice}/paid",
            new { paidOn = Iso(today), document = "п/п № 7", seen }));
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/paid", new { document = "п/п № 8" }));
        await OkAsync(await client.PostAsJsonAsync($"/api/costs/invoices/{invoice}/unpaid", new { reason = "не тот счёт" }));

        // Соответствие, запомненное строкой «Доставка»: направить на другую позицию и забыть (#1079).
        var matches = await client.GetFromJsonAsync<JsonElement>(
            $"/api/costs/supplier-matches?supplierId={supplier}&query=Доставка");
        var match = matches.GetProperty("items").EnumerateArray().First(m => m.GetProperty("source").GetString() == "Доставка");
        var matchId = match.GetProperty("id").GetGuid();
        using (var point = new HttpRequestMessage(HttpMethod.Put, $"/api/costs/supplier-matches/{matchId}"))
        {
            var other = await EntryAsync(await TypeAsync(TestDirectories.NomenclatureCode, "Номенклатура"), "Позиция для журнала");
            point.Content = JsonContent.Create(new { nomenclatureId = other });
            point.Headers.TryAddWithoutValidation("If-Match", match.GetProperty("version").GetString());
            var pointed = await client.SendAsync(point);
            await OkAsync(pointed);
            using var forget = new HttpRequestMessage(HttpMethod.Delete, $"/api/costs/supplier-matches/{matchId}");
            forget.Headers.TryAddWithoutValidation("If-Match",
                (await pointed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString());
            await OkAsync(await client.SendAsync(forget));
        }

        // «Забыть все соответствия поставщика» — у своего поставщика: общий служит соседним тестам.
        var vendor = await EntryAsync(await TypeAsync(TestDirectories.OrganizationCode, "Организация"), "ООО «Журнал соответствий»");
        var extra = await client.PostAsJsonAsync("/api/costs/invoices", new
        {
            requisites = new Dictionary<string, object?>
            {
                ["Номер"] = "Ж-соответствия", ["Дата"] = "2026-09-29", ["Поставщик"] = Reference(vendor),
            },
        });
        await OkAsync(extra);
        await LinesAsync(client, (await extra.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid(),
            [Line(cable, quantity: 1, price: Price, text: "Кабель для журнала")]);
        await OkAsync(await client.DeleteAsync($"/api/costs/supplier-matches?supplierId={vendor}"));

        // Условия поставщика (C4, issue #1080) — тем же своим поставщиком: завести и снять.
        var terms = await client.PutAsJsonAsync($"/api/costs/supplier-terms/{vendor}", new { deferralDays = 30 });
        await OkAsync(terms);
        using (var removal = new HttpRequestMessage(HttpMethod.Delete, $"/api/costs/supplier-terms/{vendor}"))
        {
            removal.Headers.TryAddWithoutValidation("If-Match",
                (await terms.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString());
            await OkAsync(await client.SendAsync(removal));
        }

        // Исправление счёта закрытого периода (E2, issue #1100) — своим счётом: счёт с книгой не удаляется и
        // оплату не отменяет, а главный счёт сценария удаляется последним. Запись называет версию и причину.
        var corrected = await CorrectedAsync(client, vendor, b, today);

        // Смена допуска туда и обратно (E2, spec v3.3 п. 1.9): у исправленного счёта расхождение 0,50, и смена
        // пишет ему книгу — событием у счёта. Называет оно допуск, а не суммы.
        try
        {
            await ToleranceAsync(client, "0.40");
        }
        finally
        {
            await ToleranceAsync(client, null);
        }

        // Удаление — последним (issue #1282): счёт снова черновик без оплаты. Запись о нём называет
        // поставщика и файл — и не называет сумму.
        await OkAsync(await client.DeleteAsync($"/api/costs/invoices/{invoice}"));

        var records = await RecordsOfAsync(invoice, article, spare, organization, matchId, vendor, corrected);

        // Сценарий обязан пройти через каждое объявленное действие: непройденное — вне присмотра.
        var declared = new InvoiceActions().Actions.Select(action => action.Code).Order().ToList();
        Assert.Equal(declared, records.Select(r => r.Action).Distinct().Order().ToList());

        foreach (var record in records)
        foreach (var text in new[] { record.TargetLabel, record.Before, record.After })
        {
            if (text is null) continue;

            Assert.DoesNotContain("₽", text);
            Assert.DoesNotContain("руб", text, StringComparison.OrdinalIgnoreCase);

            foreach (var (rubles, pattern) in Money)
                Assert.False(pattern.IsMatch(text),
                    $"Событие «{record.Action}» несёт сумму {rubles}: «{text}». Суммы счёта в журнал ядра не пишутся — " +
                    "его читают без права на счета (issue #1190).");
        }

        // А то, что не деньги, событие разноски называет по-прежнему: строку, цель и количество.
        var allocations = records.Where(r => r.Action == InvoiceActions.AllocationChanged.Code).ToList();
        Assert.Contains(allocations, r => r.After!.Contains("строка 1") && r.After.Contains("Журнал А")
            && r.After.Contains("2 эт.") && r.After.Contains("— 3") && r.After.Contains("Журнал Б") && r.After.Contains("— 4"));
        Assert.Contains(allocations, r => r.After!.Contains("Журнал А") && r.After.Contains("статья «Склад новый")
            && r.After.Contains("— суммой"));
    }

    /// <summary>Записать разноску счёта матрицей — набором частей каждой строки, по отметке версии из вида.</summary>
    private static async Task<JsonElement> MatrixAsync(
        HttpClient client, Guid invoice, JsonElement view, params (Guid Line, Dictionary<string, object?>[] Parts)[] lines)
    {
        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/allocation", new
        {
            stamp = view.GetProperty("allocation").GetProperty("stamp").GetString(),
            lines = lines.Select(l => new { line = l.Line, parts = l.Parts }),
        });
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Записи журнала по всем действиям модуля — только о счёте и статьях этого теста: база у хоста общая.</summary>
    private async Task<List<ActivityRecord>> RecordsOfAsync(params Guid[] targets)
    {
        var codes = new InvoiceActions().Actions.Select(action => action.Code).ToHashSet();
        return [.. (await ExportAsync(targets)).Where(r => codes.Contains(r.Action))];
    }

    /// <summary>
    /// ЗАПИСИ одного действия, новые первыми, — сырые, а не ряды страницы журнала. Правки шапки и строк
    /// одной сессии сворачиваются в ряд (ТЗ COST-6), а сумм обязана не нести ни одна запись, в том числе
    /// из середины серии: раскрытый ряд показывает каждую.
    /// </summary>
    private async Task<List<ActivityRecord>> RecordsOfAsync(ModuleActivityAction action, params Guid[] targets) =>
        [.. (await ExportAsync(targets)).Where(r => r.Action == action.Code)];

    /// <summary>Записи об этих целях, новые первыми, — одной выгрузкой журнала: он общий на все тесты хоста.</summary>
    private async Task<IEnumerable<ActivityRecord>> ExportAsync(Guid[] targets)
    {
        using var scope = host.Services.CreateScope();
        var ids = targets.Select(t => t.ToString()).ToHashSet();
        return (await scope.ServiceProvider.GetRequiredService<IActivityLog>().ExportAsync())
            .Where(r => r.TargetId is { } id && ids.Contains(id))
            .Reverse()
            .ToList();
    }

    private async Task<int> CountAsync(ModuleActivityAction action, Guid invoice) =>
        (await RecordsOfAsync(action, invoice)).Count;

    private async Task<DateOnly> TodayAsync()
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPeriodClosures>().TodayAsync();
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd");

    /// <summary>Допуск расхождения — состояние всего хоста: тест обязан вернуть его (<c>null</c> — умолчание).</summary>
    private static async Task ToleranceAsync(HttpClient client, string? value) =>
        await OkAsync(await client.PutAsJsonAsync("/api/settings/modules/costs",
            new { values = new Dictionary<string, string?> { ["costs.allocation.tolerance"] = value } }));

    /// <summary>
    /// Счёт на одну строку (1 × цена сценария) на стройку, оплачен вчера; стройка закрыта по вчера — и счёт
    /// исправлен без изменений по существу. Закрытие потом отменяется: оно — состояние всего хоста.
    /// </summary>
    private async Task<Guid> CorrectedAsync(HttpClient client, Guid supplier, Guid site, DateOnly today)
    {
        var created = await client.PostAsJsonAsync("/api/costs/invoices", new
        {
            requisites = new Dictionary<string, object?>
            {
                // К оплате на 0,50 больше строки: расхождение в допуске — смене допуска есть что сдвинуть.
                ["Номер"] = "Ж-исправление", ["Дата"] = "2026-09-29", ["Поставщик"] = Reference(supplier), ["Итого"] = Price + 0.50m,
            },
        });
        await OkAsync(created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var view = await LinesAsync(client, id, [Line(cable, quantity: 1, price: Price, text: "Кабель")]);
        view = await AllocateAsync(client, id, LineId(view, 1), [Part(site, quantity: 1)]);

        var paidOn = today.AddDays(-1);
        var preview = await client.PostAsJsonAsync($"/api/costs/invoices/{id}/paid/preview", new { paidOn = Iso(paidOn) });
        await OkAsync(preview);
        await OkAsync(await client.PostAsJsonAsync($"/api/costs/invoices/{id}/paid", new
        {
            paidOn = Iso(paidOn),
            seen = (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stamp").GetString(),
        }));

        using var scope = host.Services.CreateScope();
        var closures = scope.ServiceProvider.GetRequiredService<IPeriodClosures>();
        var contour = BHS.CRG.Domain.Periods.PeriodContour.Construction(site);
        await closures.CloseAsync(new ClosePeriod(contour, paidOn.AddDays(-30), paidOn, null, null));
        try
        {
            view = await ReadAsync(client, id);
            var body = new
            {
                reason = "опечатка в наименовании",
                requisites = view.GetProperty("requisites"),
                lines = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["id"] = LineId(view, 1).ToString(), ["nomenclature"] = Reference(cable),
                        ["supplierText"] = "Кабель ВВГ", ["quantity"] = 1, ["price"] = Price,
                        ["parts"] = new object[] { Part(site, quantity: 1) },
                    },
                },
                document = Array.Empty<object>(),
                vatMode = "Included",
            };
            var shown = await client.PostAsJsonAsync($"/api/costs/invoices/{id}/correction/preview", body);
            await OkAsync(shown);
            var stamp = (await shown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stamp").GetString();
            await OkAsync(await client.PostAsJsonAsync($"/api/costs/invoices/{id}/correction", new
            {
                body.reason, body.requisites, body.lines, body.document, body.vatMode, stamp,
            }));
        }
        finally
        {
            await closures.ReopenAsync(new ReopenPeriod(contour, paidOn, "для теста"));
        }

        return id;
    }
}
