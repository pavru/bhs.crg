using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Periods;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Contour = BHS.CRG.Domain.Periods.PeriodContour;

namespace BHS.CRG.Tests.Integration;

// Накладные в диалоге закрытия периода (issue #1285, вторая часть).
public partial class WaybillTests
{
    private static async Task<JsonElement> ClosingPreviewAsync(HttpClient client, DateOnly from, DateOnly through, Guid? site)
    {
        var response = await client.PostAsJsonAsync("/api/periods/close/preview", new
        {
            contour = site is null ? "company" : "construction",
            constructionId = site,
            from = from.ToString("yyyy-MM-dd"),
            through = through.ToString("yyyy-MM-dd"),
            ifMatch = "none",
        });
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static JsonElement ClosingLine(JsonElement preview, string group, string key) =>
        Assert.Single(preview.GetProperty("sections").EnumerateArray()).GetProperty(group).EnumerateArray()
            .FirstOrDefault(l => l.GetProperty("key").GetString() == key);

    /// <summary>Строка перечня числом; строки нет — ноль: модуль не называет то, чего нет.</summary>
    private static int Counted(JsonElement preview, string group, string key) =>
        ClosingLine(preview, group, key) is { ValueKind: not JsonValueKind.Undefined } line ? line.GetProperty("count").GetInt32() : 0;

    private static (int Frozen, int Unmatched, int Drafts) Waybills(JsonElement preview) =>
        (Counted(preview, "frozen", "waybills"), Counted(preview, "unfinished", "waybills-unmatched"),
            Counted(preview, "unfinished", "waybills-drafts"));

    private async Task CloseAsync(Contour contour, DateOnly from, DateOnly through, DateOnly? seen)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPeriodClosures>()
            .CloseAsync(new ClosePeriod(contour, from, through, seen, null));
    }

    private static async Task<bool> LockedAsync(HttpClient client, Guid id) =>
        (await WaybillAsync(client, id)).GetProperty("locked").ValueKind != JsonValueKind.Null;

