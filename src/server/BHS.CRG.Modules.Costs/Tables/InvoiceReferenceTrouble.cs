using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Tables;

/// <summary>Что со ссылками счёта на УДАЛЁННЫЕ записи ядра. Числа — коды клетки в запросе к базе.</summary>
public enum LostMark
{
    /// <summary>Удалённая запись стоит в поле, которое можно заменить, и счёт открыт для правки.</summary>
    Fixable = 1,

    /// <summary>Удалённая запись стоит в поле, которое можно заменить, но счёт заперт закрытым периодом:
    /// исправить нельзя, пока закрытие не отменят. Отменят — счёт станет <see cref="Fixable" />.</summary>
    Locked = 2,

    /// <summary>Удалён только тип счёта. Поля, в котором его заменяют, нет — и в отбор «исправьте» такой
    /// счёт не идёт: отбор обещает, что всё показанное можно поправить. Запертость тут ничего не меняет,
    /// поэтому стоит ВЫШЕ неё: «период закрыт» обещал бы, что после отмены закрытия счёт исправят
    /// (ревью PR #1239).</summary>
    TypeOnly = 3,
}

/// <summary>Что со ссылками счёта на записи ядра В АРХИВЕ.</summary>
public enum ArchivedMark
{
    /// <summary>Счёт ещё не оплачен — он в работе, и архивную запись в нём стоит заметить.</summary>
    Open = 1,

    /// <summary>Счёт оплачен. Ссылка законна (issue #1185: сохранённые ссылки целы), и звать к правке
    /// незачем: старые счета закрывшегося поставщика верны навсегда (решение владельца 07.10.2026).</summary>
    Paid = 2,
}

/// <summary>
/// Счета, у которых ссылки на записи ядра не на месте, — с суждением модуля о каждом.
/// </summary>
/// <param name="Lost">Счета со ссылкой на удалённую запись.</param>
/// <param name="Archived">Счета со ссылкой на запись в архиве; пусто и тогда, когда об архиве не
/// спрашивали. Оплачен ли счёт, здесь не сказано: это знает сам счёт
/// (<see cref="InvoiceReferenceTrouble.ArchivedMarkOf" />), а множество бывает большим.</param>
/// <param name="Findings">Ответ ядра, по которому это посчитано: что не проверено и на какой момент.</param>
public sealed record InvoiceTroubles(
    IReadOnlyDictionary<Guid, LostMark> Lost,
    IReadOnlySet<Guid> Archived,
    ReferenceFindings Findings)
{
    public static InvoiceTroubles None { get; } =
        new(new Dictionary<Guid, LostMark>(), new HashSet<Guid>(), new([], [], DateTimeOffset.MinValue));

    /// <summary>Ключи счетов с такой пометкой — массивом: так его принимает запрос к базе.</summary>
    public Guid[] With(LostMark mark) => [.. Lost.Where(l => l.Value == mark).Select(l => l.Key)];
}

