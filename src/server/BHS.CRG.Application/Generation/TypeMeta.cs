using System.Text.Json;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Метаполе типа объекта для data.json (issue #342): <c>_type = { code, name, chain: [self … root] }</c>.
/// <c>chain</c> ВКЛЮЧАЕТ сам тип (self-first) — по нему одной проверкой членства ловится и равенство,
/// и потомок (основа для <c>instance-of</c>, фаза 2). <c>code == chain[0]</c> — стабильный ASCII-ключ
/// прямого диспетча (переименуемое <c>name</c> — только для показа). Цепочка строится из графа
/// <see cref="DocumentType.ParentId"/>. Единый источник формы для всех штамповщиков (issue #342).
/// </summary>
public static class TypeMeta
{
    /// <summary>Коды типов от конкретного к корню по ParentId. Guard от циклов/битого графа (32).</summary>
    public static List<string> AncestorCodes(Guid typeId, IReadOnlyDictionary<Guid, DocumentType> byId)
    {
        var chain = new List<string>();
        var cur = byId.GetValueOrDefault(typeId);
        var guard = 0;
        while (cur is not null && guard++ < 32)
        {
            chain.Add(cur.Code);
            cur = cur.ParentId is { } p ? byId.GetValueOrDefault(p) : null;
        }
        return chain;
    }

    /// <summary>Строит JSON-объект метаполя <c>{ code, name, chain }</c> для типа. Неизвестный тип → пустые.</summary>
    public static JsonElement BuildElement(Guid typeId, IReadOnlyDictionary<Guid, DocumentType> byId)
    {
        var self = byId.GetValueOrDefault(typeId);
        return JsonSerializer.SerializeToElement(new
        {
            code = self?.Code ?? "",
            name = self?.Name ?? "",
            chain = AncestorCodes(typeId, byId),
        });
    }
}
