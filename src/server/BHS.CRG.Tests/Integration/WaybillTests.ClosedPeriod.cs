using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Periods;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Contour = BHS.CRG.Domain.Periods.PeriodContour;

namespace BHS.CRG.Tests.Integration;

// Накладная и закрытый период (issue #1285). До этой задачи замка у накладной не было вовсе:
// проведённую накладную закрытого месяца возвращали в черновик, правили и проводили снова, и
// «Материалы на объекте» за закрытый месяц менялись молча.
public partial class WaybillTests : IDisposable
{
    /// <summary>
    /// Закрытия — состояние всего экземпляра: оставленное соседнему классу на этой базе, оно заперло
    /// бы ему месяц (и попало бы в его копию). Убираем ПОСЛЕ каждого теста, прямым SQL — как
    /// <see cref="PeriodClosureTests" />: служба стирать не умеет нарочно.
    /// </summary>
    public void Dispose()
    {
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<BHS.CRG.Infrastructure.Persistence.AppDbContext>().Database
            .ExecuteSqlRaw("TRUNCATE TABLE period_closures");
    }

    /// <summary>
    /// Пишущие адреса накладной — ИЗ МАРШРУТОВ, а не списком: адрес, добавленный завтра (удаление,
    /// #1283), попадёт под обе проверки ниже сам.
    /// </summary>
    private List<(string Method, string Pattern)> WritingRoutes()
    {
        var routes = new List<(string, string)>();
        foreach (var endpoint in host.Services.GetRequiredService<EndpointDataSource>().Endpoints)
        {
            if (endpoint is not RouteEndpoint route) continue;
            var pattern = "/" + route.RoutePattern.RawText?.Trim('/');
            if (!pattern.StartsWith("/api/costs/waybills/{id:guid}")) continue;

            foreach (var method in route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                if (method != "GET") routes.Add((method, pattern));
        }

        Assert.True(routes.Count >= 5, $"Перепись нашла {routes.Count} пишущих адресов накладной — меньше, чем " +
            "их есть: она ослепла, и проверки по ней ничего не доказывают.");
        return routes;
    }

    /// <summary>Тело, с которым адрес доходит до связки записи, а не отказывает раньше — о другом.</summary>
    private object BodyOf(string pattern, Guid site) =>
        pattern.EndsWith("/nomenclature") ? new { nomenclature = Reference(conduit) }
        : pattern.EndsWith("/lines") ? new { lines = new[] { Row(cable, 99m, "м") } }
        : pattern.EndsWith("{id:guid}") ? Header(site, "2026-06-02")
        : new { };

    private async Task CloseAsync(Contour contour, DateOnly through)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPeriodClosures>()
            .CloseAsync(new ClosePeriod(contour, through.AddDays(-29), through, null, null));
    }