/// <summary>
/// Одно место, которое знает, у каких счетов ссылки не на месте и можно ли это исправить (issue #1186).
///
/// <para><b>Ядро находит, модуль судит</b> — как у счётчика потерь (#1184). Какие ссылки потеряны и
/// какие ведут в архив, отвечает обратный опрос ядра; запертость периодом и оплату знает только модуль.</para>
///
/// <para><b>Зачем одно место.</b> Отсюда берут и колонку таблицы счетов, и счётчик, и отбор списка.
/// Посчитай каждый своё — число на чипе разошлось бы с числом строк под ним, а в отборе «исправьте»
/// оказался бы счёт, который править нельзя: это два способа, которыми задача ломается.</para>
///
/// <para><b>Ничего не хранится и не запоминается</b> — ни между запросами, ни внутри одного: каждый
/// вызов опрашивает заново. Потери приходят восстановлением копии и гонкой удаления с записью — там,
/// где запомненный ответ устарел бы первым; а ответ, запомненный на область, пережил бы правку счёта,
/// сделанную в той же области, и повторял бы отказ первого спросившего (ревью PR #1239).</para>
///
/// <para>⚠️ <b>Это не один снимок базы.</b> Ссылки ядро читает в своём снимке и на своём соединении;
/// запертость — следующим запросом к схеме модуля, строки таблицы — ещё одним. Счёт, оплаченный или
/// запертый между ними, получит суждение по старым ссылкам и новому состоянию. Вреда нет — правку
/// запертого счёта отвергнет форма, а число перечитывается с таблицей, — но равенство «число на чипе —
/// число строк под ним» держится в покое, а не под записью.</para>
/// </summary>
public sealed class InvoiceReferenceTrouble(
    CostsDbContext db, IModuleReferenceTargets targets, IModulePeriods periods)
{
    // Чей документ — говорит объявление ссылки (ReferenceDocument.Table), а не список таблиц здесь:
    // новая таблица, дочерняя к счёту, попадёт в счета сама.
    public const string Invoices = "invoices";

    /// <summary>Колонка типа счёта. Названа, потому что заменить тип в форме нечем.</summary>
    private const string TypeColumn = "document_type_id";

    /// <param name="withArchive">Нужны ли и счета со ссылкой на запись в архиве. Тому, кто спрашивает
    /// о потерях, — нет: архивных ссылок на порядки больше, и читать их ради нуля незачем.</param>
    public async Task<InvoiceTroubles> ReadAsync(bool withArchive, CancellationToken ct)
    {
        var findings = await targets.NotPresentAsync(CostsModule.ModuleCode, withArchive, ct);
        var ofInvoices = findings.Found.Where(f => f is { DocumentKey: not null, DocumentTable: Invoices }).ToList();

        var lost = ofInvoices.Where(f => f.State == ReferenceState.Lost).ToList();
        var archived = ofInvoices.Where(f => f.State == ReferenceState.Archived)
            .Select(f => f.DocumentKey!.Value).ToHashSet();

        // Счёт, у которого потеряно что-то кроме типа, исправим: остальное заменяют в полях. Запертость
        // спрашиваем только у таких — у счёта с одним удалённым типом она ничего не решает.
        var fixable = lost.Where(f => !IsType(f)).Select(f => f.DocumentKey!.Value).ToHashSet();
        var locked = await LockedAsync(fixable, ct);

        return new InvoiceTroubles(
            lost.Select(f => f.DocumentKey!.Value).Distinct().ToDictionary(id => id, id =>
                !fixable.Contains(id) ? LostMark.TypeOnly : locked.Contains(id) ? LostMark.Locked : LostMark.Fixable),
            archived, findings);
    }

    /// <summary>Ссылка — тип счёта: поле, которого в форме нет.</summary>
    public static bool IsType(ReferenceFinding finding) => finding is { Table: Invoices, Column: TypeColumn };

    /// <summary>Зовёт ли архивная запись к правке этого счёта: только пока он не оплачен.</summary>
    public static ArchivedMark ArchivedMarkOf(Invoice invoice) =>
        invoice.Payment == InvoicePaymentState.Paid ? ArchivedMark.Paid : ArchivedMark.Open;

    /// <summary>Счета из названных, запертые закрытым периодом, — тем же правилом, каким правку
    /// запирает форма (<see cref="ClosedPeriodGuard.LockOf" />). Запирается только оплаченный.</summary>
    private async Task<IReadOnlySet<Guid>> LockedAsync(IReadOnlySet<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new HashSet<Guid>();

        var named = ids.ToArray();
        var paid = await db.Invoices.AsNoTracking()
            .Where(i => named.Contains(i.Id) && i.Payment == InvoicePaymentState.Paid).ToListAsync(ct);
        if (paid.Count == 0) return new HashSet<Guid>();

        var owners = paid.Select(i => i.Id).ToArray();
        var parts = (await db.InvoiceAllocations.AsNoTracking().Where(a => owners.Contains(a.InvoiceId)).ToListAsync(ct))
            .ToLookup(a => a.InvoiceId);
        var boundaries = await periods.BoundariesAsync(ct);

        return paid.Where(i => ClosedPeriodGuard.LockOf(i, parts[i.Id], boundaries) is not null)
            .Select(i => i.Id).ToHashSet();
    }
}
