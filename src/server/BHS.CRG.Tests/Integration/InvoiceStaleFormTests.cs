using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Costs.Endpoints;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Правка счёта, собранная по устаревшему виду, отказывает и ничего не записывает (issue #1176).
///
/// <para>До правки сервер сверял версию, прочитанную тем же запросом: окно конфликта было временем
/// одного запроса, а не временем, пока счёт открыт на экране. Второй из двух людей, открывших счёт,
/// затирал правку первого без отказа — строки, шапку, разноску; «разобран» нажимали по тому, что
/// видели, а записывали по тому, что лежит.</para>
///
/// <para>Проверяется <b>переписью адресов</b>, а не списком: новый пишущий адрес счёта попадает сюда
/// сам, и адрес, сохранивший мимо связки записи, роняет тест своим именем.</para>
/// </summary>
[Collection("Integration")]
public class InvoiceStaleFormTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    /// <summary>
    /// Адреса счёта не для чтения, которые ничего не записывают, — с причиной. Остальные обязаны
    /// требовать версию.
    /// </summary>
    private static readonly Dictionary<string, string> WritesNothing = new()
    {
        ["POST /api/costs/invoices/{id:guid}/paid/preview"] = "предпросмотр оплаты: считает расклад и не сохраняет",
        ["POST /api/costs/invoices/{id:guid}/allocation/preview"] = "предпросмотр матрицы: считает раскладку и не сохраняет",
        ["POST /api/costs/invoices/{id:guid}/recognition"] =
            "постановка распознавания: в счёт не пишет — только запись о распознавании, версия счёта прежняя. " +
            "Прочитанное потом ложится слиянием в пустые поля, и устаревшая форма при этом ничего не теряет (issue #1077)",
        ["POST /api/costs/invoices/{id:guid}/recognition/parties/{side}/organization"] =
            "заведение организации из скана: пишет в справочник ядра, а не в счёт — версия счёта прежняя. " +
            "В поле счёта организацию ставит человек обычной правкой шапки, с версией (issue #1077)",
    };

    /// <summary>
    /// Тело, с которым адрес доходит до связки записи: шесть адресов отказывают пустому телу раньше
    /// («набор строк не прислан»), и это отказ о другом. У адреса без записи здесь тело — пустой объект.
    /// </summary>
    private static readonly Dictionary<string, object> Bodies = new()
    {
        ["POST /api/costs/invoices/{id:guid}/confirmed"] = new { fields = new[] { "Номер" } },
        ["PUT /api/costs/invoices/{id:guid}/lines"] = new { lines = Array.Empty<object>() },
        ["PUT /api/costs/invoices/{id:guid}/lines/{lineId:guid}/allocation"] = new { parts = Array.Empty<object>() },
        ["PUT /api/costs/invoices/{id:guid}/allocation"] =
            new { lines = Array.Empty<object>(), document = Array.Empty<object>(), stamp = "любая" },
        ["POST /api/costs/invoices/{id:guid}/paid"] = new { paidOn = "2026-09-29", seen = "любая" },
        ["POST /api/costs/invoices/{id:guid}/unpaid"] = new { reason = "проверка версии" },
    };

    [Fact]
    public async Task Каждый_пишущий_адрес_счёта_отказывает_устаревшей_и_неназванной_версии()
    {
        var (admin, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(admin, complete: true);
        var view = await LinesAsync(admin, invoice, [Line(null, 2, 10, text: "Кабель")]);
        var line = LineId(view, 1);
        var before = await RawAsync(admin, invoice);
        var stale = (ulong.Parse(view.GetProperty("version").GetString()!) - 1).ToString();

        var routes = WritingRoutes();
        Assert.True(routes.Count >= 9, $"Перепись нашла {routes.Count} пишущих адресов счёта — меньше, чем их есть: " +
            "она ослепла, и проверка ниже ничего не доказывает.");

        var loose = new List<string>();
        foreach (var (method, pattern) in routes)
        {
            var path = pattern.Replace("{id:guid}", invoice.ToString()).Replace("{lineId:guid}", line.ToString());

            var body = Bodies.GetValueOrDefault($"{method} {pattern}", new { });

            var outdated = await SendAsync(admin, method, path, stale, body);
            if (outdated.StatusCode != HttpStatusCode.Conflict
                || !(await outdated.Content.ReadAsStringAsync()).Contains("тем временем изменили"))
                loose.Add($"{method} {pattern}: устаревшая версия → {(int)outdated.StatusCode} " +
                    $"{await outdated.Content.ReadAsStringAsync()}");

            var unnamed = await SendAsync(admin, method, path, SeenInvoiceVersion.Omit, body);
            if (unnamed.StatusCode != HttpStatusCode.BadRequest
                || !(await unnamed.Content.ReadAsStringAsync()).Contains(InvoiceDesk.SeenHeader))
                loose.Add($"{method} {pattern}: без версии → {(int)unnamed.StatusCode} " +
                    $"{await unnamed.Content.ReadAsStringAsync()}");
        }

        Assert.True(loose.Count == 0,
            "Пишущий адрес счёта обязан отказать правке, собранной по устаревшему виду (409), и правке без " +
            "версии (400). Адрес сохраняет мимо InvoiceDesk.WriteAsync — либо отказал телу раньше, чем дошёл до " +
            "связки: тогда назовите ему тело в Bodies.\n" +
            string.Join("\n", loose));

        // Ни один отказ ничего не записал: счёт тот же до последнего знака, вместе с версией.
        Assert.Equal(before, await RawAsync(admin, invoice));
    }

    [Fact]
    public async Task Второй_из_двух_открывших_счёт_получает_отказ_а_перечитав_сохраняет()
    {
        var (first, _) = await SignInAsync("Admin");
        var (second, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(first);

        // Оба открыли счёт и видят одну версию.
        var seen = (await ReadAsync(second, invoice)).GetProperty("version").GetString()!;

        // Первый сохранил строки.
        await LinesAsync(first, invoice, [Line(null, 2, 10, text: "Кабель")]);

        // Второй через час сохраняет свои — по тому, что видел. Раньше набор первого заменялся целиком.
        var late = await SendLinesAsync(second, invoice, seen, [Line(null, 1, 5, text: "Труба")]);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Contains("Перечитайте счёт", await late.Content.ReadAsStringAsync());

        var kept = await ReadAsync(second, invoice);
        Assert.Equal("Кабель", kept.GetProperty("lines")[0].GetProperty("supplierText").GetString());

        // Перечитал — и сохранение проходит: отказ закрывает устаревшее, а не правку вообще.
        var retried = await SendLinesAsync(second, invoice, kept.GetProperty("version").GetString()!,
            [Line(null, 2, 10, text: "Кабель"), Line(null, 1, 5, text: "Труба")]);
        await OkAsync(retried);
        Assert.Equal(2, (await ReadAsync(first, invoice)).GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public async Task Правка_шапки_делает_устаревшей_форму_строк_и_наоборот()
    {
        var (admin, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(admin);
        var opened = (await ReadAsync(admin, invoice)).GetProperty("version").GetString()!;

        // Версия одна на счёт (решение владельца, issue #1176): правка шапки отказывает сохранению
        // строк, собранному до неё, — хотя сами правки друг другу не мешают.
        var requisites = await RequisitesWithAsync(admin, invoice, "Номер", "СЧ-ПОСЛЕ");
        var saved = await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}", new { requisites });
        await OkAsync(saved);

        var lines = await SendLinesAsync(admin, invoice, opened, [Line(null, 2, 10, text: "Кабель")]);
        Assert.Equal(HttpStatusCode.Conflict, lines.StatusCode);

        // Ответ правки несёт новую версию: форма, сохранившая шапку, сохраняет строки без перечитывания.
        var fresh = (await ReadAsync(admin, invoice)).GetProperty("version").GetString()!;
        Assert.NotEqual(opened, fresh);
        Assert.Equal(fresh, (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString());
        await OkAsync(await SendLinesAsync(admin, invoice, fresh, [Line(null, 2, 10, text: "Кабель")]));
    }

    /// <summary>
    /// Скан уходит в хранилище ДО связки записи (выгрузка под замком держала бы закрытие периода), и
    /// версию связка проверила бы уже после. Устаревшая форма заливала бы файл целиком ради отказа —
    /// поэтому у скана проверка повторена до выгрузки (ревью PR #1208).
    /// </summary>
    [Fact]
    public async Task Скан_по_устаревшей_и_неназванной_версии_в_хранилище_не_уходит()
    {
        var (admin, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(admin);
        var opened = (await ReadAsync(admin, invoice)).GetProperty("version").GetString()!;
        await LinesAsync(admin, invoice, [Line(null, 2, 10, text: "Кабель")]);

        var blobs = host.Services.GetRequiredService<FakeBlobStorage>();
        var before = blobs.Uploads;

        var stale = await SendAsync(admin, "POST", $"/api/costs/invoices/{invoice}/scan", opened, new { });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var unnamed = await SendAsync(admin, "POST", $"/api/costs/invoices/{invoice}/scan", SeenInvoiceVersion.Omit, new { });
        Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);
        Assert.Equal(before, blobs.Uploads);

        // Со свежей версией скан прикладывается — ранняя проверка закрывает устаревшее, а не скан вообще.
        var fresh = (await ReadAsync(admin, invoice)).GetProperty("version").GetString()!;
        await OkAsync(await SendAsync(admin, "POST", $"/api/costs/invoices/{invoice}/scan", fresh, new { }));
        Assert.Equal(before + 1, blobs.Uploads);
    }

    private List<(string Method, string Pattern)> WritingRoutes()
    {
        var routes = new List<(string, string)>();
        foreach (var endpoint in host.Services.GetRequiredService<EndpointDataSource>().Endpoints)
        {
            if (endpoint is not RouteEndpoint route) continue;
            var pattern = "/" + route.RoutePattern.RawText?.Trim('/');
            if (!pattern.StartsWith("/api/costs/invoices/{id:guid}")) continue;

            foreach (var method in route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
            {
                if (method == "GET" || WritesNothing.ContainsKey($"{method} {pattern}")) continue;
                routes.Add((method, pattern));
            }
        }
        return routes;
    }

    /// <summary>
    /// Запрос с названной версией; у скана тело — файл.
    /// </summary>
    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, string method, string path, string seen, object body)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.TryAddWithoutValidation(SeenInvoiceVersion.Header, seen);

        if (path.EndsWith("/scan"))
        {
            var file = new ByteArrayContent("%PDF-1.4"u8.ToArray());
            file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            request.Content = new MultipartFormDataContent { { file, "file", "scan.pdf" } };
        }
        else
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendLinesAsync(
        HttpClient client, Guid invoice, string seen, object[] lines)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/costs/invoices/{invoice}/lines");
        request.Headers.TryAddWithoutValidation(SeenInvoiceVersion.Header, seen);
        request.Content = JsonContent.Create(new { lines });
        return await client.SendAsync(request);
    }

    private static async Task<string> RawAsync(HttpClient client, Guid invoice) =>
        await client.GetStringAsync($"/api/costs/invoices/{invoice}");
}
