using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Documents;
using MediatR;

namespace BHS.CRG.Application.Documents;

/// <summary>
/// Пакетная очистка (issue #1245) — рядом с одиночным удалением нарочно: отказы ядра у них одни
/// (<see cref="EnsureCoreFreeAsync" />), и держателей в модулях спрашивает тот же порт
/// (<see cref="IRecordHolders" />).
/// </summary>
public partial class CommonDataHandlers :
    IRequestHandler<PreviewRecordPurgeQuery, PurgeBatchPreview>,
    IRequestHandler<PurgeHeldRecordsCommand, PurgedBatch>
{
    public async Task<PurgeBatchPreview> Handle(PreviewRecordPurgeQuery q, CancellationToken ct)
    {
        if (q.Ids is { Count: > 0 } named)
        {
            var ids = named.Distinct().ToList();
            var found = await objects.RefsAmongAsync(ids, ct);
            return await PreviewAsync(found, ids.Count, ids.Count - found.Count, named: true, ct);
        }

        if (q.Of is not { } of) return new PurgeBatchPreview([], [], Checked: 0, Missing: 0, More: 0);
        var live = await objects.LiveRefsOfTypeAsync(of.TypeId, of.Scope, of.ScopeId, ct);
        return await PreviewAsync(live, live.Count, missing: 0, named: false, ct);
    }

    public async Task<PurgedBatch> Handle(PurgeHeldRecordsCommand cmd, CancellationToken ct)
    {
        var ids = cmd.Seen.Keys.ToList();
        var entries = await repo.FindAsync(e => e.Facet == null && ids.Contains(e.Id), ct);
        var now = await PreviewAsync(
            [.. entries.Select(e => new CommonDataRef(e.Id, e.CompositeTypeId, e.DisplayName, e.ArchivedAt != null))],
            ids.Count, ids.Count - entries.Count, named: true, ct);

        // Всё или ничего (решение владельца): человек подтверждал ЭТОТ список с ЭТИМИ числами.
        // Любое расхождение — отказ целиком и свежий список; удалить «что осталось годным» значило
        // бы потерять не то число ссылок, которое он ввёл.
        var changed =
            now.Missing > 0 ? $"записей пакета уже нет: {now.Missing}"
            : now.Excluded.Count > 0 ? $"удалить теперь нельзя записей: {now.Excluded.Count}"
            : now.Records.Any(r => cmd.Seen[r.Id] != r.Release.References) ? "у части записей изменилось число ссылок"
            : null;
        if (changed is not null)
            throw new PurgeBatchChangedException(
                $"Пока вы подтверждали, пакет изменился — {changed}. Ничего не удалено. " +
                "Сверьте список и подтвердите заново.", now);
        if (now.References != cmd.References)
            throw new PurgeBatchChangedException(
                $"Число ссылок не совпало: сейчас их {now.References}, а подтверждено {cmd.References}. " +
                "Ничего не удалено. Сверьте число и подтвердите заново.", now);

        // Одно сохранение — одна транзакция: отказ базы на любой записи оставляет на месте все.
        foreach (var entry in entries) repo.Remove(entry);
        await repo.SaveChangesAsync(ct);

        // Журнал — по записи на каждую удалённую: искать потерю потом будут по идентификатору
        // записи, и «пакет из сорока» по нему не найти. После удаления и без токена отмены — по тем
        // же причинам, что у одиночного (см. там).
        var types = new Dictionary<Guid, DocumentType?>();
        foreach (var entry in entries)
        {
            if (!types.TryGetValue(entry.CompositeTypeId, out var type))
                types[entry.CompositeTypeId] = type = await typeRepo.GetByIdAsync(entry.CompositeTypeId, CancellationToken.None);
            var release = now.Records.First(r => r.Id == entry.Id).Release;
            await journal.RecordAsync(
                ActivityActions.RecordPurged, entry.Id.ToString(), RecordArchiveHandlers.Label(entry, type),
                before: string.Join("; ", release.Holders.Select(h =>
                    $"{h.Owner}: {h.What} — {h.Rows}" + (h.Traceable ? "" : $" ({h.Address})"))),
                after: $"потеряно ссылок: {release.References}; удалено пакетом, записей в нём: {entries.Count}",
                ct: CancellationToken.None);
        }

        return new PurgedBatch(entries.Count, now.References, now.Untraceable);
    }

    /// <summary>
    /// Разбор записей на «войдут» и «не войдут». Одно тело на предпросмотр и на само удаление:
    /// удаление пересчитывает то же самое и сверяет с тем, что видел человек.
    /// </summary>
    /// <param name="named">
    /// Записи названы человеком (галочками) — тогда свободные и занятые включённым модулем названы
    /// как не вошедшие. У готового отбора их называть незачем: он их и не искал.
    /// </param>
    private async Task<PurgeBatchPreview> PreviewAsync(
        IReadOnlyList<CommonDataRef> records, int checkedCount, int missing, bool named, CancellationToken ct)
    {
        var each = await holders.FindEachAsync([.. records.Select(r => r.Id)], ct);
        // Не «никто не держит»: непрочитанные данные могли держать — пакета нет вовсе.
        if (each.Unverified is { } unverified) throw new ConflictException($"Пакет не собран. {unverified.Lines[0]}");

        var excluded = new List<PurgeBatchExcluded>();
        var releasable = new List<(CommonDataRef Record, DormantRelease Release)>();
        foreach (var record in records.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var holdings = each.ByRecord.GetValueOrDefault(record.Id);
            if (holdings?.Release is { } release) releasable.Add((record, release));
            else if (named)
                excluded.Add(new(record.Id, record.DisplayName ?? "", holdings is null
                    ? "никто не ссылается — удалите обычным путём, из меню строки"
                    : "держат данные включённого модуля — " + string.Join("; ",
                        holdings.Holders.Where(h => h.State == HolderState.Enabled).Select(h => h.Line))));
        }

        var more = named ? 0 : Math.Max(0, releasable.Count - RecordBatchPurge.Limit);
        var result = new List<PurgeBatchRecord>();
        foreach (var (record, release) in releasable.Take(named ? int.MaxValue : RecordBatchPurge.Limit))
        {
            // Отказы ядра принудительное удаление не обходит — ни одиночное, ни пакетное.
            try
            {
                await EnsureCoreFreeAsync(record.Id, ct);
                result.Add(new(record.Id, record.DisplayName ?? "", record.Archived, release));
            }
            catch (ConflictException ex) { excluded.Add(new(record.Id, record.DisplayName ?? "", ex.Message)); }
        }

        return new PurgeBatchPreview(result, excluded, checkedCount, missing, more);
    }
}
