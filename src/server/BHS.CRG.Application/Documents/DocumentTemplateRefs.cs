using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Objects;

namespace BHS.CRG.Application.Documents;

/// <summary>
/// Запись ссылок документа на шаблоны: выбор для генерации (<c>TemplateIds</c>), одиночный пин
/// (<c>TemplateId</c>) и ключи переопределений параметров (<c>TemplateParams</c>, вид
/// <c>{idШаблона:{имя:значение}}</c>), issue #1960. Два правила:
/// <list type="number">
/// <item>ссылка ведёт на шаблон ТИПА документа — иначе генерация её молча пропустит;</item>
/// <item>НОВАЯ ссылка выбора и параметров (её не было в сохранённом) — на ДЕЙСТВУЮЩУЮ версию:
/// погашенную в выборе генерация тоже пропускает, а показ её не рисует, и снять её человеку нечем.
/// Прежние остаются, пока существуют, — иначе давняя погашенная запирала бы любую правку документа.
/// Одиночного пина правило не касается: его генерация берёт и на исторической версии.</item>
/// </list>
///
/// <para>Нарушение — <b>отказ 400</b>, а не молчаливое отбрасывание. Ответ «сохранено», в котором
/// сохранено не то, что просили, — переодетый отказ. Ровно так и жили 29 висячих ссылок копии рабочей
/// базы: сервер принимал любые id, клиент пересылал невидимые ему, генерация их молча обходила.
/// Типичная причина отказа на живой странице — устаревший список шаблонов, поэтому текст отказа
/// просит обновить страницу.</para>
///
/// <para>Ссылки пишутся каноническим видом (<see cref="TemplateRefJson.TemplateKey(string?)"/>): ключ
/// <c>AAAA…</c> принимался бы, а генерация искала бы <c>aaaa…</c> и не находила. Где отказывать некому
/// (восстановление копии), висячие ссылки убирает <see cref="DomainObject.ForgetUnknownTemplates"/>.</para>
/// </summary>
public static class DocumentTemplateRefs
{
    private const string SelectionShape = "Выбор шаблонов — массив идентификаторов шаблонов или null.";
    private const string ParamsShape = "Параметры шаблонов — объект {idШаблона:{имя:значение}} или null.";

    /// <summary>Проверенный выбор: id по порядку без повторов; пустой выбор — null (как после
    /// <c>UnpinTemplate</c>).</summary>
    /// <param name="typeTemplates">Шаблоны типа документа: id → действует ли версия.</param>
    public static string? CheckedIds(string? json, string? stored, IReadOnlyDictionary<Guid, bool> typeTemplates)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var array = TemplateRefJson.TryParseArray(json) ?? throw new InvalidRequestException(SelectionShape);
        var ids = array.Select(n => TemplateRefJson.TemplateKey(n) ?? throw new InvalidRequestException(SelectionShape))
            .Distinct().ToList();
        Check(ids, StoredIds(stored), typeTemplates);
        return ids.Count == 0 ? null : JsonSerializer.Serialize(ids);
    }

    /// <summary>Проверенные переопределения: ключ — id шаблона типа, значение — объект. Прочее (в том
    /// числе старый плоский вид <c>{имя:значение}</c>, который генерация не читает) — отказ.</summary>
    public static string? CheckedParams(string? json, string? stored, IReadOnlyDictionary<Guid, bool> typeTemplates)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var root = TemplateRefJson.TryParseObject(json) ?? throw new InvalidRequestException(ParamsShape);
        var result = new JsonObject();
        foreach (var (key, value) in root)
        {
            if (TemplateRefJson.TemplateKey(key) is not { } id || value is not JsonObject)
                throw new InvalidRequestException(
                    $"Параметры шаблонов: «{key}» — не идентификатор шаблона с объектом значений.");
            if (!result.TryAdd(id.ToString(), value.DeepClone()))
                throw new InvalidRequestException($"Параметры шаблонов: шаблон {id} назван дважды.");
        }
        Check(result.Select(p => Guid.Parse(p.Key)), StoredParamKeys(stored), typeTemplates);
        return result.Count == 0 ? null : result.ToJsonString();
    }

    /// <summary>Одиночный пин: null или шаблон типа — любая его версия, в том числе историческая.</summary>
    public static void CheckId(Guid? id, IReadOnlyDictionary<Guid, bool> typeTemplates)
    {
        if (id is { } v) ThrowIfForeign([v], typeTemplates);
    }

    private static void Check(IEnumerable<Guid> ids, IReadOnlySet<Guid> stored, IReadOnlyDictionary<Guid, bool> typeTemplates)
    {
        var list = ids.ToList();
        ThrowIfForeign(list, typeTemplates);
        var retired = list.Where(id => !stored.Contains(id) && !typeTemplates[id]).ToList();
        if (retired.Count > 0)
            throw new InvalidRequestException(
                $"Версия шаблона уже не действует: {string.Join(", ", retired)}. Обновите страницу — у шаблона есть новая версия.");
    }

    private static void ThrowIfForeign(IReadOnlyList<Guid> ids, IReadOnlyDictionary<Guid, bool> typeTemplates)
    {
        var foreign = ids.Where(id => !typeTemplates.ContainsKey(id)).ToList();
        if (foreign.Count > 0)
            throw new InvalidRequestException(
                $"Нет таких шаблонов у типа документа: {string.Join(", ", foreign)}. Обновите страницу — список шаблонов мог измениться.");
    }

    private static HashSet<Guid> StoredIds(string? json) =>
        string.IsNullOrWhiteSpace(json) || TemplateRefJson.TryParseArray(json) is not { } array
            ? []
            : array.Select(TemplateRefJson.TemplateKey).OfType<Guid>().ToHashSet();

    private static HashSet<Guid> StoredParamKeys(string? json) =>
        string.IsNullOrWhiteSpace(json) || TemplateRefJson.TryParseObject(json) is not { } root
            ? []
            : root.Select(p => TemplateRefJson.TemplateKey(p.Key)).OfType<Guid>().ToHashSet();
}
