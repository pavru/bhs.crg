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
///
/// <para>Вторая форма ссылки — «основа», <c>_baseRef</c>: <c>{kind, id}</c> или голая строка
/// (ревью PR #1230). Это тоже выбор записи, и её данные подмешиваются в документ. Голая строка вида
/// не называет и может указывать на документ; она берётся тоже — спрашивают потом про записи общих
/// данных, и идентификатор документа среди них просто не найдётся.</para>
///
/// <para>⚠️ Обход ОДИН: и «что стояло», и «что прислали» читает <see cref="PlacedIn" />. Два обхода
/// с одинаковым признаком ссылки расходятся при первой же правке одного из них — и тогда либо
/// каждая стоявшая ссылка становится новой, либо новые проходят молча.</para>
/// </summary>
public static class CatalogRefs
{
    /// <summary>Ссылка в данных: на что, где стоит и как названа в самой ссылке.</summary>
    public readonly record struct Placed(Guid EntryId, string Path, string? DisplayName);

    public static IReadOnlySet<Guid> IdsIn(JsonElement data) =>
        PlacedIn(data).Select(p => p.EntryId).ToHashSet();

    /// <summary>Все ссылки с местами, в порядке обхода; одна запись может встретиться не раз.</summary>
    public static IReadOnlyList<Placed> PlacedIn(JsonElement data)
    {
        var found = new List<Placed>();
        Collect(data, "", found);
        return found;
    }

    private static void Collect(JsonElement el, string path, List<Placed> found)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (el.TryGetProperty("$ref", out var kind) && kind.ValueKind == JsonValueKind.String
                    && kind.GetString() == "catalog"
                    && el.TryGetProperty("entryId", out var id) && id.ValueKind == JsonValueKind.String
                    && Guid.TryParse(id.GetString(), out var parsed))
                    found.Add(new Placed(parsed, path, Text(el, "displayName")));
                foreach (var prop in el.EnumerateObject())
                {
                    var at = path.Length == 0 ? prop.Name : $"{path}.{prop.Name}";
                    if (prop.Name == BaseRefKey)
                    {
                        // Основа — указатель, а не данные: внутрь не идём.
                        if (BaseRefId(prop.Value) is { } baseId)
                            found.Add(new Placed(baseId, at, Text(prop.Value, "displayName")));
                        continue;
                    }
                    Collect(prop.Value, at, found);
                }
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in el.EnumerateArray()) Collect(item, $"{path}[{i++}]", found);
                break;
        }
    }

    public const string BaseRefKey = "_baseRef";

    /// <summary>Основа, если она может указывать на запись общих данных: вид «catalog» либо не назван.</summary>
    private static Guid? BaseRefId(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object && Text(el, "kind") is { } kind && kind != "catalog") return null;
        return BaseRefReader.ParseRef(el);
    }

    private static string? Text(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
