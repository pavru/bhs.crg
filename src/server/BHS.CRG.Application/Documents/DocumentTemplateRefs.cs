using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Application.Documents;

/// <summary>
/// Ссылки документа на шаблоны: выбор для генерации (<c>TemplateIds</c>), одиночный пин
/// (<c>TemplateId</c>) и ключи переопределений параметров (<c>TemplateParams</c>, вид
/// <c>{templateId:{имя:значение}}</c>). Правило одно: ссылаться можно только на шаблон ТИПА документа
/// (issue #1960).
///
/// <para>Запись чужого id — <b>отказ 400</b>, а не молчаливое отбрасывание. Ответ «сохранено», в
/// котором сохранено не то, что просили, — переодетый отказ: галку человек видит, генерация её
/// пропускает. Ровно так и жили 29 висячих ссылок копии рабочей базы: сервер принимал любые id,
/// клиент пересылал невидимые ему, генерация их молча обходила.</para>
///
/// <para>Где отказывать некому (восстановление копии), висячие ссылки убирает
/// <see cref="Domain.Objects.DomainObject.ForgetUnknownTemplates"/>.</para>
/// </summary>
public static class DocumentTemplateRefs
{
    /// <summary>Проверенный выбор для записи: id по порядку без повторов; пустой выбор — null
    /// (как после <c>UnpinTemplate</c>). Не массив id или id не шаблона типа — <see cref="InvalidRequestException"/>.</summary>
    public static string? CheckedIds(string? json, IReadOnlySet<Guid> typeTemplates)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        List<Guid> ids;
        try { ids = JsonSerializer.Deserialize<List<Guid>>(json) ?? []; }
        catch (JsonException ex)
        {
            throw new InvalidRequestException("Выбор шаблонов — массив идентификаторов шаблонов или null.", ex);
        }
        ids = ids.Distinct().ToList();
        ThrowIfForeign(ids, typeTemplates);
        return ids.Count == 0 ? null : JsonSerializer.Serialize(ids);
    }

    /// <summary>Проверенные переопределения: объект, ключ — id шаблона типа, значение — объект.
    /// Прочее (в том числе старый плоский вид <c>{имя:значение}</c>, который генерация не читает) — отказ.</summary>
    public static string? CheckedParams(string? json, IReadOnlySet<Guid> typeTemplates)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidRequestException("Параметры шаблонов — объект {idШаблона:{имя:значение}} или null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidRequestException("Параметры шаблонов — объект {idШаблона:{имя:значение}} или null.", ex);
        }
        var ids = new List<Guid>();
        foreach (var (key, value) in root)
        {
            if (!Guid.TryParse(key, out var id) || value is not JsonObject)
                throw new InvalidRequestException(
                    $"Параметры шаблонов: «{key}» — не идентификатор шаблона с объектом значений.");
            ids.Add(id);
        }
        ThrowIfForeign(ids, typeTemplates);
        return root.Count == 0 ? null : json;
    }

    /// <summary>Одиночный пин: null или шаблон типа.</summary>
    public static void CheckId(Guid? id, IReadOnlySet<Guid> typeTemplates)
    {
        if (id is { } v) ThrowIfForeign([v], typeTemplates);
    }

    private static void ThrowIfForeign(IEnumerable<Guid> ids, IReadOnlySet<Guid> typeTemplates)
    {
        var foreign = ids.Where(id => !typeTemplates.Contains(id)).ToList();
        if (foreign.Count > 0)
            throw new InvalidRequestException(
                $"Нет таких шаблонов у типа документа: {string.Join(", ", foreign)}. Обновите страницу — список шаблонов мог измениться.");
    }
}
