using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Settings;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Список накладных под «Избранными стройками» (issue #1222): отбор делает сервер по перечню
/// <c>sites</c>, потому что список страничный. Чем ломается: серверная ветвь правила расходится с
/// клиентской на накладной с удалённой стройкой, и <c>hidden</c> врёт, когда список усечён пределом.
/// </summary>
public partial class WaybillTests
{
    /// <summary>
    /// Общий файл случаев правила — тот же, что читает vitest (<c>rule.test.ts</c>). Буква — стройка:
    /// A, B, C живые, X и Y — ссылки, которых нет в справочнике (удалённые стройки).
    ///
    /// <para>Случаи с несколькими стройками у объекта накладной не бывают — у неё одна стройка; они
    /// названы и пропущены явно, а не потеряны. «Фильтр выключен» и «избранное пусто» — половина
    /// правила, которую решает клиент: он не шлёт <c>sites</c>, и сервер отвечает без отбора.</para>
    /// </summary>
    [Fact]
    public async Task Отбор_sites_проходит_общий_файл_случаев()
    {
        var (client, _) = await SignInAsync("Supplier");
        var letters = new Dictionary<string, Guid>
        {
            ["A"] = (await SiteAsync("Избранное A")).Site,
            ["B"] = (await SiteAsync("Избранное B")).Site,
            ["C"] = (await SiteAsync("Избранное C")).Site,
            ["X"] = Guid.NewGuid(),
            ["Y"] = Guid.NewGuid(),
        };

        var file = Path.Combine(SourceTree.SolutionDir, "..", "client", "src", "shared", "siteFilter", "siteFilterCases.json");
        var cases = JsonDocument.Parse(await File.ReadAllTextAsync(file)).RootElement.GetProperty("cases").EnumerateArray().ToList();

        var run = 0;
        var several = new List<string>();
        foreach (var c in cases)
        {
            var name = c.GetProperty("name").GetString()!;
            var sites = c.GetProperty("sites").EnumerateArray().Select(s => letters[s.GetString()!]).ToList();
            if (sites.Count > 1) { several.Add(name); continue; }

            var favorites = c.GetProperty("favorites").EnumerateArray().Select(s => letters[s.GetString()!]).ToList();
            var query = c.GetProperty("on").GetBoolean() && favorites.Count > 0
                ? $"?sites={string.Join(',', favorites)}" : string.Empty;

            var waybill = (await SeedAsync(new DateOnly(2099, 1, 1), sites.Count == 0 ? null : sites[0], 1)).Single();
            var listed = (await ListAsync(client, query)).Any(i => i.GetProperty("id").GetGuid() == waybill);
            Assert.True(c.GetProperty("visible").GetBoolean() == listed, $"{name}: в списке — {listed}");
            run++;
        }

        // Положительный якорь: пустой или переписанный файл прошёл бы цикл, ничего не проверив.
        Assert.True(run >= 8, $"прогнано случаев: {run}");
        Assert.Equal(cases.Count, run + several.Count);
    }

    [Fact]
    public async Task Негодный_перечень_sites_отказ_а_не_полный_список()
    {
        var (client, _) = await SignInAsync("Supplier");
        var a = Guid.NewGuid().ToString();
        var twenty = string.Join(',', Enumerable.Range(0, WaybillSiteFilter.MaxSites).Select(_ => Guid.NewGuid()));

        foreach (var query in new[]
                 {
                     "sites=", "sites=abc", $"sites={a},%20{Guid.NewGuid()}", $"sites={a},{a}",
                     $"sites={twenty},{Guid.NewGuid()}", $"sites={a}&sites={Guid.NewGuid()}",
                 })
            Assert.True(HttpStatusCode.BadRequest == (await client.GetAsync($"/api/costs/waybills?{query}")).StatusCode, query);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/costs/waybills?sites={twenty}")).StatusCode);
        // Предел тот же, что у настройки избранного: модуль ядра не видит и держит своё число.
        Assert.Equal(FavoriteConstructionValues.MaxFavorites, WaybillSiteFilter.MaxSites);
    }

    /// <summary>
    /// <c>hidden</c> — по всему отбору, а не по странице. Сверху лежит предел накладных избранной
    /// стройки, скрытые — старые, ниже любой страницы: число, посчитанное по странице, их не увидело бы.
    /// Без перечня <c>hidden</c> нет вовсе — «не отбирали» не выдаёт себя за «ничего не скрыто».
    /// </summary>
    [Fact]
    public async Task Число_скрытых_верно_и_когда_список_усечён()
    {
        var (client, _) = await SignInAsync("Supplier");
        var favorite = (await SiteAsync("Избранное: предел")).Site;
        var other = (await SiteAsync("Не избранное: предел")).Site;
        var seeded = new List<Guid>();
        try
        {
            seeded.AddRange(await SeedAsync(new DateOnly(2098, 1, 1), favorite, WaybillEndpoints.ListLimit + 1));
            var before = await PageAsync(client, $"?sites={favorite}");

            seeded.AddRange(await SeedAsync(new DateOnly(1990, 1, 1), other, 3));
            var after = await PageAsync(client, $"?sites={favorite}");

            Assert.True(after.GetProperty("more").GetBoolean());
            Assert.Equal(before.GetProperty("hidden").GetInt32() + 3, after.GetProperty("hidden").GetInt32());
            Assert.Equal(JsonValueKind.Null, (await PageAsync(client, "")).GetProperty("hidden").ValueKind);
        }
        finally
        {
            // Предел накладных остался бы в общей базе, и соседний тест получил бы «есть ещё».
            using var scope = host.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<CostsDbContext>().Waybills
                .Where(w => seeded.Contains(w.Id)).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// Накладные мимо адреса: он не заводит накладную на стройку, которой нет, а потерянная ссылка
    /// приходит восстановлением копии и гонкой с удалением стройки. И быстрее — пределу нужны сотни.
    /// </summary>
    private async Task<List<Guid>> SeedAsync(DateOnly issuedOn, Guid? site, int count)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var made = Enumerable.Range(0, count).Select(i =>
        {
            var waybill = Waybill.Create(null);
            waybill.Apply(new WaybillHeader($"РН-{i}", issuedOn, null, site, null, null));
            return waybill;
        }).ToList();
        db.Waybills.AddRange(made);
        await db.SaveChangesAsync();
        return [.. made.Select(w => w.Id)];
    }

    private static async Task<JsonElement> PageAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync("/api/costs/waybills" + query);
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
