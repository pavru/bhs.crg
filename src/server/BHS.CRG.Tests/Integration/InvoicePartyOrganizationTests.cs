using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Integration.InvoiceFromScanTests;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Организацию, прочитанную в скане и не найденную в справочнике, заводит модуль счетов — кнопкой в
/// счёте, под своим правом (ТЗ COST-8, задача B1b, issue #1077).
///
/// <para>Сторож: право означает «завести организацию, прочитанную в скане ЭТОГО счёта». ИНН берётся
/// из сохранённого распознавания, и заводится запись только при ответе «в справочнике нет» — любой
/// другой ответ значит, что утверждать это нельзя.</para>
///
/// <para>Раскладка по схеме и замок проверены в <c>CatalogIntakeTests</c>; здесь — адрес целиком.</para>
/// </summary>
public sealed class InvoicePartyOrganizationTests(InvoiceScanHost host)
    : InvoiceLineTestBase(host), IClassFixture<InvoiceScanHost>
{
    [Fact]
    public async Task Организация_из_скана_заводится_и_сторона_становится_найденной()
    {
        var type = await InvoiceScanPartiesTests.OrganizationsAsync(host);
        var taxId = InvoiceScanPartiesTests.NextTaxId();

        var (client, _) = await SignInAsync("Supplier");
        var id = await DraftAsync(client, Header(supplier: "ООО «Новый поставщик»", supplierTaxId: $"{taxId} / 770101001"));
        var version = (await ReadAsync(client, id)).GetProperty("version").GetString();

        var answer = await CreateAsync(client, id, "supplier", new { });

        var created = answer.GetProperty("created").GetGuid();
        var party = answer.GetProperty("party");
        Assert.Equal("matched", party.GetProperty("state").GetString());
        Assert.Equal(created, party.GetProperty("match").GetGuid());

        // ИНН — из скана, выделенный из «ИНН / КПП»; название — как прочитано.
        var stored = Assert.Single(await RecordsAsync(type, taxId));
        Assert.Equal(created, stored.Id);
        Assert.Equal("ООО «Новый поставщик»", stored.DisplayName);
        Assert.Equal("ООО «Новый поставщик»", stored.Data.RootElement.GetProperty("Наименование").GetString());
        Assert.Equal(CatalogScope.System, stored.ScopeLevel);

        // Счёт не тронут: организацию в поле ставит человек, обычной правкой шапки.
        var view = await ReadAsync(client, id);
        Assert.Equal(version, view.GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("requisites").GetProperty("Поставщик").ValueKind);

        // И чтение распознавания говорит то же, что ответ кнопки.
        Assert.Equal(created,
            (await RecognitionAsync(client, id)).GetProperty("parties").GetProperty("supplier").GetProperty("match").GetGuid());
    }

    /// <summary>Модель читает название с переносами и чужими кавычками — человек вправе поправить.</summary>
    [Fact]
    public async Task Название_можно_поправить_а_ИНН_из_запроса_не_берётся()
    {
        var type = await InvoiceScanPartiesTests.OrganizationsAsync(host);
        var taxId = InvoiceScanPartiesTests.NextTaxId();
        var foreign = InvoiceScanPartiesTests.NextTaxId();

        var (client, _) = await SignInAsync("Supplier");
        var id = await DraftAsync(client, Header(payer: "OOO \"MOHTAЖ\"", payerTaxId: taxId));

        await CreateAsync(client, id, "payer", new { name = "  ООО «Монтаж»  ", taxId = foreign, ИНН = foreign });

        Assert.Equal("ООО «Монтаж»", Assert.Single(await RecordsAsync(type, taxId)).DisplayName);
        Assert.Empty(await RecordsAsync(type, foreign));
    }

    /// <summary>
    /// Пока форма была открыта, организацию завёл кто-то ещё. Кнопка не отказывает и не заводит
    /// вторую: отвечает стороной как она есть сейчас.
    /// </summary>
    [Fact]
    public async Task Уже_заведённая_организация_второй_раз_не_заводится()
    {
        var type = await InvoiceScanPartiesTests.OrganizationsAsync(host);
        var taxId = InvoiceScanPartiesTests.NextTaxId();

        var (client, _) = await SignInAsync("Supplier");
        var id = await DraftAsync(client, Header(supplier: "ООО «Дважды»", supplierTaxId: taxId));

        var first = (await CreateAsync(client, id, "supplier", new { })).GetProperty("created").GetGuid();
        var second = await CreateAsync(client, id, "supplier", new { });

        Assert.Equal(JsonValueKind.Null, second.GetProperty("created").ValueKind);
        Assert.Equal(first, second.GetProperty("party").GetProperty("match").GetGuid());
        Assert.Single(await RecordsAsync(type, taxId));
    }

    /// <summary>
    /// ИНН прочитан с ошибкой — «в справочнике нет» утверждать нельзя, и заводить по нему нельзя:
    /// получилась бы организация-призрак рядом с настоящей.
    /// </summary>
    [Fact]
    public async Task По_ИНН_прочитанному_с_ошибкой_организация_не_заводится()
    {
        var type = await InvoiceScanPartiesTests.OrganizationsAsync(host);
        var taxId = InvoiceScanPartiesTests.NextTaxId();
        // Последняя цифра — контрольная: сдвигаем её.
        var broken = taxId[..9] + (char)('0' + (taxId[9] - '0' + 1) % 10);

        var (client, _) = await SignInAsync("Supplier");
        var id = await DraftAsync(client, Header(supplier: "ООО «Опечатка»", supplierTaxId: broken));

        var refusal = await PostAsync(client, id, "supplier", new { });

        Assert.Equal(HttpStatusCode.Conflict, refusal.StatusCode);
        Assert.Contains("контрольная сумма", await refusal.Content.ReadAsStringAsync());
        Assert.Empty(await RecordsAsync(type, broken));
    }

    [Fact]
    public async Task Сторона_о_которой_в_скане_ничего_нет_и_неизвестная_сторона_отказ()
    {
        await InvoiceScanPartiesTests.OrganizationsAsync(host);
        var (client, _) = await SignInAsync("Supplier");
        var id = await DraftAsync(client, Header(supplier: "ООО «Один»", supplierTaxId: InvoiceScanPartiesTests.NextTaxId()));

        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(client, id, "payer", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, id, "carrier", new { })).StatusCode);
    }

    /// <summary>Разобранному счёту стороны уже выбраны — заводить по его скану поздно.</summary>
    [Fact]
    public async Task У_разобранного_счёта_организация_не_заводится()
    {
        var type = await InvoiceScanPartiesTests.OrganizationsAsync(host);
        var taxId = InvoiceScanPartiesTests.NextTaxId();
        var (client, _) = await SignInAsync("Supplier");
        var id = await DraftAsync(client, Header(supplier: "ООО «Поздно»", supplierTaxId: taxId));

        // Состояние ставим в базе: переход «разобран» требует строк и разноски, а они здесь ни при чём.
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CostsDbContext>().Database
                .ExecuteSqlAsync($"UPDATE costs.invoices SET state = 'Parsed' WHERE id = {id}");

        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(client, id, "supplier", new { })).StatusCode);
        Assert.Empty(await RecordsAsync(type, taxId));
    }

    /// <summary>
    /// Сторож: отказ не выглядит результатом. В архиве лежит организация с тем же названием (ключ
    /// идентичности), а ИНН у неё другой — по ИНН сторона «нет», но ядро вторую такую молча не заводит.
    /// Ответ «заведена: нет, сторона: нет» оставил бы человека нажимать кнопку снова и снова.
    /// </summary>
    [Fact]
    public async Task Архивный_двойник_с_другим_ИНН_отказ_с_его_названием_а_не_сторона_нет()
    {
        var type = await InvoiceScanPartiesTests.OrganizationsAsync(host);
        var taxId = InvoiceScanPartiesTests.NextTaxId();
        var name = $"ООО «Двойник {Guid.NewGuid():N}»";
        Guid twin;
        using (var scope = host.Services.CreateScope())
        {
            var mediator = scope.ServiceProvider.GetRequiredService<MediatR.IMediator>();
            twin = (await mediator.Send(new BHS.CRG.Application.Documents.CreateCommonDataEntryCommand(
                name, type, JsonDocument.Parse($$"""{"Наименование":"{{name}}","ИНН":"{{InvoiceScanPartiesTests.NextTaxId()}}"}"""),
                CatalogScope.System, null))).Id;
            await mediator.Send(new BHS.CRG.Application.Documents.SetRecordArchiveCommand(twin, true));
        }

        var (client, _) = await SignInAsync("Supplier");
        var id = await DraftAsync(client, Header(supplier: name, supplierTaxId: taxId));

        var refusal = await PostAsync(client, id, "supplier", new { });

        Assert.Equal(HttpStatusCode.Conflict, refusal.StatusCode);
        var why = (await refusal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.Contains(name, why);
        Assert.Contains("в архиве", why);
        Assert.Empty(await RecordsAsync(type, taxId));
    }

    /// <summary>Абзац текста на месте названия — причина, а не сбой сохранения.</summary>
    [Fact]
    public async Task Слишком_длинное_название_отказ_с_причиной()
    {
        var type = await InvoiceScanPartiesTests.OrganizationsAsync(host);
        var taxId = InvoiceScanPartiesTests.NextTaxId();
        var (client, _) = await SignInAsync("Supplier");
        var id = await DraftAsync(client, Header(supplier: "ООО «Длинное»", supplierTaxId: taxId));

        var refusal = await PostAsync(client, id, "supplier", new { name = new string('я', 513) });

        Assert.Equal(HttpStatusCode.BadRequest, refusal.StatusCode);
        Assert.Empty(await RecordsAsync(type, taxId));
    }

    /// <summary>
    /// Права не вкладываются друг в друга: роль с одной галкой «заводить организации» иначе читала бы
    /// стороны любого счёта перебором id — адрес отвечает названием и ИНН поставщика.
    /// </summary>
    [Fact]
    public async Task Одного_права_заведения_мало_нужно_и_чтение_счетов()
    {
        var type = await InvoiceScanPartiesTests.OrganizationsAsync(host);
        var taxId = InvoiceScanPartiesTests.NextTaxId();
        var (client, _) = await SignInAsync("Supplier");
        var id = await DraftAsync(client, Header(supplier: "ООО «Закрытый счёт»", supplierTaxId: taxId));

        var onlyCreate = await Support.GrantedSignIn.WithPermissionsAsync(host, CostsModule.OrganizationCreate);
        var onlyRead = await Support.GrantedSignIn.WithPermissionsAsync(host, "costs.invoice.read");
        var both = await Support.GrantedSignIn.WithPermissionsAsync(host, CostsModule.OrganizationCreate, "costs.invoice.read");

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(onlyCreate, id, "supplier", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(onlyRead, id, "supplier", new { })).StatusCode);
        Assert.Empty(await RecordsAsync(type, taxId));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(both, id, "supplier", new { })).StatusCode);
    }

    /// <summary>Чтения счетов для этого мало: запись ложится в справочник ядра.</summary>
    [Fact]
    public async Task Без_права_заведения_отказ()
    {
        var type = await InvoiceScanPartiesTests.OrganizationsAsync(host);
        var taxId = InvoiceScanPartiesTests.NextTaxId();
        var (client, _) = await SignInAsync("Supplier");
        var id = await DraftAsync(client, Header(supplier: "ООО «Чужой»", supplierTaxId: taxId));

        var (reader, _) = await SignInAsync("User");

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(reader, id, "supplier", new { })).StatusCode);
        Assert.Empty(await RecordsAsync(type, taxId));
    }

    private async Task<Guid> DraftAsync(HttpClient client, Dictionary<string, string?> header)
    {
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromResult(Read(header)));
        var id = (await FromScanAsync(client, scan)).GetProperty("invoice").GetProperty("id").GetGuid();
        Assert.Equal("done", (await OutcomeAsync(client, id)).GetProperty("state").GetString());
        return id;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid id, string side, object body) =>
        client.PostAsJsonAsync($"/api/costs/invoices/{id}/recognition/parties/{side}/organization", body);

    private static async Task<JsonElement> CreateAsync(HttpClient client, Guid id, string side, object body)
    {
        var response = await PostAsync(client, id, side, body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Организации с этим ИНН — прямо из базы, мимо сопоставления, которое проверяем.</summary>
    private async Task<IReadOnlyList<DomainObject>> RecordsAsync(Guid type, string taxId)
    {
        using var scope = host.Services.CreateScope();
        var all = await scope.ServiceProvider.GetRequiredService<IRepository<DomainObject>>()
            .FindAsync(o => o.CompositeTypeId == type);
        return [.. all.Where(o => o.Data.RootElement.TryGetProperty("ИНН", out var value) && value.GetString() == taxId)];
    }
}
