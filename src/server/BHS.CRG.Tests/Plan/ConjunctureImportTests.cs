using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.GrandSmeta;
using BHS.CRG.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Конъюнктурный анализ приложением к смете: загрузка тем же разбором, что ЛСР, отчёт и решение человека (change
/// <c>plan-11-conjuncture</c>, ТЗ PLAN-9, PLAN-7).
/// </summary>
[Collection(PlanOnlyCollection.Name)]
public class ConjunctureImportTests(PlanOnlyHost host)
{
    /// <summary>Несколько предложений у позиции — на нём видна выбранная минимальная.</summary>
    private const string ManyOffers = "ka-16.2.0-eom-materials";

    /// <summary>
    /// Образец КА: отчёт зовёт приложить, без принятия анализа нет; принятие даёт позиции, предложения и выбранные
    /// цены по печатной форме (эталон <c>plan-05</c>), а импорт уходит.
    /// </summary>
    [Fact]
    public async Task Образец_КА_даёт_позиции_предложения_и_выбранные_цены()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var client = await EstimateImportFlow.CostEditorAsync(host);

        var report = await ConjunctureFlow.ParsedAsync(client, estimate, ManyOffers);
        Assert.Equal("parsed", report.GetProperty("state").GetString());
        Assert.Equal("ka", report.GetProperty("kind").GetString());
        var etalon = GrandSmetaSamples.Etalon(ManyOffers);
        var inFile = report.GetProperty("inFile").GetInt32();
        Assert.Equal(inFile, report.GetProperty("parsed").GetInt32());
        var mark = Assert.Single(report.GetProperty("marks").EnumerateArray());
        Assert.Equal("notEstimate", mark.GetProperty("code").GetString());
        Assert.Contains("приложите его к смете", mark.GetProperty("text").GetString());
        Assert.False(await AnyAnalysisAsync(estimate), "Анализ заведён без решения человека.");

        var accepted = await ConjunctureFlow.AcceptAsync(client, estimate, EstimateImportFlow.Id(report));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var attachment = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("attachment");
        Assert.Equal("ka", attachment.GetProperty("kind").GetString());
        Assert.Equal(ManyOffers + ".gsfx", attachment.GetProperty("fileName").GetString());
        Assert.Equal(inFile, attachment.GetProperty("inFile").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/plan/imports/{EstimateImportFlow.Id(report)}")).StatusCode);

        var page = await ConjunctureFlow.ItemsAsync(client, ConjunctureFlow.Id(attachment), limit: 1000);
        Assert.False(page.TryGetProperty("hidden", out _));
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(inFile, page.GetProperty("total").GetInt32());
        Assert.Equal(inFile, items.Count);
        // Печатная форма позиций без предложений не печатает — эталон считает только позиции с ними.
        var offered = items.Where(i => i.GetProperty("offers").GetArrayLength() > 0).ToList();
        var counts = etalon.GetProperty("counts");
        Assert.Equal(counts.Int("positions"), offered.Count);
        Assert.Equal(counts.Int("offers"), offered.Sum(i => i.GetProperty("offers").GetArrayLength()));

        foreach (var expected in etalon.Arr("positions"))
        {
            var item = Assert.Single(items, i => i.GetProperty("chapterIndex").GetInt32() == expected.Int("chapter")
                                                 && i.GetProperty("number").GetString() == expected.Str("number"));
            Assert.Equal(expected.Str("caption"), item.GetProperty("caption").GetString());
            if (expected.Dec("minPriceWithoutVat") is not { } min) continue;
            Assert.Equal("min", item.GetProperty("selection").GetString());
            Assert.Equal(min, item.GetProperty("cost").GetProperty("selectedPrice").GetDecimal());
            var selected = Assert.Single(item.GetProperty("offers").EnumerateArray(), o => o.GetProperty("selected").GetBoolean());
            Assert.Equal(min, selected.GetProperty("cost").GetProperty("price").GetDecimal());
            Assert.Equal(item.GetProperty("selectedOffer").GetInt32(), selected.GetProperty("order").GetInt32());
        }
        Assert.Contains(items, i => i.GetProperty("offers").GetArrayLength() > 1);
        var supplier = offered[0].GetProperty("offers")[0];
        Assert.False(string.IsNullOrWhiteSpace(supplier.GetProperty("supplier").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(supplier.GetProperty("supplierInn").GetString()));

        // Повтор принятия: импорта уже нет — ответ называет заведённый анализ, второго нет.
        var again = await ConjunctureFlow.AcceptAsync(client, estimate, EstimateImportFlow.Id(report));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var repeated = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(repeated.GetProperty("repeated").GetBoolean());
        Assert.Equal(ConjunctureFlow.Id(attachment), ConjunctureFlow.Id(repeated.GetProperty("attachment")));
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/plan/estimates/{estimate}/conjuncture");
        Assert.Equal(1, list.GetArrayLength());

        // Сводка строки списка (вариант 1 макета П11): «52 позиции · 3 поставщика» — глав и поставщиков по ИНН.
        var row = list[0];
        Assert.Equal(items.Select(i => i.GetProperty("chapterIndex").GetInt32()).Distinct().Count(),
            row.GetProperty("chapters").GetInt32());
        var suppliers = items.SelectMany(i => i.GetProperty("offers").EnumerateArray())
            .Select(o => o.GetProperty("supplierInn").GetString() ?? o.GetProperty("supplier").GetString())
            .Where(s => s is not null).Distinct().Count();
        Assert.True(suppliers > 1, "В образце с тремя предложениями поставщиков больше одного.");
        Assert.Equal(suppliers, row.GetProperty("suppliers").GetInt32());
    }

    /// <summary>Разобрано не всё: без согласия — 409 с числами и анализа нет; с согласием — только разобранное.</summary>
    [Fact]
    public async Task Неполный_разбор_без_согласия_анализа_нет()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var client = await EstimateImportFlow.EditorAsync(host);

        var report = await ConjunctureFlow.ParsedAsync(client, estimate,
            EstimateImportFlow.WithBrokenPositions(ConjunctureFlow.Ka, 2), "КА.xml");
        Assert.Equal(2, report.GetProperty("unparsed").GetArrayLength());
        Assert.Contains("partial", EstimateImportFlow.Marks(report));

        var refused = await ConjunctureFlow.AcceptAsync(client, estimate, EstimateImportFlow.Id(report));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("partial", body.GetProperty("code").GetString());
        Assert.Equal(2, body.GetProperty("unparsed").GetInt32());
        Assert.False(await AnyAnalysisAsync(estimate));

        // Отказался — убрал импорт: анализа нет и не будет.
        var import = await EstimateImportFlow.GetAsync(client, EstimateImportFlow.Id(report));
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.SendAsync(EstimateImportFlow.Delete(EstimateImportFlow.Id(report), import.GetProperty("version").GetString()))).StatusCode);
        Assert.False(await AnyAnalysisAsync(estimate));

        var second = await ConjunctureFlow.ParsedAsync(client, estimate,
            EstimateImportFlow.WithBrokenPositions(ConjunctureFlow.Ka, 2), "КА.xml");
        var partial = await ConjunctureFlow.AcceptAsync(client, estimate, EstimateImportFlow.Id(second), acceptPartial: true);
        Assert.Equal(HttpStatusCode.Created, partial.StatusCode);
        var attachment = (await partial.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("attachment");
        Assert.Equal(attachment.GetProperty("inFile").GetInt32() - 2, attachment.GetProperty("parsed").GetInt32());
        Assert.Equal(attachment.GetProperty("parsed").GetInt32(),
            (await ConjunctureFlow.ItemsAsync(client, ConjunctureFlow.Id(attachment))).GetProperty("total").GetInt32());
    }

    /// <summary>
    /// Приложить можно только КА и ВОР к своей смете: ЛСР — отказ словами «создайте редакцию», импорт в новую смету —
    /// «приложить не к чему», импорт чужой сметы — 400. Импорт из окна загрузки сметы прикладывается тем же адресом.
    /// </summary>
    [Fact]
    public async Task Приложить_можно_только_разобранный_КА_или_ВОР_своей_сметы()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var other = await EstimateSeed.EstimateAsync(host, site.Construction, "Другая");
        var client = await EstimateImportFlow.EditorAsync(host);

        var lsr = await ConjunctureFlow.ParsedAsync(client, estimate, "lsr-16.2.0-vn");
        var toLsr = await ConjunctureFlow.AcceptAsync(client, estimate, EstimateImportFlow.Id(lsr));
        Assert.Equal(HttpStatusCode.Conflict, toLsr.StatusCode);
        Assert.Contains("Создать редакцию", await toLsr.Content.ReadAsStringAsync());

        var forNew = await EstimateImportFlow.OutcomeAsync(client,
            await EstimateImportFlow.UploadAsync(client, site.Construction, ConjunctureFlow.Ka));
        Assert.Contains("уберите импорт", forNew.GetProperty("marks")[0].GetProperty("text").GetString());
        var toNew = await ConjunctureFlow.AcceptAsync(client, estimate, EstimateImportFlow.Id(forNew));
        Assert.Equal(HttpStatusCode.Conflict, toNew.StatusCode);
        Assert.Contains("новую смету", await toNew.Content.ReadAsStringAsync());

        // Из окна загрузки сметы — импорт в существующую смету прикладывается к ней же, к чужой — нет.
        var fromWindow = await EstimateImportFlow.OutcomeAsync(client, await EstimateImportFlow.UploadAsync(client,
            EstimateImportFlow.Form(GrandSmetaSamples.Gsfx(ConjunctureFlow.Ka), "КА.gsfx", site.Construction, estimateId: estimate)));
        Assert.Equal(HttpStatusCode.BadRequest, (await ConjunctureFlow.AcceptAsync(client, other, EstimateImportFlow.Id(fromWindow))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ConjunctureFlow.AcceptAsync(client, estimate, EstimateImportFlow.Id(fromWindow))).StatusCode);
        Assert.True(await AnyAnalysisAsync(estimate));
        Assert.False(await AnyAnalysisAsync(other));
    }

    /// <summary>
    /// Выбранное в файле предложение (<c>MainSubPosition</c>) — «выбрана в файле», даже если оно не минимальное; номер
    /// вне числа предложений — позиция не разобрана с причиной. Образцов с атрибутом нет — правка образца.
    /// </summary>
    [Fact]
    public async Task Выбранное_в_файле_предложение_названо_выбранным()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var client = await EstimateImportFlow.CostEditorAsync(host);
        var model = ConjunctureReader.Read(GrandSmetaSamples.OpenEdited(ManyOffers, text => Choose(text, 2)));
        var position = model.Positions.First(p => p.Offers.Count > 1);
        Assert.Equal(2, position.ChosenOffer);

        var report = await ConjunctureFlow.ParsedAsync(client, estimate,
            GrandSmetaSamples.Cp1251.GetBytes(Choose(GrandSmetaSamples.DataXmlText(ManyOffers), 2)), "КА.xml");
        var accepted = await ConjunctureFlow.AcceptAsync(client, estimate, EstimateImportFlow.Id(report));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var id = ConjunctureFlow.Id((await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("attachment"));
        var item = Assert.Single((await ConjunctureFlow.ItemsAsync(client, id, limit: 1000)).GetProperty("items").EnumerateArray(),
            i => i.GetProperty("selection").GetString() == "chosen");
        Assert.Equal(position.Number, item.GetProperty("number").GetString());
        Assert.Equal(2, item.GetProperty("selectedOffer").GetInt32());
        Assert.Equal(position.Offers[1].SellingPrice, item.GetProperty("cost").GetProperty("selectedPrice").GetDecimal());

        var broken = ConjunctureReader.Read(GrandSmetaSamples.OpenEdited(ManyOffers, text => Choose(text, 99)));
        Assert.Contains(broken.Unparsed, u => u.Reason.Contains("выбрано предложение 99", StringComparison.Ordinal));
    }

    /// <summary>Вписать <c>MainSubPosition</c> первой позиции с несколькими предложениями.</summary>
    private static string Choose(string text, int offer)
    {
        var at = 0;
        while ((at = text.IndexOf("<Position ", at, StringComparison.Ordinal)) >= 0)
        {
            var end = text.IndexOf("</Position>", at, StringComparison.Ordinal);
            var body = text[at..end];
            if (body.Split("<Item ").Length - 1 > 1)
                return text[..at] + $"<Position MainSubPosition=\"{offer.ToString(CultureInfo.InvariantCulture)}\" " +
                       text[(at + "<Position ".Length)..];
            at = end;
        }
        throw new InvalidOperationException("В образце нет позиции с несколькими предложениями.");
    }

    private async Task<bool> AnyAnalysisAsync(Guid estimate)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PlanDbContext>().ConjunctureAnalyses.AnyAsync(a => a.EstimateId == estimate);
    }
}
