using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Modules.Costs;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Integration.InvoiceFromScanTests;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Запомненное подставляется строкам, прочитанным со скана (задача C3, issue #1079, ТЗ COST-7.1), —
/// на живом приложении: настоящая очередь, настоящий справочник, поставщик найден по ИНН тем же сканом.
///
/// <para>Сами правила соответствий перебраны в <c>SupplierMatchTests</c>. Здесь — то, что у скана своё:
/// строки пишет сервер, а не форма, и поставщика он узнаёт в той же записи, что и строки.</para>
/// </summary>
public sealed class InvoiceScanMatchTests(InvoiceScanHost host)
    : InvoiceLineTestBase(host), IClassFixture<InvoiceScanHost>
{
    [Fact]
    public async Task Строка_скана_знакомая_поставщику_ложится_с_позицией_и_пометкой()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (vendor, taxId) = await VendorAsync();
        var position = await PositionAsync();

        // Первый счёт заведён руками: человек выбрал позицию — выбор запомнен.
        await RememberAsync(client, vendor, position, "Кабель силовой 3х2,5");

        // Второй пришёл сканом. Поставщик узнан по ИНН этим же сканом, наименование набрано иначе.
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromResult(Read(
            Header(number: "СЧ-77", supplier: "Поставщик", supplierTaxId: taxId),
            Row("  кабель СИЛОВОЙ  3х2,5", "м", "10", "5,00", "50,00"),
            Row("Труба, которую никто не выбирал", "м", "1", "7,00", "7,00"))));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        Assert.Equal("done", (await OutcomeAsync(client, id)).GetProperty("state").GetString());

        var view = await ReadAsync(client, id);
        Assert.Equal(vendor, view.GetProperty("requisites").GetProperty("Поставщик").GetProperty("entryId").GetGuid());
        var lines = view.GetProperty("lines");
        Assert.Equal(position, lines[0].GetProperty("nomenclatureId").GetGuid());
        // Пометка — та же, что ставит форма: подставленное не выдаётся за выбор человека.
        Assert.Equal("current", lines[0].GetProperty("match").GetProperty("state").GetString());
        Assert.Equal("name", lines[0].GetProperty("match").GetProperty("by").GetString());
        // Незнакомая строка ждёт человека — и одна она, а не обе, стоит в счётчике «Разобрать».
        Assert.Equal(JsonValueKind.Null, lines[1].GetProperty("nomenclatureId").ValueKind);
        Assert.Equal(JsonValueKind.Null, lines[1].GetProperty("match").ValueKind);
        Assert.Equal(1, view.GetProperty("totals").GetProperty("withoutNomenclature").GetInt32());

        Assert.Contains("из них с позицией из запомненного: 1", await JournalAsync(client, id));
    }

    [Fact]
    public async Task Архивная_позиция_и_чужой_поставщик_строке_скана_не_подставляются()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (vendor, taxId) = await VendorAsync();
        var (stranger, _) = await VendorAsync();
        var archived = await PositionAsync();
        var foreign = await PositionAsync();
        var alive = await PositionAsync();

        await RememberAsync(client, vendor, archived, "Гофра 20");
        await RememberAsync(client, stranger, foreign, "Кабель чужой");
        await RememberAsync(client, vendor, alive, "Муфта концевая");

        using (var scope = host.Services.CreateScope())
            Assert.Equal(ArchiveOutcome.Changed,
                await scope.ServiceProvider.GetRequiredService<IRecordArchive>().SetAsync(archived, archived: true));

        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromResult(Read(
            Header(number: "СЧ-78", supplier: "Поставщик", supplierTaxId: taxId),
            Row("Гофра 20", "м", "4", "5,00", "20,00"),
            Row("Кабель чужой", "м", "1", "7,00", "7,00"),
            Row("Муфта концевая", "шт", "1", "7,00", "7,00"))));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        var recognition = await OutcomeAsync(client, id);
        Assert.Equal("done", recognition.GetProperty("state").GetString());

        var view = await ReadAsync(client, id);
        var lines = view.GetProperty("lines");
        // Контроль: поставщик узнан и подстановка РАБОТАЛА — иначе «не подставлено» ниже доказывало бы
        // лишь то, что не подставлялось ничего (ревью PR #1263).
        Assert.Equal(vendor, view.GetProperty("requisites").GetProperty("Поставщик").GetProperty("entryId").GetGuid());
        Assert.Equal(alive, lines[2].GetProperty("nomenclatureId").GetGuid());

        Assert.All(new[] { lines[0], lines[1] }, line =>
        {
            Assert.Equal(JsonValueKind.Null, line.GetProperty("nomenclatureId").ValueKind);
            Assert.Equal(JsonValueKind.Null, line.GetProperty("match").ValueKind);
        });
        // Узнанная, но архивная — названа: без слова она выглядела бы незнакомой. Чужая — не узнана вовсе.
        Assert.Contains(recognition.GetProperty("notes").EnumerateArray(),
            note => note.GetString()!.Contains("запомнена позиция в архиве или удалённая: 1"));
        Assert.Contains("из них с позицией из запомненного: 1", await JournalAsync(client, id));
    }

    /// <summary>
    /// Справочник не ответил на вопрос о запомненном — распознавание от этого не падает: прочитанное
    /// оплачено, и шапка со строками ложатся в счёт. Отказ назван, а не выдан за «ничего не запомнено».
    /// </summary>
    [Fact]
    public async Task Отказ_справочника_не_губит_распознавание_и_назван()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (vendor, taxId) = await VendorAsync();
        await RememberAsync(client, vendor, await PositionAsync(), "Лоток перфорированный");

        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromResult(Read(
            Header(number: "СЧ-80", supplier: "Поставщик", supplierTaxId: taxId),
            Row("Лоток перфорированный", "м", "2", "5,00", "10,00"))));

        host.RefsFailure = new InvalidOperationException("Справочник не ответил (сценарий теста).");
        JsonElement recognition;
        Guid id;
        try
        {
            id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
            recognition = await OutcomeAsync(client, id);
        }
        finally
        {
            host.RefsFailure = null;
        }

        Assert.Equal("done", recognition.GetProperty("state").GetString());
        var view = await ReadAsync(client, id);
        Assert.Equal("СЧ-80", view.GetProperty("requisites").GetProperty("Номер").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("lines")[0].GetProperty("nomenclatureId").ValueKind);
        Assert.Contains(recognition.GetProperty("notes").EnumerateArray(),
            note => note.GetString()!.Contains("справочник не ответил"));
    }

    /// <summary>Поставщик не узнан — подставлять не по чему: строки ложатся как прочитаны.</summary>
    [Fact]
    public async Task Без_поставщика_строки_скана_ждут_человека()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (vendor, _) = await VendorAsync();
        await RememberAsync(client, vendor, await PositionAsync(), "Муфта соединительная");

        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromResult(Read(
            Header(number: "СЧ-79"), Row("Муфта соединительная", "шт", "2", "5,00", "10,00"))));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        Assert.Equal("done", (await OutcomeAsync(client, id)).GetProperty("state").GetString());

        var line = (await ReadAsync(client, id)).GetProperty("lines")[0];
        Assert.Equal(JsonValueKind.Null, line.GetProperty("nomenclatureId").ValueKind);
    }

    // ── Помощники ────────────────────────────────────────────────────────────

    /// <summary>Счёт, заведённый руками, в котором человек выбрал позицию строке, — так выбор запоминается.</summary>
    private static async Task RememberAsync(HttpClient client, Guid vendor, Guid position, string text)
    {
        var requisites = new Dictionary<string, object?>
        {
            ["Номер"] = $"СЧ-{Guid.NewGuid().ToString()[..6]}",
            ["Дата"] = "2026-09-29",
            ["Поставщик"] = Reference(vendor),
        };
        var response = await client.PostAsJsonAsync("/api/costs/invoices", new { requisites });
        await OkAsync(response);
        var invoice = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var saved = await LinesAsync(client, invoice, [Line(position, 1, 5m, text: text)]);
        Assert.Equal(1, saved.GetProperty("memory").GetProperty("remembered").GetInt32());
    }

    /// <summary>Свой поставщик на каждый тест, с ИНН: по нему скан его и узнаёт.</summary>
    private async Task<(Guid Id, string TaxId)> VendorAsync()
    {
        var type = await InvoiceScanPartiesTests.OrganizationsAsync(host);
        var taxId = InvoiceScanPartiesTests.NextTaxId();
        using var scope = host.Services.CreateScope();
        var entry = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new CreateCommonDataEntryCommand(
            $"ООО «Поставщик {Guid.NewGuid().ToString("N")[..6]}»", type,
            JsonDocument.Parse($$"""{"ИНН":"{{taxId}}"}"""), CatalogScope.System, null));
        return (entry.Id, taxId);
    }

    private async Task<Guid> PositionAsync() =>
        await EntryAsync(await TypeAsync(CostsRecordTypes.NomenclatureCode, "Номенклатура"),
            $"Позиция {Guid.NewGuid().ToString("N")[..6]}");

    /// <summary>Что записал журнал о распознавании этого счёта.</summary>
    private async Task<string> JournalAsync(HttpClient client, Guid invoice) =>
        Assert.Single(await host.JournalAsync(client, invoice, "costs.invoice.recognized")).After
        ?? string.Empty;
}
