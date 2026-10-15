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

    /// <summary>Удалённая запись стоит в поле, которое можно заменить, а закрытых дней у счёта нет — но он
    /// уже исправлялся (есть книга, E2, issue #1100): обычная правка ему отказывает, менять его — только
    /// исправлением. Отдельно от <see cref="Locked" />: «период закрыт» отправил бы человека искать
    /// закрытие, которого нет (ревью PR #1364).</summary>
    Corrected = 4,
}

/// <summary>Что со ссылками счёта на записи ядра В АРХИВЕ.</summary>
public enum ArchivedMark
{
    /// <summary>Счёт в работе — не оплачен и не отклонён, — и архивную запись в нём стоит заметить.</summary>
    Open = 1,

    /// <summary>Счёт оплачен. Ссылка законна (issue #1185: сохранённые ссылки целы), и звать к правке
    /// незачем: старые счета закрывшегося поставщика верны навсегда (решение владельца 07.10.2026).</summary>
    Paid = 2,

    /// <summary>Счёт отклонён: по нему не платят, и править поставщика ради него незачем — как у
    /// оплаченного. Без этого значения отклонённый счёт закрывшегося поставщика стоял в очереди
    /// «В архиве» вечно: убрать его оттуда можно было только правкой отклонённого счёта (ревью PR #1290).</summary>
    Rejected = 3,
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

    /// <summary>
    /// Почему ответу нельзя верить как полному — словами для человека; null — проверено всё, что
    /// проверяется.
    ///
    /// <para>⚠️ Колонка со смешанными целями (<see cref="UncheckedReason.MixedTargets" /> — выбор в
    /// дополнительном поле счёта) сюда НЕ входит: она не проверяется по построению, всегда, и сомнение
    /// из-за неё стояло бы у числа вечно — то есть перестало бы что-либо значить. Здесь — только сбой:
    /// база отказала в чтении либо схема отстала от объявления.</para>
    ///
    /// <para>Чьей таблицы сбой — счетов или накладных — не разбираем: непроверенная колонка накладных
    /// счёту не мешает, но сказать лишнее «проверено не всё» дешевле, чем промолчать о своём.</para>
    /// </summary>
    public string? Doubt
    {
        get
        {
            // Числом, а не словами объявления: те названы от записи («счета этого поставщика») и в
            // подписи колонки читались бы как отбор.
            var failed = Findings.Unchecked.Count(u => u.Reason != UncheckedReason.MixedTargets);
            return failed == 0 ? null : $"проверено не всё: не прочитано колонок со ссылками — {failed}";
        }
    }

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
                !fixable.Contains(id) ? LostMark.TypeOnly : locked.GetValueOrDefault(id, LostMark.Fixable)),
            archived, findings);
    }

    /// <summary>Ссылка — тип счёта: поле, которого в форме нет.</summary>
    public static bool IsType(ReferenceFinding finding) => finding is { Table: Invoices, Column: TypeColumn };

    /// <summary>Зовёт ли архивная запись к правке этого счёта: только пока он в работе.</summary>
    public static ArchivedMark ArchivedMarkOf(Invoice invoice) =>
        invoice.Payment == InvoicePaymentState.Paid ? ArchivedMark.Paid
        : invoice.State == InvoiceState.Rejected ? ArchivedMark.Rejected
        : ArchivedMark.Open;

    /// <summary>
    /// То же суждение «счёт в работе» — отбором для базы. Отбор «В архиве» и его чип обязаны считать
    /// одно и то же; раньше условие было выписано в каждом месте своё.
    /// </summary>
    public static IQueryable<Invoice> InWork(IQueryable<Invoice> invoices) =>
        invoices.Where(i => i.Payment != InvoicePaymentState.Paid && i.State != InvoiceState.Rejected);

    /// <summary>Счета из названных, требующие исправления, — тем же правилом, каким обычную правку
    /// запирает форма (<see cref="ClosedPeriodGuard.NeedOf" />). Заперт закрытым периодом —
    /// <see cref="LostMark.Locked" />; закрытых дней нет, а книга есть — <see cref="LostMark.Corrected" />.
    /// Только оплаченный.</summary>
    private async Task<IReadOnlyDictionary<Guid, LostMark>> LockedAsync(IReadOnlySet<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new Dictionary<Guid, LostMark>();

        var named = ids.ToArray();
        var paid = await db.Invoices.AsNoTracking()
            .Where(i => named.Contains(i.Id) && i.Payment == InvoicePaymentState.Paid).ToListAsync(ct);
        if (paid.Count == 0) return new Dictionary<Guid, LostMark>();

        var owners = paid.Select(i => i.Id).ToArray();
        var parts = (await db.InvoiceAllocations.AsNoTracking().Where(a => owners.Contains(a.InvoiceId)).ToListAsync(ct))
            .ToLookup(a => a.InvoiceId);
        var boundaries = await periods.BoundariesAsync(ct);

        return paid.Select(i => (i.Id, Need: ClosedPeriodGuard.NeedOf(i, parts[i.Id], boundaries)))
            .Where(n => n.Need is not null)
            .ToDictionary(n => n.Id, n => n.Need!.Lock is null ? LostMark.Corrected : LostMark.Locked);
    }
}
