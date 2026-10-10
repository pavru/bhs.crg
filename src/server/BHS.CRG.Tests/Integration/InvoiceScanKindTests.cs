using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Вид файла счёта определяет сервер по содержимому (issue #1265, часть эпика #1264).
///
/// <para><b>Сторож задачи:</b> файл с заголовком одного вида и содержимым другого хранится и
/// отдаётся по содержимому, а неизвестное содержимое отдаётся как
/// <c>application/octet-stream</c>. Ломается он возвратом заголовка клиента в любое из трёх мест:
/// в запись счёта, в хранилище или в ответ отдачи.</para>
/// </summary>
public sealed class InvoiceScanKindTests(InvoiceScanHost host)
    : InvoiceLineTestBase(host), IClassFixture<InvoiceScanHost>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    [Fact]
    public async Task Картинка_назвавшаяся_PDF_хранится_и_отдаётся_картинкой()
    {
        var (client, _) = await SignInAsync("Supplier");
        var id = await CreateAsync(client);

        var view = await AttachAsync(client, id, Png, "Счёт.pdf", "application/pdf");

        Assert.Equal(FileKinds.Png, Stored(view));
        Assert.Equal(FileKinds.Png, await ServedAsync(client, id));
    }

    /// <summary>
    /// Заголовок, который браузер открыл бы страницей, наружу не уходит: вид неизвестен, и файл
    /// только скачивается. Приложить его при этом можно — отказа нет.
    /// </summary>
    [Theory]
    [InlineData("text/html")]
    [InlineData("application/pdf")]
    [InlineData(null)]
    public async Task Неизвестное_содержимое_прикладывается_и_отдаётся_только_на_скачивание(string? claimed)
    {
        var (client, _) = await SignInAsync("Supplier");
        var id = await CreateAsync(client);

        var view = await AttachAsync(client, id, "<html>не счёт</html>"u8.ToArray(), "Счёт.pdf", claimed);

        Assert.Equal(FileKinds.Unknown, Stored(view));
        Assert.Equal(FileKinds.Unknown, await ServedAsync(client, id));
        Assert.False((await client.GetFromJsonAsync<JsonElement>($"/api/costs/invoices/{id}/recognition"))
            .GetProperty("canStart").GetBoolean());
    }

    /// <summary>
    /// Офисный файл с машины без Office приходит без типа или как «просто байты» — вид даёт
    /// оглавление архива, а не расширение: архив, названный таблицей, таблицей не становится.
    /// Распознать его пока нечем (читаемый образ — следующие задачи эпика), но реестру видов уже
    /// есть на что опереться.
    /// </summary>
    [Theory]
    [InlineData("Счёт.bin", "xl/workbook.xml", FileKinds.Xlsx)]
    [InlineData("Счёт.pdf", "word/document.xml", FileKinds.Docx)]
    [InlineData("Счёт.xlsx", "readme.txt", FileKinds.Unknown)]
    public async Task Офисный_файл_получает_вид_по_оглавлению_а_не_по_расширению(string name, string entry, string expected)
    {
        var (client, _) = await SignInAsync("Supplier");
        var id = await CreateAsync(client);

        var view = await AttachAsync(client, id, FileKindsTests.Zip(entry), name, "application/octet-stream");

        Assert.Equal(expected, Stored(view));
        Assert.Equal(expected, await ServedAsync(client, id));
    }

    /// <summary>
    /// «Счёт из скана» судит о годности тоже по содержимому: PDF без типа принят, а файл, только
    /// назвавшийся PDF, — нет, и черновик от него не остаётся.
    /// </summary>
    [Fact]
    public async Task Счёт_из_скана_принимает_PDF_без_заголовка_и_отказывает_назвавшемуся_PDF()
    {
        var (client, _) = await SignInAsync("Supplier");

        using var real = Form(Encoding.UTF8.GetBytes(InvoiceFromScanTests.Scan()), "Счёт.pdf", null);
        var created = await client.PostAsync("/api/costs/invoices/from-scan", real);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var invoice = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("invoice");
        Assert.Equal(FileKinds.Pdf, Stored(invoice));

        var name = $"nazvalsya-{Guid.NewGuid():N}.pdf";
        using var fake = Form("просто текст"u8.ToArray(), name, "application/pdf");
        var refused = await client.PostAsync("/api/costs/invoices/from-scan", fake);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("PDF, PNG или JPEG", await refused.Content.ReadAsStringAsync());

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        Assert.False(await db.Invoices.AnyAsync(i => i.ScanFileName == name));
    }

    /// <summary>
    /// Файл, приложенный ДО этой задачи: в записи стоит заголовок клиента, какой бы он ни был.
    /// Отдаётся такой файл по своему содержимому — запись не читается: картинка остаётся картинкой и
    /// с чужим заголовком, и с тем, которым её называли старые браузеры (ревью PR #1275).
    /// </summary>
    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/x-png")]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    public async Task Раньше_приложенный_файл_отдаётся_по_содержимому_а_не_по_записи(string recorded)
    {
        var (client, _) = await SignInAsync("Supplier");
        var id = await CreateAsync(client);
        await AttachAsync(client, id, Png, "Старый.png", "image/png");
        await RecordAsync(id, recorded);

        using var response = await client.GetAsync($"/api/costs/invoices/{id}/scan");
        await OkAsync(response);

        Assert.Equal(FileKinds.Png, response.Content.Headers.ContentType?.MediaType);
        // Начало файла прочитано ради вида — и ушло в ответ, а не потерялось.
        Assert.Equal(Png, await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>
    /// И распознаётся такой файл по содержимому: картинка, записанная как PDF, уходит движку
    /// картинкой, а записанная как «image/jpg» не получает отказ «файл другого вида» на кнопке.
    /// </summary>
    [Fact]
    public async Task Раньше_приложенный_файл_распознаётся_по_содержимому()
    {
        var (client, _) = await SignInAsync("Supplier");
        var id = await CreateAsync(client);
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, .. Guid.NewGuid().ToByteArray()];
        await AttachAsync(client, id, jpeg, "Старый.jpg", "image/jpeg");
        await RecordAsync(id, "image/jpg");

        var scan = Encoding.UTF8.GetString(jpeg);
        host.Recognition.On(scan,
            () => Task.FromResult(InvoiceFromScanTests.Read(InvoiceFromScanTests.Header(number: "СЧ-1265"))));

        var recognition = await client.GetFromJsonAsync<JsonElement>($"/api/costs/invoices/{id}/recognition");
        Assert.True(recognition.GetProperty("canStart").GetBoolean(), recognition.GetProperty("whyNot").GetString());

        await OkAsync(await client.PostAsync($"/api/costs/invoices/{id}/recognition", null));
        Assert.Equal("done", (await InvoiceFromScanTests.OutcomeAsync(client, id)).GetProperty("state").GetString());
        Assert.Equal(FileKinds.Jpeg, host.Recognition.Kinds[scan]);
    }

    /// <summary>
    /// Реестр видов — экрану (issue #1266): из него собираются выбор файла и перечень «распознаются …».
    /// Тот, кто вправе работать со счетами, получает его без отдельного права; невошедший — нет.
    /// </summary>
    [Fact]
    public async Task Реестр_видов_отдаётся_вошедшему_и_называет_что_показывается_и_что_распознаётся()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/api/files/kinds")).StatusCode);

        var (client, _) = await SignInAsync("Supplier");
        var registry = await client.GetFromJsonAsync<JsonElement>("/api/files/kinds");

        Assert.Equal(FileKinds.Unknown, registry.GetProperty("unknown").GetString());
        Assert.Equal(FileKindCatalog.MaxBytes, registry.GetProperty("maxBytes").GetInt64());
        var kinds = registry.GetProperty("kinds").EnumerateArray().ToDictionary(k => k.GetProperty("mime").GetString()!);
        Assert.Equal(FileKindCatalog.All.Select(k => k.Mime).Order(), kinds.Keys.Order());

        Assert.Equal("pdf", kinds[FileKinds.Pdf].GetProperty("view").GetString());
        Assert.True(kinds[FileKinds.Pdf].GetProperty("recognized").GetBoolean());
        Assert.Equal("image", kinds[FileKinds.WebP].GetProperty("view").GetString());
        Assert.False(kinds[FileKinds.WebP].GetProperty("recognized").GetBoolean());
        Assert.Equal(JsonValueKind.Null, kinds[FileKinds.Xlsx].GetProperty("view").ValueKind);
        Assert.False(kinds[FileKinds.Xlsx].GetProperty("recognized").GetBoolean());
        Assert.Equal([".jpg", ".jpeg"],
            kinds[FileKinds.Jpeg].GetProperty("extensions").EnumerateArray().Select(e => e.GetString()));
    }

    private async Task RecordAsync(Guid id, string recorded)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        await db.Database.ExecuteSqlAsync($"UPDATE costs.invoices SET scan_mime_type = {recorded} WHERE id = {id}");
    }

    private static string? Stored(JsonElement view) =>
        view.GetProperty("requisites").GetProperty("Скан").GetProperty("mimeType").GetString();

    private static async Task<string?> ServedAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync($"/api/costs/invoices/{id}/scan");
        await OkAsync(response);
        return response.Content.Headers.ContentType?.MediaType;
    }

    private static async Task<JsonElement> AttachAsync(
        HttpClient client, Guid id, byte[] body, string fileName, string? claimed)
    {
        using var form = Form(body, fileName, claimed);
        var response = await client.PostAsync($"/api/costs/invoices/{id}/scan", form);
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static MultipartFormDataContent Form(byte[] body, string fileName, string? claimed)
    {
        var file = new ByteArrayContent(body);
        if (claimed is not null) file.Headers.ContentType = new MediaTypeHeaderValue(claimed);
        return new MultipartFormDataContent { { file, "file", fileName } };
    }
}
