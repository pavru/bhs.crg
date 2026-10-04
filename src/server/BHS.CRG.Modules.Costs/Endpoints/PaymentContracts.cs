using System.Security.Cryptography;
using System.Text;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>Предпросмотр оплаты: какой датой. Без даты — «сегодня» по часам компании.</summary>
public sealed record PaymentPreviewRequest(DateOnly? PaidOn);

/// <summary>Отметка оплаты (задача C5, issue #1082).</summary>
/// <param name="Document">Платёжный документ — необязательный текст.</param>
/// <param name="Seen">Отметка расклада, который человек видел в предпросмотре (<c>PaymentPosting.stamp</c>).
/// Без неё оплату подтверждал бы человек, а записывался бы расклад, которого он не видел: период могли
/// закрыть, разноску — поправить.</param>
public sealed record PaymentRequest(DateOnly? PaidOn, string? Document, string? Seen);

/// <summary>Правка платёжного документа оплаченного счёта.</summary>
public sealed record PaymentDocumentRequest(string? Document);

/// <summary>Отмена ошибочной отметки оплаты — с причиной: она остаётся единственным следом в журнале.</summary>
public sealed record PaymentCancelRequest(string? Reason);

/// <summary>Доля внутри строки расклада: что именно легло на стройку.</summary>
public sealed record PostingPartView(string Label, decimal? Amount);

/// <summary>
/// Строка расклада — контур и день: стройка, статьи вне строек или неразнесённый остаток.
///
/// <para>Строка — контур, а не доля: учётную дату определяет стройка, а не строка счёта и не раздел,
/// и двадцать строк на три стройки — это три строки расклада (ревизия Дизайнера).</para>
/// </summary>
/// <param name="Kind"><c>construction</c>, <c>articles</c> или <c>remainder</c>.</param>
/// <param name="Note">Почему дата не совпала с датой платежа — словами сервера; пусто, если совпала.</param>
public sealed record PostingRowView(
    string Kind,
    Guid? ConstructionId,
    string Name,
    decimal? Amount,
    DateOnly AccountingOn,
    bool Moved,
    string? Note,
    IReadOnlyList<PostingPartView> Parts);

/// <summary>
/// Расклад оплаты — и предпросмотр до записи, и чтение записанного.
/// </summary>
/// <param name="Refusal">Почему оплатить нельзя при любой дате; тогда расклада нет.</param>
/// <param name="DateRefusal">Почему нельзя оплатить ЭТОЙ датой; тогда расклада тоже нет.</param>
/// <param name="Stamp">Отметка расклада — её возвращает запись оплаты.</param>
public sealed record PaymentPostingView(
    DateOnly Today,
    DateOnly PaidOn,
    decimal? Total,
    string? Refusal,
    string? DateRefusal,
    IReadOnlyList<PostingRowView> Rows,
    string Stamp);

/// <summary>Оплата в ответе счёта.</summary>
/// <param name="Refusal">Почему счёт сейчас оплатить нельзя — словами сервера; у оплаченного пусто.</param>
/// <param name="LockedBy">Чем счёт заперт: «период закрыт по 30.09.2026 у стройки «А»». Признак
/// запертого — только это поле, а не «оплачен»: оплаченный счёт открытого периода правится.</param>
/// <param name="Periods">Учётные месяцы счёта — «09.2026», по возрастанию.</param>
public sealed record PaymentView(
    bool Paid,
    DateOnly? PaidOn,
    string? Document,
    DateTimeOffset? PaidAt,
    string? Refusal,
    string? LockedBy,
    IReadOnlyList<string> Periods);

/// <summary>Сборка ответов оплаты.</summary>
public static class PaymentViews
{
    public const string Construction = "construction";
    public const string Articles = "articles";
    public const string Remainder = "remainder";

    /// <summary>Месяц учётной даты — с годом: «12 + 01» на границе года не читается.</summary>
    public static string Month(DateOnly date) => $"{date:MM.yyyy}";

    public static IReadOnlyList<string> Months(IEnumerable<DateOnly> dates) =>
        [.. dates.Select(d => new DateOnly(d.Year, d.Month, 1)).Distinct().Order().Select(Month)];

    /// <summary>Чем заперт счёт — словами: их показывает форма и ими же отказывает запись.</summary>
    public static string Text(PeriodLock locked, AllocationPlaces places) =>
        locked.ConstructionId is { } site
            ? $"период закрыт по {locked.Through:dd.MM.yyyy} у стройки «{places.Site(site)?.Name ?? "удалена"}»"
            : $"период компании закрыт по {locked.Through:dd.MM.yyyy}";

