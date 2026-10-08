using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Common;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Persistence;

public partial class DomainObjectRepository
{
    /// <summary>
    /// Глубже цепочка основ не читается — запись уходит в «прочитать не удалось». Настоящие цепочки —
    /// одно-два звена (роль → организация); предел нужен, чтобы запрос на список не превращался в
    /// обход всей таблицы по чьей-то ошибке в данных.
    /// </summary>
    private const int BaseDepth = 8;

    /// <summary>Одна запись в том объёме, который нужен разрешению поля: без остальных данных.</summary>
    private sealed record FieldProbe(
        Guid Id, Guid CompositeTypeId, string? DisplayName, bool Archived, CatalogScope ScopeLevel, Guid? ScopeId,
        bool HasOwn, string? Kind, string? Value, string? BaseRef);

    public async Task<IReadOnlyList<CommonDataFieldValue>> FieldValuesAsync(
        IReadOnlyCollection<Guid> typeIds, string fieldKey, RecordsFor purpose, CancellationToken ct = default)
    {
        if (typeIds.Count == 0) return [];

        var live = purpose.HidesArchive();
        var records = await Probes(fieldKey)
            (Db.Set<DomainObject>().AsNoTracking().Where(o =>
                o.Facet == null && typeIds.Contains(o.CompositeTypeId) && (!live || o.ArchivedAt == null)))
            .ToListAsync(ct);

        // Основы — любого вида и состояния: роль наследует от записи, которая сама может быть другого
        // типа или лежать в архиве, и значение у неё от этого не пропадает.
        var known = records.ToDictionary(r => r.Id);
        var wanted = Missing(records, known);
        for (var depth = 0; depth < BaseDepth && wanted.Count > 0; depth++)
        {
            var bases = await Probes(fieldKey)
                (Db.Set<DomainObject>().AsNoTracking().Where(o => wanted.Contains(o.Id)))
                .ToListAsync(ct);
            foreach (var found in bases) known[found.Id] = found;
            wanted = Missing(bases, known);
        }

        var chains = new Dictionary<(CatalogScope, Guid?), ScopeChain>();
        var result = new List<CommonDataFieldValue>(records.Count);
        foreach (var record in records)
        {
            if (!chains.TryGetValue((record.ScopeLevel, record.ScopeId), out var chain))
                chains[(record.ScopeLevel, record.ScopeId)] =
                    chain = await ScopeChains.LoadForScopeAsync(Db, record.ScopeLevel, record.ScopeId, ct);

            var (value, from, unreadable) = Resolve(record, known, chain);
            result.Add(new CommonDataFieldValue(
                new CommonDataRef(record.Id, record.CompositeTypeId, record.DisplayName, record.Archived),
                value, from, unreadable));
        }

        return result;
    }

    /// <summary>
    /// Проекция записи в пробу. Поле читается в базе (<c>-&gt;&gt;</c>, <c>?</c>, <c>jsonb_typeof</c>),
    /// а не из загруженных данных: ради одного реквизита запись целиком не поднимается.
    /// </summary>
    private static Func<IQueryable<DomainObject>, IQueryable<FieldProbe>> Probes(string fieldKey) =>
        objects => objects.Select(o => new FieldProbe(
            o.Id, o.CompositeTypeId, o.DisplayName, o.ArchivedAt != null, o.ScopeLevel, o.ScopeId,
            EF.Functions.JsonExists(o.Data, fieldKey),
            EF.Functions.JsonTypeof(o.Data.RootElement.GetProperty(fieldKey)),
            o.Data.RootElement.GetProperty(fieldKey).GetString(),
            o.Data.RootElement.GetProperty("_baseRef").GetString()));

    /// <summary>Основы, за которыми ещё не ходили: нужны только тем, у кого своего поля нет.</summary>
    private static List<Guid> Missing(IEnumerable<FieldProbe> probes, Dictionary<Guid, FieldProbe> known) =>
        [.. probes.Where(p => !p.HasOwn).Select(p => BaseOf(p)).OfType<Guid>().Where(id => !known.ContainsKey(id)).Distinct()];

    /// <summary>
    /// Ссылка на основу. В базе она лежит строкой либо объектом <c>{kind,id}</c>, а проекция отдаёт
    /// текст и того и другого — разбирает его то же правило, что у генерации.
    /// </summary>
    private static Guid? BaseOf(FieldProbe probe)
    {
        if (string.IsNullOrWhiteSpace(probe.BaseRef)) return null;
        if (!probe.BaseRef.TrimStart().StartsWith('{'))
            return Guid.TryParse(probe.BaseRef, out var id) ? id : null;

        try
        {
            using var parsed = JsonDocument.Parse(probe.BaseRef);
            return BaseRefReader.ParseRef(parsed.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string? Value, Guid? From, bool Unreadable) Resolve(
        FieldProbe record, Dictionary<Guid, FieldProbe> known, ScopeChain chain)
    {
        var visited = new HashSet<Guid> { record.Id };
        var current = record;
        while (true)
        {
            // Своё поле перекрывает основу — и пустое тоже: так слияние считает при генерации, и
            // сопоставление обязано видеть то же, что напечатает документ.
            if (current.HasOwn)
                return current.Kind switch
                {
                    "string" or "number" => (Blank(current.Value), current.Id == record.Id ? null : current.Id, false),
                    "null" => (null, null, false),
                    _ => (null, null, true),
                };

            // Ни своего, ни основы — поля у записи нет. Это знание, а не неудача.
            if (string.IsNullOrWhiteSpace(current.BaseRef)) return (null, null, false);

            // Основа названа, а дойти до неё нельзя: ссылка не читается, запись удалена, цепочка
            // замкнулась либо основа лежит не в области наследника (чужая стройка) — генерация такую
            // тоже не подмешивает.
            if (BaseOf(current) is not { } baseId || !visited.Add(baseId) || !known.TryGetValue(baseId, out var next)
                || !chain.Contains(next.ScopeLevel, next.ScopeId))
                return (null, null, true);

            current = next;
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
