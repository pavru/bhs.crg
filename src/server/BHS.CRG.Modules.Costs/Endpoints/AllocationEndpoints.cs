using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Разноска строки по стройкам и разделам (задача F1 этапа 2, issue #1085, ТЗ COST-10, COST-11,
/// COST-13, COST-15).
///
/// <para><b>Своё право — <c>costs.allocation.edit</c></b>, а не правка счёта: разноска решает, на
/// какую стройку легли деньги, и может принадлежать не тому, кто вводит счета (ТЗ COST-28).</para>
///
/// <para><b>Разноска не запрещена у разобранного счёта</b> (ТЗ COST-15): её правят и после, пока
/// период не закрыт. Но разобранный счёт, разноска которого перестала сходиться, САМ возвращается в
/// черновик — тем же приёмом, что правка строк: «разобран» не может остаться утверждением, переставшим
/// быть правдой.</para>
/// </summary>
public static class AllocationEndpoints
{
    private const string Edit = "costs.allocation.edit";

    public static void MapAllocation(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPut("/api/costs/invoices/{id:guid}/lines/{lineId:guid}/allocation", ReplaceAsync)
            .RequireAuthorization(AppPolicies.Permission(Edit))
            .WithTags("Счета на оплату");

        // Правом на счета ИЛИ на накладные (D1, issue #1083): стройку выбирают и получателем
        // накладной. См. CostsLookups.
        endpoints.MapGet("/api/costs/constructions", ConstructionsAsync)
            .RequireAuthorization(AppPolicies.Module("costs"))
            .AddEndpointFilter(CostsLookups.RequireDocumentReader)
            .WithTags("Счета на оплату");
    }

    /// <summary>
    /// Заменить части разноски строки присланным набором.
    ///
    /// <para>⚠️ <b>Разнесено больше, чем есть, — отказ</b>, а не «остаток минус сорок». Остаток «не
    /// разнесено» бывает отрицательным только у строки, которую уменьшили после разноски, — там это
    /// показывается числом, и «разобран» не проходит. Принять же перебор сразу значило бы сохранить
    /// заведомую ошибку ввода.</para>
    /// </summary>
    private static async Task<Ok<InvoiceView>> ReplaceAsync(
        Guid id, Guid lineId, AllocationRequest body, CostsDbContext db, InvoiceDesk desk,
        AllocationPlacesSource places, IModuleActivityLog log, CancellationToken ct)
    {
        if (body.Parts is null)
            throw new InvalidRequestException(
                "Набор частей не прислан. Пустой набор — это «parts»: [], и он означает «строка не разнесена». " +
                "Отсутствие поля прочитать как «не менять» нельзя: адрес заменяет набор целиком.");

        // Цели, уже записанные у этой строки, запись принимает и потерянными (ТЗ CORE-34.4).
        var known = (await places.LoadAsync(ct)).Keeping(
            await db.InvoiceAllocations.AsNoTracking().Where(a => a.InvoiceId == id && a.LineId == lineId).ToListAsync(ct));
        var (invoice, line, was, values, returned) = await desk.WriteAsync(id, write => PlaceAsync(write.Invoice), ct);

        if (!was.SequenceEqual(values))
            await log.RecordAsync(InvoiceActions.AllocationChanged, invoice.Id.ToString(),
                InvoiceEndpoints.Label(invoice),
                before: $"строка {line.Ordinal}: {InvoiceAllocations.Describe(was, known, line.Unit)}",
                after: InvoiceAllocations.DescribeAfter(
                    $"строка {line.Ordinal}: {InvoiceAllocations.Describe(was, known, line.Unit)}",
                    $"строка {line.Ordinal}: {InvoiceAllocations.Describe(values, known, line.Unit)}"),
                ct: ct);

        if (returned)
            await log.RecordAsync(InvoiceActions.Draft, invoice.Id.ToString(),
                InvoiceEndpoints.Label(invoice), after: "правка разноски: баланс не сходится", ct: ct);

        return TypedResults.Ok(await desk.ViewAsync(invoice, ct));

        // Сама правка — под замком записи, по счёту, прочитанному после него.
        async Task<(Invoice, InvoiceLine, List<AllocationValues>, List<AllocationValues>, bool)> PlaceAsync(Invoice invoice)
        {
            var line = await db.InvoiceLines.AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == lineId && l.InvoiceId == invoice.Id, ct)
                ?? throw new NotFoundException(
                    "Строки у этого счёта нет. Так бывает, когда строку удалили, пока форма была открыта: " +
                    "перечитайте счёт.");

            var mode = AllocationMath.ModeOf(line.Quantity, line.Amount);
            var parsed = body.Parts
                .Select((part, index) => (Id: InvoiceAllocations.Id(part, index + 1),
                    Values: InvoiceAllocations.Values(part, index + 1, mode)))
                .ToList();

            EnsureIdsDistinct(parsed.Select(p => p.Id));
            var values = parsed.Select(p => p.Values).ToList();
            InvoiceAllocations.EnsureTargets(values, known);
            EnsureNotOver(line, values);

            var existing = await db.InvoiceAllocations
                .Where(a => a.LineId == line.Id)
                .OrderBy(a => a.Ordinal)
                .ToListAsync(ct);
            var was = existing.Select(a => a.Snapshot()).ToList();
            var now = new List<InvoiceAllocation>(parsed.Count);

            for (var index = 0; index < parsed.Count; index++)
            {
                var (partId, part) = parsed[index];
                var entity = partId is { } sent
                    ? existing.FirstOrDefault(a => a.Id == sent)
                        ?? throw new InvalidRequestException(
                            $"Часть {index + 1}: части {sent} у этой строки нет. Так бывает, когда её удалили, " +
                            "пока форма была открыта. Перечитайте счёт и повторите правку — иначе удалённая часть " +
                            "вернулась бы молча.")
                    : Added(db, invoice.Id, line.Id);

                entity.Apply(index + 1, part);
                now.Add(entity);
            }

            var removed = existing.Except(now).ToList();
            db.InvoiceAllocations.RemoveRange(removed);

            // Возврат в черновик — ДО сохранения, по состоянию после правки: части прочих строк из базы,
            // части этой строки — те, что сейчас лягут.
            var returned = invoice.State == InvoiceState.Parsed
                && !await InvoiceAllocations.AllocatedAfterAsync(db, places, invoice,
                    await InvoiceLineEndpoints.StoredLinesAsync(db, invoice, ct), ct, id => id == line.Id, now);
            if (returned) invoice.ReturnToDraft();

            // Разноска — часть счёта: её правка отмечается у него самого. От этого зависит и время правки
            // счёта, и защита от одновременной записи (issue #1173).
            var changed = !was.SequenceEqual(values);
            if (changed) invoice.ContentChanged();

            await db.SaveChangesAsync(ct);
            return (invoice, line, was, values, returned);
        }
    }

    /// <summary>Стройки с разделами — для выбора цели части. Узким списком модуля, как организации.</summary>
    private static async Task<Ok<IReadOnlyList<ModuleConstruction>>> ConstructionsAsync(
        IModuleConstructions sites, CancellationToken ct) =>
        TypedResults.Ok(await sites.ListAsync(ct));

    /// <summary>
    /// Разнесено больше, чем есть в строке, — отказ (см. <see cref="ReplaceAsync" />).
    ///
    /// <para>⚠️ У строки суммой знак каждой части — знак строки. Сверки одной суммы частей мало: +300 и
    /// −200 на строке в 100 ₽ дают те же 100, но одна стройка получила бы 300 ₽ затрат из ниоткуда, а
    /// другая — отрицательные.</para>
    /// </summary>
    internal static void EnsureNotOver(InvoiceLine line, IReadOnlyList<AllocationValues> parts)
    {
        if (line.Quantity is > 0 and var quantity)
        {
            var given = parts.Sum(p => p.Quantity ?? 0m);
            if (given > quantity)
                throw new InvalidRequestException(
                    $"Разнесено {given:0.###}, а в строке {quantity:0.###}{(line.Unit is { } unit ? " " + unit : "")}. " +
                    "Разнести больше, чем куплено, нельзя: лишнее легло бы в затраты стройки из ниоткуда.");
            return;
        }

        if (line.Amount is { } amount)
        {
            var opposite = parts.Select((p, index) => (Number: index + 1, p.Amount))
                .FirstOrDefault(p => p.Amount is { } value && Math.Sign(value) != Math.Sign(amount));
            if (opposite.Amount is not null)
                throw new InvalidRequestException(
                    $"Часть {opposite.Number}: {opposite.Amount:0.00} ₽ при сумме строки {amount:0.00} ₽. Знак " +
                    "части — знак строки: часть с обратным знаком позволила бы разнести на одну стройку больше, " +
                    "чем стоит строка, списав разницу с другой.");

            var spent = parts.Sum(p => p.Amount ?? 0m);
            if (Math.Abs(spent) > Math.Abs(amount))
                throw new InvalidRequestException(
                    $"Разнесено {spent:0.00} ₽, а сумма строки {amount:0.00} ₽. Разнести больше, чем стоит " +
                    "строка, нельзя: лишнее легло бы в затраты стройки из ниоткуда.");
        }
    }

    private static InvoiceAllocation Added(CostsDbContext db, Guid invoiceId, Guid lineId)
    {
        var part = InvoiceAllocation.Create(invoiceId, lineId);
        db.InvoiceAllocations.Add(part);
        return part;
    }

    /// <summary>Одна и та же часть дважды — отказ: набор заменяет состояние, и одна из двух исчезла бы.</summary>
    private static void EnsureIdsDistinct(IEnumerable<Guid?> ids)
    {
        var repeated = ids.OfType<Guid>().GroupBy(i => i).FirstOrDefault(g => g.Count() > 1);
        if (repeated is not null)
            throw new InvalidRequestException(
                $"Часть {repeated.Key} прислана дважды. Набор заменяет состояние целиком, и одна из двух частей " +
                "исчезла бы без следа. Новые части присылайте без «id».");
    }
}
