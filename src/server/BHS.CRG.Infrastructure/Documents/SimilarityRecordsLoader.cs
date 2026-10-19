using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Documents;

/// <summary>
/// Загрузчик записей сверки — см. <see cref="ISimilarityRecordsLoader" />.
///
/// <para>Цена — по запросу на поле ключа, каждый читает все записи семейства лёгкой строкой (проекция
/// поля, картинки не едут), и один запрос альтернативных имён. Но <c>data-&gt;&gt;'key'</c> по большому
/// jsonb база разжимает целиком — на каждое поле ключа. Поэтому подбор спрашивают при открытии окна, а
/// не на каждую букву; вырастет справочник на порядок — сверку переносить в базу.</para>
/// </summary>
public sealed class SimilarityRecordsLoader(AppDbContext db, ISender mediator) : ISimilarityRecordsLoader
{
    public async Task<SimilarityRecords> LoadAsync(
        IReadOnlyDictionary<Guid, DocumentType> all, IReadOnlyList<DocumentType> family,
        IReadOnlyList<string>? identity, CancellationToken ct = default)
    {
        var everything = all.Values.ToList();
        var own = family.ToDictionary(t => t.Id, t => SchemaTags.OrderedKeysWithTag(t, everything, FunctionalTag.Identity));
        // Ключ, по которому сверяется запись: общий у шага «Новая позиция», свой у подбора.
        var keys = family.ToDictionary(t => t.Id, t => identity ?? own[t.Id]);

        var values = new Dictionary<Guid, (CommonDataRef Ref, Dictionary<string, string?> Values)>();
        var unreadable = new HashSet<Guid>();
        // Поле — одним запросом на все виды, у которых оно в ключе: у семейства ключ обычно общий.
        foreach (var field in keys.SelectMany(k => k.Value.Select(key => (Key: key, Type: k.Key))).GroupBy(x => x.Key, x => x.Type))
            // Показ, а не выбор: архивный двойник — тоже двойник, а подбору нужно число архивных.
            foreach (var value in await mediator.Send(
                         new CommonDataFieldValuesQuery([.. field.Distinct()], field.Key, RecordsFor.Display), ct))
            {
                if (!values.TryGetValue(value.Record.Id, out var known))
                    values[value.Record.Id] = known = (value.Record, new(StringComparer.Ordinal));
                known.Values[field.Key] = value.Value;
                if (value.Unreadable) unreadable.Add(value.Record.Id);
            }

        // Вид без ключа идентичности (бывает у корня семейства): спросить у его записей нечего, но сами
        // они — позиции справочника, и поиск в том же окне их находит. Сверяются названием записи —
        // так же, как запись с пустым полем названия.
        var keyless = keys.Where(k => k.Value.Count == 0).Select(k => k.Key).ToList();
        if (keyless.Count > 0)
            foreach (var bare in await db.DomainObjects.AsNoTracking()
                         .Where(o => o.Facet == null && keyless.Contains(o.CompositeTypeId))
                         .Select(o => new CommonDataRef(o.Id, o.CompositeTypeId, o.DisplayName, o.ArchivedAt != null, null))
                         .ToListAsync(ct))
                values.TryAdd(bare.Id, (bare, new(StringComparer.Ordinal)));

        var ownKey = await OwnKeyAsync(own, keys, unreadable, ct);
        var aliases = await AliasesAsync([.. family.Select(t => t.Id)], ct);

        return new(
            // Нечитаемая запись в сверку не идёт: о ней нельзя сказать ни «совпала», ни «не совпала».
            [.. values.Values.Where(r => !unreadable.Contains(r.Ref.Id))
                .Select(r => new SimilarRecord(
                    r.Ref.Id, r.Ref.CompositeTypeId, all.GetValueOrDefault(r.Ref.CompositeTypeId)?.Name ?? "",
                    r.Ref.DisplayName, r.Ref.Archived,
                    [.. keys[r.Ref.CompositeTypeId].Select(k => r.Values.GetValueOrDefault(k))],
                    aliases.GetValueOrDefault(r.Ref.Id) ?? [], ownKey.Contains(r.Ref.Id)))],
            keys.ToDictionary(k => k.Key, k => Titles(k.Key, k.Value, all)),
            unreadable.Count);
    }

    /// <summary>
    /// Записи, у которых заполнены поля ключа их СОБСТВЕННОГО вида, не входящие в ключ сверки. Обычно
    /// таких полей нет вовсе (а у подбора, где ключ у записи свой, — никогда), и запросов здесь ноль.
    /// </summary>
    private async Task<HashSet<Guid>> OwnKeyAsync(
        Dictionary<Guid, IReadOnlyList<string>> own, Dictionary<Guid, IReadOnlyList<string>> keys,
        HashSet<Guid> unreadable, CancellationToken ct)
    {
        var typesByKey = own
            .SelectMany(t => t.Value.Where(k => !keys[t.Key].Contains(k)).Select(k => (Key: k, Type: t.Key)))
            .GroupBy(x => x.Key, x => x.Type);

        var found = new HashSet<Guid>();
        foreach (var group in typesByKey)
            foreach (var value in await mediator.Send(
                         new CommonDataFieldValuesQuery([.. group.Distinct()], group.Key, RecordsFor.Display), ct))
            {
                if (value.Unreadable) unreadable.Add(value.Record.Id);
                else if (!string.IsNullOrWhiteSpace(value.Value)) found.Add(value.Record.Id);
            }
        return found;
    }

    /// <summary>Альтернативные имена записей семейства — только тех, у кого они есть.</summary>
    private async Task<Dictionary<Guid, List<string>>> AliasesAsync(IReadOnlyCollection<Guid> typeIds, CancellationToken ct) =>
        await db.DomainObjects.AsNoTracking()
            .Where(o => o.Facet == null && typeIds.Contains(o.CompositeTypeId) && o.Aliases.Any())
            .Select(o => new { o.Id, o.Aliases })
            .ToDictionaryAsync(o => o.Id, o => o.Aliases, ct);

    private static IReadOnlyList<string> Titles(Guid type, IReadOnlyList<string> keys, IReadOnlyDictionary<Guid, DocumentType> all)
    {
        var schema = DocumentTypeSchemaReader.EffectiveFields(type, all);
        return [.. keys.Select(k => schema.FirstOrDefault(f => f.Key == k) is { } field && !string.IsNullOrWhiteSpace(field.Title)
            ? field.Title : k)];
    }
}
