using System.Net.Http.Json;
using System.Text.Json;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Подсказка в шаге «Новая позиция» (issue #1533, ТЗ CORE-8, CORE-9): описание двери называет у поля код
/// причины правила <c>Suggestable</c>. Шаг схемы не разбирает — адрес подсказок он зовёт только у поля с
/// кодом <c>suggested</c>.
/// </summary>
public partial class NomenclatureIntakeTests
{
    [Fact]
    public async Task Описание_называет_у_поля_подсказывается_ли_оно()
    {
        var (client, _) = await SignInAsync("Supplier");
        var kind = await SubtypeAsync("ПозицияСПодсказкойКлюча", "Позиция с подсказкой ключа", """
            {"fields":[
              {"key":"Наименование","type":"string","title":"Наименование","required":true,"tags":["identity:1"]},
              {"key":"Производитель","type":"string","title":"Производитель","required":true,"tags":["identity:2"]},
              {"key":"Артикул","type":"string","title":"Артикул","required":true,"tags":["nomenclature.article"]},
              {"key":"Описание","type":"text","title":"Описание","required":true},
              {"key":"Серия","type":"string","title":"Серия","required":true,"tags":["input.noSuggest"]}
            ]}
            """);

        var kinds = (await client.GetFromJsonAsync<JsonElement>("/api/nomenclature/intake")).GetProperty("kinds");
        var fields = kinds.EnumerateArray().Single(k => k.GetProperty("typeId").GetGuid() == kind)
            .GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("key").GetString()!, f => f.GetProperty("suggest").GetString());

        // Поля ключа — компоненты, сами по себе не уникальные: «Производитель» и есть главный потребитель
        // подсказки. Дубль ключа целиком шаг ловит ответом «такая уже есть», а не молчанием поля.
        Assert.Equal("suggested", fields["Наименование"]);
        Assert.Equal("suggested", fields["Производитель"]);
        // Артикул однозначен: подсказка чужого вела бы к дублю позиции.
        Assert.Equal("article", fields["Артикул"]);
        // `text` — абзацы, а не повторяющиеся значения: правило не подсказывает его нигде.
        Assert.Equal("type", fields["Описание"]);
        Assert.Equal("manual", fields["Серия"]);
    }
}
