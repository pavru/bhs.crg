using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>Что строка списка говорит о скане счёта.</summary>
/// <param name="State"><c>running</c> — скан читается; остальные — у черновика со сканом без строк:
/// <c>failed</c> — распознавание кончилось отказом или оборвалось, <c>done</c> — прочитано, но строк
/// нет, <c>none</c> — не распознавался.</param>
/// <param name="Reason">Вид отказа у <c>failed</c> — то же слово, что в форме.</param>
public sealed record InvoiceListScan(string State, string? Reason);

/// <summary>
/// Что список счетов знает о распознавании сканов (ТЗ COST-8, задача B1b, issue #1077).
///
/// <para><b>Одно определение — на пометку строки, на отбор и на число чипа.</b> Считай их три места
/// порознь, под чипом «3» стояло бы два счёта, а третий носил бы пометку вне отбора.</para>
///
/// <para><b>«Не распознано»</b> — черновик со сканом, в котором нет строк и скан сейчас не читается
/// (решение владельца 08.10.2026). Три случая, работа одна — счёт со сканом ещё пуст: распознавание
/// отказало, прочитало шапку без строк либо не запускалось. Какой из трёх — называет строка.
/// Строки появились — счёт заполнили, и звать человека обратно незачем. Счёт, ушедший из черновиков,
/// сюда тоже не входит: его разобрали.</para>
/// </summary>
public sealed class InvoiceListRecognition(CostsDbContext db, IModuleJobs jobs)
{
    public const string Running = "running";
    public const string Failed = "failed";
    public const string Done = "done";
    public const string None = "none";

    /// <summary>
    /// «Счёт → что со сканом» — только для счетов, о которых есть что сказать: читаются сейчас либо
    /// стоят под «Не распознано». Счёт со строками здесь не появляется, чем бы распознавание ни кончилось.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, InvoiceListScan>> ReadAsync(CancellationToken ct)
    {
        // Пустые черновики со сканом — рабочая очередь, их немного. Запись распознавания берётся левым
        // соединением: её может не быть вовсе («не распознавался»).
        var bare = await (
            from i in db.Invoices.AsNoTracking()
            where i.State == InvoiceState.Draft && i.ScanBlobPath != null
                && !db.InvoiceLines.Any(l => l.InvoiceId == i.Id)
            join r in db.InvoiceRecognitions.AsNoTracking() on i.Id equals r.InvoiceId into found
            from r in found.DefaultIfEmpty()
            select new
            {
                i.Id,
                // Запись о ПРЕЖНЕМ файле про нынешний скан не говорит ничего: скан заменили.
                Mine = r != null && r.ScanBlobPath == i.ScanBlobPath,
                Outcome = r != null ? r.Outcome : (InvoiceRecognitionOutcome?)null,
                JobId = r != null ? r.JobId : null,
                Reason = r != null ? r.Reason : null,
                StartedAt = r != null ? r.StartedAt : (DateTimeOffset?)null,
            }).ToListAsync(ct);

        // И те, что читаются, какими бы они ни были: у счёта со строками повтор тоже идёт.
        var pending = await db.InvoiceRecognitions.AsNoTracking()
            .Where(r => r.Outcome == InvoiceRecognitionOutcome.Pending)
            .Join(db.Invoices.AsNoTracking(), r => r.InvoiceId, i => i.Id, (r, i) => new { r, i })
            .Where(x => x.r.ScanBlobPath == x.i.ScanBlobPath)
            .Select(x => new { x.i.Id, x.r.JobId, x.r.StartedAt })
            .ToListAsync(ct);

        // Идёт ли на самом деле, знает только задача: запись «ждёт исхода» остаётся и у той, что упала
        // мимо нас. Задачи спрашиваются ПАЧКОЙ: список опрашивается, пока хоть один скан читается, и
        // обращение на каждую запись при загруженной пачке сканов шло бы десятками на каждый опрос.
        var jobStates = await jobs.GetManyAsync([.. pending.Select(p => p.JobId).OfType<Guid>()], ct);
        var alive = pending
            .Where(p => InvoiceScanRecognition.IsStarting(InvoiceRecognitionOutcome.Pending, p.JobId, p.StartedAt)
                || (p.JobId is { } job && InvoiceScanRecognition.IsAlive(jobStates.GetValueOrDefault(job))))
            .Select(p => p.Id)
            .ToHashSet();

        var states = alive.ToDictionary(id => id, _ => new InvoiceListScan(Running, null));
        foreach (var row in bare.Where(b => !alive.Contains(b.Id)))
            states[row.Id] = !row.Mine ? new(None, null) : row.Outcome switch
            {
                InvoiceRecognitionOutcome.Done => new(Done, null),
                InvoiceRecognitionOutcome.Failed => new(Failed, row.Reason),
                // Ждёт исхода, а задачи нет — прервано, как и в форме.
                _ => new(Failed, InvoiceRecognition.Interrupted),
            };

        return states;
    }

    /// <summary>Счета под отбором «Не распознано»: всё названное, кроме читающихся.</summary>
    public static Guid[] Unrecognized(IReadOnlyDictionary<Guid, InvoiceListScan> states) =>
        [.. states.Where(s => s.Value.State != Running).Select(s => s.Key)];
}
