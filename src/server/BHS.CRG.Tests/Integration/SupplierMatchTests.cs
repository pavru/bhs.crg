using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Claims;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Objects;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Endpoints;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Соответствия наименований поставщика (задача C3, issue #1079, ТЗ COST-7.1): выбор запоминается,
/// второй счёт того же поставщика получает позицию сам, подстановка помечена и отменяется.
///
/// <para>У каждого теста свой поставщик: база у класса общая с соседями, а соответствия единственны
/// по поставщику — чужая запись под тем же наименованием сделала бы «запомнено 1» случайностью.</para>
/// </summary>
[Collection("Integration")]
public class SupplierMatchTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private readonly InvoiceLineHost host = host;

    [Fact]
    public async Task Второй_счёт_поставщика_получает_позицию_а_подстановка_помечена_и_отменяется()
    {
        var (client, _) = await SignInAsync("Supplier");
        var vendor = await VendorAsync();
        var position = await PositionAsync();

        // Первый счёт: человек выбрал позицию сам — пометки нет, выбор запомнен.
        var first = await InvoiceAsync(client, vendor);
        var saved = await LinesAsync(client, first, [Line(position, 10, 5m, text: "Кабель силовой 3х2,5")]);
        Assert.Equal(1, saved.GetProperty("memory").GetProperty("remembered").GetInt32());
        Assert.Equal(0, saved.GetProperty("memory").GetProperty("replaced").GetInt32());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("lines")[0].GetProperty("match").ValueKind);

        // Второй счёт: то же наименование, набранное иначе (регистр, пробелы), узнано.
        var offered = Assert.Single(await SuggestAsync(client, vendor, (null, "  кабель  СИЛОВОЙ 3х2,5 ")));
        Assert.Equal(position, offered.GetProperty("nomenclatureId").GetGuid());
        Assert.Equal("name", offered.GetProperty("by").GetString());
        Assert.Equal(JsonValueKind.Null, offered.GetProperty("issue").ValueKind);

        var second = await InvoiceAsync(client, vendor);
        var placed = await LinesAsync(client, second,
            [Marked(Line(position, 3, 5m, text: "кабель силовой 3х2,5"), offered.GetProperty("matchId").GetGuid())]);
        var line = placed.GetProperty("lines")[0];
        Assert.Equal("current", line.GetProperty("match").GetProperty("state").GetString());
        Assert.Equal("name", line.GetProperty("match").GetProperty("by").GetString());
        // Подставленное не запоминается заново: оно и есть запомненное.
        Assert.Equal(0, placed.GetProperty("memory").GetProperty("remembered").GetInt32());

        // Пометка ХРАНИТСЯ, а не живёт в ответе сохранения.
        var read = await ReadAsync(client, second);
        Assert.Equal("current", read.GetProperty("lines")[0].GetProperty("match").GetProperty("state").GetString());

        // Отмена: позиция и пометка сняты одним сохранением…
        var cancelled = await LinesAsync(client, second,
            [Line(null, 3, 5m, text: "кабель силовой 3х2,5", id: line.GetProperty("id").GetGuid())]);
        var bare = cancelled.GetProperty("lines")[0];
        Assert.Equal(JsonValueKind.Null, bare.GetProperty("nomenclatureId").ValueKind);
        Assert.Equal(JsonValueKind.Null, bare.GetProperty("match").ValueKind);

        // …а соответствие осталось (решение владельца от 09.10.2026: отмена снимает только строку).
        Assert.Single(await SuggestAsync(client, vendor, (null, "Кабель силовой 3х2,5")));
    }

    [Fact]
    public async Task Тот_же_набор_повторно_и_отказ_человека_ничего_не_запоминают()
    {
        var (client, _) = await SignInAsync("Supplier");
        var vendor = await VendorAsync();
        var position = await PositionAsync();
        var invoice = await InvoiceAsync(client, vendor);

        // «Только в этой строке»: выбор есть, запоминания нет.
        var row = Line(position, 1, 100m, text: "Доставка до объекта");
        row["remember"] = false;
        var saved = await LinesAsync(client, invoice, [row]);
        Assert.Equal(0, saved.GetProperty("memory").GetProperty("remembered").GetInt32());
        Assert.Empty(await SuggestAsync(client, vendor, (null, "Доставка до объекта")));

        // Форма присылает строки целиком на каждое сохранение — уже без слова «не запоминать». Строка
        // не изменилась, значит и новости нет: иначе отказ действовал бы до первого же сохранения.
        var again = await LinesAsync(client, invoice,
            [Line(position, 1, 100m, text: "Доставка до объекта", id: LineId(saved, 1))]);
        Assert.Equal(0, again.GetProperty("memory").GetProperty("remembered").GetInt32());
        Assert.Empty(await SuggestAsync(client, vendor, (null, "Доставка до объекта")));
    }

    [Fact]
    public async Task Другая_позиция_заменяет_запомненное_и_помеченная_строка_об_этом_узнаёт()
    {
        var (client, _) = await SignInAsync("Supplier");
        var vendor = await VendorAsync();
        var (old, fresh) = (await PositionAsync(), await PositionAsync());
        var before = await CountAsync(InvoiceActions.MatchesRemembered);

        await LinesAsync(client, await InvoiceAsync(client, vendor), [Line(old, 1, 1m, text: "Автомат С16")]);
        var offered = Assert.Single(await SuggestAsync(client, vendor, (null, "Автомат С16")));

        // Счёт с подстановкой — он и узнает о замене.
        var marked = await InvoiceAsync(client, vendor);
        await LinesAsync(client, marked,
            [Marked(Line(old, 1, 1m, text: "Автомат С16"), offered.GetProperty("matchId").GetGuid())]);

        // Человек ставит той же строке другую позицию — запомненное заменено, и об этом сказано.
        var replaced = await LinesAsync(client, await InvoiceAsync(client, vendor),
            [Line(fresh, 1, 1m, text: "Автомат С16")]);
        Assert.Equal(1, replaced.GetProperty("memory").GetProperty("remembered").GetInt32());
        Assert.Equal(1, replaced.GetProperty("memory").GetProperty("replaced").GetInt32());

        var now = Assert.Single(await SuggestAsync(client, vendor, (null, "Автомат С16")));
        Assert.Equal(fresh, now.GetProperty("nomenclatureId").GetGuid());

        // Строку чужого счёта замена не переписала — она говорит, что соответствие ушло на другое.
        var stale = (await ReadAsync(client, marked)).GetProperty("lines")[0];
        Assert.Equal(old, stale.GetProperty("nomenclatureId").GetGuid());
        Assert.Equal("changed", stale.GetProperty("match").GetProperty("state").GetString());

        // Журнал: запись на сохранение, которое запомнило, — и ни одной на то, которое подставило.
        Assert.Equal(before + 2, await CountAsync(InvoiceActions.MatchesRemembered));
    }

    [Fact]
    public async Task Пометка_которая_не_подтверждается_отвергается()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (vendor, stranger) = (await VendorAsync(), await VendorAsync());
        var (position, other) = (await PositionAsync(), await PositionAsync());

        await LinesAsync(client, await InvoiceAsync(client, vendor), [Line(position, 1, 1m, text: "Короб 40х25")]);
        await LinesAsync(client, await InvoiceAsync(client, stranger), [Line(position, 1, 1m, text: "Короб 40х25")]);
        var own = Assert.Single(await SuggestAsync(client, vendor, (null, "Короб 40х25"))).GetProperty("matchId").GetGuid();
        var foreign = Assert.Single(await SuggestAsync(client, stranger, (null, "Короб 40х25"))).GetProperty("matchId").GetGuid();

        var invoice = await InvoiceAsync(client, vendor);

        // Соответствия нет вовсе; оно чужого поставщика; оно ведёт на другую позицию; оно не узнаёт
        // эту строку; пометка без позиции.
        await RefusedAsync(client, invoice, Marked(Line(position, 1, 1m, text: "Короб 40х25"), Guid.NewGuid()), "нет");
        await RefusedAsync(client, invoice, Marked(Line(position, 1, 1m, text: "Короб 40х25"), foreign), "другого поставщика");
        await RefusedAsync(client, invoice, Marked(Line(other, 1, 1m, text: "Короб 40х25"), own), "другую позицию");
        await RefusedAsync(client, invoice, Marked(Line(position, 1, 1m, text: "Короб 60х40"), own), "не узнаёт");
        await RefusedAsync(client, invoice, Marked(Line(null, 1, 1m, text: "Короб 40х25"), own), "без позиции");

        // Верная пометка проходит — отказы выше не про адрес вообще.
        var placed = await LinesAsync(client, invoice, [Marked(Line(position, 1, 1m, text: "Короб 40х25"), own)]);
        Assert.Equal("current", placed.GetProperty("lines")[0].GetProperty("match").GetProperty("state").GetString());

        // Стоявшая пометка перепроверяется, когда строку ПЕРЕПИСАЛИ: «Короб» стал «Доставкой», и память
        // о коробе к ней не относится. Та же строка без правки ключа сохраняется как была.
        var line = LineId(placed, 1);
        await RefusedAsync(client, invoice, Marked(Line(position, 1, 1m, text: "Доставка", id: line), own), "не узнаёт");
        await LinesAsync(client, invoice, [Marked(Line(position, 2, 1m, text: "КОРОБ  40х25", id: line), own)]);
    }

    [Fact]
    public async Task Архивная_позиция_стоявшая_в_строке_сменой_наименования_не_запоминается()
    {
        var (client, _) = await SignInAsync("Supplier");
        var vendor = await VendorAsync();
        var position = await PositionAsync();
        var invoice = await InvoiceAsync(client, vendor);

        // Выбор без запоминания, затем позиция уходит в архив: стоявшая ссылка законна.
        var row = Line(position, 1, 1m, text: "Лоток 100х50");
        row["remember"] = false;
        var saved = await LinesAsync(client, invoice, [row]);
        await ArchiveAsync(position);

        // Правка опечатки меняет ключ — но запоминать архивную позицию нельзя: это новая ссылка.
        var again = await LinesAsync(client, invoice, [Line(position, 1, 1m, text: "Лоток 100х50 мм", id: LineId(saved, 1))]);

        Assert.Equal(0, again.GetProperty("memory").GetProperty("remembered").GetInt32());
        Assert.Empty(await SuggestAsync(client, vendor, (null, "Лоток 100х50 мм")));
    }

    [Fact]
    public async Task Артикул_старше_наименования_и_ключ_у_строки_один()
    {
        var (client, _) = await SignInAsync("Supplier");
        var vendor = await VendorAsync();
        var position = await PositionAsync();

        var row = Line(position, 1, 1m, text: "Розетка двойная белая");
        row["supplierCode"] = "RZ-2W";
        await LinesAsync(client, await InvoiceAsync(client, vendor), [row]);

        // По артикулу узнаётся, как бы строку ни назвали.
        var byCode = Assert.Single(await SuggestAsync(client, vendor, ("rz-2w", "что угодно")));
        Assert.Equal("code", byCode.GetProperty("by").GetString());

        // А по одному наименованию — нет: запомнен ровно один ключ, иначе правка одного соответствия
        // оставляла бы второе подставлять прежнее.
        Assert.Empty(await SuggestAsync(client, vendor, (null, "Розетка двойная белая")));
    }

    [Fact]
    public async Task Спор_внутри_счёта_не_запоминается()
    {
        var (client, _) = await SignInAsync("Supplier");
        var vendor = await VendorAsync();
        var (one, two) = (await PositionAsync(), await PositionAsync());

        var saved = await LinesAsync(client, await InvoiceAsync(client, vendor),
            [Line(one, 1, 1m, text: "Кабель"), Line(two, 1, 1m, text: "Кабель")]);

        Assert.Equal(0, saved.GetProperty("memory").GetProperty("remembered").GetInt32());
        Assert.Empty(await SuggestAsync(client, vendor, (null, "Кабель")));
    }

    [Fact]
    public async Task Архивная_позиция_не_подставляется_и_это_названо()
    {
        var (client, _) = await SignInAsync("Supplier");
        var vendor = await VendorAsync();
        var position = await PositionAsync();
        await LinesAsync(client, await InvoiceAsync(client, vendor), [Line(position, 1, 1m, text: "Щит ЩРН-12")]);

        await ArchiveAsync(position);

        // Соответствие в ответе ЕСТЬ, с причиной: молчание читалось бы как «строка незнакома».
        var offered = Assert.Single(await SuggestAsync(client, vendor, (null, "Щит ЩРН-12")));
        Assert.Equal("archived", offered.GetProperty("issue").GetString());

        // И пометкой архивную позицию в новую строку не пронести: правило новой ссылки то же.
        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{await InvoiceAsync(client, vendor)}/lines",
            new { lines = new[] { Marked(Line(position, 1, 1m, text: "Щит ЩРН-12"), offered.GetProperty("matchId").GetGuid()) } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Сторож задачи: сопоставление номенклатуру НЕ создаёт. Незнакомое наименование после запоминания
    /// не становится позицией справочника — ни молча, ни «заодно».
    /// </summary>
    [Fact]
    public async Task Запоминание_позицию_номенклатуры_не_создаёт()
    {
        var (client, _) = await SignInAsync("Supplier");
        var vendor = await VendorAsync();
        var position = await PositionAsync();
        var text = $"Наименование поставщика {Guid.NewGuid():N}";

        var saved = await LinesAsync(client, await InvoiceAsync(client, vendor), [Line(position, 1, 1m, text: text)]);
        Assert.Equal(1, saved.GetProperty("memory").GetProperty("remembered").GetInt32());

        var found = await client.GetFromJsonAsync<JsonElement>($"/api/costs/nomenclature?query={Uri.EscapeDataString(text)}");
        Assert.Empty(found.GetProperty("items").EnumerateArray());

        // И заводить номенклатуру модулю нечем: в перечне типов, которые он вправе заводить, её нет.
        Assert.DoesNotContain(new CostsModule().IntakeTypes, t => t.TypeCode == CostsRecordTypes.NomenclatureCode);
    }

    /// <summary>
    /// Сторож задачи: сопоставление — под <c>costs.invoice.edit</c>, а не под правом номенклатуры.
    ///
    /// <para>⚠️ Роль собрана нарочно: право номенклатуры и ЧТЕНИЕ счетов, без правки. Системной роли с
    /// таким составом нет, а сметчик (у него право номенклатуры есть) сторожем не годится — его отсекает
    /// политика модуля раньше, чем дело доходит до права: адрес, отданный под право номенклатуры,
    /// отвечал бы ему тем же 403 (проверено поломкой).</para>
    /// </summary>
    [Fact]
    public async Task Право_номенклатуры_сопоставление_не_открывает()
    {
        var vendor = await VendorAsync();
        var body = new { supplierId = vendor, lines = new[] { new { supplierText = "Кабель" } } };

        var role = $"Справочник-{Guid.NewGuid():N}"[..24];
        using (var scope = host.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var created = new IdentityRole<Guid>(role);
            Assert.True((await roles.CreateAsync(created)).Succeeded);
            foreach (var code in new[] { "core.nomenclature.edit", "core.catalog.read", "costs.invoice.read" })
                Assert.True((await roles.AddClaimAsync(created, new Claim(RoleSynchronizer.PermissionClaim, code))).Succeeded);
        }

        var (keeper, _) = await SignInAsync(role);
        // Модуль ему открыт — иначе 403 ниже был бы про модуль, а не про право.
        Assert.Equal(HttpStatusCode.OK, (await keeper.GetAsync("/api/costs/nomenclature?query=")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await keeper.PostAsJsonAsync("/api/costs/supplier-matches/suggestions", body)).StatusCode);

        // Читающему счета — тоже: подставить ему некуда.
        var (accountant, _) = await SignInAsync("Accountant");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await accountant.PostAsJsonAsync("/api/costs/supplier-matches/suggestions", body)).StatusCode);

        var (buyer, _) = await SignInAsync("Supplier");
        Assert.Equal(HttpStatusCode.OK,
            (await buyer.PostAsJsonAsync("/api/costs/supplier-matches/suggestions", body)).StatusCode);
    }

    [Fact]
    public async Task Два_счёта_разом_запоминают_одну_строку_одной_записью()
    {
        var (client, _) = await SignInAsync("Supplier");
        var vendor = await VendorAsync();
        var position = await PositionAsync();
        var (left, right) = (await InvoiceAsync(client, vendor), await InvoiceAsync(client, vendor));

        // Замок записи — по счёту, а таблица соответствий общая: оба сохранения добавляют одну строку.
        var both = await Task.WhenAll(
            client.PutAsJsonAsync($"/api/costs/invoices/{left}/lines", new { lines = new[] { Line(position, 1, 1m, text: "Гофра 20") } }),
            client.PutAsJsonAsync($"/api/costs/invoices/{right}/lines", new { lines = new[] { Line(position, 1, 1m, text: "Гофра 20") } }));

        Assert.All(both, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Single(await SuggestAsync(client, vendor, (null, "Гофра 20")));
    }

    private async Task ArchiveAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        Assert.Equal(ArchiveOutcome.Changed,
            await scope.ServiceProvider.GetRequiredService<IRecordArchive>().SetAsync(id, archived: true));
    }

    private static Dictionary<string, object?> Marked(Dictionary<string, object?> line, Guid match)
    {
        line["matchedBy"] = match.ToString();
        return line;
    }

    private static async Task RefusedAsync(HttpClient client, Guid invoice, Dictionary<string, object?> line, string because)
    {
        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/lines", new { lines = new[] { line } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(because, await response.Content.ReadAsStringAsync());
    }

    private static async Task<List<JsonElement>> SuggestAsync(
        HttpClient client, Guid vendor, params (string? Code, string? Text)[] lines)
    {
        var response = await client.PostAsJsonAsync("/api/costs/supplier-matches/suggestions", new
        {
            supplierId = vendor,
            lines = lines.Select(l => new { supplierCode = l.Code, supplierText = l.Text }),
        });
        await OkAsync(response);
        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray()];
    }

    private static async Task<Guid> InvoiceAsync(HttpClient client, Guid vendor)
    {
        var requisites = new Dictionary<string, object?>
        {
            ["Номер"] = $"СЧ-{Guid.NewGuid().ToString()[..6]}",
            ["Дата"] = "2026-09-29",
            ["Поставщик"] = Reference(vendor),
        };

        var response = await client.PostAsJsonAsync("/api/costs/invoices", new { requisites });
        await OkAsync(response);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<Guid> VendorAsync() =>
        await EntryAsync(await TypeAsync(CostsRecordTypes.OrganizationCode, "Организация"),
            $"ООО «Поставщик {Guid.NewGuid().ToString("N")[..6]}»");

    private async Task<Guid> PositionAsync() =>
        await EntryAsync(await TypeAsync(CostsRecordTypes.NomenclatureCode, "Номенклатура"),
            $"Позиция {Guid.NewGuid().ToString("N")[..6]}");

    private async Task<int> CountAsync(BHS.CRG.Modules.Ports.ModuleActivityAction action)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<BHS.CRG.Application.Activity.IActivityLog>()
            .CountAsync(BHS.CRG.Application.Activity.ActivityVisibility.Whole, action.Code);
    }
}
