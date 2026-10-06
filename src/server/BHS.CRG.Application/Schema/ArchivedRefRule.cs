using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Schema;

/// <summary>
/// Правило архива при записи (ТЗ CORE-34.4, issue #1185): уже стоящая ссылка на архивную запись
/// остаётся, НОВАЯ — не появляется. До этого правило держали только экраны (в окне выбора архивных
/// записей нет) и привязка наборов; ссылку, присланную прямо в теле запроса — чужим клиентом, через
/// MCP или ошибшимся окном выбора, — сервер принимал молча.
///
/// <para>«Новая» — по ИДЕНТИФИКАТОРУ, а не по месту: ссылки, которые станут, минус ссылки, которые
/// лежат. Запись, переехавшая из одной строки таблицы в другую, новой не становится — человек её
/// не выбирал заново, и отказ на таком сохранении запер бы документ закрытого периода.</para>
///
/// <para>⚠️ У создания «как лежит» — ничего, поэтому новой считается каждая ссылка. Это верно для
/// путей, которыми пишет человек или внешний клиент: там каждая ссылка и есть выбор. Машинные пути,
/// которые переносят СТОЯВШИЕ ссылки (копия и перенос документа, восстановление копии, починки
/// данных), этой охраны не зовут вовсе — их вердикты названы в <c>RecordWriteGuardCoverageTests</c>.
/// Известный край: вынос вложенного значения в общие данные заводит запись создания, и вложенная в
/// него ссылка на архивную запись даст отказ, хотя в документе она стояла. Отказ называет поле и
/// оба выхода, так что человек не заперт.</para>
/// </summary>
public static class ArchivedRefRule
{
    public const string ArchivedRef = "archived-ref";

    /// <summary>Ссылка в данных: на что, где стоит и как названа в самой ссылке.</summary>
    public readonly record struct Placed(Guid EntryId, string Path, string? DisplayName);

    /// <summary>
    /// Ссылки, которых в лежащих данных нет, — каждая запись один раз, с первым местом, где она
    /// встретилась: отказу нужно назвать поле, а не перечислить все строки таблицы.
    /// </summary>
    public static IReadOnlyList<Placed> Added(JsonElement stored, JsonElement incoming)
    {
        var all = new List<Placed>();
        Collect(incoming, "", all);
        if (all.Count == 0) return all;

        var standing = Objects.CatalogRefs.IdsIn(stored);
        var seen = new HashSet<Guid>();
        return [.. all.Where(p => !standing.Contains(p.EntryId) && seen.Add(p.EntryId))];
    }

    /// <summary>
    /// Находки правила. ⚠️ Запрос к базе — только при непустой разности: обычное сохранение ссылок
    /// не добавляет, и платить за правило оно не должно.
    /// </summary>
    public static async Task<IReadOnlyList<AuditIssue>> RefusalsAsync(
        JsonElement stored, JsonElement incoming, Guid typeId,
        IRepository<DocumentType> types, IDomainObjectRepository objects, CancellationToken ct)
    {
        var added = Added(stored, incoming);
        if (added.Count == 0) return [];

        var archived = (await objects.ArchivedAmongAsync([.. added.Select(p => p.EntryId)], ct)).ToHashSet();
        if (archived.Count == 0) return [];

        // Заголовки полей читаются только здесь, на пути отказа: он редок, а справочник типов тяжёл.
        var byId = (await types.GetAllAsync(ct)).ToDictionary(t => t.Id);
        var titles = byId.ContainsKey(typeId)
            ? DocumentTypeSchemaReader.EffectiveFields(typeId, byId).ToDictionary(f => f.Key, f => f.Title ?? f.Key)
            : [];

        return [.. added.Where(p => archived.Contains(p.EntryId)).Select(p =>
        {
            var key = TopKey(p.Path);
            var field = titles.GetValueOrDefault(key, key);
            var name = string.IsNullOrWhiteSpace(p.DisplayName) ? "выбранная запись" : $"запись «{p.DisplayName}»";
            return new AuditIssue(ArchivedRef, AuditSeverity.Error, p.Path,
                $"Поле «{field}»: {name} в архиве, поставить ссылку на неё нельзя. " +
                "Верните запись из архива или выберите другую.");
        })];
    }

    /// <summary>Поле верхнего уровня: заголовок есть у него, а не у строки таблицы внутри.</summary>
    private static string TopKey(string path)
    {
        var end = path.IndexOfAny(['.', '[']);
        return end < 0 ? path : path[..end];
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
                    found.Add(new Placed(parsed, path,
                        el.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String
                            ? dn.GetString() : null));
                foreach (var prop in el.EnumerateObject())
                    Collect(prop.Value, path.Length == 0 ? prop.Name : $"{path}.{prop.Name}", found);
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in el.EnumerateArray()) Collect(item, $"{path}[{i++}]", found);
                break;
        }
    }
}
