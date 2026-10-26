using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Contour = BHS.CRG.Domain.Periods.PeriodContour;

namespace BHS.CRG.Tests.Integration;

// Исправление накладной закрытого периода (issue #1340, ТЗ COST-16, CORE-36): одно действие — причина и всё
// содержимое; прежнее уходит версией, накладная остаётся проведённой, «Материалы на объекте» не удваиваются.
public partial class WaybillTests
{
    private static readonly DateOnly June = new(2026, 6, 30);

    /// <summary>Исправление: тело как у правки черновика плюс причина.</summary>
    private static Dictionary<string, object?> Correction(
        Guid site, string issuedOn, string? reason, params Dictionary<string, object?>[] rows)
    {
        var body = Header(site, issuedOn);
        body["reason"] = reason;
        body["lines"] = rows;
        return body;
    }

    private static async Task<HttpResponseMessage> CorrectAsync(HttpClient client, Guid id, object body) =>
        await client.PostAsJsonAsync($"/api/costs/waybills/{id}/correction", body);

    private static async Task<List<JsonElement>> VersionsAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/costs/waybills/{id}/versions");
        await OkAsync(response);
        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()];
    }

    /// <summary>Проведённая накладная июня, запертая закрытием стройки по 30.06.</summary>
    private async Task<(HttpClient Client, Guid Site, Guid Id)> LockedAsync(string name, decimal quantity = 10m)
    {
        var (client, _) = await SignInAsync("Supplier");
        var (site, _) = await SiteAsync(name);
        var id = (await PostedAsync(client, site, "2026-06-09", Row(cable, quantity, "м"))).GetProperty("id").GetGuid();
        await CloseAsync(Contour.Construction(site), June);
        return (client, site, id);
    }

    [Fact]
    public async Task Исправление_пишет_версию_с_причиной_и_материалы_не_удваиваются()
    {
        var (client, site, id) = await LockedAsync("Накладная: исправление");

        var corrected = await CorrectAsync(client, id, Correction(site, "2026-06-09", "Пересчёт по факту", Row(cable, 8m, "м")));
        await OkAsync(corrected);
        var view = await corrected.Content.ReadFromJsonAsync<JsonElement>();

        // Накладная остаётся проведённой и запертой, учётная дата та же: стройка не менялась.
        Assert.Equal("Posted", view.GetProperty("state").GetString());
        Assert.Equal("2026-06-09", view.GetProperty("accountingOn").GetString());
        Assert.NotEqual(JsonValueKind.Null, view.GetProperty("locked").ValueKind);
        Assert.Equal(2, view.GetProperty("currentVersion").GetInt32());
        Assert.Equal(8m, view.GetProperty("lines")[0].GetProperty("quantity").GetDecimal());

        // «Материалы на объекте» — новое количество, а не сумма с версией.
        Assert.Equal(8m, Issued(await MaterialsAsync(client, site)));

        var first = Assert.Single(await VersionsAsync(client, id));
        Assert.Equal(1, first.GetProperty("number").GetInt32());
        Assert.Equal("Пересчёт по факту", first.GetProperty("reason").GetString());
        Assert.False(string.IsNullOrEmpty(first.GetProperty("authorName").GetString()));
        Assert.Equal(10m, first.GetProperty("content").GetProperty("lines")[0].GetProperty("quantity").GetDecimal());

        // Пометка в списке — с причиной последнего исправления.
        var row = Assert.Single(await ListAsync(client, $"?constructionId={site}"));
        Assert.Equal("Пересчёт по факту", row.GetProperty("correctedAfterClosing").GetProperty("reason").GetString());

        // Второе исправление — вторая версия; прежняя на месте, свежие сверху.
        await OkAsync(await CorrectAsync(client, id, Correction(site, "2026-06-09", "Ещё раз", Row(cable, 7m, "м"))));
        var versions = await VersionsAsync(client, id);
        Assert.Equal([2, 1], versions.Select(v => v.GetProperty("number").GetInt32()));
        Assert.Equal(8m, versions[0].GetProperty("content").GetProperty("lines")[0].GetProperty("quantity").GetDecimal());
        Assert.Equal(7m, Issued(await MaterialsAsync(client, site)));
        Assert.Equal("Ещё раз",
            Assert.Single(await ListAsync(client, $"?constructionId={site}")).GetProperty("correctedAfterClosing")
                .GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Неисправленная_накладная_в_списке_не_помечена()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (site, _) = await SiteAsync("Накладная: без пометки");
        await PostedAsync(client, site, "2026-09-09", Row(cable, 1m, "м"));

        var row = Assert.Single(await ListAsync(client, $"?constructionId={site}"));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("correctedAfterClosing").ValueKind);
    }

    /// <summary>
    /// Порядок отказов — как у исправления счёта: «не нужно» и «нет права» раньше версии формы (D3).
    /// </summary>
    [Fact]
    public async Task Исправление_отказывает_в_названном_порядке()
    {
        var (client, site, id) = await LockedAsync("Накладная: отказы исправления");
        var body = Correction(site, "2026-06-09", "Причина", Row(cable, 9m, "м"));
        var before = await WaybillAsync(client, id);

        var noReason = await CorrectAsync(client, id, Correction(site, "2026-06-09", "  ", Row(cable, 9m, "м")));
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Contains("причина", (await noReason.Content.ReadAsStringAsync()).ToLowerInvariant());

        // Право правки накладных без права исправления: заперта — 403 с выходом, а не «нет права» вообще.
        var editor = await WithOnlyAsync("costs.waybill.read", "costs.waybill.edit");
        var forbidden = await CorrectAsync(editor, id, body);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Contains("Исправлять накладные закрытого периода", await forbidden.Content.ReadAsStringAsync());

        var unnamed = await SeenAsync(client, HttpMethod.Post, $"/api/costs/waybills/{id}/correction", SeenInvoiceVersion.Omit, body);
        Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);
        Assert.Contains("If-Match", await unnamed.Content.ReadAsStringAsync());

        var stale = (ulong.Parse(before.GetProperty("version").GetString()!) - 1).ToString();
        var outdated = await SeenAsync(client, HttpMethod.Post, $"/api/costs/waybills/{id}/correction", stale, body);
        Assert.Equal(HttpStatusCode.Conflict, outdated.StatusCode);
        Assert.Contains("тем временем изменили", await outdated.Content.ReadAsStringAsync());

        // Ничего не записано.
        Assert.Equal(before.GetRawText(), (await WaybillAsync(client, id)).GetRawText());
        Assert.Empty(await VersionsAsync(client, id));

        // Обычная правка запертой — отказ с выходом через исправление.
        var lines = await LinesAsync(client, id, Row(cable, 9m, "м"));
        Assert.Equal(HttpStatusCode.Conflict, lines.StatusCode);
        Assert.Contains("исправлением с причиной", await lines.Content.ReadAsStringAsync());

        // Неизменное исправление — не версия.
        var same = await CorrectAsync(client, id, SameAs(before, site));
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);
        Assert.Contains("ничего не меняет", await same.Content.ReadAsStringAsync());
    }

    /// <summary>Тело исправления, повторяющее накладную как есть.</summary>
    private static Dictionary<string, object?> SameAs(JsonElement view, Guid site)
    {
        var body = new Dictionary<string, object?>
        {
            ["number"] = view.GetProperty("number").GetString(),
            ["issuedOn"] = view.GetProperty("issuedOn").GetString(),
            ["warehouse"] = view.GetProperty("warehouse").GetString(),
            ["construction"] = site.ToString(),
            ["receivedBy"] = view.GetProperty("receivedBy").GetString(),
            ["reason"] = "Причина",
            ["lines"] = view.GetProperty("lines").EnumerateArray().Select(l => Row(
                l.GetProperty("nomenclatureId").GetGuid(), l.GetProperty("quantity").GetDecimal(),
                l.GetProperty("unit").GetString()!, id: l.GetProperty("id").GetGuid())).ToArray(),
        };
        return body;
    }

    [Fact]
    public async Task Исправление_открытой_накладной_не_нужно()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (site, _) = await SiteAsync("Накладная: исправление не нужно");
        var posted = (await PostedAsync(client, site, "2026-09-09", Row(cable, 1m, "м"))).GetProperty("id").GetGuid();
        var draft = await DraftAsync(client, site, "2026-09-09", Row(cable, 1m, "м"));

        foreach (var id in new[] { posted, draft })
        {
            var refused = await CorrectAsync(client, id, Correction(site, "2026-09-09", "Причина", Row(cable, 2m, "м")));
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("исправления не требует", await refused.Content.ReadAsStringAsync());
        }
    }

    /// <summary>
    /// Учётная дата не двигается, кроме смены стройки (D4): тогда накладная ложится в первый открытый день
    /// новой стройки не раньше даты отпуска (ТЗ COST-16).
    /// </summary>
    [Fact]
    public async Task Смена_стройки_исправлением_пересчитывает_учётную_дату()
    {
        var (client, _, id) = await LockedAsync("Накладная: стройка до");
        var (other, _) = await SiteAsync("Накладная: стройка после");
        await CloseAsync(Contour.Construction(other), new DateOnly(2026, 6, 15));

        var moved = await CorrectAsync(client, id, Correction(other, "2026-06-09", "Не та стройка", Row(cable, 10m, "м")));
        await OkAsync(moved);
        var view = await moved.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(other, view.GetProperty("constructionId").GetGuid());
        Assert.Equal("2026-06-16", view.GetProperty("accountingOn").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("locked").ValueKind);
        Assert.Equal(10m, Issued(await MaterialsAsync(client, other)));
    }

    /// <summary>
    /// После отмены закрытия накладная правится обычной правкой, версии и пометка остаются (В-3); удалить
    /// исправленную нельзя и черновиком — её история не удаляется (D8).
    /// </summary>
    [Fact]
    public async Task После_отмены_закрытия_история_остаётся_и_не_удаляется()
    {
        var (client, site, id) = await LockedAsync("Накладная: отмена закрытия");
        await OkAsync(await CorrectAsync(client, id, Correction(site, "2026-06-09", "Ошибка в количестве", Row(cable, 4m, "м"))));

        await ReopenAsync(Contour.Construction(site), June);
        await OkAsync(await client.PostAsync($"/api/costs/waybills/{id}/draft", null));

        Assert.Single(await VersionsAsync(client, id));
        Assert.Equal("Ошибка в количестве",
            Assert.Single(await ListAsync(client, $"?constructionId={site}")).GetProperty("correctedAfterClosing")
                .GetProperty("reason").GetString());

        var deleted = await client.DeleteAsync($"/api/costs/waybills/{id}");
        Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
        Assert.Contains("история исправлений", await deleted.Content.ReadAsStringAsync());
    }
}
