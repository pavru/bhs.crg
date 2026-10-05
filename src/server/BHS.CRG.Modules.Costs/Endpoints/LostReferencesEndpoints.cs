using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>Сколько потеряно: ссылок и в скольких документах.</summary>
/// <param name="References">Ссылок. Удалённая стройка уносит и разделы — часть разноски даёт две.</param>
/// <param name="Invoices">В скольких счетах.</param>
/// <param name="Waybills">В скольких накладных.</param>
/// <param name="Other">Ссылок, у которых документ не назван: колонка новой таблицы, которую этот счётчик
/// ещё не знает, либо объявление без документа. Числом ссылок, а не документов, — и отдельно, чтобы они
/// не выдавали себя за накладные (ревью PR #1211).</param>
public sealed record LostTally(int References, int Invoices, int Waybills, int Other);

/// <summary>Объявленная ссылка модуля, которую опрос не проверил, — словами.</summary>
public sealed record UncheckedReferenceView(string What, string Reason);

/// <summary>Потерянные ссылки модуля на записи ядра (ТЗ CORE-34.3, issue #1184).</summary>
/// <param name="Editable">То, что ещё можно исправить, — это и есть «потеряно ссылок» (решение
/// владельца: записи закрытого периода в счётчик не идут, требовать их правки незачем).</param>
/// <param name="Locked">В счетах закрытого периода: исправить нельзя. Названо отдельно, а не выброшено —
/// иначе после отмены закрытия потери «появлялись бы из ниоткуда».</param>
/// <param name="Unchecked">Что не проверено. ⚠️ Нули при непустом этом списке — не «потерь нет».</param>
public sealed record LostReferencesView(
    LostTally Editable, LostTally Locked, IReadOnlyList<UncheckedReferenceView> Unchecked, DateTimeOffset AsOf);

/// <summary>
/// Счётчик потерянных ссылок модуля (ТЗ CORE-34.3, issue #1184).
///
/// <para><b>Ядро находит, модуль судит.</b> Какие ссылки потеряны, отвечает обратный опрос ядра по
/// объявлениям модуля (<see cref="IModuleReferenceTargets.LostAsync" />). Можно ли запись ещё править,
/// знает только модуль — тем же правилом, каким правку запирает закрытый период
/// (<see cref="ClosedPeriodGuard.LockOf" />).</para>
///
/// <para>Ничего не хранится: число считается на чтении. Потери возникают восстановлением копии и гонкой
/// удаления с записью — ровно там, где хранимая пометка устарела бы первой.</para>
/// </summary>
public static class LostReferencesEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/costs/lost-references", ReadAsync)
            .WithTags("Счета")
            .RequireAuthorization(AppPolicies.Permission("costs.invoice.read"));

    // Чей документ — говорит объявление ссылки (ReferenceDocument.Table), а не список таблиц здесь:
    // новая таблица, дочерняя к счёту, попадёт в счета сама — и под проверку закрытого периода тоже.
    private const string Invoices = "invoices";
    private const string Waybills = "waybills";

    private static async Task<Ok<LostReferencesView>> ReadAsync(
        CostsDbContext db, IModuleReferenceTargets targets, IModulePeriods periods, CancellationToken ct)
    {
        var found = await targets.LostAsync(CostsModule.ModuleCode, ct);

        var ofInvoices = found.Lost.Where(l => l is { DocumentKey: not null, DocumentTable: Invoices }).ToList();
        var ofWaybills = found.Lost.Where(l => l is { DocumentKey: not null, DocumentTable: Waybills }).ToList();
        var ofOther = found.Lost.Except(ofInvoices).Except(ofWaybills).ToList();
        var locked = await LockedAsync(db, periods, [.. ofInvoices.Select(l => l.DocumentKey).OfType<Guid>().Distinct()], ct);

        LostTally Tally(bool closed)
        {
            var invoices = ofInvoices.Where(l => (l.DocumentKey is { } key && locked.Contains(key)) == closed).ToList();
            // Накладная закрытым периодом не запирается: её потери — всегда из тех, что можно исправить.
            // То же с тем, чей документ не назван: запереть их нечем.
            var waybills = closed ? [] : ofWaybills;
            var other = closed ? 0 : ofOther.Sum(l => l.Rows);
            return new(
                invoices.Sum(l => l.Rows) + waybills.Sum(l => l.Rows) + other,
                invoices.Select(l => l.DocumentKey).Distinct().Count(),
                waybills.Select(l => l.DocumentKey).Distinct().Count(),
                other);
        }

        return TypedResults.Ok(new LostReferencesView(
            Tally(closed: false), Tally(closed: true),
            [.. found.Unchecked.Select(u => new UncheckedReferenceView(u.What, Reason(u.Reason)))],
            found.AsOf));
    }

    /// <summary>Счета из названных, запертые закрытым периодом. Запирается только оплаченный.</summary>
    private static async Task<IReadOnlySet<Guid>> LockedAsync(
        CostsDbContext db, IModulePeriods periods, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new HashSet<Guid>();

        var paid = await db.Invoices.AsNoTracking()
            .Where(i => ids.Contains(i.Id) && i.Payment == InvoicePaymentState.Paid).ToListAsync(ct);
        if (paid.Count == 0) return new HashSet<Guid>();

        var owners = paid.Select(i => i.Id).ToList();
        var parts = (await db.InvoiceAllocations.AsNoTracking().Where(a => owners.Contains(a.InvoiceId)).ToListAsync(ct))
            .ToLookup(a => a.InvoiceId);
        var boundaries = await periods.BoundariesAsync(ct);

        return paid.Where(i => ClosedPeriodGuard.LockOf(i, parts[i.Id], boundaries) is not null)
            .Select(i => i.Id).ToHashSet();
    }

    private static string Reason(UncheckedReason reason) => reason switch
    {
        UncheckedReason.MixedTargets => "вид записи не известен заранее — проверить негде",
        UncheckedReason.Unreadable => "данные не удалось прочитать",
        _ => "в базе нет такой колонки: схема модуля отстала от программы",
    };
}
