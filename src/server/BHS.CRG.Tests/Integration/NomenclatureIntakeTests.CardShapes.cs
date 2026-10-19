using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Карточка (issue #1516, ревью PR #1543): каждая форма поля — своим видом строки. Перечисление — подписью,
/// булево — словом, ссылка на архивную — с признаком, на удалённую — «не прочитано», таблица — числом, ключ с
/// картинкой — строкой «скрыто», не-скаляр в простом поле — «не прочитано», уровень области назван.
/// </summary>
public partial class NomenclatureIntakeTests
{
    [Fact]
    public async Task Карточка_показывает_каждую_форму_поля_своим_видом()
    {
        var unitType = await TypeAsync(CoreRecordTypes.UnitCode, "Единица измерения");
        var protection = await EnumAsync("Защита оболочки", """[{"code":"IP44","label":"Брызгозащищённое"}]""");
        var type = await SubtypeAsync("ПозицияСФормами", "Позиция с формами полей", $$"""
            {"fields":[
              {"key":"Наименование","type":"string","title":"Наименование","required":true,"tags":["identity:1"]},
              {"key":"Фото","type":"image","title":"Фото","tags":["identity:2"]},
              {"key":"Исполнение","type":"enum","title":"Исполнение","typeId":"{{protection}}"},
              {"key":"Уличный","type":"boolean","title":"Уличный"},
              {"key":"Сертификаты","type":"array","title":"Сертификаты","typeId":"{{unitType}}"},
              {"key":"Упаковка","type":"complex","title":"Упаковка","typeId":"{{unitType}}"},
              {"key":"Замена","type":"complex","title":"Замена","typeId":"{{unitType}}"},
              {"key":"Примечание","type":"string","title":"Примечание"}
            ]}
            """);
        var retired = await EntryAsync(unitType, Unique("Коробка"));
        var (site, _) = await SiteAsync("Карточка: область");

        var name = Unique("Светильник уличный");
        Guid id;
        using (var scope = host.Services.CreateScope())
            id = (await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new CreateCommonDataEntryCommand(
                name, type, JsonDocument.Parse($$"""
                {"Наименование":"{{name}}","Фото":"data:image/png;base64,iVBORw0KGgo=","Исполнение":"IP44",
                 "Уличный":true,"Сертификаты":[{"a":1},{"a":2}],
                 "Упаковка":{"$ref":"catalog","entryId":"{{retired}}"},
                 "Замена":{"$ref":"catalog","entryId":"{{Guid.NewGuid()}}"},
                 "Примечание":{"не":"скаляр"} }
                """), CatalogScope.Construction, site))).Id;

        // В архив — после: новую ссылку на архивную запись охрана записи не пропускает, а стоявшая остаётся.
        await SqlAsync("""UPDATE domain_objects SET "ArchivedAt" = now() WHERE "Id" = {0}""", retired);

        var (client, _) = await SignInAsync("Supplier");
        var response = await client.GetAsync($"/api/costs/nomenclature/{id}/card");
        await OkAsync(response);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("data:", text);
        var card = JsonDocument.Parse(text).RootElement;
        var lines = card.GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("title").GetString()!);

        Assert.Equal("hidden", lines["Фото"].GetProperty("kind").GetString());
        Assert.True(lines["Фото"].GetProperty("identity").GetBoolean());
        Assert.Equal("Брызгозащищённое", lines["Исполнение"].GetProperty("text").GetString());
        Assert.Equal("choice", lines["Исполнение"].GetProperty("kind").GetString());
        Assert.Equal("да", lines["Уличный"].GetProperty("text").GetString());
        Assert.True(lines["Упаковка"].GetProperty("archived").GetBoolean());
        Assert.StartsWith("Коробка", lines["Упаковка"].GetProperty("text").GetString());
        Assert.True(lines["Замена"].GetProperty("unreadable").GetBoolean());
        Assert.True(lines["Примечание"].GetProperty("unreadable").GetBoolean());

        Assert.Equal(["Сертификаты (2)"], card.GetProperty("notShown").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal("construction", card.GetProperty("scope").GetString());

        // Содержимое непрозрачных полей из базы не читается вовсе — приходит признаком и числом.
        using (var scope = host.Services.CreateScope())
        {
            var chain = await scope.ServiceProvider.GetRequiredService<IDomainObjectRepository>()
                .CardChainAsync(id, ["Фото", "Сертификаты", "Упаковка"]);
            var data = Assert.Single(chain!.Links).Data;
            Assert.Equal(JsonValueKind.True, data.GetProperty("Фото").ValueKind);
            Assert.Equal(2, data.GetProperty("Сертификаты").GetInt32());
            Assert.Equal(retired.ToString(), data.GetProperty("Упаковка").GetProperty("entryId").GetString());
            Assert.Equal(JsonValueKind.Object, data.GetProperty("Примечание").ValueKind);
        }
    }

    /// <summary>
    /// Неназначенный справочник: и карточка, и названия по идентификатору — ноль. На втором адрес и отличает
    /// «справочник не назначен» (409) от «позицию удалили» (404).
    /// </summary>
    [Fact]
    public async Task Неназначенный_справочник_карточка_и_названия_ноль()
    {
        var unassigned = new TestDirectory();
        using var scope = host.Services.CreateScope();
        var port = unassigned.Port(scope.ServiceProvider);
        Assert.Null(await port.CardAsync(unassigned.Role, Guid.NewGuid(), []));
        Assert.Null(await port.RefsAsync(unassigned.Role, [Guid.NewGuid()]));
    }

    private async Task<Guid> EnumAsync(string name, string values)
    {
        using var scope = host.Services.CreateScope();
        var enums = scope.ServiceProvider.GetRequiredService<IRepository<EnumType>>();
        var created = EnumType.Create(name, $"Enum{Guid.NewGuid():N}"[..16], null, JsonDocument.Parse(values));
        await enums.AddAsync(created);
        await enums.SaveChangesAsync();
        return created.Id;
    }
}
