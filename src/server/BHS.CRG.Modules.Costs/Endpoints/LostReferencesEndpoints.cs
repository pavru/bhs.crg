using BHS.CRG.Modules.Costs.Tables;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

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
/// <param name="Unfixable">Ссылок на удалённый ТИП счёта (issue #1186). Заменить тип в форме нечем,
/// поэтому ни в «можно исправить», ни в «заперто» они не идут — где бы счёт ни стоял и что бы ещё в нём
/// ни было потеряно. Своим числом, а не в <see cref="LostTally.Other" />: там ссылки без документа, и
/// одна и та же ссылка не должна считаться по-разному от соседей по счёту (ревью PR #1239).</param>
/// <param name="Unchecked">Что не проверено. ⚠️ Нули при непустом этом списке — не «потерь нет».</param>
public sealed record LostReferencesView(
    LostTally Editable, LostTally Locked, int Unfixable,
    IReadOnlyList<UncheckedReferenceView> Unchecked, DateTimeOffset AsOf);

/// <summary>
/// Счётчик потерянных ссылок модуля (ТЗ CORE-34.3, issue #1184).
///
/// <para><b>Ядро находит, модуль судит.</b> Какие ссылки потеряны, отвечает обратный опрос ядра по
/// объявлениям модуля (<see cref="IModuleReferenceTargets.NotPresentAsync" />). Можно ли запись ещё править,
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

    private const string Waybills = "waybills";

    private static async Task<Ok<LostReferencesView>> ReadAsync(InvoiceReferenceTrouble trouble, CancellationToken ct)
    {
        // Суждение о счетах — у общего места: то же множество кормит колонку таблицы счетов и отбор
        // (issue #1186). Свой расчёт здесь разошёлся бы с числом строк под отбором.
        // Архив счётчику не нужен: ссылок на архивные записи на порядки больше, чем потерянных.
        var troubles = await trouble.ReadAsync(withArchive: false, ct);
        var types = troubles.Findings.Lost.Where(InvoiceReferenceTrouble.IsType).ToList();
        var lost = troubles.Findings.Lost.Except(types).ToList();

        var ofInvoices = lost.Where(l => l is { DocumentKey: not null, DocumentTable: InvoiceReferenceTrouble.Invoices }).ToList();
        var ofWaybills = lost.Where(l => l is { DocumentKey: not null, DocumentTable: Waybills }).ToList();
        var ofOther = lost.Except(ofInvoices).Except(ofWaybills).ToList();

        LostTally Tally(bool closed)
        {
            var invoices = ofInvoices.Where(l => (troubles.Lost[l.DocumentKey!.Value] == LostMark.Locked) == closed).ToList();
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
            Tally(closed: false), Tally(closed: true), types.Sum(t => t.Rows),
            [.. troubles.Findings.Unchecked.Select(u => new UncheckedReferenceView(u.What, Reason(u.Reason)))],
            troubles.Findings.AsOf));
    }

    private static string Reason(UncheckedReason reason) => reason switch
    {
        UncheckedReason.MixedTargets => "вид записи не известен заранее — проверить негде",
        UncheckedReason.Unreadable => "данные не удалось прочитать",
        _ => "в базе нет такой колонки: схема модуля отстала от программы",
    };
}
