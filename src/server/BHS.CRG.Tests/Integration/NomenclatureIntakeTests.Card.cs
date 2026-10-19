using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Карточка позиции (issue #1516): <c>GET /api/costs/nomenclature/{id}/card</c> — порт <c>CardAsync</c> и
/// запрос ядра <see cref="CommonDataCardQuery" />. Здесь, рядом с подбором: позиции с ключом заводит этот класс.
/// </summary>
public partial class NomenclatureIntakeTests
{
    /// <summary>
    /// Поля ключа — первыми, в порядке схемы, и всегда, пустое тоже; ссылка — названием; картинка — только заголовком, и в
    /// ответе нет ни байта её содержимого.
    /// </summary>
    [Fact]
    public async Task Карточка_ключ_первым_ссылка_названием_а_картинки_нет()
    {
        var kind = await KindAsync();
        var article = Unique("ABB-S201").Replace(' ', '-');
        var position = await PositionAsync(kind, Unique("Автомат"), null, article);
        await SqlAsync("""
            UPDATE domain_objects SET "Data" = "Data"
                || jsonb_build_object('Изображение', 'data:image/png;base64,iVBORw0KGgo=', 'Группа', 'Защита')
                || jsonb_build_object('ЕдиницаИзмерения', jsonb_build_object('$ref', 'catalog', 'entryId', {0}::text))
            WHERE "Id" = {1}
            """, kind.Unit.ToString(), position);

        var (reader, _) = await SignInAsync(await RoleAsync("costs.invoice.read"));
        var response = await reader.GetAsync($"/api/costs/nomenclature/{position}/card");
        await OkAsync(response);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("data:", text);

        var card = JsonDocument.Parse(text).RootElement;
        var lines = card.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(["Наименование", "Артикул", "Производитель"], lines.Take(3).Select(l => l.GetProperty("title").GetString()));
        Assert.All(lines.Take(3), l => Assert.True(l.GetProperty("identity").GetBoolean()));
        // Пустое поле ключа — на месте, «не заполнено», а не пропало.
        Assert.Equal(article, lines[1].GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, lines[2].GetProperty("text").ValueKind);
        Assert.False(lines[2].GetProperty("unreadable").GetBoolean());

        var unit = Assert.Single(lines, l => l.GetProperty("title").GetString() == "Единица измерения");
        Assert.Equal("reference", unit.GetProperty("kind").GetString());
        Assert.Equal("шт-приём", unit.GetProperty("text").GetString());
        Assert.Contains(lines, l => l.GetProperty("text").GetString() == "Защита");

        Assert.Equal(["Изображение"], card.GetProperty("notShown").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal("Позиция с ключом", card.GetProperty("typeName").GetString());
        Assert.Equal(0, card.GetProperty("chosen").GetProperty("times").GetInt32());
    }

    /// <summary>
    /// Роль с основой: унаследованный артикул виден с названием основы; основа удалена — поле ключа «не
    /// прочитано», а не «пусто».
    /// </summary>
    [Fact]
    public async Task Унаследованное_поле_названо_основой_а_нечитаемая_основа_не_пусто()
    {
        var kind = await KindAsync();
        var article = Unique("ИЭК-ВА47").Replace(' ', '-');
        var baseName = Unique("Автомат базовый");
        var source = await PositionAsync(kind, baseName, null, article);
        var heir = await EntryWithDataAsync(kind.Type, Unique("Автомат роль"), $$"""{"_baseRef":"{{source}}","Наименование":"Своё имя"}""");
        var orphan = await EntryWithDataAsync(kind.Type, Unique("Автомат сирота"), $$"""{"_baseRef":"{{Guid.NewGuid()}}","Наименование":"Сирота"}""");

        var (client, _) = await SignInAsync("Supplier");

        var lines = await CardLinesAsync(client, heir);
        Assert.Equal("Своё имя", lines["Наименование"].GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, lines["Наименование"].GetProperty("inheritedFrom").ValueKind);
        Assert.Equal(article, lines["Артикул"].GetProperty("text").GetString());
        Assert.Equal(baseName, lines["Артикул"].GetProperty("inheritedFrom").GetString());

        var broken = await CardLinesAsync(client, orphan);
        Assert.Equal("Сирота", broken["Наименование"].GetProperty("text").GetString());
        Assert.True(broken["Артикул"].GetProperty("unreadable").GetBoolean());
        Assert.False(broken["Наименование"].GetProperty("unreadable").GetBoolean());
    }

    /// <summary>
    /// Карточку получает читатель счетов или накладных без прав справочника; запись не из номенклатуры
    /// (организация) и несуществующая — 404, модуль без документов — 403. Выбор в счёте посчитан тому, кто
    /// счета читает.
    /// </summary>
    [Fact]
    public async Task Карточка_открыта_читателю_документов_а_чужая_запись_не_найдена()
    {
        var kind = await KindAsync();
        var position = await PositionAsync(kind, Unique("Розетка"), null, null);
        var organization = await EntryAsync(await TypeAsync(TestDirectories.OrganizationCode, "Организация"), Unique("ООО «Чужая»"));

        var (buyer, _) = await SignInAsync("Supplier");
        await LinesAsync(buyer, await CreateAsync(buyer), [Line(position, 2, 10m, text: "Розетка двойная")]);

        // Выбор считается по документам, открытым спрашивающему: читатель накладных о счетах не узнаёт и
        // так (COST-29). Дата — документа, а не правки строки.
        foreach (var (right, times, last) in new[] { ("costs.invoice.read", 1, "2026-09-29"), ("costs.waybill.read", 0, null) })
        {
            var (reader, _) = await SignInAsync(await RoleAsync(right));
            var card = await reader.GetFromJsonAsync<JsonElement>($"/api/costs/nomenclature/{position}/card");
            Assert.Equal(times, card.GetProperty("chosen").GetProperty("times").GetInt32());
            Assert.Equal(last, card.GetProperty("chosen").GetProperty("lastOn").GetString());

            Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/costs/nomenclature/{organization}/card")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/costs/nomenclature/{Guid.NewGuid()}/card")).StatusCode);
        }

        var (keeper, _) = await SignInAsync(await RoleAsync("costs.articles.edit"));
        Assert.Equal(HttpStatusCode.Forbidden, (await keeper.GetAsync($"/api/costs/nomenclature/{position}/card")).StatusCode);
    }

    private static async Task<Dictionary<string, JsonElement>> CardLinesAsync(HttpClient client, Guid id)
    {
        var card = await client.GetFromJsonAsync<JsonElement>($"/api/costs/nomenclature/{id}/card");
        return card.GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("title").GetString()!);
    }

    private async Task<Guid> EntryWithDataAsync(Guid type, string name, string data)
    {
        using var scope = host.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new CreateCommonDataEntryCommand(
            name, type, JsonDocument.Parse(data), CatalogScope.System, null))).Id;
    }
}
