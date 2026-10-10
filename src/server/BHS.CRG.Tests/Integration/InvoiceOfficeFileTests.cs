using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Files;
using BHS.CRG.Tests.Renditions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Renditions.RenditionFixtures;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Счёт в Excel и Word (issue #1270, последний шаг эпика #1264): файл прикладывается, рядом с
/// формой стоит его читаемый образ, и распознавание читает образ.
///
/// <para><b>Сторож задачи:</b> движок распознавания получает ОБРАЗ, а не оригинал. Ломается он
/// одним присваиванием — отдать движку приложенный файл, как раньше: офисный файл движок либо
/// отвергнет, либо, что хуже, «прочитает» архив как текст.</para>
///
/// <para>Конвертер здесь подставной: что делает настоящий, проверяет
/// <c>deploy/converter.tests.sh</c> и живой прогон в браузере.</para>
/// </summary>
public sealed class InvoiceOfficeFileTests(InvoiceScanHost host)
    : InvoiceLineTestBase(host), IClassFixture<InvoiceScanHost>, IDisposable
{
    public void Dispose()
    {
        host.Converter = (request, ct) => MustNotBeCalled(request, ct);
        host.RenditionsFailure = null;
    }

    /// <summary>Книга и образ, который из неё «строит» подставной конвертер. Слова — свои у теста.</summary>
    private (byte[] Book, byte[] Image, string Seen) Office()
    {
        string[] words = ["invoice", "supplier", $"n{Guid.NewGuid():N}"];
        var image = Pdf(string.Join(" ", words));
        host.Converter = Returns(image).Invoke;
        return (Workbook(words), image, Encoding.UTF8.GetString(image));
    }

    [Fact]
    public async Task Распознавание_офисного_файла_отдаёт_движку_образ_а_не_оригинал()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (book, image, seen) = Office();
        host.Recognition.On(seen,
            () => Task.FromResult(InvoiceFromScanTests.Read(InvoiceFromScanTests.Header(number: "СЧ-1270"))));
        var id = await CreateAsync(client);

        var attached = await AttachAsync(client, id, book, "Счёт.xlsx");
        Assert.Equal(FileKinds.Xlsx,
            attached.GetProperty("requisites").GetProperty("Скан").GetProperty("mimeType").GetString());

        // Образ построен уже загрузкой — и версию счёта построение не двинуло: форма рядом не
        // получит 409 от того, что сервер привёл файл к читаемому виду.
        var view = await ImageAsync(client, id);
        Assert.Equal("built", view.GetProperty("state").GetString());
        Assert.Equal(1, view.GetProperty("pages").GetInt32());
        Assert.Equal("gotenberg 8.37.0", view.GetProperty("converter").GetString());
        Assert.Equal(attached.GetProperty("version").ToString(), (await ReadAsync(client, id)).GetProperty("version").ToString());

        // Рядом с формой — образ, PDF; а скачивается оригинал, байт в байт.
        using var shown = await client.GetAsync($"/api/costs/invoices/{id}/scan/image/content");
        await OkAsync(shown);
        Assert.Equal(FileKinds.Pdf, shown.Content.Headers.ContentType?.MediaType);
        Assert.Equal(image, await shown.Content.ReadAsByteArrayAsync());
        Assert.Equal(book, await client.GetByteArrayAsync($"/api/costs/invoices/{id}/scan"));

        await OkAsync(await client.PostAsync($"/api/costs/invoices/{id}/recognition", null));
        var recognition = await InvoiceFromScanTests.OutcomeAsync(client, id);

        Assert.Equal("done", recognition.GetProperty("state").GetString());
        // Движку ушёл образ: ответ сценария назначен его содержимому, и вид у него — PDF.
        Assert.Equal(FileKinds.Pdf, host.Recognition.Kinds[seen]);
        Assert.DoesNotContain(Encoding.UTF8.GetString(book), host.Recognition.Kinds.Keys);
        Assert.Equal("СЧ-1270", recognition.GetProperty("values").GetProperty(CostsRecognitionProfiles.Number).GetString());
        Assert.False(recognition.GetProperty("byFormerImage").GetBoolean());
    }

    /// <summary>
    /// Образ перестроили — распознанное не прячется, а помечается: бумага та же, вид другой.
    /// И «Перестроить» в счёт не пишет: версия прежняя, заголовок <c>If-Match</c> адресу не нужен.
    /// </summary>
    [Fact]
    public async Task После_перестроения_распознанное_остаётся_и_помечено_прежним_видом()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (book, _, seen) = Office();
        host.Recognition.On(seen,
            () => Task.FromResult(InvoiceFromScanTests.Read(InvoiceFromScanTests.Header(number: "СЧ-1271"))));
        var id = await CreateAsync(client);
        await AttachAsync(client, id, book, "Счёт.xlsx");
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{id}/recognition", null));
        var outcome = await InvoiceFromScanTests.OutcomeAsync(client, id);
        Assert.True(outcome.GetProperty("state").GetString() == "done", outcome.ToString());
        var before = (await ReadAsync(client, id)).GetProperty("version").ToString();

        using var bare = new HttpRequestMessage(HttpMethod.Post, $"/api/costs/invoices/{id}/scan/image");
        var rebuilt = await client.SendAsync(bare);
        await OkAsync(rebuilt);
        Assert.Equal("built", (await rebuilt.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());

        var recognition = await InvoiceFromScanTests.RecognitionAsync(client, id);
        Assert.Equal("done", recognition.GetProperty("state").GetString());
        Assert.Equal("СЧ-1271", recognition.GetProperty("values").GetProperty(CostsRecognitionProfiles.Number).GetString());
        Assert.True(recognition.GetProperty("byFormerImage").GetBoolean());
        Assert.Equal(before, (await ReadAsync(client, id)).GetProperty("version").ToString());
    }

    /// <summary>
    /// Защищённый паролем файл ПРИЛОЖЕН: отказ построения — не отказ загрузки. Он скачивается,
    /// а распознать его нельзя — и причину называет служба своими словами.
    /// </summary>
    [Fact]
    public async Task Защищённый_файл_приложен_и_скачивается_а_распознать_его_нельзя_с_причиной()
    {
        var (client, _) = await SignInAsync("Supplier");
        var locked = RepoFile("deploy", "converter-protected.xlsx");
        var id = await CreateAsync(client);

        await AttachAsync(client, id, locked, "Закрытый.xlsx");

        Assert.Equal(locked, await client.GetByteArrayAsync($"/api/costs/invoices/{id}/scan"));
        var view = await ImageAsync(client, id);
        Assert.Equal("refused", view.GetProperty("state").GetString());
        Assert.Contains("парол", view.GetProperty("reason").GetString());
        Assert.True(view.GetProperty("aboutFile").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/costs/invoices/{id}/scan/image/content")).StatusCode);

        var recognition = await InvoiceFromScanTests.RecognitionAsync(client, id);
        Assert.False(recognition.GetProperty("canStart").GetBoolean());
        Assert.Contains("парол", recognition.GetProperty("whyNot").GetString());

        var refused = await client.PostAsync($"/api/costs/invoices/{id}/recognition", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("парол", await refused.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// «Счёт из файла» принимает офисный файл тем же путём: черновик заведён сразу, образ строит
    /// само распознавание. А файл, который в образ не превращается, черновик всё равно заводит —
    /// причина становится исходом распознавания.
    /// </summary>
    [Fact]
    public async Task Счёт_из_файла_принимает_офисный_файл_а_отказ_образа_становится_исходом()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (book, _, seen) = Office();
        host.Recognition.On(seen,
            () => Task.FromResult(InvoiceFromScanTests.Read(InvoiceFromScanTests.Header(number: "СЧ-1272"))));

        var created = await FromFileAsync(client, book, "Счёт.xlsx");
        var id = created.GetProperty("invoice").GetProperty("id").GetGuid();
        var done = await InvoiceFromScanTests.OutcomeAsync(client, id);
        Assert.True(done.GetProperty("state").GetString() == "done", done.ToString());
        Assert.Equal("СЧ-1272", (await ReadAsync(client, id)).GetProperty("requisites").GetProperty("Номер").GetString());
        Assert.Equal("built", (await ImageAsync(client, id)).GetProperty("state").GetString());

        // Старый Excel под паролем остаётся Excel по виду — черновик заводится, а пароль называет исход.
        var locked = await FromFileAsync(client, RepoFile("deploy", "converter-protected.xls"), "Закрытый.xls");
        var lockedId = locked.GetProperty("invoice").GetProperty("id").GetGuid();
        var outcome = await InvoiceFromScanTests.OutcomeAsync(client, lockedId);
        Assert.Equal("failed", outcome.GetProperty("state").GetString());
        // Причина — своя: «образа нет», а не «прочитанное не записалось» — читать не начинали.
        Assert.Equal("NoImage", outcome.GetProperty("reason").GetString());
        Assert.Contains("парол", outcome.GetProperty("error").GetString());
    }

    /// <summary>
    /// Конвертер не ответил — файл всё равно приложен, а отказ не запомнен: когда сервис вернётся,
    /// панель построит образ сама, без «Перестроить» у каждого файла.
    /// </summary>
    [Fact]
    public async Task Молчащий_конвертер_не_мешает_приложить_файл_и_не_запоминается()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (book, _, _) = Office();
        var working = host.Converter;
        host.Converter = (_, _) => throw new HttpRequestException("связи с конвертером нет");
        var id = await CreateAsync(client);

        await AttachAsync(client, id, book, "Счёт.xlsx");

        var down = await ImageAsync(client, id);
        Assert.Equal("refused", down.GetProperty("state").GetString());
        Assert.False(down.GetProperty("aboutFile").GetBoolean());
        // Распознать при этом можно попробовать: отказ не о файле, и запоминать его нечем.
        Assert.True((await InvoiceFromScanTests.RecognitionAsync(client, id)).GetProperty("canStart").GetBoolean());

        host.Converter = working;
        Assert.Equal("built", (await ImageAsync(client, id)).GetProperty("state").GetString());
    }

    /// <summary>
    /// Конвертер не открыл файл — отказ запомнен, и сам собой он не пройдёт. Выход обязан быть:
    /// «Построить заново» строит и по отказу, а после него файл снова можно распознать. Без этого
    /// разовый сбой конвертера запирал бы файл навсегда — в закрытом периоде его и заменить нельзя.
    /// </summary>
    [Fact]
    public async Task Запомненный_отказ_снимается_построением_заново()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (book, _, _) = Office();
        var working = host.Converter;
        host.Converter = Answers(HttpStatusCode.InternalServerError).Invoke;
        var id = await CreateAsync(client);
        await AttachAsync(client, id, book, "Счёт.xlsx");

        var refused = await ImageAsync(client, id);
        Assert.Equal("refused", refused.GetProperty("state").GetString());
        Assert.True(refused.GetProperty("aboutFile").GetBoolean());
        Assert.False((await InvoiceFromScanTests.RecognitionAsync(client, id)).GetProperty("canStart").GetBoolean());

        host.Converter = working;
        // Просто спросить ещё раз — мало: отказ запомнен, конвертер не зовут.
        Assert.Equal("refused", (await ImageAsync(client, id)).GetProperty("state").GetString());

        var rebuilt = await client.PostAsync($"/api/costs/invoices/{id}/scan/image", null);
        await OkAsync(rebuilt);
        Assert.Equal("built", (await rebuilt.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.True((await InvoiceFromScanTests.RecognitionAsync(client, id)).GetProperty("canStart").GetBoolean());
    }

    /// <summary>
    /// Образа сейчас нет вовсе (после восстановления копии его ещё не построили) — это не «вид
    /// построен заново»: сравнивать не с чем, и пометки нет.
    /// </summary>
    [Fact]
    public async Task Пока_образа_нет_распознанное_прежним_видом_не_помечено()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (book, _, seen) = Office();
        host.Recognition.On(seen,
            () => Task.FromResult(InvoiceFromScanTests.Read(InvoiceFromScanTests.Header(number: "СЧ-1273"))));
        var id = await CreateAsync(client);
        await AttachAsync(client, id, book, "Счёт.xlsx");
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{id}/recognition", null));
        var outcome = await InvoiceFromScanTests.OutcomeAsync(client, id);
        Assert.True(outcome.GetProperty("state").GetString() == "done", outcome.ToString());

        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<BHS.CRG.Infrastructure.Persistence.AppDbContext>()
                .Database.ExecuteSqlRawAsync("DELETE FROM renditions");

        Assert.False((await InvoiceFromScanTests.RecognitionAsync(client, id)).GetProperty("byFormerImage").GetBoolean());
        // Панель построила вид заново — теперь он другой, и пометка появляется.
        Assert.Equal("built", (await ImageAsync(client, id)).GetProperty("state").GetString());
        Assert.True((await InvoiceFromScanTests.RecognitionAsync(client, id)).GetProperty("byFormerImage").GetBoolean());
    }

    /// <summary>
    /// Упала сама служба образов — база, хранилище. Файл к этой минуте уже записан в счёт, и ответом
    /// обязано быть «приложено»: получив ошибку, человек приложил бы его второй раз.
    /// </summary>
    [Fact]
    public async Task Сбой_службы_образов_не_отнимает_ответ_приложено()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (book, _, _) = Office();
        var id = await CreateAsync(client);
        host.RenditionsFailure = new InvalidOperationException("база образов не ответила");

        await AttachAsync(client, id, book, "Счёт.xlsx");

        Assert.Equal(book, await client.GetByteArrayAsync($"/api/costs/invoices/{id}/scan"));
        host.RenditionsFailure = null;
        // Образ построит первое же открытие панели.
        Assert.Equal("built", (await ImageAsync(client, id)).GetProperty("state").GetString());
    }

    /// <summary>PDF и изображения образа не имеют: рядом с формой стоит сам файл, и перестраивать нечего.</summary>
    [Fact]
    public async Task У_PDF_образа_нет_и_перестроить_его_нельзя()
    {
        var (client, _) = await SignInAsync("Supplier");
        var id = await CreateAsync(client);
        await AttachAsync(client, id, Encoding.UTF8.GetBytes(InvoiceFromScanTests.Scan()), "Счёт.pdf");

        Assert.Equal("original", (await ImageAsync(client, id)).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/costs/invoices/{id}/scan/image/content")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsync($"/api/costs/invoices/{id}/scan/image", null)).StatusCode);

        // И у файла вида, которого система не знает: образ ему не положен, и это не «образ не
        // построен» — про такой файл панель говорит «показать нечем», а не называет причину поломкой.
        var other = await CreateAsync(client);
        await AttachAsync(client, other, "просто текст, не счёт"u8.ToArray(), "Заметки.txt");
        Assert.Equal("original", (await ImageAsync(client, other)).GetProperty("state").GetString());
        var refused = await client.PostAsync($"/api/costs/invoices/{other}/scan/image", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("перестраивать нечего", await refused.Content.ReadAsStringAsync());
    }

    private static Task<JsonElement> ImageAsync(HttpClient client, Guid id) =>
        client.GetFromJsonAsync<JsonElement>($"/api/costs/invoices/{id}/scan/image");

    private static async Task<JsonElement> AttachAsync(HttpClient client, Guid id, byte[] body, string fileName)
    {
        using var form = Form(body, fileName);
        var response = await client.PostAsync($"/api/costs/invoices/{id}/scan", form);
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> FromFileAsync(HttpClient client, byte[] body, string fileName)
    {
        using var form = Form(body, fileName);
        var response = await client.PostAsync("/api/costs/invoices/from-scan", form);
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // Тип — «неизвестный»: так офисный файл называет браузер на машине без Office, и вид обязан
    // определиться по содержимому.
    private static MultipartFormDataContent Form(byte[] body, string fileName)
    {
        var file = new ByteArrayContent(body);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent { { file, "file", fileName } };
    }
}
