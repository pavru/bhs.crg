using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Tables;
using BHS.CRG.Modules.Home;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Числа очередей счетов — одной службой на всех, кто их показывает (shell-08): адреса чипов экрана
/// (<c>/queues</c>, <c>/queues/parsing</c>) и Главная. Второй расчёт числа однажды разошёлся бы с
/// первым, а карточка Главной, обещающая «3» над списком из двух, хуже, чем никакой.
///
/// <para>«Разобрать» считается отдельно от остальных трёх (ревью PR #1302): те стоят на обратном
/// опросе ядра и падают вместе с ним, а это число — счёт по своей таблице, отказывать ему нечем.</para>
/// </summary>
public sealed class InvoiceQueueCounts(
    CostsDbContext db, InvoiceReferenceTrouble trouble, InvoiceListRecognition recognition)
{
    /// <summary>Черновиков под отбором «Разобрать» — тем же условием, каким отбирает список.</summary>
    public Task<int> ParsingAsync(CancellationToken ct) =>
        InvoiceListEndpoint.Queue(db.Invoices.AsNoTracking()).CountAsync(ct);

    /// <summary>Числа чипов «наведите порядок» — одним опросом ядра на все три.</summary>
    public async Task<InvoiceQueuesView> TroublesAsync(CancellationToken ct)
    {
        var troubles = await trouble.ReadAsync(withArchive: true, ct);
        var archived = troubles.Archived.ToArray();
        var keys = db.Database.SqlQuery<Guid>($"SELECT unnest({archived}) AS \"Value\"");
        return new InvoiceQueuesView(
            troubles.With(LostMark.Fixable).Length,
            archived.Length == 0 ? 0 : await InvoiceReferenceTrouble.InWork(db.Invoices.AsNoTracking())
                .CountAsync(i => keys.Contains(i.Id), ct),
            troubles.With(LostMark.Locked).Length + troubles.With(LostMark.Corrected).Length,
            troubles.Doubt,
            InvoiceListRecognition.Unrecognized(await recognition.ReadAsync(ct)).Length);
    }
}

/// <summary>
/// Очереди счетов на Главной (shell-08, ТЗ UI-34) — поставщик контракта «Очереди Главной». Объявления —
/// <see cref="Declarations" />; числа — <see cref="InvoiceQueueCounts" />, та же служба, что у чипов.
/// </summary>
public sealed class CostsHomeQueueCounts(InvoiceQueueCounts counts, ILogger<CostsHomeQueueCounts> log)
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
        if (codes.Contains(Parsing)) result.Add(new(Parsing, await counts.ParsingAsync(ct)));

        string[] troubleCodes = [Unrecognized, Lost, Archived];
        var asked = troubleCodes.Where(codes.Contains).ToList();
        if (asked.Count == 0) return result;

        // Опрос ссылок ядра отказал — «не посчитано» трём, а «Разобрать» уже пришла с числом.
        InvoiceQueuesView view;
        try
        {
            view = await counts.TroublesAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Очереди счетов Главной без чисел ссылок: обратный опрос ядра отказал");
            result.AddRange(asked.Select(code => new HomeQueueCount(code, null, Refused: "проверка ссылок не ответила")));
            return result;
        }

        foreach (var code in asked)
            result.Add(code switch
            {
                // Сомнение — у тех же очередей, что у чипа экрана: у «удалённых» и «в архиве».
                Lost => new HomeQueueCount(code, view.Lost, view.Doubt),
                Archived => new HomeQueueCount(code, view.Archived, view.Doubt),
                _ => new HomeQueueCount(code, view.Unrecognized),
            });
        return result;
    }
}
