using System.Text.Json;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Части разноски строки ЦЕЛИКОМ (задача F1, issue #1085) — набором, как и сами строки: человек
/// работает с частями строки как с таблицей, и присланный набор есть новое состояние. Часть с
/// <c>id</c> правится на месте.
/// </summary>
public sealed record AllocationRequest(IReadOnlyList<JsonElement>? Parts);

/// <summary>Часть разноски в ответе.</summary>
/// <param name="TargetLost">Стройки (или раздела в ней) больше нет — удалили в ядре. Потеря, и
/// выглядеть она обязана иначе, чем «цель не выбрана»: деньги этой части сейчас не относятся ни к
/// чему, и «разобран» с ней не проходит.</param>
/// <param name="Quantity">Количество части — у строки, разносимой количеством.</param>
/// <param name="Amount">Сумма части: у строки с количеством — ПОСЧИТАННАЯ (доля суммы строки), у
/// строки без количества — введённая. <c>null</c> — посчитать нечем.</param>
/// <param name="Rounding">Копейки округления, ушедшие в эту часть. Не нуль только у последней
/// части — и форма помечает её, иначе «33,34» среди «33,33» выглядело бы опечаткой.</param>
/// <param name="Discrepancy">Расхождение с суммой к оплате, ушедшее в эту часть (в пределах
/// допуска).</param>
/// <param name="Mismatched">Часть не того вида, что строка (у строки убрали количество, а часть
/// хранит метры) — она не разносит ничего.</param>
public sealed record AllocationPartView(
    Guid Id,
    int Ordinal,
    Guid ConstructionId,
    string? ConstructionName,
    Guid? SectionId,
    string? SectionName,
    bool TargetLost,
    decimal? Quantity,
    decimal? Amount,
    decimal Rounding,
    decimal Discrepancy,
    bool Mismatched);

/// <summary>Разноска строки: части и остаток «не разнесено» (ТЗ COST-13 — остаток виден всегда).</summary>
/// <param name="Mode"><c>quantity</c> — делится количество, <c>amount</c> — сумма, <c>none</c> —
/// делить нечего. Решает строка, а не форма: иначе форма предлагала бы ввести метры строке, которую
/// сервер разносит рублями.</param>
public sealed record LineAllocationView(
    string Mode,
    IReadOnlyList<AllocationPartView> Parts,
    decimal? UnallocatedQuantity,
    decimal? UnallocatedAmount,
    bool Balanced);

/// <summary>
/// Разноска счёта целиком суммой — у счёта без строк (задача F2, issue #1086, ТЗ COST-11).
/// </summary>
/// <param name="UnallocatedAmount">«Не разнесено» от суммы к оплате; <c>null</c> — у счёта есть строки
/// или нет суммы к оплате. Остаток есть и у документа целиком, как у строки (ТЗ COST-13).</param>
/// <param name="Pending">Строки у счёта появились, а части счёта остались: разноска ждёт пересчёта по
/// строкам, и «разнесён» до него не наступает.</param>
public sealed record DocumentAllocationView(
    IReadOnlyList<AllocationPartView> Parts,
    decimal? UnallocatedAmount,
    bool Pending,
    bool Balanced);

/// <summary>Разноска счёта целиком: признак «разнесён» и чего ему не хватает.</summary>
/// <param name="Allocated">«Разнесён» (ТЗ COST-9): все строки разнесены, цели на месте, расхождение с
/// суммой к оплате в допуске, разноска счёта без строк не ждёт пересчёта. Ровно это условие проверяет
/// переход «разобран».</param>
/// <param name="Unbalanced">Номера строк, разнесённых не полностью (или с частями не того вида).</param>
/// <param name="Lost">Сколько частей указывают на удалённую стройку или раздел.</param>
/// <param name="Discrepancy">Сумма к оплате минус сумма строк; <c>null</c> — суммы к оплате нет.</param>
/// <param name="Tolerance">Допуск расхождения. Приезжает от сервера, а не зашит в форму: по ТЗ это
/// настройка, и форма, знающая число сама, разошлась бы с ней на первой же правке.</param>
public sealed record AllocationSummaryView(
    bool Allocated,
    IReadOnlyList<int> Unbalanced,
    int Lost,
    decimal? Discrepancy,
    decimal Tolerance,
    bool WithinTolerance,
    DocumentAllocationView Document);

/// <summary>
/// Разноска счёта, прочитанная и посчитанная: то, что нужно ответу, переходу «разобран» и журналу.
/// </summary>
public sealed record InvoiceAllocationRead(
    IReadOnlyDictionary<Guid, LineAllocationView> Lines,
    AllocationSummaryView Summary);

/// <summary>Чтение разноски, её разбор и сборка ответов.</summary>
public static class InvoiceAllocations
{
    private const string ConstructionKey = "construction";
    private const string SectionKey = "section";

    /// <summary>
    /// Прочитать и посчитать разноску счёта. Стройки спрашиваются только если частей больше нуля:
    /// счёт без разноски — самый частый случай, и список строек ему не нужен.
    /// </summary>
    public static async Task<InvoiceAllocationRead> ReadAsync(
        CostsDbContext db, IModuleConstructions sites, Invoice invoice, IReadOnlyList<InvoiceLine> lines,
        CancellationToken ct)
    {
        var parts = await db.InvoiceAllocations.AsNoTracking()
            .Where(a => a.InvoiceId == invoice.Id)
            .ToListAsync(ct);

        var known = parts.Count == 0 ? [] : await sites.ListAsync(ct);
        return Read(invoice, lines.Select(Line), parts, known);
    }

    /// <summary>
    /// Сойдётся ли разноска счёта ПОСЛЕ правки, которая ещё не сохранена, — условие, по которому
    /// разобранный счёт сам возвращается в черновик. Один помощник на все правки (строки, части строки,
    /// шапка счёта): разойдись они — правка строк и правка разноски отвечали бы на один вопрос по-разному.
    ///
    /// <para>Строки — как они лягут; части — из базы, кроме частей тех строк, что отмечает
    /// <paramref name="replaced" /> (<c>null</c> в нём — части счёта целиком): их заменяет
    /// <paramref name="replacement" /> из памяти. Части строк, которых среди <paramref name="lines" /> нет,
    /// в счёт не идут: их унесёт каскад вместе со строкой. Сумма к оплате берётся у
    /// <paramref name="invoice" /> — тоже как ляжет.</para>
    /// </summary>
    public static async Task<bool> AllocatedAfterAsync(
        CostsDbContext db, IModuleConstructions sites, Invoice invoice, IReadOnlyList<AllocationLine> lines,
        CancellationToken ct, Func<Guid?, bool>? replaced = null, IReadOnlyList<InvoiceAllocation>? replacement = null)
    {
        var kept = lines.Select(l => l.Id).ToHashSet();
        var stored = await db.InvoiceAllocations.AsNoTracking()
            .Where(a => a.InvoiceId == invoice.Id)
            .ToListAsync(ct);

        List<InvoiceAllocation> parts =
            [.. stored.Where(a => (a.LineId is not { } line || kept.Contains(line)) && replaced?.Invoke(a.LineId) != true),
             .. replacement ?? []];

        var known = parts.Count == 0 ? [] : await sites.ListAsync(ct);
        return Read(invoice, lines, parts, known).Summary.Allocated;
    }

    /// <summary>Посчитать разноску по уже прочитанному.</summary>
    public static InvoiceAllocationRead Read(
        Invoice invoice, IEnumerable<AllocationLine> lines, IReadOnlyList<InvoiceAllocation> parts,
        IReadOnlyList<ModuleConstruction> sites)
    {
        var balance = AllocationMath.Of(lines, parts.Select(Part), invoice.Total);
        var byId = parts.ToDictionary(p => p.Id);
        var lost = parts.Count(p => Lost(p, sites));

        var views = balance.Lines.ToDictionary(l => l.LineId, l => new LineAllocationView(
            Mode(l.Mode),
            [.. l.Parts.Select(share => View(byId[share.Id], share, sites))],
            l.UnallocatedQuantity,
            l.UnallocatedAmount,
            l.Balanced));

        var document = new DocumentAllocationView(
            [.. balance.Document.Parts.Select(share => View(byId[share.Id], share, sites))],
            balance.Document.UnallocatedAmount,
            balance.Document.Pending,
            balance.Document.Balanced);

        return new InvoiceAllocationRead(views, new AllocationSummaryView(
            balance.Allocated && lost == 0,
            balance.Unbalanced,
            lost,
            balance.Discrepancy,
            balance.Tolerance,
            balance.WithinTolerance,
            document));
    }

    public static AllocationLine Line(InvoiceLine line) => new(line.Id, line.Ordinal, line.Quantity, line.Amount);

    public static AllocationPart Part(InvoiceAllocation part) =>
        new(part.Id, part.LineId, part.Ordinal, part.Quantity, part.Amount);

    /// <summary>Идентификатор присланной части: есть — правим её, нет — заводим новую.</summary>
    public static Guid? Id(JsonElement part, int number) => Identifier(part, "id", $"Часть {number}: идентификатор");

    /// <summary>
    /// Значения присланной части — разобранные по тому, как разносится строка.
    ///
    /// <para>⚠️ <b>Сумму части строки с количеством присылать нельзя</b>, и это не придирка: она
    /// считается из количества, и присланная сумма была бы вторым ответом на тот же вопрос. Прими мы
    /// её молча и выбрось — человек, вписавший рубли, увидел бы в ответе другие рубли и не понял бы,
    /// почему.</para>
    /// </summary>
    public static AllocationValues Values(JsonElement part, int number, AllocationMode mode)
    {
        if (part.ValueKind != JsonValueKind.Object)
            throw new InvalidRequestException(
                $"Часть {number} прислана как {part.ValueKind}, а ожидается объект с полями части.");

        var construction = Identifier(part, ConstructionKey, $"Стройка, часть {number}")
            ?? throw new InvalidRequestException(
                $"Часть {number}: стройка не выбрана. Часть разноски — это «сколько и куда», и без «куда» " +
                "её деньги не относятся ни к чему. Раздел можно не указывать, стройку — нельзя.");

        var section = Identifier(part, SectionKey, $"Раздел, часть {number}");
        var quantity = CostsValues.Money(part, "quantity", $"Количество, часть {number}");
        var amount = CostsValues.Money(part, "amount", $"Сумма, часть {number}");

        return mode switch
        {
            AllocationMode.Quantity when amount is not null => throw new InvalidRequestException(
                $"Часть {number}: у строки есть количество, поэтому разносится количество, а сумма части " +
                "считается — количество × цена строки. Присланную сумму принять нельзя: она стала бы вторым " +
                "ответом на тот же вопрос и разошлась бы с первым на копейки."),
            AllocationMode.Quantity => new AllocationValues(construction, section,
                Positive(quantity, 3, $"Количество, часть {number}"), null),

            AllocationMode.Amount when quantity is not null => throw new InvalidRequestException(
                $"Часть {number}: у строки нет количества — это доставка, услуга или «1 компл.», — поэтому " +
                "разносится сумма. Количество части делить не из чего."),
            AllocationMode.Amount => new AllocationValues(construction, section, null,
                Nonzero(amount, $"Сумма, часть {number}")),

            _ => throw new InvalidRequestException(
                "У строки нет ни количества, ни суммы — разносить нечего. Заполните строку, и разноска " +
                "станет возможна."),
        };
    }

    /// <summary>
    /// Стройки и разделы, на которые ссылаются части, обязаны существовать, а раздел — принадлежать
    /// своей стройке. Раздел чужой стройки — не опечатка, которую можно простить: затраты легли бы
    /// на одну стройку, а в разрезе разделов — на другую.
    /// </summary>
    public static void EnsureTargets(IReadOnlyList<AllocationValues> parts, IReadOnlyList<ModuleConstruction> sites)
    {
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            var site = sites.FirstOrDefault(s => s.Id == part.ConstructionId)
                ?? throw new InvalidRequestException(
                    $"Часть {index + 1}: такой стройки нет. Так бывает, когда стройку удалили, пока форма была " +
                    "открыта. Выберите стройку заново — разнести деньги на несуществующую стройку значило бы " +
                    "потерять их из затрат.");

            if (part.SectionId is { } section && site.Sections.All(s => s.Id != section))
                throw new InvalidRequestException(
                    $"Часть {index + 1}: раздела нет у стройки «{site.Name}». Раздел выбирают внутри стройки — " +
                    "иначе затраты легли бы на одну стройку, а в разрезе разделов на другую.");
        }

        var repeated = parts
            .Select((p, index) => (Number: index + 1, Target: (p.ConstructionId, p.SectionId)))
            .GroupBy(p => p.Target)
            .FirstOrDefault(g => g.Count() > 1);

        if (repeated is not null)
            throw new InvalidRequestException(
                $"Части {string.Join(" и ", repeated.Select(p => p.Number))} идут на одну и ту же цель. " +
                "Сложите их в одну часть: две части на одну стройку и раздел читаются в отчёте как два " +
                "разных решения, а решение одно.");
    }

    /// <summary>Разноска текстом для журнала: «Стройка / раздел — 100 м; …» (ТЗ COST-15).</summary>
    public static string Describe(IReadOnlyList<AllocationValues> parts, IReadOnlyList<ModuleConstruction> sites, string? unit)
    {
        if (parts.Count == 0) return "не разнесена";

        return string.Join("; ", parts.Select(p =>
        {
            var site = sites.FirstOrDefault(s => s.Id == p.ConstructionId);
            var target = site?.Name ?? $"стройка {p.ConstructionId}";
            if (p.SectionId is { } id)
                target += " / " + (site?.Sections.FirstOrDefault(s => s.Id == id)?.Name ?? $"раздел {id}");

            var size = p.Quantity is { } quantity
                ? $"{quantity:0.###} {unit}".TrimEnd()
                : $"{p.Amount:0.00} ₽";
            return $"{target} — {size}";
        }));
    }

    private static AllocationPartView View(
        InvoiceAllocation part, AllocationShare share, IReadOnlyList<ModuleConstruction> sites)
    {
        var site = sites.FirstOrDefault(s => s.Id == part.ConstructionId);
        var section = part.SectionId is { } id ? site?.Sections.FirstOrDefault(s => s.Id == id) : null;

        return new AllocationPartView(
            part.Id,
            part.Ordinal,
            part.ConstructionId,
            site?.Name,
            part.SectionId,
            section?.Name,
            Lost(part, sites),
            part.Quantity,
            share.Amount,
            share.Rounding,
            share.Discrepancy,
            share.Mismatched);
    }

    private static bool Lost(InvoiceAllocation part, IReadOnlyList<ModuleConstruction> sites)
    {
        var site = sites.FirstOrDefault(s => s.Id == part.ConstructionId);
        return site is null || (part.SectionId is { } id && site.Sections.All(s => s.Id != id));
    }

    public static string Mode(AllocationMode mode) => mode switch
    {
        AllocationMode.Quantity => "quantity",
        AllocationMode.Amount => "amount",
        _ => "none",
    };

    internal static Guid? Identifier(JsonElement source, string key, string label) =>
        CostsValues.Value(source, key) switch
        {
            null => null,
            { ValueKind: JsonValueKind.String } value when System.Guid.TryParse(value.GetString(), out var id) => id,
            var other => throw CostsValues.Wrong(label, other, "строку-идентификатор либо ничего"),
        };

    /// <summary>
    /// Количество части: больше нуля и не точнее тысячной — точнее не хранит колонка, и база
    /// округлила бы молча, то есть баланс, сошедшийся при сохранении, разошёлся бы на чтении.
    /// </summary>
    private static decimal Positive(decimal? value, int digits, string label) => value switch
    {
        null => throw new InvalidRequestException(
            $"Поле «{label}» не заполнено. У строки есть количество, и часть разноски — это его доля."),
        <= 0 => throw new InvalidRequestException(
            $"Поле «{label}»: «{value}» — не доля. Ожидается количество больше нуля; ненужную часть удаляют, " +
            "а не обнуляют."),
        { } quantity when decimal.Round(quantity, digits) != quantity => throw new InvalidRequestException(
            $"Поле «{label}»: «{quantity}» точнее тысячной. Количество строки хранится до тысячных, и часть " +
            "точнее строки разошлась бы с ней при сохранении."),
        { } quantity => quantity,
    };

    private static decimal Nonzero(decimal? value, string label) => value switch
    {
        null => throw new InvalidRequestException(
            $"Поле «{label}» не заполнено. У строки нет количества, и часть разноски — это доля суммы."),
        0 => throw new InvalidRequestException(
            $"Поле «{label}»: нуль. Ненужную часть удаляют, а не обнуляют."),
        { } amount when decimal.Round(amount, 2) != amount => throw new InvalidRequestException(
            $"Поле «{label}»: «{amount}» точнее копейки."),
        { } amount => amount,
    };
}
