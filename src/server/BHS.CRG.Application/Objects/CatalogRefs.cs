using System.Text.Json;

namespace BHS.CRG.Application.Objects;

/// <summary>
/// Ссылки на записи общих данных, стоящие в данных объекта: <c>{"$ref":"catalog","entryId":…}</c> на
/// любой глубине — в поле, в строке таблицы, во вложенном составном значении.
///
/// <para>Нужен правилу архива (ТЗ CORE-34.4, issue #1185): уже стоящая ссылка на архивную запись
/// остаётся, новая — не появляется. «Уже стоит» — это и есть этот набор, снятый с сохранённых
/// данных до правки. Сравнение идёт по идентификатору, а не по месту: запись, переехавшая из одной
/// строки таблицы в другую, новой ссылкой не становится.</para>
/// </summary>
public static class CatalogRefs
{
    public static IReadOnlySet<Guid> IdsIn(JsonElement data)
    {
        var found = new HashSet<Guid>();
        Collect(data, found);
        return found;
    }

    private static void Collect(JsonElement el, HashSet<Guid> found)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (el.TryGetProperty("$ref", out var kind) && kind.ValueKind == JsonValueKind.String
                    && kind.GetString() == "catalog"
                    && el.TryGetProperty("entryId", out var id) && id.ValueKind == JsonValueKind.String
                    && Guid.TryParse(id.GetString(), out var parsed))
                    found.Add(parsed);
                foreach (var prop in el.EnumerateObject()) Collect(prop.Value, found);
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) Collect(item, found);
                break;
        }
    }
}
