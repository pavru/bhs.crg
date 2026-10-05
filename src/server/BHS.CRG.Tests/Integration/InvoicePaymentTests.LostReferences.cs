using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Потерянные ссылки модуля на записи ядра (issue #1184, ТЗ CORE-34.2–34.4). Тот же класс, что
/// <c>InvoicePaymentTests.cs</c>, — отдельным файлом по занятию: здесь нужны и оплата, и закрытие периода.
///
/// <para>Записи ядра убираются ПРЯМО В БАЗЕ, в обход отказа (#1094): через приложение занятую запись
/// удалить нельзя, а потери приходят как раз мимо него — восстановлением копии и гонкой.</para>
/// </summary>
public partial class InvoicePaymentTests
{
    private sealed record LostCase(
        Guid Invoice, Guid Supplier, Guid Payer, Guid Position, Guid Article, Guid Site, Guid Section, Guid KeptSite);

    /// <summary>
    /// Счёт, у которого потеряно всё, что может быть потеряно: поставщик, плательщик, позиция строки,
    /// стройка с разделом и статья. Вторая строка — на живую стройку: она обязана остаться чистой.
    /// </summary>
    private async Task<LostCase> LostInvoiceAsync(HttpClient admin)
    {
        var organizations = await TypeAsync(CostsRecordTypes.OrganizationCode, "Организация");
        var nomenclature = await TypeAsync(CostsRecordTypes.NomenclatureCode, "Номенклатура");
        var mark = Guid.NewGuid().ToString()[..6];
        var lostSupplier = await EntryAsync(organizations, $"Поставщик потерянный {mark}");
        var lostPayer = await EntryAsync(organizations, $"Плательщик потерянный {mark}");
        var position = await EntryAsync(nomenclature, $"Позиция потерянная {mark}");
        var (article, _) = await ArticleAsync(admin, "Статья потерянная");
        var (site, sections) = await SiteAsync("Стройка потерянная", "раздел");
        var (kept, _) = await SiteAsync("Стройка живая");

        var invoice = await CreateAsync(admin, complete: true);
        await OkAsync(await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(admin, invoice, "Поставщик", Reference(lostSupplier)) }));
        await OkAsync(await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(admin, invoice, "Плательщик", Reference(lostPayer)) }));
        var view = await LinesAsync(admin, invoice, [Line(position, 100, 400), Line(conduit, 50, 1200)]);
        await AllocateAsync(admin, invoice, LineId(view, 1),
            [Part(site, quantity: 60, section: sections[0]), ArticlePart(article, quantity: 40)]);
        await AllocateAsync(admin, invoice, LineId(view, 2), [Part(kept, quantity: 50)]);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            """DELETE FROM domain_objects WHERE "Id" IN ({0}, {1}, {2}, {3})""", lostSupplier, lostPayer, position, article);
        await db.Sections.Where(s => s.ConstructionId == site).ExecuteDeleteAsync();
        await db.Constructions.Where(c => c.Id == site).ExecuteDeleteAsync();

        return new(invoice, lostSupplier, lostPayer, position, article, site, sections[0], kept);
    }

    private async Task<LostReferences> LostAsync()
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IModuleReferenceTargets>().LostAsync(CostsModule.ModuleCode);
    }

    private static async Task<(int References, int Invoices)> TallyAsync(HttpClient client, string which)
    {
        var tally = (await client.GetFromJsonAsync<JsonElement>("/api/costs/lost-references")).GetProperty(which);
        return (tally.GetProperty("references").GetInt32(), tally.GetProperty("invoices").GetInt32());
    }

    /// <summary>
    /// Сторож задачи: <b>запись ядра убрана в обход отказа — опрос называет ровно эти ссылки</b>, по
    /// каждой колонке отдельно, и ничего сверх: живая стройка второй строки и общие записи посева в
    /// ответ не попали. Молчащий опрос здесь упал бы первым.
    /// </summary>
    [Fact]
    public async Task Опрос_называет_ровно_ссылки_на_записи_убранные_в_обход_отказа()
    {
        var (admin, _) = await SignInAsync("Admin");
        var before = await TallyAsync(admin, "editable");
        var lost = await LostInvoiceAsync(admin);

        // Тип документа ядро удалить не даст и мимо отказа (на него смотрят записи), поэтому потеря
        // сделана с другой стороны: у второго счёта в колонке — идентификатор, которого нет.
        var orphan = await CreateAsync(admin);
        var noType = Guid.NewGuid();
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync(
                "UPDATE costs.invoices SET document_type_id = {0} WHERE id = {1}", noType, orphan);

        var found = await LostAsync();
        Assert.Equal(
            new[]
            {
                ("invoice_allocations", "article_id", ReferenceTarget.Record, lost.Article, 1),
                ("invoice_allocations", "construction_id", ReferenceTarget.Construction, lost.Site, 1),
                ("invoice_allocations", "section_id", ReferenceTarget.Section, lost.Section, 1),
                ("invoice_lines", "nomenclature_id", ReferenceTarget.Record, lost.Position, 1),
                ("invoices", "payer_id", ReferenceTarget.Record, lost.Payer, 1),
                ("invoices", "supplier_id", ReferenceTarget.Record, lost.Supplier, 1),
            },
            found.Lost.Where(l => l.DocumentKey == lost.Invoice)
                .Select(l => (l.Table, l.Column, l.Target, l.TargetId, l.Rows))
                .OrderBy(l => l.Table, StringComparer.Ordinal).ThenBy(l => l.Column, StringComparer.Ordinal));
        Assert.Equal(
            [("invoices", "document_type_id", ReferenceTarget.DocumentType, noType)],
            found.Lost.Where(l => l.DocumentKey == orphan).Select(l => (l.Table, l.Column, l.Target, l.TargetId)));

        // Дополнительные поля типа не проверены — и это сказано, а не спрятано за нулём.
        Assert.Contains(found.Unchecked, u => u is { Table: "invoices", Column: "data", Reason: UncheckedReason.MixedTargets });

        // Счётчик модуля: шесть ссылок первого счёта и одна второго — в двух счетах.
        var after = await TallyAsync(admin, "editable");
        Assert.Equal((before.References + 7, before.Invoices + 2), after);
        var unchecked_ = (await admin.GetFromJsonAsync<JsonElement>("/api/costs/lost-references")).GetProperty("unchecked");
        Assert.Contains(unchecked_.EnumerateArray(), u => u.GetProperty("what").GetString()!.Contains("дополнительном поле"));
    }

    /// <summary>
    /// <b>Счёт с потерянными ссылками открывается, показывает потерю на месте значения и сохраняется</b>
    /// (ТЗ CORE-34.4): правка несвязанного поля, строк и разноски проходит, а идентификаторы потерянных
    /// записей остаются в базе прежними — потеря не стирается. Новая же ссылка в пустоту отвергается.
    /// </summary>
    [Fact]
    public async Task Счёт_с_потерянными_ссылками_помечен_сохраняется_и_ссылок_не_теряет()
    {
        var (admin, _) = await SignInAsync("Admin");
        var lost = await LostInvoiceAsync(admin);

        var view = await ReadAsync(admin, lost.Invoice);
        var references = view.GetProperty("references");
        Assert.Equal("lost", references.GetProperty("supplier").GetString());
        Assert.Equal("lost", references.GetProperty("payer").GetString());
        Assert.Equal("present", references.GetProperty("documentType").GetString());
        Assert.Equal([true, false], view.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("nomenclatureLost").GetBoolean()));
        static string[] Issues(JsonElement invoice, int line) =>
            [.. invoice.GetProperty("lines")[line - 1].GetProperty("allocation").GetProperty("parts").EnumerateArray()
                .Select(p => p.GetProperty("targetIssue").GetString() ?? "—")];
        Assert.Equal(["construction-lost", "article-lost"], Issues(view, 1));
        Assert.Equal(["—"], Issues(view, 2));

        // Правка шапки: сумма к оплате — поле, с потерянными не связанное.
        await OkAsync(await admin.PutAsJsonAsync($"/api/costs/invoices/{lost.Invoice}",
            new { requisites = await RequisitesWithAsync(admin, lost.Invoice, "Итого", 100_000m) }));

        // Правка строк: цена второй строки; первая — с потерянной позицией — едет как была.
        var first = view.GetProperty("lines")[0];
        var second = view.GetProperty("lines")[1];
        await LinesAsync(admin, lost.Invoice,
        [
            Line(lost.Position, 100, 400, id: first.GetProperty("id").GetGuid()),
            Line(conduit, 50, 1300, id: second.GetProperty("id").GetGuid()),
        ]);

        // Правка разноски строки с потерянными целями: те же цели, другие количества.
        await AllocateAsync(admin, lost.Invoice, first.GetProperty("id").GetGuid(),
            [Part(lost.Site, quantity: 70, section: lost.Section), ArticlePart(lost.Article, quantity: 30)]);

        using (var scope = host.Services.CreateScope())
        {
            var costs = scope.ServiceProvider.GetRequiredService<BHS.CRG.Modules.Costs.Data.CostsDbContext>();
            var stored = await costs.Invoices.AsNoTracking().SingleAsync(i => i.Id == lost.Invoice);
            Assert.Equal((lost.Supplier, lost.Payer), (stored.SupplierId!.Value, stored.PayerId!.Value));
            Assert.Contains(await costs.InvoiceLines.AsNoTracking().Where(l => l.InvoiceId == lost.Invoice).ToListAsync(),
                l => l.NomenclatureId == lost.Position);
            var parts = await costs.InvoiceAllocations.AsNoTracking().Where(a => a.InvoiceId == lost.Invoice).ToListAsync();
            Assert.Contains(parts, a => a.ConstructionId == lost.Site && a.SectionId == lost.Section && a.Quantity == 70);
            Assert.Contains(parts, a => a.ArticleId == lost.Article && a.Quantity == 30);
        }
        Assert.Equal("lost", (await ReadAsync(admin, lost.Invoice)).GetProperty("references").GetProperty("supplier").GetString());

        // Новая ссылка в пустоту — отказ с именем поля: потерю своими руками завести нельзя.
        var nowhere = await admin.PutAsJsonAsync($"/api/costs/invoices/{lost.Invoice}",
            new { requisites = await RequisitesWithAsync(admin, lost.Invoice, "Поставщик", Reference(Guid.NewGuid())) });
        Assert.Equal(HttpStatusCode.BadRequest, nowhere.StatusCode);
        Assert.Contains("«Поставщик»", await nowhere.Content.ReadAsStringAsync());

        var newPart = await AllocateRawAsync(admin, lost.Invoice, second.GetProperty("id").GetGuid(), [Part(lost.Site, quantity: 50)]);
        Assert.Equal(HttpStatusCode.BadRequest, newPart.StatusCode);
    }

    /// <summary>
    /// <b>Счёт закрытого периода в счётчик не идёт</b> (решение владельца): исправить его нельзя, и
    /// требовать этого незачем. Но и не пропадает — назван отдельно; а после отмены закрытия переезжает
    /// в «можно исправить» сам, потому что нигде не хранится.
    /// </summary>
    [Fact]
    public async Task Потеря_в_счёте_закрытого_периода_в_счётчик_не_идёт_а_после_отмены_закрытия_возвращается()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var nomenclature = await TypeAsync(CostsRecordTypes.NomenclatureCode, "Номенклатура");
        var position = await EntryAsync(nomenclature, $"Позиция закрытого {Guid.NewGuid().ToString()[..6]}");
        var (site, _) = await SiteAsync("Стройка закрытая");

        var invoice = await CreateAsync(admin, complete: true);
        var view = await LinesAsync(admin, invoice, [Line(position, 100, 400)]);
        await AllocateAsync(admin, invoice, LineId(view, 1), [Part(site, quantity: 100)]);
        await OkAsync(await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(admin, invoice, "Итого", 40_000m) }));
        // Оплачен задним числом: закрыть можно только прошедшие дни.
        var paidOn = today.AddDays(-5);
        await PayAsync(admin, invoice, paidOn, await PreviewAsync(admin, invoice, paidOn), null);

        var (editable, locked) = (await TallyAsync(admin, "editable"), await TallyAsync(admin, "locked"));
        await ForgetAsync(position);
        Assert.Equal((editable.References + 1, editable.Invoices + 1), await TallyAsync(admin, "editable"));

        await CloseAsync(site, today.AddDays(-1));
        Assert.Equal(editable, await TallyAsync(admin, "editable"));
        Assert.Equal((locked.References + 1, locked.Invoices + 1), await TallyAsync(admin, "locked"));

        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync("TRUNCATE TABLE period_closures");
        Assert.Equal((editable.References + 1, editable.Invoices + 1), await TallyAsync(admin, "editable"));
        Assert.Equal(locked, await TallyAsync(admin, "locked"));
    }

    /// <summary>
    /// <b>Раздел другой стройки — не потеря</b> (решение владельца 06.10.2026): запись на месте, опрос
    /// ядра ответит «существует». Назван он отдельно, и в счётчик не идёт — иначе пометка в форме и число
    /// говорили бы разное.
    /// </summary>
    [Fact]
    public async Task Раздел_другой_стройки_назван_отдельно_и_потерей_не_считается()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (a, _) = await SiteAsync("Чужой раздел А");
        var (_, ofB) = await SiteAsync("Чужой раздел Б", "раздел Б");

        var invoice = await CreateAsync(admin, complete: true);
        var view = await LinesAsync(admin, invoice, [Line(cable, 100, 400)]);
        await AllocateAsync(admin, invoice, LineId(view, 1), [Part(a, quantity: 100)]);
        // Через приложение такую часть не записать — раздел выбирают внутри стройки. Это старые данные.
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync(
                "UPDATE costs.invoice_allocations SET section_id = {0} WHERE invoice_id = {1}", ofB[0], invoice);

        var part = (await ReadAsync(admin, invoice)).GetProperty("lines")[0].GetProperty("allocation").GetProperty("parts")[0];
        Assert.Equal("section-foreign", part.GetProperty("targetIssue").GetString());
        Assert.True(part.GetProperty("targetLost").GetBoolean());
        Assert.DoesNotContain((await LostAsync()).Lost, l => l.DocumentKey == invoice);
    }
}
