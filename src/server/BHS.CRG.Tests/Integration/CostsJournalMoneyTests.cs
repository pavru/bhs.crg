using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Periods;
using BHS.CRG.Domain.Activity;
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
public class CostsJournalMoneyTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
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
                ["nomenclature"] = Reference(conduit), ["supplierText"] = "Доставка", ["amount"] = Delivery,
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

        var scan = new ByteArrayContent("%PDF-1.4 скан"u8.ToArray());
        scan.Headers.ContentType = new("application/pdf");
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/scan",
            new MultipartFormDataContent { { scan, "file", "счёт.pdf" } }));

        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));
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

        var records = await RecordsOfAsync(invoice, article, spare);

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
        var records = new List<ActivityRecord>();
        foreach (var action in new InvoiceActions().Actions)
            records.AddRange(await RecordsOfAsync(action, targets));
        return records;
    }

    /// <summary>
    /// Записи одного действия, новые первыми. ⚠️ Журнал читается ДО КОНЦА, страница за страницей: база у
    /// хоста общая на все классы, и записей «счёт заведён» в ней больше страницы — первая страница молча
    /// отдала бы «записей нет» там, где они есть.
    /// </summary>
    private async Task<List<ActivityRecord>> RecordsOfAsync(ModuleActivityAction action, params Guid[] targets)
    {
        const int Page = 200;
        using var scope = host.Services.CreateScope();
        var journal = scope.ServiceProvider.GetRequiredService<IActivityLog>();
        var ids = targets.Select(t => t.ToString()).ToHashSet();

        var records = new List<ActivityRecord>();
        for (var skip = 0; ; skip += Page)
        {
            var page = await journal.ReadAsync(skip, Page, ActivityVisibility.Whole, action.Code);
            records.AddRange(page.Where(r => r.TargetId is { } id && ids.Contains(id)));
            if (page.Count < Page) return records;
        }
    }

    private async Task<int> CountAsync(ModuleActivityAction action, Guid invoice) =>
        (await RecordsOfAsync(action, invoice)).Count;

    private async Task<DateOnly> TodayAsync()
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPeriodClosures>().TodayAsync();
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd");
}
