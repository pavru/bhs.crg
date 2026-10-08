using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Integration.InvoiceFromScanTests;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Распознавание в СПИСКЕ счетов: пометка строки, отбор «Не распознано» и число на его чипе (ТЗ COST-8,
/// задача B1b, issue #1077).
///
/// <para>Сторож: неудача видна там, где человек её ищет, — в списке, а не только в открытом счёте.
/// Черновик из скана, который не распознался, без пометки неотличим от черновика, который ещё никто не
/// трогал. И все три места говорят одно: счёт с пометкой стоит под отбором и входит в число.</para>
///
/// <para>⚠️ База у классов на этом хосте общая, поэтому число сверяется приростом и вхождением, а не
/// равенством.</para>
/// </summary>
public sealed class InvoiceScanListTests(InvoiceScanHost host)
    : InvoiceLineTestBase(host), IClassFixture<InvoiceScanHost>
{
    [Fact]
    public async Task Нераспознанный_черновик_помечен_стоит_под_отбором_и_входит_в_число()
    {
        var (client, _) = await SignInAsync("Supplier");
        var before = await UnrecognizedCountAsync(client);
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromException<ModuleRecognitionResult>(new RecognitionRefusedException(
            RecognitionRefusal.Unavailable, "Движок распознавания не ответил.")));

        var created = await FromScanAsync(client, scan, "Счёт от Ромашки.pdf");
        var id = created.GetProperty("invoice").GetProperty("id").GetGuid();
        Assert.Equal("failed", (await OutcomeAsync(client, id)).GetProperty("state").GetString());

        var row = await RowAsync(client, id);
        Assert.Equal("failed", State(row));
        Assert.Equal("Unavailable", row.GetProperty("recognition").GetProperty("reason").GetString());
        // Номера и поставщика у такого счёта нет — строку различают по имени файла.
        Assert.Equal("Счёт от Ромашки.pdf", row.GetProperty("scanFileName").GetString());

        Assert.Contains(id, await UnrecognizedAsync(client));
        Assert.True(await UnrecognizedCountAsync(client) > before);
    }

    /// <summary>
    /// Шапка прочитана, строк нет — счёт со сканом всё ещё пуст, и работа та же: он под отбором, а строка
    /// говорит, что это не отказ (решение владельца 08.10.2026).
    /// </summary>
    [Fact]
    public async Task Прочитанный_без_строк_под_отбором_и_назван_не_отказом()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromResult(Read(Header(number: "СЧ-701"))));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        Assert.Equal("done", (await OutcomeAsync(client, id)).GetProperty("state").GetString());

        Assert.Equal("done", State(await RowAsync(client, id)));
        Assert.Contains(id, await UnrecognizedAsync(client));
    }

    /// <summary>Скан приложен к счёту, заведённому руками, и не распознавался — тоже пустой счёт со сканом.</summary>
    [Fact]
    public async Task Черновик_с_приложенным_сканом_без_распознавания_под_отбором()
    {
        var (client, _) = await SignInAsync("Supplier");
        var id = await CreateAsync(client);
        Assert.DoesNotContain(id, await UnrecognizedAsync(client));

        await AttachAsync(client, id);

        Assert.Equal("none", State(await RowAsync(client, id)));
        Assert.Contains(id, await UnrecognizedAsync(client));
    }

    /// <summary>
    /// Пока скан читается, строка говорит «читается» — и под отбор «Не распознано» не попадает: исхода
    /// ещё нет, и назвать его отказом было бы неправдой.
    /// </summary>
    [Fact]
    public async Task Пока_скан_читается_строка_говорит_об_этом_и_под_отбором_её_нет()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Recognition.On(scan, async () =>
        {
            await gate.Task;
            return Read(Header(number: "СЧ-702"));
        });

        Guid id;
        try
        {
            id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();

            Assert.Equal("running", State(await RowAsync(client, id)));
            Assert.DoesNotContain(id, await UnrecognizedAsync(client));
        }
        finally
        {
            gate.SetResult();
        }

        Assert.Equal("done", (await OutcomeAsync(client, id)).GetProperty("state").GetString());
    }

    /// <summary>
    /// Строки появились — счёт заполнили руками, и звать человека обратно незачем: из-под отбора он
    /// уходит, пометка со строки тоже. Отказ при этом остаётся виден в самом счёте.
    /// </summary>
    [Fact]
    public async Task Нераспознанный_счёт_со_строками_под_отбор_не_попадает()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan(); // сценария нет — подменный порт отвечает NoAnswer
        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        Assert.Equal("failed", (await OutcomeAsync(client, id)).GetProperty("state").GetString());
        Assert.Contains(id, await UnrecognizedAsync(client));

        await LinesAsync(client, id, [Line(cable, 3, 100)]);

        Assert.DoesNotContain(id, await UnrecognizedAsync(client));
        Assert.Null(State(await RowAsync(client, id)));
        Assert.Equal("failed", (await RecognitionAsync(client, id)).GetProperty("state").GetString());
    }

    /// <summary>
    /// Запись «ждёт исхода» без живой задачи — прервано, а не «читается»: иначе строка говорила бы
    /// «распознаётся…» вечно, и под отбор такой счёт не попал бы никогда.
    /// </summary>
    [Fact]
    public async Task Прерванное_распознавание_в_списке_не_распознан_а_не_читается()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromResult(Read(Header(number: null))));
        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        await OutcomeAsync(client, id);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
            var stored = await db.InvoiceRecognitions.SingleAsync(r => r.InvoiceId == id);
            stored.Restart(stored.ScanBlobPath);
            await db.SaveChangesAsync();
        }

        // Только что поставлено, номера задачи ещё нет — «читается», а не «прервано»: под отбор и в
        // число чипа такой счёт не попадает (ревью PR #1259).
        Assert.Equal("running", State(await RowAsync(client, id)));
        Assert.DoesNotContain(id, await UnrecognizedAsync(client));

        await AgeAsync(host, id);
        var row = await RowAsync(client, id);
        Assert.Equal("failed", State(row));
        Assert.Equal("Interrupted", row.GetProperty("recognition").GetProperty("reason").GetString());
        Assert.Contains(id, await UnrecognizedAsync(client));
    }

    /// <summary>Запись о ПРЕЖНЕМ файле про нынешний скан не говорит ничего: новый — «не распознавался».</summary>
    [Fact]
    public async Task После_замены_скана_отказ_о_прежнем_файле_в_списке_не_показывается()
    {
        var (client, _) = await SignInAsync("Supplier");
        var id = (await FromScanAsync(client, Scan())).GetProperty("invoice").GetProperty("id").GetGuid();
        Assert.Equal("failed", (await OutcomeAsync(client, id)).GetProperty("state").GetString());

        await AttachAsync(client, id);

        Assert.Equal("none", State(await RowAsync(client, id)));
    }

    private static string? State(JsonElement row) =>
        row.GetProperty("recognition") is { ValueKind: JsonValueKind.Object } scan
            ? scan.GetProperty("state").GetString() : null;

    private static async Task AttachAsync(HttpClient client, Guid id)
    {
        var version = (await ReadAsync(client, id)).GetProperty("version").GetString()!;
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(Scan()));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "Другой.pdf");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/costs/invoices/{id}/scan") { Content = form };
        request.Headers.TryAddWithoutValidation("If-Match", version);
        await OkAsync(await client.SendAsync(request));
    }

    private static async Task<JsonElement> RowAsync(HttpClient client, Guid id)
    {
        var list = await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices");
        return list.EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == id);
    }

    private static async Task<Guid[]> UnrecognizedAsync(HttpClient client)
    {
        var list = await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices?unrecognized=true");
        // Под отбором — только помеченные, и ни одного читающегося: строка без пометки значила бы,
        // что отбор и пометка разошлись.
        Assert.All(list.EnumerateArray(), i => Assert.Contains(State(i), new[] { "failed", "done", "none" }));
        return [.. list.EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];
    }

    private static async Task<int> UnrecognizedCountAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices/queues")).GetProperty("unrecognized").GetInt32();
}
