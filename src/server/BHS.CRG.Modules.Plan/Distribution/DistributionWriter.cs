using System.Globalization;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Endpoints;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Distribution;

/// <summary>
/// Запись распределения редакции (change <c>plan-10-distribution</c>, задачи 1.3, 1.4, 1.8, ТЗ PLAN-5.1, COST-13):
/// правило раздела ЛСР, набор строк и решение по позиции отчёта переноса — каждое сразу, одной фиксацией.
///
/// <para><b>Версия распределения</b> — заголовок <see cref="RevisionDistribution" />: каждая запись его трогает, и
/// проигравшая одновременная получает 409 с тем, кто и когда изменил. Зафиксированная и не действующая редакции
/// распределяются так же: распределение — слой над строками, а не их содержание (ТЗ PLAN-5.1).</para>
///
/// <para><b>Баланс держится здесь</b> для всех путей записи частей: набор строк, строки «Отменить» и решение по
/// переносу. Набор с одной нарушившей строкой не пишет ничего и называет все нарушившие
/// (<see cref="DistributionBalanceException" />).</para>
///
/// <para>⚠️ Строки ОДНОЙ редакции, которую распределяют, — не план стройки (<see cref="PlanRows.Current" />).</para>
/// </summary>
internal sealed class DistributionWriter(
    PlanDbContext db, IModuleConstructions constructions, IModuleUser user, IModuleActivityLog log,
    DistributionReader reader)
{
    public async Task<DistributionWriteDto> RulesAsync(Guid revisionId, string seen, RuleWrite write, CancellationToken ct)
    {
        var place = await PrepareAsync(revisionId, seen, ct);
        var rows = await RowsAsync(revisionId, ct);
        var key = new LsrKey(write.LsrSectionCode);
        var own = rows.Values.Where(r => new LsrKey(r.LsrSectionCode) == key).ToList();
        if (own.Count == 0)
            throw new InvalidRequestException(write.LsrSectionCode is null
                ? "Строк вне разделов ЛСР в этой редакции нет — правило им не назначить."
                : $"Раздела ЛСР «{write.LsrSectionCode}» в этой редакции нет. Перечитайте распределение — коды разделов в нём.");
        if (write.Target is { SectionId: { } chosen }) EnsureSections(place, [chosen]);
        var ownIds = own.Select(r => r.Id).ToHashSet();
        if (write.Lines?.FirstOrDefault(l => !ownIds.Contains(l.RowId)) is { } stranger)
            throw new InvalidRequestException(
                $"Строка {stranger.RowId} не из раздела ЛСР «{write.LsrSectionCode}» — с правилом раздела пишутся только его строки.");

        var rule = await db.DistributionRules.FirstOrDefaultAsync(
            r => r.RevisionId == revisionId && r.LsrSectionCode == write.LsrSectionCode, ct);
        var before = rule?.Target;
        var inLines = (write.Lines ?? []).Select(l => l.RowId).ToHashSet();
        var touched = write.ResetExceptions ? ownIds : inLines;
        var parts = await PartsAsync(revisionId, touched, ct);
        var previous = Previous(parts, touched, inLines);

        // Строки набора правит Apply по их прежним частям — сброс снимает части остальных строк раздела.
        var dropped = write.ResetExceptions ? parts.Keys.Where(id => !inLines.Contains(id)).ToList() : [];
        db.DistributionParts.RemoveRange(dropped.SelectMany(id => parts[id]));
        if (write.Lines is { } lines) Apply(place, rows, parts, lines);
        if (write.Target is null && rule is not null) db.DistributionRules.Remove(rule);
        else if (write.Target is { } target && rule is not null) rule.Point(target);
        else if (write.Target is { } added) db.DistributionRules.Add(DistributionRule.Create(revisionId, write.LsrSectionCode, added));

        var head = await SaveAsync(place, ct);
        var section = own[0].LsrSectionName is { Length: > 0 } name ? $"раздел ЛСР «{name}»" : "строки вне разделов ЛСР";
        var cleared = dropped.Count > 0
            ? "; сняты части строк " + string.Join(", ", dropped.Select(id => rows[id]).OrderBy(r => r.Order).Select(LineFacts.NumberOf))
            : "";
        await log.RecordAsync(EstimateActions.DistributionRule, revisionId.ToString(), Label(place),
            $"{section}: {TargetText(place, before)}", $"{section}: {TargetText(place, write.Target)}{cleared}" +
            (write.Lines is { Count: > 0 } set ? "; " + LinesText(place, rows, set) : ""), ct);
        return await ResultAsync(place, head, new DistributionPreviousDto(
            new PreviousRuleDto(write.LsrSectionCode, DistributionReader.TargetOf(before)), previous), ct);
    }

    public async Task<DistributionWriteDto> LinesAsync(Guid revisionId, string seen, IReadOnlyList<LineWrite> lines, CancellationToken ct)
    {
        var place = await PrepareAsync(revisionId, seen, ct);
        var rows = await RowsAsync(revisionId, ct);
        var touched = lines.Select(l => l.RowId).ToHashSet();
        var parts = await PartsAsync(revisionId, touched, ct);
        var previous = Previous(parts, touched, touched);
        Apply(place, rows, parts, lines);

        var head = await SaveAsync(place, ct);
        await log.RecordAsync(EstimateActions.DistributionLines, revisionId.ToString(), Label(place),
            PreviousText(place, rows, previous, touched), LinesText(place, rows, lines), ct);
        return await ResultAsync(place, head, new DistributionPreviousDto(null, previous), ct);
    }

    /// <summary>
    /// Решение по позиции отчёта переноса (задача 1.8): <c>apply</c> пишет прежние части на строку-кандидат, только
    /// если у неё своих частей нет и баланс сходится, — молча ничего не затирается и не складывается.
    /// </summary>
    public async Task<DistributionWriteDto> DecideAsync(
        Guid revisionId, Guid itemId, string seen, CarryOverDecision decision, CancellationToken ct)
    {
        var place = await PrepareAsync(revisionId, seen, ct);
        var item = await db.CarryOverItems.FirstOrDefaultAsync(i => i.Id == itemId && i.RevisionId == revisionId, ct)
            ?? throw new NotFoundException("Позиции отчёта переноса нет — перечитайте распределение.");
        if (item.Resolution is not null)
            throw new ConflictException("По этой позиции отчёта переноса уже решено — перечитайте распределение.");
        var allowed = item.Category switch
        {
            CarryOverCategory.Ambiguous => decision.Action is CarryOverAction.Apply or CarryOverAction.Skip,
            _ => decision.Action == CarryOverAction.Acknowledge,
        };
        if (!allowed)
            throw new InvalidRequestException(item.Category == CarryOverCategory.Ambiguous
                ? "Неоднозначную строку переносят на строку-кандидат (apply) или не переносят (skip)."
                : "Эта позиция отчёта решается только «понятно» (acknowledge): строку можно распределить заново в окне строки.");

        var rows = await RowsAsync(revisionId, ct);
        var previous = new List<PreviousLineDto>();
        var after = decision.Action switch { CarryOverAction.Skip => "не переносить", _ => "понятно" };
        if (decision.Action == CarryOverAction.Apply)
        {
            var rowId = decision.RowId!.Value;
            if (!item.Candidates.Contains(rowId) || !rows.TryGetValue(rowId, out var row))
                throw new InvalidRequestException("Эта строка не из кандидатов позиции — выберите строку из её списка.");
            if (!string.Equals(row.SourceUnit?.Trim(), item.SourceUnit?.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidRequestException(
                    $"Единица строки сменилась («{item.SourceUnit}» → «{row.SourceUnit}»): прежние части в ней значат другое " +
                    "количество. Не переносите позицию (skip) и распределите строку заново в окне строки.");
            if (await db.DistributionParts.AnyAsync(p => p.RowId == rowId, ct))
                throw new ConflictException(
                    "У строки уже есть части — верните её к правилу или выберите другую. Её части не тронуты.");
            var write = new LineWrite(rowId, PartUnit.Row, [.. item.PartList.Select(p => new PartRequest(p.SectionId, p.Quantity))]);
            Apply(place, rows, new Dictionary<Guid, List<DistributionPart>>(), [write]);
            previous.Add(new PreviousLineDto(rowId, "row", null));
            after = "перенесено на строку " + LineFacts.NumberOf(row);
        }
        item.Resolve(decision.Action switch
        {
            CarryOverAction.Apply => CarryOverResolution.Applied,
            CarryOverAction.Skip => CarryOverResolution.Skipped,
            _ => CarryOverResolution.Acknowledged,
        }, DateTimeOffset.UtcNow);

        var head = await SaveAsync(place, ct);
        await log.RecordAsync(EstimateActions.DistributionCarryOver, revisionId.ToString(), Label(place),
            $"{DistributionReader.CategoryCode(item.Category)}: строка {item.Number ?? item.NumberInSection} «{item.Name ?? item.LsrSectionName}»",
            after, ct);
        return await ResultAsync(place, head, new DistributionPreviousDto(null, previous), ct);
    }

    /// <summary>Редакция, её смета и версия: смета на разделе — 400, устаревшая версия — 409 с тем, кто изменил.</summary>
    private async Task<DistributionPlace> PrepareAsync(Guid revisionId, string seen, CancellationToken ct)
    {
        var place = await DistributionPlace.LoadAsync(db, constructions, revisionId, ct);
        if (place.OnSection is { } onSection) throw onSection;
        if (seen != db.VersionOf(place.Head))
            throw new DistributionChangedException(db.VersionOf(place.Head), place.Head?.ChangedByName, place.Head?.ChangedAt);
        return place;
    }

    /// <summary>
    /// Записать набор строк: всё проверено до первой правки контекста — нарушившая строка не оставляет за собой
    /// половины набора. Части, чей раздел остался, правятся на месте: удаление и запись той же пары «строка —
    /// раздел» одной фиксацией упёрлись бы в её единственность.
    /// </summary>
    private void Apply(DistributionPlace place, IReadOnlyDictionary<Guid, EstimateRow> rows,
        IReadOnlyDictionary<Guid, List<DistributionPart>> existing, IReadOnlyList<LineWrite> lines)
    {
        if (lines.FirstOrDefault(l => !rows.ContainsKey(l.RowId)) is { } missing)
            throw new InvalidRequestException($"Строки {missing.RowId} нет в этой редакции — перечитайте распределение.");
        EnsureSections(place, [.. lines.SelectMany(l => l.Parts ?? []).Select(p => p.SectionId)]);

        var stored = new List<(Guid RowId, IReadOnlyList<StoredPart>? Parts)>();
        var errors = new List<BalanceError>();
        foreach (var line in lines)
        {
            if (line.Parts is null)
            {
                stored.Add((line.RowId, null));
                continue;
            }
            var (parts, error) = DistributionMath.Store(LineFacts.Of(rows[line.RowId]), line.Unit, line.Parts);
            if (error is not null) errors.Add(error);
            else stored.Add((line.RowId, parts));
        }
        if (errors.Count > 0) throw new DistributionBalanceException(errors);

        foreach (var (rowId, parts) in stored)
        {
            var own = existing.GetValueOrDefault(rowId) ?? [];
            var wanted = (parts ?? []).ToDictionary(p => p.SectionId, p => p.Quantity);
            foreach (var part in own)
                if (wanted.Remove(part.SectionId, out var quantity)) db.Entry(part).Property(p => p.Quantity).CurrentValue = quantity;
                else db.DistributionParts.Remove(part);
            foreach (var (section, quantity) in wanted)
                db.DistributionParts.Add(DistributionPart.Create(place.Revision.Id, rowId, section, quantity));
        }
    }

    private static void EnsureSections(DistributionPlace place, IReadOnlyCollection<Guid> sections)
    {
        var known = place.Sections.Select(s => s.Id).ToHashSet();
        if (sections.Where(s => !known.Contains(s)).Select(s => (Guid?)s).FirstOrDefault() is { } stranger)
            throw new InvalidRequestException(
                $"Раздела стройки {stranger} у стройки этой сметы нет — его удалили, либо он другой стройки.");
    }

    private async Task<RevisionDistribution> SaveAsync(DistributionPlace place, CancellationToken ct)
    {
        var head = place.Head;
        if (head is null)
        {
            head = RevisionDistribution.Create(place.Estimate, place.Revision);
            db.RevisionDistributions.Add(head);
        }
        head.Touch(place.Estimate, place.Revision, user.Id, user.Name, DateTimeOffset.UtcNow);
        try
        {
            await db.SaveChangesAsync(ct);
            return head;
        }
        catch (DistributionRaceException)
        {
            db.ChangeTracker.Clear();
            var now = await db.RevisionDistributions.FirstOrDefaultAsync(h => h.RevisionId == place.Revision.Id, ct);
            throw new DistributionChangedException(db.VersionOf(now), now?.ChangedByName, now?.ChangedAt);
        }
    }

    private async Task<DistributionWriteDto> ResultAsync(
        DistributionPlace place, RevisionDistribution head, DistributionPreviousDto previous, CancellationToken ct)
    {
        var view = await reader.ReadAsync(place with { Head = head }, ct);
        return new DistributionWriteDto(view.Version, previous, view);
    }

    private async Task<Dictionary<Guid, EstimateRow>> RowsAsync(Guid revisionId, CancellationToken ct) =>
        await db.EstimateRows.AsNoTracking().Where(r => r.RevisionId == revisionId).ToDictionaryAsync(r => r.Id, ct);

    private async Task<Dictionary<Guid, List<DistributionPart>>> PartsAsync(
        Guid revisionId, IReadOnlySet<Guid> rowIds, CancellationToken ct)
    {
        var ids = rowIds.ToList();
        return (await db.DistributionParts.Where(p => p.RevisionId == revisionId && ids.Contains(p.RowId)).ToListAsync(ct))
            .GroupBy(p => p.RowId).ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>
    /// Прежнее состояние строк в форме набора — им же правку и отменяют: строка с частями — её части, строка набора
    /// без частей — <c>parts: null</c>, «по правилу». Строки, которые запись не меняет (не из набора и без частей при
    /// сбросе), не называются.
    /// </summary>
    /// <param name="written">Строки, которые запись переписывает набором.</param>
    private static List<PreviousLineDto> Previous(
        IReadOnlyDictionary<Guid, List<DistributionPart>> parts, IReadOnlySet<Guid> touched, IReadOnlySet<Guid> written) =>
    [
        .. touched.Where(id => parts.ContainsKey(id) || written.Contains(id)).Select(id => new PreviousLineDto(id, "row",
            parts.TryGetValue(id, out var own)
                ? [.. own.OrderByDescending(p => p.Quantity).Select(p => new PreviousPartDto(p.SectionId, p.Quantity))]
                : null)),
    ];

    private static string Label(DistributionPlace place) =>
        $"{EstimateActions.Label(place.Estimate)}, ред. {place.Revision.Number}";

    private static string TargetText(DistributionPlace place, DistributionTarget? target) => target switch
    {
        null => "не распределено",
        { Kind: DistributionTargetKind.Whole } => "стройка в целом",
        { SectionId: { } id } => $"«{place.SectionName(id) ?? "удалённый раздел"}»",
        _ => "не распределено",
    };

    private static string LinesText(DistributionPlace place, IReadOnlyDictionary<Guid, EstimateRow> rows, IEnumerable<LineWrite> lines) =>
        string.Join("; ", lines.Select(l => $"строка {LineFacts.NumberOf(rows[l.RowId])}: " + (l.Parts is null
            ? "по правилу раздела"
            : string.Join(" + ", l.Parts.Select(p => $"«{place.SectionName(p.SectionId)}» {Number(p.Quantity)}")) +
              (l.Unit == PartUnit.Plan ? " в единице плана" : $" ({rows[l.RowId].SourceUnit ?? "единица строки"})"))));

    private static string PreviousText(
        DistributionPlace place, IReadOnlyDictionary<Guid, EstimateRow> rows, IReadOnlyList<PreviousLineDto> previous,
        IEnumerable<Guid> touched)
    {
        var had = previous.ToDictionary(p => p.RowId);
        return string.Join("; ", touched.Select(id => $"строка {LineFacts.NumberOf(rows[id])}: " + (had.TryGetValue(id, out var p) && p.Parts is { } was
            ? string.Join(" + ", was.Select(x => $"«{place.SectionName(x.SectionId)}» {Number(x.Quantity)}"))
            : "по правилу раздела")));
    }

    private static string Number(decimal value) => value.ToString("0.######", CultureInfo.GetCultureInfo("ru-RU"));
}
