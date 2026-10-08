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
    /// Глубже цепочка основ не читается — запись уходит в «прочитать не удалось». Предел общий с
    /// таблицей объектов и печатью: у одной записи они обязаны видеть одно.
    /// </summary>
    private const int BaseDepth = BaseRefReader.MaxDepth;

    /// <summary>Одна запись в том объёме, который нужен разрешению поля: без остальных данных.</summary>
    private sealed record FieldProbe(
        Guid Id, Guid CompositeTypeId, string? DisplayName, bool Archived, CatalogScope ScopeLevel, Guid? ScopeId,
        bool HasOwn, string? Kind, string? Value, string? BaseRef, bool IsDocument);

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

        // Цепочки областей — всем, кто участвует в наследовании, двумя запросами на всех: у записи со
        // своим полем сверять нечего, а по запросу на область выходили десятки обращений — роли лежат
        // по комплектам и разделам (ревью PR #1256).
        var heirs = known.Values.Where(p => !p.HasOwn && BaseOf(p) is not null).ToList();
        var scopes = heirs
            .Concat(heirs.Select(h => known.GetValueOrDefault(BaseOf(h)!.Value)).OfType<FieldProbe>())
            .Select(p => (p.ScopeLevel, p.ScopeId)).Distinct().ToList();
        var chains = scopes.Count == 0 ? [] : await ScopeChainBatch.LoadAsync(Db, scopes, ct);

        var result = new List<CommonDataFieldValue>(records.Count);
        foreach (var record in records)
        {
            var (value, from, unreadable) = Resolve(record, known, chains);
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
            o.Data.RootElement.GetProperty("_baseRef").GetString(),
            o.Facet != null));

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

    /// <summary>
    /// Доходит ли наследник до основы.
    ///
    /// <para>Печать сверяет основу с цепочкой КОМПЛЕКТА, в котором выпускается документ. У списка
    /// записей комплекта нет, поэтому правило здесь — «одна ветка»: основа лежит в области наследника
    /// или выше (её увидит любой документ, видящий наследника) либо ниже него, в его же поддереве
    /// (её увидит документ из того комплекта). Запись системы, наследующая от записи стройки, печатается
    /// на этой стройке со значением — и «не прочитана» про неё было бы ложью, из-за которой любой не
    /// найденный ИНН получал бы «неизвестно» вместо «нет» (ревью PR #1256).</para>
    ///
    /// <para>Чужая ветка (роль одной стройки наследует от записи другой) остаётся недостижимой: такую
    /// основу не подмешивает ни один документ. Основа-документ — только из комплекта наследника, как у
    /// печати.</para>
    /// </summary>
    private static bool Reaches(
        FieldProbe heir, FieldProbe source, Dictionary<(CatalogScope, Guid?), ScopeChain> chains)
    {
        if (!chains.TryGetValue((heir.ScopeLevel, heir.ScopeId), out var own)) return false;
        if (source.IsDocument)
            return source.ScopeLevel == CatalogScope.Set && own.SetId != Guid.Empty && source.ScopeId == own.SetId;

        return own.Contains(source.ScopeLevel, source.ScopeId)
            || (chains.TryGetValue((source.ScopeLevel, source.ScopeId), out var theirs)
                && theirs.Contains(heir.ScopeLevel, heir.ScopeId));
    }

    private static (string? Value, Guid? From, bool Unreadable) Resolve(
        FieldProbe record, Dictionary<Guid, FieldProbe> known, Dictionary<(CatalogScope, Guid?), ScopeChain> chains)
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
                || !Reaches(record, next, chains))
                return (null, null, true);

            current = next;
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
