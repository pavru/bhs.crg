using System.Text.Json;

namespace BHS.CRG.Application.QualityDocs;

/// <summary>
/// Подмешивание документа качества в строки материалов контекста генерации — чистое ядро
/// <c>QualityLinkResolver</c>: связки и реквизиты сертификатов ищет он, а по данным ходит эта часть.
///
/// Обход РЕКУРСИВНЫЙ (issue #648). Прежний проходил только по ключам верхнего уровня, значение
/// которых само является массивом, и inline-ветка union «массив ИЛИ ссылка на реестр» (#320)
/// выпадала: в АОСР материалы лежат в <c>Материалы.Материалы</c>, то есть внутри составной
/// обёртки, — заведённая связка в PDF не попадала вовсе.
///
/// Совпадение ищем по составному ключу идентичности (issue #582) у КАЖДОГО встреченного объекта,
/// а не только у элементов массива: одиночное составное поле материального типа — тот же материал,
/// просто в единственном числе. Ключ обязан совпасть целиком, включая пустые слоты, поэтому
/// посторонний объект под совпадение не подходит — у него нет тех же полей.
/// </summary>
public static class MaterialQualityInjector
{
    /// <summary>Предел глубины: защита от патологических данных, а не от нормальной вложенности.</summary>
    private const int MaxDepth = 8;

    /// <param name="value">Значение ключа контекста генерации.</param>
    /// <param name="identityFields">Поля идентичности материала, в порядке компонентов ключа.</param>
    /// <param name="targetField">Ключ поля, в которое кладётся документ качества.</param>
    /// <param name="byKey">Ключ материала → идентификатор документа (уже с учётом приоритета scope).</param>
    /// <param name="reqByDoc">Реквизиты документов качества по идентификатору.</param>
    /// <param name="injected">Значение с подмешанными документами; исходное, если ничего не совпало.</param>
    /// <returns>Изменилось ли значение — вызывающий не переписывает контекст зря.</returns>
    public static bool TryInject(
        JsonElement value,
        string[] identityFields,
        string targetField,
        IReadOnlyDictionary<string, Guid> byKey,
        IReadOnlyDictionary<Guid, JsonElement> reqByDoc,
        out JsonElement injected)
    {
        injected = Walk(value, identityFields, targetField, byKey, reqByDoc, 0, out var changed);
        return changed;
    }

    private static JsonElement Walk(
        JsonElement value, string[] identityFields, string targetField,
        IReadOnlyDictionary<string, Guid> byKey, IReadOnlyDictionary<Guid, JsonElement> reqByDoc,
        int depth, out bool changed)
    {
        changed = false;
        if (depth >= MaxDepth) return value;

        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
            {
                var items = new List<JsonElement>();
                foreach (var item in value.EnumerateArray())
                {
                    items.Add(Walk(item, identityFields, targetField, byKey, reqByDoc, depth + 1, out var itemChanged));
                    changed |= itemChanged;
                }
                return changed ? JsonSerializer.SerializeToElement(items) : value;
            }

            case JsonValueKind.Object:
            {
                // Сначала вглубь, потом сопоставление: подмешанные реквизиты сертификата — уже
                // результат, и обходить их как данные материала незачем.
                var props = new Dictionary<string, JsonElement>();
                foreach (var p in value.EnumerateObject())
                {
                    // Внутрь уже стоящего документа качества не идём: его реквизиты — не данные
                    // материала, и искать в них материалы значит подмешивать сертификат в сертификат.
                    if (p.Name == targetField) { props[p.Name] = p.Value.Clone(); continue; }
                    props[p.Name] = Walk(p.Value, identityFields, targetField, byKey, reqByDoc, depth + 1, out var propChanged);
                    changed |= propChanged;
                }

                if (TryMatch(value, identityFields, byKey, out var docId)
                    && reqByDoc.TryGetValue(docId, out var reqs)
                    && !HasValue(value, targetField)) // не перетираем заданное вручную
                {
                    props[targetField] = reqs;
                    changed = true;
                }

                return changed ? JsonSerializer.SerializeToElement(props) : value;
            }

            default:
                return value;
        }
    }

    // Ключ материала СОСТАВНОЙ: все поля идентичности разом, в порядке параметра тэга (#582).
    // Прежний перебор «совпало любое поле» допускал две связки на один материал с разными
    // сертификатами и выбирал между ними по порядку полей — то есть молча и не в пользу более
    // точной привязки. Здесь выбирать не из чего: у материала ровно один ключ.
    private static bool TryMatch(JsonElement elem, string[] identityFields,
        IReadOnlyDictionary<string, Guid> byKey, out Guid docId)
    {
        var key = IdentityKey.From(identityFields, field =>
            elem.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null);

        if (!IdentityKey.IsEmpty(key) && byKey.TryGetValue(key, out docId)) return true;

        docId = default;
        return false;
    }

    private static bool HasValue(JsonElement elem, string field)
    {
        if (!elem.TryGetProperty(field, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(v.GetString()),
            JsonValueKind.Object => v.EnumerateObject().Any(),
            _ => true,
        };
    }
}