    /// <summary>
    /// Диалог закрытия называет накладные периода: сколько проведённых запрётся, у скольких из них
    /// строки без позиции и сколько черновиков остаётся. По стройке — числа точные (стройка своя); по
    /// компании — ПРИРОСТОМ: база у класса общая. Накладная соседней стройки в перечень стройки не идёт.
    /// </summary>
    [Fact]
    public async Task Диалог_закрытия_называет_накладные_периода()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (site, _) = await SiteAsync("Накладная: диалог закрытия");
        var (other, _) = await SiteAsync("Накладная: диалог закрытия, соседняя");
        var (from, through) = (new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30));
        var company = Waybills(await ClosingPreviewAsync(admin, from, through, null));

        await PostedAsync(admin, site, "2026-04-09", Row(cable, 10m, "м"));
        await PostedAsync(admin, site, "2026-04-10", Row(cable, 5m, "м"), Row(null, 2m, "шт", "Скоба"));
        await DraftAsync(admin, site, "2026-04-11", Row(cable, 1m, "м"));
        // Мимо периода и мимо стройки: майские накладная и черновик, накладная соседней стройки.
        await PostedAsync(admin, site, "2026-05-02", Row(cable, 1m, "м"));
        await DraftAsync(admin, site, "2026-05-03", Row(cable, 1m, "м"));
        await PostedAsync(admin, other, "2026-04-12", Row(cable, 1m, "м"));

        var mine = await ClosingPreviewAsync(admin, from, through, site);
        Assert.Equal((2, 1, 1), Waybills(mine));
        Assert.Equal((company.Frozen + 3, company.Unmatched + 1, company.Drafts + 1),
            Waybills(await ClosingPreviewAsync(admin, from, through, null)));
        Assert.Equal((1, 0, 0), Waybills(await ClosingPreviewAsync(admin, from, through, other)));

        // Футер диалога и журнал перечисляют незавершённое числами подряд: у черновиков единица своя,
        // иначе вышло бы «1 накладная, 1 накладная» (ревью PR #1297).
        Assert.Equal("1 накладная", ClosingLine(mine, "unfinished", "waybills-unmatched").GetProperty("counted").GetString());
        Assert.Equal("1 черновик накладной", ClosingLine(mine, "unfinished", "waybills-drafts").GetProperty("counted").GetString());
    }

    /// <summary>
    /// Главный сторож: диалог называет РОВНО те накладные, что закрытие запрёт, — число строки равно
    /// числу накладных, которые были открыты до закрытия и заперты после. В наборе — все случаи, где
    /// день накладной не равен дате отпуска или читается запасным правилом: проведённая после
    /// закрытия апреля апрельской датой (учётный день — 1 мая) и накладная без учётной даты, как из
    /// резервной копии, снятой до колонки. Уже запертая апрельская и июньская в число не идут.
    /// </summary>
    [Fact]
    public async Task Перечень_называет_ровно_те_накладные_что_закрытие_запрёт()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (site, _) = await SiteAsync("Накладная: диалог по учётной дате");
        Guid Id(JsonElement waybill) => waybill.GetProperty("id").GetGuid();

        var april = Id(await PostedAsync(admin, site, "2026-04-09", Row(cable, 10m, "м")));
        await CloseAsync(Contour.Construction(site), new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30), null);

        var late = await PostedAsync(admin, site, "2026-04-20", Row(cable, 3m, "м"));
        Assert.Equal("2026-05-01", late.GetProperty("accountingOn").GetString());
        var former = Id(await PostedAsync(admin, site, "2026-05-12", Row(cable, 1m, "м")));
        var june = Id(await PostedAsync(admin, site, "2026-06-02", Row(cable, 1m, "м")));
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CostsDbContext>().Database
                .ExecuteSqlInterpolatedAsync($"UPDATE costs.waybills SET accounting_on = NULL WHERE id = {former}");

        Guid[] all = [april, Id(late), former, june];
        var open = new List<Guid>();
        foreach (var id in all)
            if (!await LockedAsync(admin, id)) open.Add(id);
        Assert.Equal(3, open.Count);

        var named = Waybills(await ClosingPreviewAsync(admin, new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31), site)).Frozen;
        await CloseAsync(Contour.Construction(site), new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31), new DateOnly(2026, 4, 30));

        var locked = 0;
        foreach (var id in open)
            if (await LockedAsync(admin, id)) locked++;
        Assert.Equal(2, locked);
        Assert.Equal(locked, named);
    }

    /// <summary>
    /// Черновик относится к периоду, в который лёг бы при проведении СЕЙЧАС, а не к периоду даты
    /// отпуска: апрель закрыт, и черновик с апрельской датой, проведённый сегодня, лёг бы в 1 мая.
    /// Закрывая май, человек обязан о нём узнать — после закрытия он уедет в июнь (ревью PR #1297).
    /// </summary>
    [Fact]
    public async Task Черновик_с_датой_отпуска_в_закрытых_днях_назван_в_следующем_закрытии()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (site, _) = await SiteAsync("Накладная: черновик закрытого месяца");
        await DraftAsync(admin, site, "2026-04-15", Row(cable, 1m, "м"));
        await CloseAsync(Contour.Construction(site), new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30), null);

        Assert.Equal((0, 0, 1), Waybills(await ClosingPreviewAsync(admin, new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31), site)));
    }

    /// <summary>
    /// Стройка, закрытая своим закрытием дальше компании, свои накладные этих дней уже заперла: закрытие
    /// компании второй раз их не называет. А её черновик ляжет за её границей — и в мае компании его нет.
    /// </summary>
    [Fact]
    public async Task Закрытие_компании_не_называет_накладные_стройки_закрытой_дальше()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (site, _) = await SiteAsync("Накладная: стройка закрыта дальше компании");
        var (from, through) = (new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31));
        var before = Waybills(await ClosingPreviewAsync(admin, from, through, null));

        await PostedAsync(admin, site, "2026-05-07", Row(null, 2m, "шт", "Скоба"));
        await DraftAsync(admin, site, "2026-05-08", Row(cable, 1m, "м"));
        Assert.Equal((before.Frozen + 1, before.Unmatched + 1, before.Drafts + 1),
            Waybills(await ClosingPreviewAsync(admin, from, through, null)));

        await CloseAsync(Contour.Construction(site), from, through, null);
        Assert.Equal(before, Waybills(await ClosingPreviewAsync(admin, from, through, null)));
    }
}
