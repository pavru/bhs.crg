using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// «Тот же файл приложен к другому счёту» (issue #1295): оговорка по СОДЕРЖИМОМУ файла, а не по имени,
/// — и оговорка, а не запрет: черновик из повторного файла всё равно заводится.
///
/// <para><b>Сторож задачи</b> — пара «те же байты под другим именем — оговорка есть; то же имя с другим
/// содержимым — оговорки нет». Ломается сравнением по имени файла (отказ, переодетый в результат: на
/// обычной пробе «один файл дважды» оно выглядит работающим) и отпечатком уже прочитанного потока —
/// тогда отпечаток у всех файлов один, и совпадает всё со всем.</para>
/// </summary>
public sealed class InvoiceSameFileTests(InvoiceScanHost host)
    : InvoiceLineTestBase(host), IClassFixture<InvoiceScanHost>
{
    private static MultipartFormDataContent Form(string content, string fileName)
    {
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        return new MultipartFormDataContent { { file, "file", fileName } };
    }

    private static string Pdf() => $"%PDF-1.4 {Guid.NewGuid():N}";

    /// <summary>Счёт из файла; возвращает счёт из ответа приёма — оговорка обязана быть уже в нём.</summary>
    private async Task<JsonElement> FromScanAsync(HttpClient client, string content, string fileName)
    {
        var created = await client.PostAsync("/api/costs/invoices/from-scan", Form(content, fileName));
        await OkAsync(created);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("invoice");
    }

    private async Task<JsonElement> AttachAsync(HttpClient client, Guid invoice, string content, string fileName)
    {
        var attached = await client.PostAsync($"/api/costs/invoices/{invoice}/scan", Form(content, fileName));
        await OkAsync(attached);
        return await attached.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Guid[] SameFile(JsonElement view) =>
        [.. view.GetProperty("sameFile").EnumerateArray().Select(s => s.GetProperty("id").GetGuid())];

    private static Guid Id(JsonElement view) => view.GetProperty("id").GetGuid();

    [Fact]
    public async Task Те_же_байты_под_другим_именем_получают_оговорку_со_ссылкой_на_первый_счёт()
    {
        var (client, _) = await SignInAsync("Admin");
        var content = Pdf();
        var first = await FromScanAsync(client, content, "Счёт 7.pdf");
        Assert.Empty(SameFile(first));

        var second = await FromScanAsync(client, content, "Скан (копия).pdf");

        // Оговорка — уже в ответе приёма: пакет загрузки узнаёт о повторе из него.
        Assert.Equal([Id(first)], SameFile(second));
        var named = second.GetProperty("sameFile")[0];
        Assert.Equal("Счёт 7.pdf", named.GetProperty("scanFileName").GetString());
        Assert.True(named.TryGetProperty("createdAt", out _));
        // И она взаимна: первый счёт, прочитанный заново, называет второй.
        Assert.Equal([Id(second)], SameFile(await ReadAsync(client, Id(first))));
    }

    [Fact]
    public async Task То_же_имя_с_другим_содержимым_оговорки_не_получает()
    {
        var (client, _) = await SignInAsync("Admin");
        var first = await FromScanAsync(client, Pdf(), "Счёт.pdf");

        var second = await FromScanAsync(client, Pdf(), "Счёт.pdf");

        Assert.Empty(SameFile(second));
        Assert.Empty(SameFile(await ReadAsync(client, Id(first))));
    }

    /// <summary>Удалённый счёт совпадением не считается: его файла больше нет.</summary>
    [Fact]
    public async Task После_удаления_первого_счёта_оговорка_у_второго_гаснет()
    {
        var (client, _) = await SignInAsync("Admin");
        var content = Pdf();
        var first = await FromScanAsync(client, content, "Дважды.pdf");
        var second = await FromScanAsync(client, content, "Дважды.pdf");
        Assert.Equal([Id(first)], SameFile(second));

        var deleted = await client.DeleteAsync($"/api/costs/invoices/{Id(first)}");

        Assert.Equal(System.Net.HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(SameFile(await ReadAsync(client, Id(second))));
    }

    /// <summary>
    /// Замена файла меняет отпечаток: с прежним двойником связь пропадает, с тем, чьё содержимое
    /// приложили теперь, — появляется, и сказано это ответом самой замены.
    /// </summary>
    [Fact]
    public async Task Замена_файла_меняет_отпечаток_в_обе_стороны()
    {
        var (client, _) = await SignInAsync("Admin");
        var (shared, other) = (Pdf(), Pdf());
        var first = await FromScanAsync(client, shared, "А.pdf");
        var second = await FromScanAsync(client, shared, "Б.pdf");
        var third = await FromScanAsync(client, other, "В.pdf");

        var replaced = await AttachAsync(client, Id(second), other, "Б.pdf");

        Assert.Equal([Id(third)], SameFile(replaced));
        Assert.Empty(SameFile(await ReadAsync(client, Id(first))));
    }

    /// <summary>Тот же файл, приложенный повторно к ТОМУ ЖЕ счёту, — не повод: счёт себе не двойник.</summary>
    [Fact]
    public async Task Повторная_загрузка_файла_в_тот_же_счёт_оговорки_не_даёт()
    {
        var (client, _) = await SignInAsync("Admin");
        var content = Pdf();
        var invoice = await FromScanAsync(client, content, "Сам.pdf");

        var again = await AttachAsync(client, Id(invoice), content, "Сам.pdf");

        Assert.Empty(SameFile(again));
    }

    /// <summary>
    /// Путь файла сменился в обход приёма — так делает восстановление из копии, снятой до появления
    /// отпечатка: путь возвращается прежний, а отпечаток остаётся от файла, приложенного позже. Такому
    /// отпечатку верить нельзя: счёт ни с чем не сравнивается, в обе стороны.
    /// </summary>
    [Fact]
    public async Task Отпечаток_чужого_файла_совпадением_не_считается()
    {
        var (client, _) = await SignInAsync("Admin");
        var content = Pdf();
        var first = await FromScanAsync(client, content, "Копия.pdf");
        var second = await FromScanAsync(client, content, "Копия.pdf");
        Assert.Equal([Id(first)], SameFile(second));

        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CostsDbContext>().Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE costs.invoices SET scan_blob_path = scan_blob_path || '.прежний' WHERE id = {Id(first)}");

        Assert.Empty(SameFile(await ReadAsync(client, Id(second))));
        Assert.Empty(SameFile(await ReadAsync(client, Id(first))));
    }

    /// <summary>Один бланк, загруженный десятки раз: оговорка называет первые, а не всех.</summary>
    [Fact]
    public async Task Двойников_называется_не_больше_предела()
    {
        var (client, _) = await SignInAsync("Admin");
        var content = Pdf();
        JsonElement last = default;
        for (var i = 0; i < InvoiceSameFiles.Limit + 2; i++)
            last = await FromScanAsync(client, content, $"Бланк {i}.pdf");

        Assert.Equal(InvoiceSameFiles.Limit, SameFile(last).Length);
    }

    /// <summary>
    /// Появление двойника первый счёт не меняет: его версия та же, и форма, открытая рядом, отказа
    /// «счёт тем временем изменили» не получит.
    /// </summary>
    [Fact]
    public async Task Второй_приём_не_двигает_версию_первого_счёта()
    {
        var (client, _) = await SignInAsync("Admin");
        var content = Pdf();
        var first = await FromScanAsync(client, content, "Версия.pdf");
        var before = (await ReadAsync(client, Id(first))).GetProperty("version").GetString();

        await FromScanAsync(client, content, "Версия.pdf");

        Assert.Equal(before, (await ReadAsync(client, Id(first))).GetProperty("version").GetString());
    }
}
