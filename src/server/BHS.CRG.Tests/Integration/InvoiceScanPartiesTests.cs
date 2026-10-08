using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.EntityFrameworkCore;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Integration.InvoiceFromScanTests;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Поставщик и плательщик распознанного счёта находятся в справочнике по ИНН (ТЗ COST-8, задача B1b,
/// issue #1077) — на живом приложении, через настоящую очередь и настоящий справочник ядра.
///
/// <para>Все состояния сопоставления перебраны в <c>InvoicePartiesTests</c> на подменном справочнике.
/// Здесь — то, что подменой не проверить: наследование реквизитов читается из базы, найденная
/// организация ложится в счёт помеченной, а вид пересчитывает ответ по сегодняшнему справочнику.</para>
/// </summary>
public sealed class InvoiceScanPartiesTests(InvoiceScanHost host)
    : InvoiceLineTestBase(host), IClassFixture<InvoiceScanHost>
{
    [Fact]
    public async Task Найденные_по_ИНН_стороны_ложатся_в_пустые_поля_помеченными()
    {
        var type = await OrganizationsAsync();
        var (seller, sellerTaxId) = await OrganizationAsync(type, "ООО «Свет-Опт»");
        var (buyer, buyerTaxId) = await OrganizationAsync(type, "ООО «Монтаж»");

        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromResult(Read(Header(
            number: "СЧ-9", supplier: "Свет-Опт, ООО", supplierTaxId: sellerTaxId,
            payer: "МОНТАЖ ООО", payerTaxId: $"{buyerTaxId}/770101001"))));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        var recognition = await OutcomeAsync(client, id);
        Assert.Equal("done", recognition.GetProperty("state").GetString());

        var view = await ReadAsync(client, id);
        var requisites = view.GetProperty("requisites");
        Assert.Equal(seller, requisites.GetProperty("Поставщик").GetProperty("entryId").GetGuid());
        Assert.Equal(buyer, requisites.GetProperty("Плательщик").GetProperty("entryId").GetGuid());
        // Подставленное — предложение: помечено, пока человек не сверил.
        Assert.Contains("Поставщик", Unconfirmed(view));
        Assert.Contains("Плательщик", Unconfirmed(view));

        var supplier = recognition.GetProperty("parties").GetProperty("supplier");
        Assert.Equal("matched", supplier.GetProperty("state").GetString());
        Assert.Equal("Свет-Опт, ООО", supplier.GetProperty("name").GetString());
        Assert.Equal(sellerTaxId, supplier.GetProperty("taxId").GetString());
    }

    /// <summary>
    /// Роль наследует ИНН от организации — в её собственных данных его нет. Сопоставление видит обе
    /// записи и узнаёт в них одну организацию: в счёт ложится она сама, а не роль.
    /// </summary>
    [Fact]
    public async Task Организация_со_своей_ролью_подставляется_сама()
    {
        var type = await OrganizationsAsync();
        var (org, taxId) = await OrganizationAsync(type, "ООО «Кабель-Сервис»");
        var role = await EntryAsync(type, "Кабель-Сервис (подрядчик)", $$"""{"_baseRef":"{{org}}"}""");

        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromResult(Read(Header(supplier: "Кабель-Сервис", supplierTaxId: taxId))));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        var supplier = (await OutcomeAsync(client, id)).GetProperty("parties").GetProperty("supplier");

        Assert.Equal("matched", supplier.GetProperty("state").GetString());
        Assert.Equal(org, supplier.GetProperty("match").GetGuid());
        var candidates = supplier.GetProperty("candidates").EnumerateArray().ToList();
        Assert.Equal(new[] { org, role }.Order(), candidates.Select(c => c.GetProperty("id").GetGuid()).Order());
        Assert.Equal(org, candidates.Single(c => c.GetProperty("id").GetGuid() == role).GetProperty("inheritedFrom").GetGuid());

        var view = await ReadAsync(client, id);
        Assert.Equal(org, view.GetProperty("requisites").GetProperty("Поставщик").GetProperty("entryId").GetGuid());
        Assert.Contains("Поставщик", Unconfirmed(view));
    }

    /// <summary>Две разные записи с одним ИНН не подставляются: поле пусто, выбирает человек.</summary>
    [Fact]
    public async Task Дубли_с_одним_ИНН_не_подставляются_а_предлагаются_списком()
    {
        var type = await OrganizationsAsync();
        var (first, taxId) = await OrganizationAsync(type, "ООО «Дубль»");
        var second = await EntryAsync(type, "Дубль, ООО", $$"""{"ИНН":"{{taxId}}"}""");

        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromResult(Read(Header(supplier: "Дубль", supplierTaxId: taxId))));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        var supplier = (await OutcomeAsync(client, id)).GetProperty("parties").GetProperty("supplier");

        Assert.Equal("several", supplier.GetProperty("state").GetString());
        Assert.Equal(new[] { first, second }.Order(),
            supplier.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).Order());

        var view = await ReadAsync(client, id);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("requisites").GetProperty("Поставщик").ValueKind);
        Assert.DoesNotContain("Поставщик", Unconfirmed(view));
    }

    /// <summary>
    /// Сторож: «нет в справочнике» не хранится. Организацию завели после распознавания — и тот же
    /// вид отвечает уже «найдена», а счёт при этом не тронут: вид только читает.
    /// </summary>
    [Fact]
    public async Task Вид_пересчитывает_ответ_по_сегодняшнему_справочнику_и_счёт_не_трогает()
    {
        var type = await OrganizationsAsync();
        var taxId = NextTaxId();

        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromResult(Read(Header(supplier: "ООО «Новый»", supplierTaxId: taxId))));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        var before = (await OutcomeAsync(client, id)).GetProperty("parties").GetProperty("supplier");
        Assert.Equal("absent", before.GetProperty("state").GetString());
        Assert.Equal("ООО «Новый»", before.GetProperty("name").GetString());
        var version = (await ReadAsync(client, id)).GetProperty("version").GetString();

        var created = await EntryAsync(type, "ООО «Новый»", $$"""{"ИНН":"{{taxId}}"}""");

        var after = (await RecognitionAsync(client, id)).GetProperty("parties").GetProperty("supplier");
        Assert.Equal("matched", after.GetProperty("state").GetString());
        Assert.Equal(created, Assert.Single(after.GetProperty("candidates").EnumerateArray()).GetProperty("id").GetGuid());

        var view = await ReadAsync(client, id);
        Assert.Equal(version, view.GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("requisites").GetProperty("Поставщик").ValueKind);
    }

    /// <summary>
    /// Стороны нужны, пока счёт — черновик. Разобранный счёт справочник на каждое открытие не обходит.
    /// </summary>
    [Fact]
    public async Task У_разобранного_счёта_стороны_не_сопоставляются()
    {
        var type = await OrganizationsAsync();
        var (_, sellerTaxId) = await OrganizationAsync(type, "ООО «Продавец»");
        var (_, buyerTaxId) = await OrganizationAsync(type, "ООО «Покупатель»");

        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromResult(Read(Header(
            number: "СЧ-77", date: "01.04.2026", total: "100,00",
            supplier: "Продавец", supplierTaxId: sellerTaxId, payer: "Покупатель", payerTaxId: buyerTaxId))));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        Assert.Equal(JsonValueKind.Object, (await OutcomeAsync(client, id)).GetProperty("parties").ValueKind);

        // Состояние ставим в базе: переход «разобран» требует строк и разноски, а они здесь ни при чём.
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CostsDbContext>().Database
                .ExecuteSqlAsync($"UPDATE costs.invoices SET state = 'Parsed' WHERE id = {id}");

        var recognition = await RecognitionAsync(client, id);
        Assert.Equal("done", recognition.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, recognition.GetProperty("parties").ValueKind);
        // Сохранённое прочитанное на месте: пропали только стороны.
        Assert.Equal("СЧ-77", recognition.GetProperty("values").GetProperty("НомерСчёта").GetString());
    }

    /// <summary>Архивная организация в новый счёт не подставляется — и названа архивной.</summary>
    [Fact]
    public async Task Архивная_организация_не_подставляется()
    {
        var type = await OrganizationsAsync();
        var (org, taxId) = await OrganizationAsync(type, "ООО «Прежний поставщик»");
        await SendAsync(new SetRecordArchiveCommand(org, true));

        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromResult(Read(Header(supplier: "Прежний поставщик", supplierTaxId: taxId))));

        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        var supplier = (await OutcomeAsync(client, id)).GetProperty("parties").GetProperty("supplier");

        Assert.Equal("archived", supplier.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null,
            (await ReadAsync(client, id)).GetProperty("requisites").GetProperty("Поставщик").ValueKind);
    }

    /// <summary>
    /// Сторож: поставщика, которого человек выбрал сам, пока скан читался, распознавание не заменяет —
    /// даже найдя по ИНН другую организацию. Найденная остаётся в ответе сопоставления.
    /// </summary>
    [Fact]
    public async Task Поставщик_выбранный_человеком_не_заменяется_найденным()
    {
        var type = await OrganizationsAsync();
        var (found, taxId) = await OrganizationAsync(type, "ООО «Из скана»");

        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Recognition.On(scan, async () =>
        {
            await gate.Task;
            return Read(Header(supplier: "Из скана", supplierTaxId: taxId));
        });

        Guid id;
        try
        {
            id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
            var patched = await RequisitesWithAsync(client, id, "Поставщик", Reference(supplier));
            await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{id}", new { requisites = patched }));
        }
        finally
        {
            gate.SetResult();
        }

        var party = (await OutcomeAsync(client, id)).GetProperty("parties").GetProperty("supplier");
        Assert.Equal("matched", party.GetProperty("state").GetString());
        Assert.Equal(found, Assert.Single(party.GetProperty("candidates").EnumerateArray()).GetProperty("id").GetGuid());

        var view = await ReadAsync(client, id);
        Assert.Equal(supplier, view.GetProperty("requisites").GetProperty("Поставщик").GetProperty("entryId").GetGuid());
        Assert.DoesNotContain("Поставщик", Unconfirmed(view));
    }

    // ── Помощники ────────────────────────────────────────────────────────────

    private static int taxIdSeed = 770100000;

    /// <summary>Свой ИНН на каждую организацию — с верной контрольной суммой: справочник у класса общий.</summary>
    private static string NextTaxId()
    {
        var head = Interlocked.Increment(ref taxIdSeed).ToString();
        int[] weights = [2, 4, 10, 3, 5, 9, 4, 6, 8];
        return head + weights.Select((weight, index) => weight * (head[index] - '0')).Sum() % 11 % 10;
    }

    private async Task<(Guid Id, string TaxId)> OrganizationAsync(Guid type, string name)
    {
        var taxId = NextTaxId();
        return (await EntryAsync(type, name, $$"""{"ИНН":"{{taxId}}"}"""), taxId);
    }

    private async Task<Guid> EntryAsync(Guid type, string name, string data) =>
        (await SendAsync(new CreateCommonDataEntryCommand(name, type, JsonDocument.Parse(data), CatalogScope.System, null))).Id;

    /// <summary>
    /// Тип «Организация» с полем ИНН. Посев класса заводит его без полей — как на чистой базе, где тип
    /// ещё не настроен; у заказчика поле есть, и ведёт его человек.
    /// </summary>
    private async Task<Guid> OrganizationsAsync()
    {
        using var scope = host.Services.CreateScope();
        var types = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
        var type = (await types.FindAsync(t => t.Code == CostsRecordTypes.OrganizationCode)).Single();
        if (!type.Schema.RootElement.GetRawText().Contains("\"ИНН\""))
        {
            type.UpdateSchema(JsonDocument.Parse("""{"fields":[{"key":"ИНН","type":"string","title":"ИНН"}]}"""));
            types.Update(type);
            await types.SaveChangesAsync();
        }

        return type.Id;
    }

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }
}
