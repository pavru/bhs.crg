using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Endpoints;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Plan.Matching;

/// <summary>
/// Сопоставление материала строки с позицией номенклатуры (change <c>plan-08-matching</c>, ТЗ PLAN-9.1, CORE-9,
/// D8 зонтика). Позиция — новая ссылка: на месте и не в архиве. Новую позицию номенклатуры здесь не заводят:
/// её заводит человек дверью справочника ядра после показа похожих (ТЗ COST-7.1), а сюда приходит готовая.
///
/// <para><b>Зафиксированная редакция</b> (решение В-6 (а), на материалы — решение владельца 25.10.2026): не
/// сопоставленный материал сопоставить можно, пересопоставить или снять — 409 с названием производной и путём
/// «новая редакция»: из материалов строятся материалы контракта.</para>
///
/// <para>Материал и отметка редакции — одной фиксацией (версия редакции сдвигается), запоминание — после,
/// вне её (<see cref="ResourceMatching.RememberSafelyAsync" />).</para>
/// </summary>
public sealed class MaterialMatching(
    PlanDbContext db, IModuleCatalog catalog, IModuleUser user, IModuleActivityLog log, ILogger<MaterialMatching> logger)
{
    /// <summary>Сопоставить материал; <paramref name="nomenclatureId" /> = <c>null</c> — снять сопоставление.</summary>
    public async Task<MatchMemory> MatchAsync(
        EstimateRevision revision, EstimateRow row, EstimateRowMaterial material, Guid? nomenclatureId, bool remember,
        CancellationToken ct)
    {
        var where = $"редакция {revision.Number}, строка {row.Order}, материал «{material.SourceName}»";

        if (nomenclatureId is not { } id)
        {
            if (material.NomenclatureId is null && material.ResourceMatchId is null) return MatchMemory.Nothing;
            if (!material.Unmatched) revision.EnsureEditable("Снять сопоставление материала");
            material.Unmatch();
            revision.RowsMatched(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(ct);
            var unmatchedIn = await EstimateAsync(revision, ct);
            await log.RecordAsync(EstimateActions.MaterialUnmatched, unmatchedIn.Id.ToString(), EstimateActions.Label(unmatchedIn),
                before: where, ct: ct);
            return MatchMemory.Nothing;
        }

        var position = await ResourceMatching.ResolveAsync(catalog, id, ct);
        // Тот же выбор материал не меняет, но запомнить его можно: сопоставивший без запоминания передумал.
        if (material.NomenclatureId != position.Id)
        {
            if (!material.Unmatched) revision.EnsureEditable("Пересопоставить материал");
            material.Match(position.Id, resourceMatchId: null);
            revision.RowsMatched(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(ct);
            var estimate = await EstimateAsync(revision, ct);
            await log.RecordAsync(EstimateActions.MaterialMatched, estimate.Id.ToString(), EstimateActions.Label(estimate),
                after: $"{where}: {position.DisplayName}", ct: ct);
        }

        return remember && ResourceMatchKey.Of(material.SourceCode, material.SourceName) is { } key
            ? await ResourceMatching.RememberSafelyAsync(db, [new(key, position.Id)], user, logger, ct)
            : MatchMemory.Nothing;
    }

    /// <summary>Смета редакции — для записи журнала; читается, только когда есть что записать.</summary>
    private Task<Estimate> EstimateAsync(EstimateRevision revision, CancellationToken ct) =>
        db.Estimates.FirstAsync(e => e.Id == revision.EstimateId, ct);
}
