using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Common;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Persistence;

public partial class DomainObjectRepository
{
    /// <summary>
    /// Запись и её основы для карточки (issue #1516): по два запроса на звено, по ключу. Достижимость основы —
    /// тем же правилом, что у значений поля (<see cref="OnBranch" />): карточка и сопоставление по реквизиту
    /// обязаны видеть у записи одно и то же.
    ///
    /// <para>Данные звена приходят из базы уже без содержимого «непрозрачных» полей (<paramref name="opaque" />):
    /// картинка — base64 на сотни килобайт, а в таблице и вложенном объекте могут лежать такие же. Вместо
    /// значения — то, что карточке о нём нужно: <c>null</c> — пусто, у списка — число элементов, у ссылки на
    /// запись — сама ссылка, иначе <c>true</c> — «заполнено».</para>
    /// </summary>
    public async Task<CardChain?> CardChainAsync(Guid id, IReadOnlyCollection<string> opaque, CancellationToken ct = default)
    {
        var links = new List<CardSource>();
        var visited = new HashSet<Guid>();
        var keys = opaque.ToArray();
        Guid? next = id;
        var broken = false;

        while (next is { } wanted)
        {
            if (!visited.Add(wanted) || links.Count > BaseDepth)
            {
                broken = true;
                break;
            }

            var found = await Db.Set<DomainObject>().AsNoTracking()
                .Where(o => o.Id == wanted)
                .Select(o => new { o.Id, o.CompositeTypeId, o.DisplayName, Archived = o.ArchivedAt != null,
                    o.ScopeLevel, o.ScopeId, IsDocument = o.Facet != null, o.Aliases })
                .FirstOrDefaultAsync(ct);
            if (found is null)
            {
                if (links.Count == 0) return null;
                broken = true;
                break;
            }

            var data = await DataAsync(wanted, keys, ct);
            links.Add(new CardSource(found.Id, found.CompositeTypeId, found.DisplayName, found.Archived,
                found.ScopeLevel, found.ScopeId, found.IsDocument, found.Aliases, data));

            next = BaseRefReader.GetBaseRefId(data);
            // Основа названа, а ссылка не читается — тоже обрыв, а не «основы нет».
            if (next is null && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("_baseRef", out var named)
                && named.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                && !(named.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(named.GetString())))
                broken = true;
        }

        if (links.Count == 1) return new CardChain(links, broken);

        // Ветка областей — по звеньям, как у значений поля: первое негодное обрывает цепочку на себе.
        var chains = await ScopeChainBatch.LoadAsync(Db, [.. links.Select(l => (l.ScopeLevel, l.ScopeId)).Distinct()], ct);
        var deepest = Probe(links[0]);
        for (var i = 1; i < links.Count; i++)
        {
            if (OnBranch(deepest, Probe(links[i]), chains) is not { } low)
                return new CardChain(links[..i], Broken: true);
            deepest = low;
        }
        return new CardChain(links, broken);
    }

    /// <summary>Данные звена, непрозрачные поля — признаком (см. <see cref="CardChainAsync" />).</summary>
    private async Task<JsonElement> DataAsync(Guid id, string[] opaque, CancellationToken ct)
    {
        var text = await Db.Database.SqlQuery<string>($$"""
            SELECT CASE WHEN jsonb_typeof(o."Data") <> 'object' THEN o."Data"::text
              ELSE COALESCE((SELECT jsonb_object_agg(e.key, CASE
                  WHEN NOT (e.key = ANY({{opaque}})) THEN e.value
                  WHEN e.value IN ('null'::jsonb, '""'::jsonb, '[]'::jsonb, '{}'::jsonb) THEN 'null'::jsonb
                  WHEN jsonb_typeof(e.value) = 'array' THEN to_jsonb(jsonb_array_length(e.value))
                  WHEN jsonb_typeof(e.value) = 'object' AND e.value->>'$ref' = 'catalog'
                    THEN jsonb_build_object('$ref', 'catalog', 'entryId', e.value->'entryId')
                  ELSE 'true'::jsonb END)
                FROM jsonb_each(o."Data") e), '{}'::jsonb)::text END AS "Value"
            FROM domain_objects o WHERE o."Id" = {{id}}
            """).FirstAsync(ct);
        using var parsed = JsonDocument.Parse(text);
        return parsed.RootElement.Clone();
    }

    private static FieldProbe Probe(CardSource link) => new(
        link.Id, link.CompositeTypeId, link.DisplayName, link.Archived, link.ScopeLevel, link.ScopeId,
        HasOwn: false, Kind: null, Value: null, BaseRef: null, link.IsDocument);
}
