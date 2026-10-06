using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Tables;

/// <summary>Что со ссылками счёта на УДАЛЁННЫЕ записи ядра. Числа — коды клетки в запросе к базе.</summary>
public enum LostMark
{
    /// <summary>Удалённая запись стоит в поле, которое можно заменить, и счёт открыт для правки.</summary>
    Fixable = 1,

    /// <summary>Счёт заперт закрытым периодом: исправить нельзя, пока закрытие не отменят.</summary>
    Locked = 2,

    /// <summary>Удалён только тип счёта. Поля, в котором его заменяют, нет — и в отбор «исправьте» такой
    /// счёт не идёт: отбор обещает, что всё показанное можно поправить.</summary>
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
/// <param name="Archived">Счета со ссылкой на запись в архиве.</param>
/// <param name="Findings">Ответ ядра, по которому это посчитано: что не проверено и на какой момент.</param>
public sealed record InvoiceTroubles(
    IReadOnlyDictionary<Guid, LostMark> Lost,
    IReadOnlyDictionary<Guid, ArchivedMark> Archived,
    ReferenceFindings Findings)
{
    public static InvoiceTroubles None { get; } =
        new(new Dictionary<Guid, LostMark>(), new Dictionary<Guid, ArchivedMark>(), new([], [], DateTimeOffset.MinValue));

    /// <summary>Ключи счетов с такой пометкой — массивом: так его принимает запрос к базе.</summary>
    public Guid[] With(LostMark mark) => [.. Lost.Where(l => l.Value == mark).Select(l => l.Key)];

    /// <inheritdoc cref="With(LostMark)" />
    public Guid[] With(ArchivedMark mark) => [.. Archived.Where(a => a.Value == mark).Select(a => a.Key)];
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
/// <para><b>Ничего не хранится</b>, и опрос идёт один раз на запрос: служба живёт в его области, и
/// второй спросивший получает тот же ответ. Не один раз на приложение: потери приходят
/// восстановлением копии и гонкой удаления с записью — там, где запомненный ответ устарел бы первым.</para>
/// </summary>
public sealed class InvoiceReferenceTrouble(
    CostsDbContext db, IModuleReferenceTargets targets, IModulePeriods periods)
{
    // Чей документ — говорит объявление ссылки (ReferenceDocument.Table), а не список таблиц здесь:
    // новая таблица, дочерняя к счёту, попадёт в счета сама.
    public const string Invoices = "invoices";

    /// <summary>Колонка типа счёта. Названа, потому что заменить тип в форме нечем.</summary>
    private const string TypeColumn = "document_type_id";

    private Task<InvoiceTroubles>? _read;

    public Task<InvoiceTroubles> ReadAsync(CancellationToken ct) => _read ??= LoadAsync(ct);

    private async Task<InvoiceTroubles> LoadAsync(CancellationToken ct)
    {
        var findings = await targets.NotPresentAsync(CostsModule.ModuleCode, ct);
        var ofInvoices = findings.Found.Where(f => f is { DocumentKey: not null, DocumentTable: Invoices }).ToList();

        var lostKeys = ofInvoices.Where(f => f.State == ReferenceState.Lost).Select(f => f.DocumentKey!.Value).ToHashSet();
        var archivedKeys = ofInvoices.Where(f => f.State == ReferenceState.Archived).Select(f => f.DocumentKey!.Value).ToHashSet();
        if (lostKeys.Count == 0 && archivedKeys.Count == 0) return InvoiceTroubles.None with { Findings = findings };

        // Оплаченные из названных: запирается только оплаченный счёт, и «в работе» — тоже про оплату.
        var named = lostKeys.Union(archivedKeys).ToArray();
        var paid = await db.Invoices.AsNoTracking()
            .Where(i => named.Contains(i.Id) && i.Payment == InvoicePaymentState.Paid).ToListAsync(ct);
        var locked = await LockedAsync(paid.Where(i => lostKeys.Contains(i.Id)).ToList(), ct);

        // Счёт, у которого потеряно что-то кроме типа, исправим: остальное заменяют в полях.
        var fixable = ofInvoices.Where(f => f.State == ReferenceState.Lost && !IsType(f))
            .Select(f => f.DocumentKey!.Value).ToHashSet();
        var paidKeys = paid.Select(i => i.Id).ToHashSet();

        return new InvoiceTroubles(
            lostKeys.ToDictionary(id => id, id =>
                locked.Contains(id) ? LostMark.Locked : fixable.Contains(id) ? LostMark.Fixable : LostMark.TypeOnly),
            archivedKeys.ToDictionary(id => id, id => paidKeys.Contains(id) ? ArchivedMark.Paid : ArchivedMark.Open),
            findings);
    }

    /// <summary>Ссылка — тип счёта: поле, которого в форме нет.</summary>
    public static bool IsType(ReferenceFinding finding) => finding is { Table: Invoices, Column: TypeColumn };

    /// <summary>Счета из названных оплаченных, запертые закрытым периодом, — тем же правилом, каким
    /// правку запирает форма (<see cref="ClosedPeriodGuard.LockOf" />).</summary>
    private async Task<IReadOnlySet<Guid>> LockedAsync(IReadOnlyList<Invoice> paid, CancellationToken ct)
    {
        if (paid.Count == 0) return new HashSet<Guid>();

        var owners = paid.Select(i => i.Id).ToArray();
        var parts = (await db.InvoiceAllocations.AsNoTracking().Where(a => owners.Contains(a.InvoiceId)).ToListAsync(ct))
            .ToLookup(a => a.InvoiceId);
        var boundaries = await periods.BoundariesAsync(ct);

        return paid.Where(i => ClosedPeriodGuard.LockOf(i, parts[i.Id], boundaries) is not null)
            .Select(i => i.Id).ToHashSet();
    }
}
