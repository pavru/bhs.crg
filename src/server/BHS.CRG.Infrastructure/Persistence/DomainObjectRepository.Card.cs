using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Common;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Persistence;

public partial class DomainObjectRepository
{
    /// <summary>
    /// Запись и её основы для карточки (issue #1516): по запросу на звено, по ключу. Данные — целиком: одна
    /// запись и одна-две основы, а поля карточка выбирает по схеме. Достижимость основы — тем же правилом, что
    /// у значений поля (<see cref="OnBranch" />): карточка и сопоставление по реквизиту обязаны видеть у записи
    /// одно и то же.
    /// </summary>
    public async Task<CardChain?> CardChainAsync(Guid id, CancellationToken ct = default)
    {
        var links = new List<CardSource>();
        var visited = new HashSet<Guid>();
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
                    o.ScopeLevel, o.ScopeId, IsDocument = o.Facet != null, o.Aliases, o.Data })
                .FirstOrDefaultAsync(ct);
            if (found is null)
            {
                if (links.Count == 0) return null;
                broken = true;
                break;
            }

            var data = found.Data.RootElement.Clone();
            links.Add(new CardSource(found.Id, found.CompositeTypeId, found.DisplayName, found.Archived,
                found.ScopeLevel, found.ScopeId, found.IsDocument, found.Aliases, data));

            next = BaseRefReader.GetBaseRefId(data);
            // Основа названа, а ссылка не читается — тоже обрыв, а не «основы нет».
            if (next is null && data.ValueKind == System.Text.Json.JsonValueKind.Object && data.TryGetProperty("_baseRef", out var named)
                && named.ValueKind is not (System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined)
                && !(named.ValueKind == System.Text.Json.JsonValueKind.String && string.IsNullOrWhiteSpace(named.GetString())))
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

    private static FieldProbe Probe(CardSource link) => new(
        link.Id, link.CompositeTypeId, link.DisplayName, link.Archived, link.ScopeLevel, link.ScopeId,
        HasOwn: false, Kind: null, Value: null, BaseRef: null, link.IsDocument);
}
