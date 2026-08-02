using System.Text.Json;
using BHS.CRG.Application.Generation;

namespace BHS.CRG.Application.DataSnapshots;

/// <summary>
/// Свёртка повторов в развёрнутых реквизитах (issues #594, #595).
///
/// При <c>resolveRefs=true</c> каждая ссылка разворачивается ПО МЕСТУ. В титульном листе ЭОМ-1
/// карточка ООО «Инвест Строй» (наименование, ИНН/КПП/ОГРН, СРО, юридический адрес) присутствовала
/// трижды — как заказчик, как организация подписанта и как эмитент вложенного приказа; объект
/// стройки там же повторялся побайтово тоже трижды. Поле <c>ОсновнойДокумент</c> в реестрах-
/// приложениях несло полную копию реквизитов акта со всеми его организациями — именно так реестр
/// работ доходил до 16 МБ.
///
/// Оба повтора сворачиваются здесь, а не в резолвере: генерации PDF нужны значения по месту, шаблон
/// не умеет ходить по словарю. Свёртка — свойство внешнего ЧТЕНИЯ, и живёт она в снимке.
/// </summary>
public static class RequisiteFolding
{
    /// <summary>Ключ ссылки на запись словаря <c>entities</c>.</summary>
    public const string EntityRefKey = "$entity";

    /// <summary>Ключ ссылки на документ, оставленной вместо его развёрнутой копии.</summary>
    public const string DocumentRefKey = "$document";

    /// <param name="Entities">Карточки, на которые ссылается документ, по одному разу каждая. Ключ —
    /// идентификатор записи каталога: тождество проверяется по нему, а не сравнением карточек по
    /// значениям.</param>
    public record Folded(JsonElement Requisites, IReadOnlyDictionary<string, JsonElement> Entities);

    /// <summary>
    /// Сворачивает развёрнутые реквизиты: карточки каталога — в словарь, документы — в ссылку.
    /// </summary>
    /// <param name="documentNames">Имена документов по идентификатору: голый идентификатор
    /// человеку ничего не говорит, а идти за именем отдельным вызовом — тот же лишний круг, ради
    /// сокращения которого свёртка и делается.</param>
    /// <param name="expandDocuments">Оставить развёрнутые документы как есть. По умолчанию нет:
    /// полная копия чужого акта нужна редко, а стоит она дороже всего остального вместе.</param>
    public static Folded Fold(
        JsonElement requisites,
        IReadOnlyDictionary<Guid, string> documentNames,
        bool expandDocuments = false)
    {
        var entities = new Dictionary<string, JsonElement>();
        var result = Walk(requisites, entities, documentNames, expandDocuments);
        return new Folded(result, entities);
    }

    private static JsonElement Walk(
        JsonElement node,
        Dictionary<string, JsonElement> entities,
        IReadOnlyDictionary<Guid, string> documentNames,
        bool expandDocuments)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object when !expandDocuments && IdOf(node, RefProvenance.InstanceIdKey) is { } docId:
            {
                // Реквизиты чужого документа не разворачиваем, но и не прячем: по этой ссылке агент
                // возьмёт документ отдельным вызовом, если он ему действительно нужен.
                var stub = new Dictionary<string, JsonElement>
                {
                    [DocumentRefKey] = JsonSerializer.SerializeToElement(docId.ToString()),
                };
                if (documentNames.TryGetValue(docId, out var name))
                    stub["displayName"] = JsonSerializer.SerializeToElement(name);
                return JsonSerializer.SerializeToElement(stub);
            }

            case JsonValueKind.Object when IdOf(node, RefProvenance.EntryIdKey) is { } entryId:
            {
                var key = entryId.ToString();
                // Содержимое карточки обходим ТОЖЕ: внутри неё живут свои развёрнутые ссылки — тот
                // самый третий экземпляр организации, спрятанный внутри приказа.
                if (!entities.ContainsKey(key))
                {
                    // Заглушка до обхода: карточка, ссылающаяся сама на себя через цепочку, иначе
                    // ушла бы в бесконечную рекурсию.
                    entities[key] = default;
                    entities[key] = WalkProperties(node, entities, documentNames, expandDocuments);
                }
                return JsonSerializer.SerializeToElement(
                    new Dictionary<string, JsonElement>
                    {
                        [EntityRefKey] = JsonSerializer.SerializeToElement(key),
                    });
            }

            case JsonValueKind.Object:
                return WalkProperties(node, entities, documentNames, expandDocuments);

            case JsonValueKind.Array:
            {
                var items = node.EnumerateArray()
                    .Select(item => Walk(item, entities, documentNames, expandDocuments))
                    .ToList();
                return JsonSerializer.SerializeToElement(items);
            }

            default:
                return node.Clone();
        }
    }

    private static JsonElement WalkProperties(
        JsonElement node,
        Dictionary<string, JsonElement> entities,
        IReadOnlyDictionary<Guid, string> documentNames,
        bool expandDocuments)
    {
        var dict = new Dictionary<string, JsonElement>();
        foreach (var p in node.EnumerateObject())
            dict[p.Name] = Walk(p.Value, entities, documentNames, expandDocuments);
        return JsonSerializer.SerializeToElement(dict);
    }

    private static Guid? IdOf(JsonElement node, string key)
        => node.TryGetProperty(key, out var value)
           && value.ValueKind == JsonValueKind.String
           && Guid.TryParse(value.GetString(), out var id)
            ? id : null;

    /// <summary>Идентификаторы документов, развёрнутых внутри реквизитов, — чтобы подтянуть их имена
    /// одним запросом до свёртки.</summary>
    public static IReadOnlyCollection<Guid> DocumentIdsIn(JsonElement node)
    {
        var found = new HashSet<Guid>();
        Collect(node, found);
        return found;

        static void Collect(JsonElement n, HashSet<Guid> acc)
        {
            switch (n.ValueKind)
            {
                case JsonValueKind.Object:
                    if (IdOf(n, RefProvenance.InstanceIdKey) is { } id) acc.Add(id);
                    foreach (var p in n.EnumerateObject()) Collect(p.Value, acc);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in n.EnumerateArray()) Collect(item, acc);
                    break;
            }
        }
    }
}
