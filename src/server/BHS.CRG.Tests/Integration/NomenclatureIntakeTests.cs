using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Costs;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Новая позиция номенклатуры коротким окном (задача C3, issue #1079, ТЗ COST-7.1, TYPE-8): дверь ядра
/// под <c>core.nomenclature.edit</c>.
///
/// <para>Тип «Номенклатура» у этого хоста — с ПУСТОЙ схемой (его заводит посев соседей), и это
/// отдельный случай: вид без ключа идентичности завести нельзя. Годный вид — подтип со схемой, какая
/// лежит у заказчика: наименование, производитель и артикул в ключе, обязательная единица измерения.</para>
///
/// <para>База у класса общая с соседями: названия и артикулы у каждого теста свои.</para>
/// </summary>
[Collection("Integration")]
public class NomenclatureIntakeTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private readonly InvoiceLineHost host = host;

    private const string KindSchema = """
        {"fields":[
          {"key":"Группа","type":"string","title":"Группа","required":false},
          {"key":"Наименование","type":"string","title":"Наименование","required":true,"tags":["identity:1"]},
          {"key":"Артикул","type":"string","title":"Артикул","required":false,"tags":["identity:3"]},
          {"key":"Производитель","type":"string","title":"Производитель","required":false,"tags":["identity:2"]},
          {"key":"ЕдиницаИзмерения","type":"complex","title":"Единица измерения","required":true,"typeId":"UNIT"},
          {"key":"Изображение","type":"image","title":"Изображение","required":false}
        ]}
        """;

    [Fact]
    public async Task Описание_называет_что_спросить_и_какой_вид_отсюда_не_завести()
    {
        var (client, _) = await SignInAsync("Supplier");
        var kind = await KindAsync();
        var counted = await CountedKindAsync();

        var kinds = (await client.GetFromJsonAsync<JsonElement>("/api/nomenclature/intake")).GetProperty("kinds");

        // Годный вид: ключ — в порядке КЛЮЧА (наименование, производитель, артикул), а не схемы;
        // необязательная «Группа» и картинка не спрашиваются; единица — выбором, с записями.
        var good = kinds.EnumerateArray().Single(k => k.GetProperty("typeId").GetGuid() == kind.Type);
        Assert.Empty(good.GetProperty("refusals").EnumerateArray());
        Assert.Equal(["Наименование", "Производитель", "Артикул", "ЕдиницаИзмерения"],
            good.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("key").GetString()));
        var unit = good.GetProperty("fields").EnumerateArray().Last();
        Assert.Contains(unit.GetProperty("options").EnumerateArray(), o => o.GetProperty("id").GetGuid() == kind.Unit);
        Assert.Equal(JsonValueKind.Null, good.GetProperty("fields")[0].GetProperty("options").ValueKind);

        // Обязательное число — свойство строки: спросить его у справочника нечем, и вид назван негодным.
        var bad = kinds.EnumerateArray().Single(k => k.GetProperty("typeId").GetGuid() == counted);
        Assert.Contains("«Количество»", bad.GetProperty("refusals")[0].GetString());

        // Корень семейства у этого хоста — без схемы: сверить позицию не с чем.
        var root = kinds.EnumerateArray().Single(k => k.GetProperty("code").GetString() == CostsRecordTypes.NomenclatureCode);
        Assert.Contains("Идентификатор", root.GetProperty("refusals")[0].GetString());
    }

    [Fact]
    public async Task Позиция_заводится_на_уровне_системы_с_названием_из_ключа_и_находится_в_выборе()
    {
        var (client, _) = await SignInAsync("Supplier");
        var kind = await KindAsync();
        var name = Unique("Кабель силовой");
        var before = await JournalAsync();

        var response = await client.PostAsJsonAsync("/api/nomenclature", new
        {
            typeId = kind.Type,
            values = new Dictionary<string, string> { ["Наименование"] = $"  {name}  ", ["Артикул"] = "RZ-2W" },
            refs = new Dictionary<string, Guid> { ["ЕдиницаИзмерения"] = kind.Unit },
        });
        await OkAsync(response);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(name, created.GetProperty("name").GetString());

        using (var scope = host.Services.CreateScope())
        {
            var record = await scope.ServiceProvider.GetRequiredService<AppDbContext>().DomainObjects
                .AsNoTracking().SingleAsync(o => o.Id == created.GetProperty("id").GetGuid());
            Assert.Equal(CatalogScope.System, record.ScopeLevel);
            Assert.Equal(kind.Type, record.CompositeTypeId);
            var data = record.Data.RootElement;
            Assert.Equal(name, data.GetProperty("Наименование").GetString());
            Assert.Equal("RZ-2W", data.GetProperty("Артикул").GetString());
            // Незаполненное поле ключа не пишется пустой строкой — его в данных нет.
            Assert.False(data.TryGetProperty("Производитель", out _));
            Assert.Equal(kind.Unit, data.GetProperty("ЕдиницаИзмерения").GetProperty("entryId").GetGuid());
            Assert.Equal("catalog", data.GetProperty("ЕдиницаИзмерения").GetProperty("$ref").GetString());
        }

        Assert.Equal(before + 1, await JournalAsync());

        // Ради этого и заводили: позиция выбирается в строке счёта.
        var found = await client.GetFromJsonAsync<JsonElement>(
            $"/api/costs/nomenclature?query={Uri.EscapeDataString(name)}");
        Assert.Contains(found.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("id").GetGuid() == created.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Вторая_позиция_с_тем_же_ключом_не_заводится_и_лежащая_названа()
    {
        var (client, _) = await SignInAsync("Supplier");
        var kind = await KindAsync();
        var name = Unique("Труба ПНД");
        object Body(string named) => new
        {
            typeId = kind.Type,
            values = new Dictionary<string, string> { ["Наименование"] = named },
            refs = new Dictionary<string, Guid> { ["ЕдиницаИзмерения"] = kind.Unit },
        };

        var first = await client.PostAsJsonAsync("/api/nomenclature", Body(name));
        await OkAsync(first);
        var id = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Производитель и артикул пусты у обеих — это ТОТ ЖЕ ключ, хотя резолвер ядра позицию с пустым
        // полем ключа не сопоставляет вовсе. Регистр и лишние пробелы ключа не меняют.
        var second = await client.PostAsJsonAsync("/api/nomenclature", Body($" {name.ToUpperInvariant()} "));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var refusal = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exists", refusal.GetProperty("code").GetString());
        Assert.Equal(id, refusal.GetProperty("existing").GetProperty("id").GetGuid());
        Assert.False(refusal.GetProperty("existing").GetProperty("archived").GetBoolean());
        Assert.Equal(1, await CountAsync(name));

        // Архивная — тоже двойник, и сказано, что она в архиве: «не найдена» читалось бы как «заводите».
        await SqlAsync("""UPDATE domain_objects SET "ArchivedAt" = now() WHERE "Id" = {0}""", id);
        var third = await client.PostAsJsonAsync("/api/nomenclature", Body(name));
        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
        Assert.True((await third.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("existing").GetProperty("archived").GetBoolean());
        Assert.Equal(1, await CountAsync(name));
    }

    [Fact]
    public async Task Два_нажатия_разом_заводят_одну_позицию()
    {
        var (client, _) = await SignInAsync("Supplier");
        var kind = await KindAsync();
        var name = Unique("Лоток перфорированный");
        var body = new
        {
            typeId = kind.Type,
            values = new Dictionary<string, string> { ["Наименование"] = name },
            refs = new Dictionary<string, Guid> { ["ЕдиницаИзмерения"] = kind.Unit },
        };

        var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.PostAsJsonAsync("/api/nomenclature", body)));

        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
        Assert.Equal(3, answers.Count(a => a.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await CountAsync(name));
    }

    [Fact]
    public async Task Похожие_находят_тот_же_артикул_и_слова_названия_а_общий_производитель_не_сходство()
    {
        var (client, _) = await SignInAsync("Supplier");
        var kind = await KindAsync();
        var mark = Guid.NewGuid().ToString("N")[..6];
        var maker = $"Завод-{mark}";

        var twin = await PositionAsync(kind, $"Автомат {mark} C16 однополюсный", maker, $"ART-{mark}");
        var wordy = await PositionAsync(kind, $"Щит {mark} распределительный навесной", maker, null);
        // Производитель общий для двенадцати позиций: он позицию не отличает.
        for (var i = 0; i < 10; i++) await PositionAsync(kind, $"Прочее {mark} №{i}", maker, null);

        var answer = await SimilarAsync(client, kind, new()
        {
            ["Наименование"] = $"Выключатель {mark} автоматический", ["Производитель"] = maker, ["Артикул"] = $"art-{mark}",
        });
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("exact").ValueKind);
        var hit = Assert.Single(answer.GetProperty("similar").EnumerateArray());
        Assert.Equal(twin, hit.GetProperty("position").GetProperty("id").GetGuid());
        Assert.Contains("артикул", hit.GetProperty("why").GetString());

        // Слова названия: два из трёх набранных есть у лежащей.
        var words = await SimilarAsync(client, kind, new() { ["Наименование"] = $"Щит {mark} навесной" });
        Assert.Equal(wordy, Assert.Single(words.GetProperty("similar").EnumerateArray())
            .GetProperty("position").GetProperty("id").GetGuid());

        // Тот же ключ целиком — «такая уже есть», и в похожих она не повторяется.
        var same = await SimilarAsync(client, kind, new()
        {
            ["Наименование"] = $"Автомат {mark} C16 однополюсный", ["Производитель"] = maker, ["Артикул"] = $"ART-{mark}",
        });
        Assert.Equal(twin, same.GetProperty("exact").GetProperty("id").GetGuid());
        Assert.DoesNotContain(same.GetProperty("similar").EnumerateArray(),
            h => h.GetProperty("position").GetProperty("id").GetGuid() == twin);
    }

    [Fact]
    public async Task Негодный_запрос_получает_отказ_с_причиной_и_ничего_не_заводит()
    {
        var (client, _) = await SignInAsync("Supplier");
        var kind = await KindAsync();
        var name = Unique("Короб");
        async Task<string> RefusedAsync(HttpStatusCode status, object body)
        {
            var response = await client.PostAsJsonAsync("/api/nomenclature", body);
            Assert.Equal(status, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }
        var unit = new Dictionary<string, Guid> { ["ЕдиницаИзмерения"] = kind.Unit };

        // Обязательная единица не выбрана.
        Assert.Contains("Единица измерения", await RefusedAsync(HttpStatusCode.BadRequest, new
        {
            typeId = kind.Type, values = new Dictionary<string, string> { ["Наименование"] = name },
        }));
        // Названия нет.
        Assert.Contains("Наименование", await RefusedAsync(HttpStatusCode.BadRequest, new
        {
            typeId = kind.Type, values = new Dictionary<string, string> { ["Артикул"] = "X-1" }, refs = unit,
        }));
        // Поле мимо описания: «Группу» окно не спрашивает, и молча принять её нельзя.
        Assert.Contains("«Группа»", await RefusedAsync(HttpStatusCode.BadRequest, new
        {
            typeId = kind.Type, refs = unit,
            values = new Dictionary<string, string> { ["Наименование"] = name, ["Группа"] = "Кабель" },
        }));
        // Единицей названа запись не из выбора — позиция номенклатуры.
        Assert.Contains("нет среди действующих", await RefusedAsync(HttpStatusCode.BadRequest, new
        {
            typeId = kind.Type, values = new Dictionary<string, string> { ["Наименование"] = name },
            refs = new Dictionary<string, Guid> { ["ЕдиницаИзмерения"] = cable },
        }));
        // Вид, который отсюда не завести.
        Assert.Contains("«Количество»", await RefusedAsync(HttpStatusCode.BadRequest, new
        {
            typeId = await CountedKindAsync(), values = new Dictionary<string, string> { ["Наименование"] = name },
        }));
        // Тип не из семейства номенклатуры: дверь открывает один справочник, а не все.
        await RefusedAsync(HttpStatusCode.NotFound, new
        {
            typeId = await TypeAsync(CostsRecordTypes.OrganizationCode, "Организация"),
            values = new Dictionary<string, string> { ["Наименование"] = name },
        });

        Assert.Equal(0, await CountAsync(name));
    }

    [Fact]
    public async Task Архивная_единица_на_выбор_не_предлагается_и_не_принимается()
    {
        var (client, _) = await SignInAsync("Supplier");
        var kind = await KindAsync();
        var retired = await EntryAsync(await TypeAsync(CoreRecordTypes.UnitCode, "Единица измерения"), Unique("ед"));
        await SqlAsync("""UPDATE domain_objects SET "ArchivedAt" = now() WHERE "Id" = {0}""", retired);

        var kinds = (await client.GetFromJsonAsync<JsonElement>("/api/nomenclature/intake")).GetProperty("kinds");
        var options = kinds.EnumerateArray().Single(k => k.GetProperty("typeId").GetGuid() == kind.Type)
            .GetProperty("fields").EnumerateArray().Last().GetProperty("options");
        Assert.Contains(options.EnumerateArray(), o => o.GetProperty("id").GetGuid() == kind.Unit);
        Assert.DoesNotContain(options.EnumerateArray(), o => o.GetProperty("id").GetGuid() == retired);

        // Присланная мимо окна — отказ: новая ссылка на архивную запись не ставится (CORE-34.4).
        var name = Unique("Муфта");
        var response = await client.PostAsJsonAsync("/api/nomenclature", new
        {
            typeId = kind.Type, values = new Dictionary<string, string> { ["Наименование"] = name },
            refs = new Dictionary<string, Guid> { ["ЕдиницаИзмерения"] = retired },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountAsync(name));
    }

    [Fact]
    public async Task Дверь_открывает_право_номенклатуры_а_право_на_счета_не_открывает()
    {
        var kind = await KindAsync();
        var body = new { typeId = kind.Type, values = new Dictionary<string, string> { ["Наименование"] = Unique("Гильза") } };

        // Тот, кто вводит счета, без права номенклатуры: ни описания, ни похожих, ни создания.
        var (buyer, _) = await SignInAsync(await RoleAsync("costs.invoice.read", "costs.invoice.edit", "core.catalog.read"));
        Assert.Equal(HttpStatusCode.OK, (await buyer.GetAsync("/api/costs/nomenclature?query=")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await buyer.GetAsync("/api/nomenclature/intake")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await buyer.PostAsJsonAsync("/api/nomenclature/similar", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await buyer.PostAsJsonAsync("/api/nomenclature", body)).StatusCode);

        // И всё право каталога дверь тоже не открывает: это другое право.
        var (editor, _) = await SignInAsync(await RoleAsync("core.catalog.read", "core.catalog.edit"));
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync("/api/nomenclature/intake")).StatusCode);

        // Одно право номенклатуры — без чтения каталога и без модуля счетов: окно собирается из
        // описания двери, и других прав ему не нужно.
        var (keeper, _) = await SignInAsync(await RoleAsync("core.nomenclature.edit"));
        var kinds = await keeper.GetFromJsonAsync<JsonElement>("/api/nomenclature/intake");
        Assert.Contains(kinds.GetProperty("kinds").EnumerateArray(), k => k.GetProperty("typeId").GetGuid() == kind.Type);
        Assert.Equal(HttpStatusCode.OK, (await keeper.PostAsJsonAsync("/api/nomenclature/similar", body)).StatusCode);
    }

    // ── помощники ────────────────────────────────────────────────────────────

    private static string Unique(string name) => $"{name} {Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>Годный вид — подтип «Номенклатуры» со схемой заказчика — и единица измерения на выбор.</summary>
    private async Task<(Guid Type, Guid Unit)> KindAsync()
    {
        var unitType = await TypeAsync(CoreRecordTypes.UnitCode, "Единица измерения");
        var type = await SubtypeAsync("ПозицияСКлючом", "Позиция с ключом", KindSchema.Replace("UNIT", unitType.ToString()));

        using var scope = host.Services.CreateScope();
        var units = await scope.ServiceProvider.GetRequiredService<IMediator>()
            .Send(new SearchCommonDataForChoiceQuery([unitType], "шт-приём"));
        return (type, units.Items.Count > 0 ? units.Items[0].Id : await EntryAsync(unitType, "шт-приём"));
    }

    /// <summary>Подтип с обязательным числом — так устроен «Материал» у заказчика.</summary>
    private Task<Guid> CountedKindAsync() => SubtypeAsync("ПозицияСКоличеством", "Позиция с количеством", """
        {"fields":[
          {"key":"Наименование","type":"string","title":"Наименование","required":true,"tags":["identity:1"]},
          {"key":"Количество","type":"number","title":"Количество","required":true}
        ]}
        """);

    private async Task<Guid> SubtypeAsync(string code, string name, string schema)
    {
        var root = await TypeAsync(CostsRecordTypes.NomenclatureCode, "Номенклатура");
        using var scope = host.Services.CreateScope();
        var types = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();

        var found = await types.FindAsync(t => t.Code == code);
        if (found.Count > 0) return found[0].Id;

        var created = DocumentType.Create(name, code, DocumentTypeKind.Composite, root,
            JsonDocument.Parse(schema), TypeOwner.Core, TypeVisibility.Shared);
        await types.AddAsync(created);
        await types.SaveChangesAsync();
        return created.Id;
    }

    /// <summary>Лежащая позиция с полями ключа — как её завела бы форма справочника.</summary>
    private async Task<Guid> PositionAsync((Guid Type, Guid Unit) kind, string name, string? maker, string? article)
    {
        var data = new Dictionary<string, string?> { ["Наименование"] = name };
        if (maker is not null) data["Производитель"] = maker;
        if (article is not null) data["Артикул"] = article;

        using var scope = host.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new CreateCommonDataEntryCommand(
            name, kind.Type, JsonSerializer.SerializeToDocument(data), CatalogScope.System, null))).Id;
    }

    private static async Task<JsonElement> SimilarAsync(
        HttpClient client, (Guid Type, Guid Unit) kind, Dictionary<string, string> values)
    {
        var response = await client.PostAsJsonAsync("/api/nomenclature/similar", new { typeId = kind.Type, values });
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string> RoleAsync(params string[] permissions)
    {
        var role = $"Приём-{Guid.NewGuid():N}"[..24];
        using var scope = host.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var created = new IdentityRole<Guid>(role);
        Assert.True((await roles.CreateAsync(created)).Succeeded);
        foreach (var code in permissions)
            Assert.True((await roles.AddClaimAsync(created, new Claim(RoleSynchronizer.PermissionClaim, code))).Succeeded);
        return role;
    }

    private async Task<int> CountAsync(string name)
    {
        using var scope = host.Services.CreateScope();
        var lowered = name.ToLowerInvariant();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().DomainObjects
            .CountAsync(o => o.DisplayName != null && o.DisplayName.ToLower() == lowered);
    }

    private async Task<int> JournalAsync()
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IActivityLog>()
            .CountAsync(ActivityVisibility.Whole, ActivityActions.NomenclatureCreated.Code);
    }

    private async Task SqlAsync(string sql, params object[] args)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync(sql, args);
    }
}
