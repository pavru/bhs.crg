using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Plan.Matching;

/// <summary>Выбор сметчика, который надо запомнить: ресурс → позиция номенклатуры.</summary>
public sealed record ResourceToRemember(ResourceMatchKey Key, Guid NomenclatureId);

/// <summary>
/// Запомненные сопоставления ресурсов (change <c>plan-08-matching</c>, ТЗ PLAN-9.1): найти, вспомнить, запомнить.
/// По образцу <see cref="RateMatching" /> и <c>SupplierMatching</c> модуля <c>costs</c>.
/// </summary>
public static class ResourceMatching
{
    /// <summary>Запомненное по ключам — словарём по виду и отпечатку.</summary>
    public static async Task<Dictionary<(ResourceMatchKind Kind, string Hash), ResourceMatch>> FindAsync(
        PlanDbContext db, IEnumerable<ResourceMatchKey> keys, CancellationToken ct)
    {
        var list = keys.ToList();
        var codes = list.Where(k => k.Kind == ResourceMatchKind.Code).Select(k => k.Hash).Distinct().ToList();
        var names = list.Where(k => k.Kind == ResourceMatchKind.Name).Select(k => k.Hash).Distinct().ToList();
        if (codes.Count + names.Count == 0) return [];

        return await db.ResourceMatches.AsNoTracking()
            .Where(m => (m.Kind == ResourceMatchKind.Code && codes.Contains(m.KeyHash))
                || (m.Kind == ResourceMatchKind.Name && names.Contains(m.KeyHash)))
            .ToDictionaryAsync(m => (m.Kind, m.KeyHash), ct);
    }

    /// <summary>Запомненное для ресурса — по старшему ключу, который знаком: код, затем наименование.</summary>
    public static ResourceMatch? Recall(
        IReadOnlyDictionary<(ResourceMatchKind Kind, string Hash), ResourceMatch> known, string? code, string? name) =>
        ResourceMatchKey.Sought(code, name).Select(k => known.GetValueOrDefault((k.Kind, k.Hash))).FirstOrDefault(m => m is not null);

    /// <summary>
    /// Позиция номенклатуры, на которую ставят НОВУЮ ссылку: в справочнике номенклатуры и не в архиве (ТЗ
    /// CORE-34.4). Справочник не назначен — отказ словами, а не «позиции нет».
    /// </summary>
    public static async Task<ModuleCatalogRef> ResolveAsync(IModuleCatalog catalog, Guid nomenclatureId, CancellationToken ct)
    {
        var position = (await catalog.RefsAsync(FunctionalTag.RoleNomenclature, [nomenclatureId], ct)
                ?? throw new InvalidRequestException(EstimateRowReferences.NoNomenclature))
            .FirstOrDefault(r => r.Id == nomenclatureId)
            ?? throw new InvalidRequestException("Такой позиции номенклатуры нет — её удалили либо это запись другого вида.");
        return position.Archived
            ? throw new InvalidRequestException(
                $"Позиция «{position.DisplayName}» в архиве — новая ссылка на неё не записывается. Выберите действующую " +
                "либо верните эту из архива.")
            : position;
    }

    /// <summary>
    /// Запомнить выбор: новое сопоставление либо замена прежнего. Вне транзакции сопоставления и с одним
    /// повтором при гонке — по той же причине, что <see cref="RateMatching.RememberAsync" />.
    /// </summary>
    public static async Task<MatchMemory> RememberAsync(
        PlanDbContext db, IReadOnlyList<ResourceToRemember> chosen, IModuleUser user, CancellationToken ct)
    {
        if (chosen.Count == 0) return MatchMemory.Nothing;
        var hashes = chosen.Select(c => c.Key.Hash).ToList();

        for (var attempt = 0; ; attempt++)
        {
            var known = await db.ResourceMatches
                .Where(m => hashes.Contains(m.KeyHash))
                .ToListAsync(ct);
            int remembered = 0, replaced = 0;
            foreach (var (key, nomenclatureId) in chosen)
            {
                var match = known.FirstOrDefault(m => m.Kind == key.Kind && m.KeyHash == key.Hash);
                if (match is null)
                {
                    db.ResourceMatches.Add(ResourceMatch.Create(key, nomenclatureId, user.Id, user.Name));
                    remembered++;
                }
                else if (match.NomenclatureId != nomenclatureId)
                {
                    match.Point(key.Source, nomenclatureId, user.Id, user.Name);
                    remembered++;
                    replaced++;
                }
            }

            try
            {
                await db.SaveChangesAsync(ct);
                return new(remembered, replaced);
            }
            catch (Exception lost) when (attempt == 0
                && lost is DbUpdateException or ConflictException { InnerException: DbUpdateConcurrencyException })
            {
                Detach(db);
            }
        }
    }

    /// <summary>Запомнить, не дав отказу запоминания стать отказом сопоставления: материал к этому мгновению записан.</summary>
    public static async Task<MatchMemory> RememberSafelyAsync(
        PlanDbContext db, IReadOnlyList<ResourceToRemember> chosen, IModuleUser user, ILogger log, CancellationToken ct)
    {
        try
        {
            return await RememberAsync(db, chosen, user, ct);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            Detach(db);
            log.LogWarning(failure, "Сопоставления ресурсов не запомнены ({Count}): материалы сметы сопоставлены.", chosen.Count);
            return new(0, 0, Failed: true);
        }
    }

    private static void Detach(PlanDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries<ResourceMatch>().ToList())
            entry.State = EntityState.Detached;
    }
}