    private async Task ReopenAsync(Contour contour, DateOnly seen)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPeriodClosures>()
            .ReopenAsync(new ReopenPeriod(contour, seen, "для теста"));
    }

    private static decimal Issued(JsonElement materials) =>
        materials.GetProperty("items").EnumerateArray().Sum(i => i.GetProperty("quantity").GetDecimal());

    /// <summary>
    /// Воспроизведение из issue #1285, теперь сторожем: проведённая накладная закрытого месяца не
    /// возвращается в черновик, не правится и не сопоставляется — ни одним пишущим адресом. Отказ
    /// называет период и выход; «Материалы на объекте» те же до последней цифры.
    /// </summary>
    [Fact]
    public async Task Закрытый_период_запирает_проведённую_накладную_по_всем_пишущим_адресам()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (site, _) = await SiteAsync("Накладная: закрытый период");
        var posted = await PostedAsync(client, site, "2026-06-09", Row(cable, 10m, "м"), Row(null, 2m, "шт", "Скоба"));
        var id = posted.GetProperty("id").GetGuid();
        var line = posted.GetProperty("lines")[1].GetProperty("id").GetGuid();
        Assert.Equal("2026-06-09", posted.GetProperty("accountingOn").GetString());
        Assert.Equal(JsonValueKind.Null, posted.GetProperty("locked").ValueKind);

        await CloseAsync(Contour.Construction(site), new DateOnly(2026, 6, 30));

        var before = await WaybillAsync(client, id);
        Assert.Contains("период закрыт по 30.06.2026 у стройки «Накладная: закрытый период",
            before.GetProperty("locked").GetString());

        var open = new List<string>();
        foreach (var (method, pattern) in WritingRoutes())
        {
            var path = pattern.Replace("{id:guid}", id.ToString()).Replace("{lineId:guid}", line.ToString());
            using var request = new HttpRequestMessage(new HttpMethod(method), path)
            {
                Content = JsonContent.Create(BodyOf(pattern, site)),
            };
            var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != HttpStatusCode.Conflict || !text.Contains("заперта")
                || !text.Contains("сначала отмените закрытие периода"))
                open.Add($"{method} {pattern} → {(int)response.StatusCode} {text}");
        }

        Assert.True(open.Count == 0,
            "Пишущий адрес накладной обязан отказать накладной, запертой закрытым периодом (409, «заперта», " +
            "с выходом). Адрес сохраняет мимо WaybillDesk.WriteAsync — либо отказал телу раньше, чем дошёл до " +
            "связки: тогда назовите ему тело в BodyOf.\n" + string.Join("\n", open));

        // Ничего не изменилось: накладная та же вместе с версией, перечень отпущенного — тот же.
        Assert.Equal(before.GetRawText(), (await WaybillAsync(client, id)).GetRawText());
        Assert.Equal(10m, Issued(await MaterialsAsync(client, site)));

        // Отмена закрытия отпирает: исправить накладную закрытого месяца можно, но только так.
        await ReopenAsync(Contour.Construction(site), new DateOnly(2026, 6, 30));
        Assert.Equal(JsonValueKind.Null, (await WaybillAsync(client, id)).GetProperty("locked").ValueKind);
        await OkAsync(await client.PostAsync($"/api/costs/waybills/{id}/draft", null));
    }

    /// <summary>
    /// Проведённая накладная без учётной даты — так выглядит накладная, восстановленная из резервной
    /// копии, снятой до появления колонки. Её закрытый период запирает по дате отпуска: без запасного
    /// правила такую накладную не запирало бы ничто, и прежняя дыра вернулась бы через восстановление.
    /// </summary>
    [Fact]
    public async Task Накладная_без_учётной_даты_запирается_по_дате_отпуска()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (site, _) = await SiteAsync("Накладная: из старой копии");
        var id = (await PostedAsync(client, site, "2026-06-09", Row(cable, 10m, "м"))).GetProperty("id").GetGuid();

        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<BHS.CRG.Modules.Costs.Data.CostsDbContext>().Database
                .ExecuteSqlRawAsync("UPDATE costs.waybills SET accounting_on = NULL WHERE id = {0}", id);

        await CloseAsync(Contour.Construction(site), new DateOnly(2026, 6, 30));

        Assert.Contains("период закрыт по 30.06.2026", (await WaybillAsync(client, id)).GetProperty("locked").GetString());
        var refused = await client.PostAsync($"/api/costs/waybills/{id}/draft", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    /// <summary>
    /// Закрытие компании запирает накладную любой стройки — и называется оно раньше закрытия стройки:
    /// «у стройки А» про день, закрытый для всех, отправило бы отменять не то закрытие.
    /// </summary>
    [Fact]
    public async Task Закрытие_компании_запирает_накладную_и_названо_компанией()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (site, _) = await SiteAsync("Накладная: период компании");
        // Давний месяц: граница компании одна на всех, и свежая заперла бы счета соседних тестов.
        var id = (await PostedAsync(client, site, "2021-03-09", Row(cable, 1m, "м"))).GetProperty("id").GetGuid();
        var through = new DateOnly(2021, 3, 31);

        await CloseAsync(Contour.Company, through);
        try
        {
            Assert.Equal("период компании закрыт по 31.03.2021",
                (await WaybillAsync(client, id)).GetProperty("locked").GetString());

            var refused = await client.PostAsync($"/api/costs/waybills/{id}/draft", null);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("период компании закрыт", await refused.Content.ReadAsStringAsync());
        }
        finally
        {
            await ReopenAsync(Contour.Company, through);
        }
    }

    /// <summary>
    /// Черновик не принадлежит ни одному периоду. Накладная, введённая ПОСЛЕ закрытия датой закрытого
    /// месяца, заводится, правится и проводится — и ложится в первый открытый день (ТЗ COST-16). Поэтому
    /// закрытый период её не запирает: ошибку в ней исправляют обычным возвратом в черновик.
    /// </summary>
    [Fact]
    public async Task Накладная_введённая_после_закрытия_ложится_в_открытый_период_и_не_заперта()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (site, _) = await SiteAsync("Накладная: после закрытия");
        await CloseAsync(Contour.Construction(site), new DateOnly(2026, 6, 30));

        var id = await DraftAsync(client, site, "2026-06-15", Row(cable, 3m, "м"));
        await OkAsync(await LinesAsync(client, id, Row(cable, 4m, "м")));

        var response = await client.PostAsync($"/api/costs/waybills/{id}/posted", null);
        await OkAsync(response);
        var posted = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("2026-06-15", posted.GetProperty("issuedOn").GetString());
        Assert.Equal("2026-07-01", posted.GetProperty("accountingOn").GetString());
        Assert.Equal(JsonValueKind.Null, posted.GetProperty("locked").ValueKind);
        Assert.Equal(4m, Issued(await MaterialsAsync(client, site)));

        // Возврат в черновик снимает учётную дату: черновик снова ничей.
        var draft = await client.PostAsync($"/api/costs/waybills/{id}/draft", null);
        await OkAsync(draft);
        Assert.Equal(JsonValueKind.Null,
            (await draft.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accountingOn").ValueKind);
    }

    /// <summary>
    /// Каждый пишущий адрес накладной называет версию, которую видел человек: устаревшей — 409, без
    /// версии — 400. До issue #1285 версию называли два адреса из пяти, и «Провести» по форме, открытой
    /// час назад, проводило строки, которых человек не видел.
    /// </summary>
    [Fact]
    public async Task Каждый_пишущий_адрес_накладной_отказывает_устаревшей_и_неназванной_версии()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (site, _) = await SiteAsync("Накладная: версия у всех");
        var id = await DraftAsync(client, site, "2026-09-13", Row(null, 1m, "шт", "Скоба"));
        var view = await WaybillAsync(client, id);
        var line = view.GetProperty("lines")[0].GetProperty("id").GetGuid();
        var stale = (ulong.Parse(view.GetProperty("version").GetString()!) - 1).ToString();

        var loose = new List<string>();
        foreach (var (method, pattern) in WritingRoutes())
        {
            var path = pattern.Replace("{id:guid}", id.ToString()).Replace("{lineId:guid}", line.ToString());

            var outdated = await SeenAsync(client, new HttpMethod(method), path, stale, BodyOf(pattern, site));
            if (outdated.StatusCode != HttpStatusCode.Conflict
                || !(await outdated.Content.ReadAsStringAsync()).Contains("тем временем изменили"))
                loose.Add($"{method} {pattern}: устаревшая версия → {(int)outdated.StatusCode}");

            var unnamed = await SeenAsync(client, new HttpMethod(method), path, SeenInvoiceVersion.Omit, BodyOf(pattern, site));
            if (unnamed.StatusCode != HttpStatusCode.BadRequest
                || !(await unnamed.Content.ReadAsStringAsync()).Contains("If-Match"))
                loose.Add($"{method} {pattern}: без версии → {(int)unnamed.StatusCode}");
        }

        Assert.True(loose.Count == 0,
            "Пишущий адрес накладной обязан отказать правке по устаревшему виду (409) и правке без версии " +
            "(400):\n" + string.Join("\n", loose));
        Assert.Equal(view.GetRawText(), (await WaybillAsync(client, id)).GetRawText());
    }
}
