using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Costs;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Поиск позиции номенклатуры смотрит и альтернативные имена записи (issue #1169, находка ревью
/// PR #1116).
///
/// <para>Цена промаха названа у самого адреса поиска: пустой список читается как «в справочнике нет»,
/// и человек заводит вторую такую же позицию. Раньше так отвечал запрос по альтернативному имени.</para>
///
/// <para>Каждый тест заводит СВОИ записи с меткой в именах: посев общий, а соседние тесты поиска
/// считают найденное поштучно.</para>
/// </summary>
[Collection("Integration")]
public class NomenclatureAliasSearchTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    /// <summary>
    /// Найдено по альтернативному имени — и это имя НАЗВАНО: в названии набранного нет, и без пояснения
    /// строка списка читается как промах поиска. Имён у записи два, совпало второе — названо оно, а не
    /// первое попавшееся. Регистр набранного другой нарочно.
    /// </summary>
    [Fact]
    public async Task Поиск_находит_по_альтернативному_имени_и_называет_его()
    {
        var (client, _) = await SignInAsync("Admin");
        var tag = Tag();
        var position = await PositionAsync($"Кабель медный {tag}", $"провод-{tag}", $"ВВГ-{tag} 3*2.5");

        var found = await SearchAsync(client, $"ввг-{tag.ToUpperInvariant()}");

        var item = Assert.Single(found.GetProperty("items").EnumerateArray());
        Assert.Equal(position, item.GetProperty("id").GetGuid());
        Assert.Equal($"Кабель медный {tag}", item.GetProperty("name").GetString());
        Assert.Equal($"ВВГ-{tag} 3*2.5", item.GetProperty("matchedAlias").GetString());
        Assert.False(found.GetProperty("more").GetBoolean());
    }

    /// <summary>
    /// Найдено по названию — альтернативное имя не называется, хотя оно у записи есть: пояснять нечего,
    /// набранное видно в самой строке.
    ///
    /// <para>⚠️ Ищется метка, которая есть И в названии, И в альтернативном имени. Запрос, которого в
    /// альтернативном имени нет, проверял бы пустоту: пояснение не появилось бы и у кода, который
    /// называет имя всегда (так и было — поймано поломкой).</para>
    /// </summary>
    [Fact]
    public async Task Найденное_по_названию_альтернативного_имени_не_называет()
    {
        var (client, _) = await SignInAsync("Admin");
        var tag = Tag();
        var position = await PositionAsync($"Кабель медный {tag}", $"ВВГ-{tag} 3*2.5");

        var item = Assert.Single((await SearchAsync(client, tag)).GetProperty("items").EnumerateArray());

        Assert.Equal(position, item.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("matchedAlias").ValueKind);
    }

    /// <summary>
    /// Знаки образца в набранном обезврежены и для альтернативных имён: «_» — это подчёркивание, а не
    /// «любой знак». Иначе запрос находил бы лишнее и выглядел бы исправной работой.
    /// </summary>
    [Fact]
    public async Task Знаки_образца_в_запросе_обезврежены_и_у_альтернативных_имён()
    {
        var (client, _) = await SignInAsync("Admin");
        var tag = Tag();
        var exact = await PositionAsync($"Лоток А {tag}", $"{tag}_x");
        await PositionAsync($"Лоток Б {tag}", $"{tag}yx");

        var item = Assert.Single((await SearchAsync(client, $"{tag}_x")).GetProperty("items").EnumerateArray());

        Assert.Equal(exact, item.GetProperty("id").GetGuid());
    }

    /// <summary>
    /// Названия уже выбранных позиций (без поиска) альтернативных имён не несут: спрашивали по
    /// идентификатору, и «найдено по …» сказать не о чем. Проверяется на счёте — так ссылку читает форма.
    /// </summary>
    [Fact]
    public async Task Позиция_с_альтернативным_именем_в_строке_счёта_называется_названием()
    {
        var (client, _) = await SignInAsync("Admin");
        var tag = Tag();
        var position = await PositionAsync($"Кабель медный {tag}", $"ВВГ-{tag}");
        var invoice = await CreateAsync(client);

        var view = await LinesAsync(client, invoice, [Line(position, quantity: 1, price: 10m)]);

        var line = view.GetProperty("lines")[0];
        Assert.Equal($"Кабель медный {tag}", line.GetProperty("nomenclatureName").GetString());
        Assert.False(line.GetProperty("nomenclatureLost").GetBoolean());
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private async Task<Guid> PositionAsync(string name, params string[] aliases) =>
        await EntryAsync(await TypeAsync(CostsRecordTypes.NomenclatureCode, "Номенклатура"), name, aliases);

    private static async Task<JsonElement> SearchAsync(HttpClient client, string query) =>
        await client.GetFromJsonAsync<JsonElement>(
            $"/api/costs/nomenclature?query={Uri.EscapeDataString(query)}");
}