    /// <summary>Строки расклада: переносимые сверху, затем стройки по названию, статьи и остаток — последними.</summary>
    public static IReadOnlyList<PostingRowView> Rows(
        PaymentPlan plan, IReadOnlyDictionary<Guid, int> lineOrdinals, AllocationPlaces places,
        PeriodBoundaries boundaries)
    {
        var rows = plan.Shares
            .GroupBy(s => (Site: s.Target.ConstructionId, s.AccountingOn))
            .Select(g =>
            {
                var contour = PaymentPosting.ContourOf(g.First().Target);
                var known = g.Where(s => s.Amount is not null).ToList();
                return new PostingRowView(
                    g.Key.Site is null ? Articles : Construction,
                    g.Key.Site,
                    g.Key.Site is { } site ? places.Site(site)?.Name ?? "Стройка удалена" : "Статьи вне строек",
                    known.Count == 0 ? null : known.Sum(s => s.Amount!.Value),
                    g.Key.AccountingOn,
                    g.Key.AccountingOn != plan.PaidOn,
                    Note(plan.PaidOn, g.Key.AccountingOn, contour, boundaries),
                    [.. g.Select(s => new PostingPartView(Part(s, lineOrdinals, places), s.Amount))]);
            })
            .ToList();

        if (plan.Remainder is { } rest)
            rows.Add(new PostingRowView(Remainder, null, "Не разнесено", rest.Amount, rest.AccountingOn, rest.Moved,
                Note(plan.PaidOn, rest.AccountingOn, new PeriodContour.Company(), boundaries), []));

        return [.. rows
            .OrderByDescending(r => r.Moved)
            .ThenBy(r => r.Kind switch { Construction => 0, Articles => 1, _ => 2 })
            .ThenBy(r => r.Name, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), true))
            .ThenBy(r => r.AccountingOn)];
    }

    /// <summary>
    /// Почему дата перенесена. Причину называем только ту, что верна СЕЙЧАС: у записанного расклада
    /// период могли с тех пор открыть, и «закрыто по…» про открытый месяц было бы неправдой.
    /// </summary>
    private static string? Note(DateOnly paidOn, DateOnly accountingOn, PeriodContour contour, PeriodBoundaries boundaries)
    {
        if (accountingOn == paidOn) return null;

        var whose = contour is PeriodContour.Construction ? "у стройки" : "у компании";
        return boundaries.ClosedThrough(contour) is { } through && through.AddDays(1) == accountingOn
            ? $"{whose} закрыто по {through:dd.MM.yyyy} — войдёт в затраты с {accountingOn:dd.MM.yyyy}"
            : $"учётная дата {accountingOn:dd.MM.yyyy} записана, когда период платежа был закрыт";
    }

    private static string Part(PostedShare share, IReadOnlyDictionary<Guid, int> lineOrdinals, AllocationPlaces places)
    {
        var where = share.LineId is { } line && lineOrdinals.TryGetValue(line, out var ordinal)
            ? $"строка {ordinal}"
            : "счёт целиком";

        if (share.Target.ArticleId is { } article)
            return $"{where} — {places.Article(article)?.Name ?? "статья удалена"}";

        var section = share.Target.SectionId is { } id
            ? places.Site(share.Target.ConstructionId)?.Sections.FirstOrDefault(s => s.Id == id)?.Name
            : null;
        return section is null ? where : $"{where} — {section}";
    }

    private static string Invariant(decimal value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Отметка расклада — непрозрачная: дата платежа, сумма счёта, версия разноски и каждая учётная дата.
    /// Списка «увиденных дат» мало: он не ловит долю, добавленную между предпросмотром и оплатой, и
    /// смену суммы.
    /// </summary>
    public static string Stamp(PaymentPlan plan, decimal total, string allocationStamp)
    {
        var text = new StringBuilder()
            .Append(plan.PaidOn.ToString("O")).Append('|').Append(Invariant(total)).Append('|').Append(allocationStamp);
        foreach (var share in plan.Shares.OrderBy(s => s.Id))
            text.Append('|').Append(share.Id).Append(':').Append(share.AccountingOn.ToString("O"));
        if (plan.Remainder is { } rest)
            text.Append("|rest:").Append(Invariant(rest.Amount)).Append(':').Append(rest.AccountingOn.ToString("O"));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }
}
