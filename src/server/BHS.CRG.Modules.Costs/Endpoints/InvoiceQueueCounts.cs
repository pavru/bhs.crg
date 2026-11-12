using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Tables;
using BHS.CRG.Modules.Home;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Число «Разобрать» — счёт по своей таблице, отказывать ему нечем (ревью PR #1302). Отдельной службой
/// с одной базой модуля: адрес чипа (<c>/queues/parsing</c>) и Главная не должны падать вместе с портами
/// ядра, которые нужны остальным очередям, — даже на сборке зависимостей.
/// </summary>
public sealed class InvoiceParsingQueue(CostsDbContext db)
{
    /// <summary>Черновиков под отбором «Разобрать» — тем же условием, каким отбирает список.</summary>
    public Task<int> CountAsync(CancellationToken ct) =>
        InvoiceListEndpoint.Queue(db.Invoices.AsNoTracking()).CountAsync(ct);
}

/// <summary>Числа «удалённых записей» и «в архиве» — из одного обратного опроса ссылок ядра.</summary>
public sealed record InvoiceReferenceCounts(int Lost, int Archived, int Locked, string? Doubt);

/// <summary>
/// Числа очередей «наведите порядок» — одной службой на всех, кто их показывает (shell-08): адрес чипов
/// экрана (<c>/queues</c>) и Главная. Второй расчёт числа однажды разошёлся бы с первым, а карточка
/// Главной, обещающая «3» над списком из двух, хуже, чем никакой.
///
/// <para>Источники у них разные, и отказывают они порознь: «удалённые» и «в архиве» — обратный опрос
/// ссылок ядра, «Не распознано» — задачи распознавания (<see cref="InvoiceListRecognition" />). Главная
/// спрашивает их по отдельности: отказ одного не прячет число другого и называет свою причину.</para>
/// </summary>
public sealed class InvoiceQueueCounts(
    CostsDbContext db, InvoiceReferenceTrouble trouble, InvoiceListRecognition recognition)
{
    /// <summary>«Удалённые записи» и «в архиве» — одним опросом ядра на обе.</summary>
    public async Task<InvoiceReferenceCounts> ReferencesAsync(CancellationToken ct)
    {
        var troubles = await trouble.ReadAsync(withArchive: true, ct);
        var archived = troubles.Archived.ToArray();
        var keys = db.Database.SqlQuery<Guid>($"SELECT unnest({archived}) AS \"Value\"");
        return new InvoiceReferenceCounts(
            troubles.With(LostMark.Fixable).Length,
            archived.Length == 0 ? 0 : await InvoiceReferenceTrouble.InWork(db.Invoices.AsNoTracking())
                .CountAsync(i => keys.Contains(i.Id), ct),
            troubles.With(LostMark.Locked).Length + troubles.With(LostMark.Corrected).Length,
            troubles.Doubt);
    }

    /// <summary>«Не распознано» — по задачам распознавания, без опроса ссылок.</summary>
    public async Task<int> UnrecognizedAsync(CancellationToken ct) =>
        InvoiceListRecognition.Unrecognized(await recognition.ReadAsync(ct)).Length;

    /// <summary>Числа чипов «наведите порядок» одним ответом — для адреса экрана (<c>/queues</c>).</summary>
    public async Task<InvoiceQueuesView> TroublesAsync(CancellationToken ct)
    {
        var references = await ReferencesAsync(ct);
        return new InvoiceQueuesView(references.Lost, references.Archived, references.Locked, references.Doubt,
            await UnrecognizedAsync(ct));
    }
}

/// <summary>
/// Очереди счетов на Главной (shell-08, ТЗ UI-34) — поставщик контракта «Очереди Главной». Объявления —
/// <see cref="Declarations" />; числа — <see cref="InvoiceParsingQueue" /> и <see cref="InvoiceQueueCounts" />,
/// те же службы, что у чипов.
/// </summary>
public sealed class CostsHomeQueueCounts(
    InvoiceParsingQueue parsing, InvoiceQueueCounts counts, ILogger<CostsHomeQueueCounts> log)
    : IHomeQueueCounts
{
    public const string Parsing = "parsing";
    public const string Unrecognized = "unrecognized";
    public const string Lost = InvoiceListReferences.FixLost;
    public const string Archived = InvoiceListReferences.FixArchived;

    private const string Read = "costs.invoice.read";

    /// <summary>
    /// Очереди счетов. Подписи — как у чипов экрана (<c>QUEUE_LABEL</c> клиента), пояснения — с макета
    /// «B · Главная». Права — правило чипа:
    /// «Разобрать» видит каждый читающий счета; остальные три зовут к правке, и тому, кто исправить не
    /// может, число было бы упрёком без выхода. Чтение у них тоже названо: без него карточка вела бы
    /// на закрытый экран.
    /// </summary>
    public static IReadOnlyList<ModuleHomeQueue> Declarations { get; } =
    [
        new(Parsing, "Разобрать", "Черновики, которые ждут проверки", [Read]),
        new(Unrecognized, "Не распознано", "Скан не прочитался, поля пустые", [Read, CostsModule.InvoiceEdit]),
        new(Lost, "Удалённые записи", "Счета ссылаются на то, чего больше нет", [Read, CostsModule.InvoiceEdit]),
        new(Archived, "В архиве", "Неоплаченные счета с записью из архива", [Read, CostsModule.InvoiceEdit],
            Quiet: true),
    ];

    public async Task<IReadOnlyList<HomeQueueCount>> CountAsync(
        ModuleReader reader, IReadOnlyList<string> codes, CancellationToken ct)
    {
        var result = new List<HomeQueueCount>();
        if (codes.Contains(Parsing)) result.Add(new(Parsing, await parsing.CountAsync(ct)));

        if (codes.Contains(Unrecognized))
            result.Add(await AskAsync(Unrecognized, "задачи распознавания не ответили",
                async () => new HomeQueueCount(Unrecognized, await counts.UnrecognizedAsync(ct))));

        string[] referenceCodes = [Lost, Archived];
        var asked = referenceCodes.Where(codes.Contains).ToList();
        if (asked.Count == 0) return result;
        // Опрос ссылок ядра отказал — «не посчитано» обеим его очередям; прочие пришли со своими числами.
        InvoiceReferenceCounts view;
        try
        {
            view = await counts.ReferencesAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Очереди счетов Главной без чисел ссылок: обратный опрос ядра отказал");
            result.AddRange(asked.Select(code => new HomeQueueCount(code, null, Refused: "проверка ссылок не ответила")));
            return result;
        }
        // Сомнение — у тех же очередей, что у чипа экрана: у «удалённых» и «в архиве».
        result.AddRange(asked.Select(code =>
            new HomeQueueCount(code, code == Lost ? view.Lost : view.Archived, view.Doubt)));
        return result;
    }

    /// <summary>Одна очередь со своим источником: его отказ — «не посчитано» с причиной, в журнал — исключение.</summary>
    private async Task<HomeQueueCount> AskAsync(string code, string refused, Func<Task<HomeQueueCount>> count)
    {
        try
        {
            return await count();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Очередь счетов Главной {Code} без числа: источник отказал", code);
            return new HomeQueueCount(code, null, Refused: refused);
        }
    }
}
