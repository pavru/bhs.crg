using System.Text.Json;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Быстрая разноска документа и матрица «строки × объекты» (задача F2 этапа 2, issue #1086, ТЗ COST-11,
/// COST-12, COST-6.2).
///
/// <para><b>Два адреса: предпросмотр и запись.</b> Предпросмотр считает раскладку «поровну», «по %» или
/// пересчёт счёта без строк и НИЧЕГО не пишет; запись принимает состояние матрицы целиком — то, что
/// вернул предпросмотр, или то, что человек набрал в клетках руками. Отдельного «применить раскладку» нет
/// нарочно: применение, которое считало бы заново, могло бы разойтись с показанным, а запись присланного
/// — не может.</para>
/// </summary>
public static class AllocationMatrixEndpoints
{
    private const string Edit = "costs.allocation.edit";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/costs/invoices/{id:guid}/allocation/preview", PreviewAsync)
            .RequireAuthorization(AppPolicies.Permission(Edit))
            .WithTags("Счета на оплату");

        endpoints.MapPut("/api/costs/invoices/{id:guid}/allocation", ReplaceAsync)
            .RequireAuthorization(AppPolicies.Permission(Edit))
            .WithTags("Счета на оплату");
    }

    /// <summary>
    /// Посчитать быструю разноску и показать её — не записывая.
    ///
    /// <para>Право — то же, что у записи: предпросмотр существует только ради записи, и кнопки «поровну»
    /// у того, кто записать не может, быть не должно вовсе, а не «нажиматься и получать отказ».</para>
    /// </summary>
    private static async Task<Ok<AllocationPreview>> PreviewAsync(
        Guid id, AllocationPreviewRequest body, CostsDbContext db, AllocationPlacesSource places, CancellationToken ct)
    {
        var invoice = await InvoiceEndpoints.FindAsync(db, id, ct);
        var lines = await InvoiceLineEndpoints.StoredLinesAsync(db, invoice, ct);
        var known = await places.LoadAsync(ct);

        var targets = body.Method switch
        {
            "equal" => Targets(body.Targets, known, percent: false),
            "percent" => Targets(body.Targets, known, percent: true),
            "document" => await DocumentTargetsAsync(db, invoice, lines, known, ct),
            _ => throw new InvalidRequestException(
                $"Способ «{body.Method}» не известен. Ожидается «equal» (поровну), «percent» (по процентам) или " +
                "«document» (пересчитать разноску счёта по появившимся строкам)."),
        };

        if (lines.Count == 0 && invoice.Total is null or 0)
            throw new InvalidRequestException(
                $"{InvoiceEndpoints.Label(invoice)}: строк нет, и сумма к оплате не заполнена — разносить нечего. " +
                "Счёт без строк разносится суммой к оплате (ТЗ COST-11): заполните её или введите строки.");

        var plan = AllocationSplit.Plan(lines, invoice.Total, targets, wholeUnits: body.Method != "document");
        var parts = plan
            .GroupBy(p => p.LineId)
            .SelectMany(g => g.Select((p, index) => Transient(invoice.Id, p, index + 1)))
            .ToList();
        var read = InvoiceAllocations.Read(invoice, lines, parts, known);

        var state = new MatrixState(
            [.. lines.OrderBy(l => l.Ordinal).Select(l => new MatrixLine(l.Id,
                [.. plan.Where(p => p.LineId == l.Id).Select(Matrix)]))],
            [.. plan.Where(p => p.LineId is null).Select(Matrix)]);

        return TypedResults.Ok(new AllocationPreview(state, read.Lines, read.Summary,
            [.. plan.Where(p => p.Remainder)
                .Select(p => new RemainderCell(p.LineId, p.Target.ConstructionId, p.Target.SectionId, p.Target.ArticleId))]));
    }

    /// <summary>
    /// Записать разноску счёта целиком — состояние матрицы.
    ///
    /// <para>⚠️ <b>Каждая строка счёта обязана быть в наборе.</b> Набор заменяет разноску всего счёта, и
    /// строка, которую форма не прислала (открыта до того, как строку добавили), потеряла бы свои части
    /// молча. Отказ называет её номер.</para>
    ///
    /// <para>Часть правится на месте, если у строки уже есть часть на ту же цель: у матрицы клетка — это
    /// строка и объект, и идентификатор части ей не нужен.</para>
    /// </summary>
    private static async Task<Ok<InvoiceView>> ReplaceAsync(
        Guid id, AllocationMatrixRequest body, CostsDbContext db, IModuleCatalog catalog,
        AllocationPlacesSource places, IModuleActivityLog log, CancellationToken ct)
    {
        if (body.Lines is null)
            throw new InvalidRequestException(
                "Набор строк не прислан. Адрес заменяет разноску всего счёта, и строки без частей присылаются " +
                "с «parts»: [] — отсутствие поля прочитать как «не менять» нельзя.");

        var invoice = await InvoiceEndpoints.FindAsync(db, id, ct);
        var lines = await db.InvoiceLines.AsNoTracking()
            .Where(l => l.InvoiceId == invoice.Id)
            .OrderBy(l => l.Ordinal)
            .ToListAsync(ct);
        var known = await places.LoadAsync(ct);

        var byLine = ParseLines(body.Lines, lines, known);
        var document = ParseDocument(body.Document ?? [], invoice, lines.Count, known);

        var existing = await db.InvoiceAllocations.Where(a => a.InvoiceId == invoice.Id).ToListAsync(ct);
        if (body.Stamp is null)
            throw new InvalidRequestException(
                "Отметка версии разноски («stamp») не прислана. Набор заменяет разноску всего счёта, и без отметки " +
                "не отличить свежий набор от собранного по устаревшему виду. Берётся из «allocation.stamp» счёта.");
        if (body.Stamp != InvoiceAllocations.Stamp(existing))
            throw new ConflictException(
                $"{InvoiceEndpoints.Label(invoice)}: разноску изменили, пока матрица была открыта. Перечитайте счёт " +
                "и повторите — записанный сейчас набор молча вернул бы удалённые части и стёр бы добавленные.");

        var was = Describe(lines, existing.OrderBy(a => a.Ordinal).ToLookup(a => a.LineId, a => a.Snapshot()), known);
        var now = new List<InvoiceAllocation>();

        foreach (var line in lines)
            Place(db, invoice.Id, line.Id, byLine[line.Id], existing, now);
        Place(db, invoice.Id, null, document, existing, now);

        db.InvoiceAllocations.RemoveRange(existing.Except(now));

        // Заменяется разноска ВСЕГО счёта — сохранённые части в расчёт не идут, и читать их снова незачем.
        var returned = invoice.State == InvoiceState.Parsed
            && !InvoiceAllocations.Read(invoice, lines.Select(InvoiceAllocations.Line), now, known).Summary.Allocated;
        if (returned) invoice.ReturnToDraft();

        await db.SaveChangesAsync(ct);

        var after = Describe(lines, now.ToLookup(a => a.LineId, a => a.Snapshot()), known);
        if (was != after)
            await log.RecordAsync(InvoiceActions.AllocationChanged, invoice.Id.ToString(),
                InvoiceEndpoints.Label(invoice), before: was, after: after, ct: ct);

        if (returned)
            await log.RecordAsync(InvoiceActions.Draft, invoice.Id.ToString(),
                InvoiceEndpoints.Label(invoice), after: "правка разноски: баланс не сходится", ct: ct);

        return TypedResults.Ok(await InvoiceEndpoints.ViewAsync(db, catalog, places, invoice, ct));
    }

    /// <summary>Цели «поровну» и «по %». Проценты обязаны дать ровно 100 — иначе делить нечего и не на что.</summary>
    private static IReadOnlyList<SplitTarget> Targets(
        IReadOnlyList<JsonElement>? sent, AllocationPlaces known, bool percent)
    {
        if (sent is not { Count: > 0 })
            throw new InvalidRequestException(
                "Не выбрано ни одного объекта. Быстрая разноска делит счёт между выбранными объектами — выберите " +
                "хотя бы один.");

        var targets = sent.Select((target, index) =>
        {
            var number = index + 1;
            if (target.ValueKind != JsonValueKind.Object)
                throw new InvalidRequestException($"Объект {number} прислан как {target.ValueKind}, а ожидается объект.");

            var place = InvoiceAllocations.Target(target, $"Объект {number}");
            var weight = percent ? Percent(CostsValues.Number(target, "percent", $"Процент, объект {number}"), number) : 1m;
            return new SplitTarget(place, weight);
        }).ToList();

        InvoiceAllocations.EnsureTargets(
            [.. targets.Select(t => new AllocationValues(t.Target, null, null))], known);

        if (percent && targets.Sum(t => t.Weight) is var sum && sum != 100m)
            throw new InvalidRequestException(
                $"Проценты объектов в сумме дают {sum:0.##}, а должны ровно 100. Раздели мы по ним молча — часть " +
                "счёта не легла бы никуда или легла бы дважды.");

        return targets;
    }

    private static decimal Percent(decimal? value, int number) => value switch
    {
        null => throw new InvalidRequestException($"Объект {number}: процент не заполнен."),
        <= 0 or > 100 => throw new InvalidRequestException(
            $"Объект {number}: процент «{value}» — ожидается больше нуля и не больше ста. Объект, которому не " +
            "достаётся ничего, из раскладки убирают."),
        { } share when decimal.Round(share, 2) != share => throw new InvalidRequestException(
            $"Объект {number}: процент «{share}» точнее сотой."),
        { } share => share,
    };

    /// <summary>
    /// Цели пересчёта — прежняя разноска счёта без строк: объект и его сумма как вес (ТЗ COST-11).
    /// </summary>
    private static async Task<IReadOnlyList<SplitTarget>> DocumentTargetsAsync(
        CostsDbContext db, Invoice invoice, IReadOnlyList<AllocationLine> lines,
        AllocationPlaces known, CancellationToken ct)
    {
        var parts = await db.InvoiceAllocations.AsNoTracking()
            .Where(a => a.InvoiceId == invoice.Id && a.LineId == null)
            .OrderBy(a => a.Ordinal)
            .ToListAsync(ct);

        if (lines.Count == 0 || parts.Count == 0)
            throw new InvalidRequestException(
                $"{InvoiceEndpoints.Label(invoice)}: пересчитывать нечего. Пересчёт переносит разноску счёта, " +
                "сделанную суммой, пока строк не было, на появившиеся строки — а здесь " +
                (lines.Count == 0 ? "строк ещё нет." : "разноски суммой нет."));

        // Вес — модуль суммы: у корректировочного счёта (к оплате −1 000) части отрицательны, а пропорция та же.
        // Знак у всех частей один — его держит запись (знак части — знак суммы к оплате).
        var targets = parts
            .GroupBy(p => p.Target)
            .Select(g => new SplitTarget(g.Key, Math.Abs(g.Sum(p => p.Amount ?? 0m))))
            .ToList();

        InvoiceAllocations.EnsureTargets(
            [.. targets.Select(t => new AllocationValues(t.Target, null, null))], known);

        if (targets.Any(t => t.Weight == 0))
            throw new InvalidRequestException(
                $"{InvoiceEndpoints.Label(invoice)}: у разноски суммой есть объект с нулевой суммой — пропорции из " +
                "неё не получить. Разнесите строки поровну или по процентам.");

        // ⚠️ Разноска суммой бывает НЕПОЛНОЙ: к оплате 1 000, на объект A — 300, остальное человек ещё не решил.
        // Нормируй веса по разнесённому — A получил бы все строки, то есть 1 000 вместо 300, и нерешённое
        // легло бы на него молча. Нерешённое остаётся нерешённым: своей долей «не разнесено».
        var spent = targets.Sum(t => t.Weight);
        if (invoice.Total is { } total && Math.Abs(total) > spent)
            targets.Add(new SplitTarget(default, Math.Abs(total) - spent, Unallocated: true));

        return targets;
    }

    /// <summary>Строки набора: каждая строка счёта ровно один раз, части — по правилам разноски строки.</summary>
    private static Dictionary<Guid, IReadOnlyList<AllocationValues>> ParseLines(
        IReadOnlyList<JsonElement> sent, IReadOnlyList<InvoiceLine> lines, AllocationPlaces known)
    {
        var byLine = new Dictionary<Guid, IReadOnlyList<AllocationValues>>();

        for (var index = 0; index < sent.Count; index++)
        {
            var item = sent[index];
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidRequestException(
                    $"Элемент {index + 1} набора строк прислан как {item.ValueKind}, а ожидается объект.");

            var lineId = InvoiceAllocations.Identifier(item, "line", $"Строка, элемент {index + 1}")
                ?? throw new InvalidRequestException($"Элемент {index + 1} набора: строка не указана.");
            var line = lines.FirstOrDefault(l => l.Id == lineId)
                ?? throw new InvalidRequestException(
                    $"Элемент {index + 1} набора: строки {lineId} у этого счёта нет. Так бывает, когда строку " +
                    "удалили, пока матрица была открыта: перечитайте счёт.");

            if (!byLine.TryAdd(line.Id, OfLine(line, Parts(item), known)))
                throw new InvalidRequestException(
                    $"Строка {line.Ordinal} прислана дважды. Набор заменяет разноску целиком, и одна из двух " +
                    "исчезла бы без следа.");
        }

        var missing = lines.Where(l => !byLine.ContainsKey(l.Id)).Select(l => l.Ordinal).ToList();
        if (missing.Count > 0)
            throw new InvalidRequestException(
                $"Не присланы строки {string.Join(", ", missing)}. Набор заменяет разноску всего счёта, и " +
                "неприсланная строка потеряла бы свои части молча. Так бывает, когда строки добавили, пока " +
                "матрица была открыта: перечитайте счёт.");

        return byLine;
    }

    private static IReadOnlyList<AllocationValues> OfLine(
        InvoiceLine line, IReadOnlyList<JsonElement> parts, AllocationPlaces known)
    {
        try
        {
            var mode = AllocationMath.ModeOf(line.Quantity, line.Amount);
            var values = parts.Select((p, index) => InvoiceAllocations.Values(p, index + 1, mode)).ToList();
            InvoiceAllocations.EnsureTargets(values, known);
            AllocationEndpoints.EnsureNotOver(line, values);
            return values;
        }
        catch (InvalidRequestException e)
        {
            throw new InvalidRequestException($"Строка {line.Ordinal}: {e.Message}", e);
        }
    }

    /// <summary>
    /// Части счёта целиком — только у счёта без строк и только суммой, со знаком суммы к оплате и не
    /// больше её (ТЗ COST-11, COST-13).
    /// </summary>
    private static IReadOnlyList<AllocationValues> ParseDocument(
        IReadOnlyList<JsonElement> sent, Invoice invoice, int lineCount, AllocationPlaces known)
    {
        if (sent.Count == 0) return [];

        if (lineCount > 0)
            throw new InvalidRequestException(
                $"{InvoiceEndpoints.Label(invoice)}: у счёта есть строки, и разносятся они (ТЗ COST-11). Разноска " +
                "счёта целиком суммой — только пока строк нет; прежнюю переносит на строки пересчёт.");

        if (invoice.Total is not { } total || total == 0)
            throw new InvalidRequestException(
                $"{InvoiceEndpoints.Label(invoice)}: сумма к оплате не заполнена, а счёт без строк разносится " +
                "именно ею. Заполните её — иначе остаток «не разнесено» считать не от чего.");

        try
        {
            var values = sent.Select((p, index) => InvoiceAllocations.Values(p, index + 1, AllocationMode.Amount)).ToList();
            InvoiceAllocations.EnsureTargets(values, known);

            if (values.Any(v => Math.Sign(v.Amount!.Value) != Math.Sign(total)))
                throw new InvalidRequestException(
                    "знак части — знак суммы к оплате: часть с обратным знаком позволила бы разнести на один объект " +
                    "больше счёта, списав разницу с другого.");

            var spent = values.Sum(v => v.Amount!.Value);
            if (Math.Abs(spent) > Math.Abs(total))
                throw new InvalidRequestException(
                    $"разнесено {spent:0.00} ₽, а к оплате {total:0.00} ₽. Разнести больше счёта нельзя: лишнее " +
                    "легло бы в затраты стройки из ниоткуда.");

            return values;
        }
        catch (InvalidRequestException e)
        {
            throw new InvalidRequestException($"Счёт целиком: {e.Message}", e);
        }
    }

    private static IReadOnlyList<JsonElement> Parts(JsonElement item) => CostsValues.Value(item, "parts") switch
    {
        { ValueKind: JsonValueKind.Array } parts => [.. parts.EnumerateArray()],
        var other => throw CostsValues.Wrong("parts", other, "набор частей строки ([] — строка не разнесена)"),
    };

    /// <summary>Положить части строки (или счёта), правя на месте часть той же цели.</summary>
    private static void Place(
        CostsDbContext db, Guid invoiceId, Guid? lineId, IReadOnlyList<AllocationValues> values,
        List<InvoiceAllocation> existing, List<InvoiceAllocation> now)
    {
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            var part = existing.FirstOrDefault(a => a.LineId == lineId && a.Target == value.Target && !now.Contains(a))
                ?? Added(db, invoiceId, lineId);

            part.Apply(index + 1, value);
            now.Add(part);
        }
    }

    private static InvoiceAllocation Added(CostsDbContext db, Guid invoiceId, Guid? lineId)
    {
        var part = InvoiceAllocation.Create(invoiceId, lineId);
        db.InvoiceAllocations.Add(part);
        return part;
    }

    private static InvoiceAllocation Transient(Guid invoiceId, SplitPart part, int ordinal)
    {
        var entity = InvoiceAllocation.Create(invoiceId, part.LineId);
        entity.Apply(ordinal, new AllocationValues(part.Target, part.Quantity, part.Amount));
        return entity;
    }

    private static MatrixPart Matrix(SplitPart part) =>
        new(part.Target.ConstructionId, part.Target.SectionId, part.Target.ArticleId, part.Quantity, part.Amount);

    /// <summary>Разноска всего счёта текстом для журнала (ТЗ COST-15): «строка 1: …; счёт целиком: …».</summary>
    private static string Describe(
        IReadOnlyList<InvoiceLine> lines, ILookup<Guid?, AllocationValues> parts, AllocationPlaces known)
    {
        var text = lines
            .Where(l => parts[l.Id].Any())
            .Select(l => $"строка {l.Ordinal}: {InvoiceAllocations.Describe([.. parts[l.Id]], known, l.Unit)}")
            .ToList();

        if (parts[null].Any())
            text.Add($"счёт целиком: {InvoiceAllocations.Describe([.. parts[null]], known, null)}");

        return text.Count == 0 ? "не разнесён" : string.Join("; ", text);
    }
}
