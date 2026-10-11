using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Periods;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BHS.CRG.Modules.Costs.Data;
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

    /// <summary>Строка перечня числом; строки нет — ноль: модуль не называет то, чего нет.</summary>
    private static int Counted(JsonElement preview, string group, string key)
    {
        var line = Assert.Single(preview.GetProperty("sections").EnumerateArray()).GetProperty(group).EnumerateArray()
            .FirstOrDefault(l => l.GetProperty("key").GetString() == key);
        return line.ValueKind == JsonValueKind.Undefined ? 0 : line.GetProperty("count").GetInt32();
    }

    private static (int Frozen, int Unmatched, int Drafts) Waybills(JsonElement preview) =>
        (Counted(preview, "frozen", "waybills"), Counted(preview, "unfinished", "waybills-unmatched"),
            Counted(preview, "unfinished", "waybills-drafts"));

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
        // Мимо периода и мимо стройки: майская накладная и накладная соседней стройки.
        await PostedAsync(admin, site, "2026-05-02", Row(cable, 1m, "м"));
        await PostedAsync(admin, other, "2026-04-12", Row(cable, 1m, "м"));

        Assert.Equal((2, 1, 1), Waybills(await ClosingPreviewAsync(admin, from, through, site)));
        Assert.Equal((company.Frozen + 3, company.Unmatched + 1, company.Drafts + 1),
            Waybills(await ClosingPreviewAsync(admin, from, through, null)));
        Assert.Equal((1, 0, 0), Waybills(await ClosingPreviewAsync(admin, from, through, other)));
    }

    /// <summary>
    /// «Запрутся» считает то же правило, что запирает: по УЧЁТНОЙ дате. Накладная, проведённая после
    /// закрытия апреля датой отпуска в апреле, легла в первый открытый день — в мае, — и в перечень
    /// следующего закрытия попадает она, а не в апрельский. И названное число равно числу запертых.
    /// </summary>
    [Fact]
    public async Task Перечень_накладных_идёт_по_учётной_дате_и_равен_числу_запертых()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (site, _) = await SiteAsync("Накладная: диалог по учётной дате");
        var first = (await PostedAsync(admin, site, "2026-04-09", Row(cable, 10m, "м"))).GetProperty("id").GetGuid();
        await CloseAsync(Contour.Construction(site), new DateOnly(2026, 4, 30));

        // Датой отпуска — закрытый апрель; учётный день — 1 мая.
        var late = await PostedAsync(admin, site, "2026-04-20", Row(cable, 3m, "м"));
        Assert.Equal("2026-05-01", late.GetProperty("accountingOn").GetString());

        // Уже закрытый апрель второй раз не называется; май называет одну — ту, что с апрельской датой отпуска.
        var may = await ClosingPreviewAsync(admin, new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31), site);
        Assert.Equal((1, 0, 0), Waybills(may));

        using (var closing = host.Services.CreateScope())
            await closing.ServiceProvider.GetRequiredService<IPeriodClosures>().CloseAsync(new ClosePeriod(
                Contour.Construction(site), new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31), new DateOnly(2026, 4, 30), null));
        var locked = 0;
        foreach (var id in new[] { first, late.GetProperty("id").GetGuid() })
            if ((await WaybillAsync(admin, id)).GetProperty("locked").ValueKind != JsonValueKind.Null) locked++;
        Assert.Equal(2, locked);

        // Сторож от слепоты: накладные стройки в базе именно такие, какими их считал перечень.
        using var scope = host.Services.CreateScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<CostsDbContext>().Waybills
            .CountAsync(w => w.ConstructionId == site && w.State == WaybillState.Posted));
    }
}
