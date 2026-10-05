using System.Globalization;
using System.Security.Claims;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Periods;
using BHS.CRG.Domain.Periods;
using BHS.CRG.Modules;

namespace BHS.CRG.Api.Endpoints.Core;

/// <summary>
/// Учётный период: границы закрытия, закрытие и отмена (ТЗ CORE-35; задача E1a, issue #1081).
///
/// <para><b>Чтение границ открыто любому вошедшему</b>, закрытие и отмена — праву
/// <c>core.period.close</c>. Границы — это даты, а не суммы: по ним экран счёта объясняет, почему
/// запись не правится и в какой месяц ляжет оплата, и спрятать их за правом закрытия значило бы
/// оставить это объяснение только тому, кто период и закрыл.</para>
///
/// <para><b>Перечень диалога закрытия</b> (задача E1b, issue #1099) — предпросмотром: что войдёт в
/// период и что не завершено, по модулям, и отпечаток увиденного, который закрытие обязано назвать.
/// ⚠️ Суммы модулей режутся ЗДЕСЬ, по праву смотрящего, — и в предпросмотре, и в «Истории»: диалог —
/// экран ядра, а право закрывать период сумм счетов не открывает.</para>
///
/// <para>⚠️ Границы отдаются ДЕЙСТВУЮЩИМИ и посчитанными здесь: у стройки без своих закрытий —
/// граница компании, начало следующего периода и «что отменит отмена» — готовыми полями. Отдай мы
/// сырые записи, клиент завёл бы вторую формулу, и расходилась бы она ровно на границе месяца.</para>
/// </summary>
public static class PeriodEndpoints
{
    /// <summary>Значение <c>ifMatch</c>, которым клиент говорит «я видел, что не закрыто ничего».</summary>
    private const string NothingClosed = "none";

    public static void MapPeriodEndpoints(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("/api/periods").RequireAuthorization();
        var close = app.MapGroup("/api/periods")
            .RequireAuthorization(AppPolicies.Permission(CorePermissions.PeriodClose));

        read.MapGet("/", async (IPeriodClosures closures, CancellationToken ct) =>
        {
            var ledger = await closures.LedgerAsync(ct);
            // Стройки — ВСЕ, а не только со своим закрытием: стройка без него закрыта границей
            // компании, и пропусти мы её, клиент прочёл бы отсутствие как «открыта». Только
            // идентификаторы — названия строек открывает своё право.
            var ids = await closures.ConstructionsAsync(ct);

            return Results.Ok(new PeriodsDto(
                await closures.TodayAsync(ct),
                Contour(ledger, PeriodContour.Company, null),
                [.. ids.Select(id => Contour(ledger, PeriodContour.Construction(id), id))]));
        });

        close.MapGet("/history", async (int? take, IPeriodClosures closures, ClaimsPrincipal user,
            IUserPermissions permissions, CancellationToken ct) =>
        {
            var granted = await permissions.ForAsync(user, ct);
            return Results.Ok((await closures.HistoryAsync(take ?? 50, ct)).Select(r => new ClosureDto(
                r.Id, r.Kind.ToString(), r.Contour.ToString(), r.ConstructionId, r.From, r.Through,
                r.At, r.ByName, r.Reason,
                ClosingReport.FromJson(r.Report) is { } report ? Sections(report, granted) : null)));
        });

        close.MapPost("/close/preview", async (PreviewClosingRequest req, IPeriodClosures closures,
            ClaimsPrincipal user, IUserPermissions permissions, CancellationToken ct) =>
        {
            if (ContourProblem(req.Contour, req.ConstructionId, out var contour) is { } problem)
                return Results.BadRequest(new { error = problem });
            if (req.From is not { } from || req.Through is not { } through)
                return Results.BadRequest(new { error = "Назовите период: первый и последний день." });

            var preview = await closures.PreviewAsync(new PreviewClosing(contour, from, through), ct);
            return Results.Ok(new ClosingPreviewDto(
                preview.Stamp, Sections(preview.Report, await permissions.ForAsync(user, ct))));
        });

        close.MapPost("/close", async (ClosePeriodRequest req, IPeriodClosures closures, CancellationToken ct) =>
        {
            if (Problem(req.Contour, req.ConstructionId, req.IfMatch, out var contour, out var seen) is { } problem)
                return Results.BadRequest(new { error = problem });
            if (req.From is not { } from || req.Through is not { } through)
                return Results.BadRequest(new { error = "Назовите период: первый и последний день." });
            // Без отпечатка «закрыть» подтверждает человек, а в запись ложится перечень, которого он
            // не видел, — диалог стал бы украшением.
            if (string.IsNullOrWhiteSpace(req.Report))
                return Results.BadRequest(new { error = "Не назван перечень, который вы видели (report): " +
                    "отпечаток из предпросмотра закрытия. Без него закрыть нельзя." });

            var row = await closures.CloseAsync(new ClosePeriod(contour, from, through, seen, req.Reason, req.Report), ct);
            return Results.Ok(new { row.Id });
        });

        close.MapPost("/reopen", async (ReopenPeriodRequest req, IPeriodClosures closures, CancellationToken ct) =>
        {
            if (Problem(req.Contour, req.ConstructionId, req.IfMatch, out var contour, out var seen) is { } problem)
                return Results.BadRequest(new { error = problem });

            var row = await closures.ReopenAsync(new ReopenPeriod(contour, seen, req.Reason ?? ""), ct);
            return Results.Ok(new { row.Id });
        });
    }

    private static ContourDto Contour(PeriodLedger ledger, PeriodContour contour, Guid? constructionId)
    {
        var reopenable = ledger.ReopenableOrNull(contour);
        return new ContourDto(
            constructionId,
            ledger.ClosedThrough(contour),
            ledger.Own(contour),
            ledger.ExpectedFrom(contour),
            reopenable is null ? null : new ReopenableDto(reopenable.From, reopenable.Through,
                contour.Kind == PeriodContourKind.Company ? ledger.KeptClosedByOwn(reopenable) : []));
    }

    /// <summary>
    /// Разбор контура и увиденной границы. <c>ifMatch</c> обязателен: без него «закрыть» подтверждает
    /// человек, а закрывается состояние, которого он не видел.
    /// </summary>
    private static string? Problem(
        string? kind, Guid? constructionId, string? ifMatch, out PeriodContour contour, out DateOnly? seen)
    {
        seen = null;
        if (ContourProblem(kind, constructionId, out contour) is { } problem) return problem;

        if (string.IsNullOrWhiteSpace(ifMatch))
            return "Не названа граница периода, которую вы видели (ifMatch): дата «закрыто по» или " +
                   $"«{NothingClosed}», если не закрыто ничего.";
        if (ifMatch == NothingClosed) return null;
        if (!DateOnly.TryParseExact(ifMatch, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return $"Граница периода (ifMatch) — дата вида 2026-09-30 или «{NothingClosed}».";

        seen = date;
        return null;
    }

    private static string? ContourProblem(string? kind, Guid? constructionId, out PeriodContour contour)
    {
        contour = PeriodContour.Company;
        switch (kind?.ToLowerInvariant())
        {
            case "company":
                return constructionId is not null ? "У контура «компания» стройки нет." : null;
            case "construction":
                if (constructionId is not { } id) return "Назовите стройку, чей период закрывается.";
                contour = PeriodContour.Construction(id);
                return null;
            default:
                return "Назовите контур: «company» — компания или «construction» — стройка.";
        }
    }

    /// <summary>Перечень так, как его можно показать этому человеку: суммы — по его правам.</summary>
    private static ClosingSectionDto[] Sections(ClosingReport report, IReadOnlyCollection<string> granted)
    {
        var shown = report.VisibleTo(granted);
        return [.. shown.Sections.Zip(report.Sections, (mine, full) => new ClosingSectionDto(
            mine.Module, mine.Title, mine.DateRule, [.. mine.Unfinished.Select(Line)], [.. mine.Frozen.Select(Line)],
            ClosingReport.HidesAmounts(mine, full)))];

        static ClosingLineDto Line(ClosingLine line) =>
            new(line.Key, line.Text, line.Count, line.Unit.Text(line.Count), line.Amount, line.Note);
    }

    /// <param name="Stamp">Отпечаток увиденного — его называет закрытие (<c>report</c>).</param>
    private record ClosingPreviewDto(string Stamp, ClosingSectionDto[] Sections);

    /// <param name="AmountsHidden">В разделе есть суммы, которых этому человеку не показали.</param>
    private record ClosingSectionDto(
        string Module, string Title, string DateRule, ClosingLineDto[] Unfinished, ClosingLineDto[] Frozen,
        bool AmountsHidden);

    /// <param name="Counted">Число документов словами: «3 счёта».</param>
    /// <param name="Amount">Сумма; <c>null</c> — строка денег не несёт либо сумма закрыта правом.</param>
    private record ClosingLineDto(string Key, string Text, int Count, string Counted, decimal? Amount, string? Note);

    private record PeriodsDto(DateOnly Today, ContourDto Company, ContourDto[] Constructions);

    /// <param name="ConstructionId">Стройка; у компании — <c>null</c>.</param>
    /// <param name="ClosedThrough">Действующая граница: у стройки — позднейшая из своей и компании.</param>
    /// <param name="OwnClosedThrough">Собственная граница контура — чтобы показать, чьим закрытием он закрыт.</param>
    /// <param name="ExpectedFrom">С какого дня начнётся следующее закрытие; <c>null</c> — закрытий не было.</param>
    /// <param name="Reopenable">Закрытие, которое отменит отмена; <c>null</c> — отменять нечего.</param>
    private record ContourDto(
        Guid? ConstructionId, DateOnly? ClosedThrough, DateOnly? OwnClosedThrough, DateOnly? ExpectedFrom,
        ReopenableDto? Reopenable);

    /// <param name="KeptClosed">
    /// Стройки, которым отмена этих дней НЕ откроет: они закрыты своим закрытием. Только у компании.
    /// </param>
    private record ReopenableDto(DateOnly From, DateOnly Through, IReadOnlyList<Guid> KeptClosed);

    private record ClosureDto(
        Guid Id, string Kind, string Contour, Guid? ConstructionId, DateOnly From, DateOnly Through,
        DateTimeOffset At, string ByName, string? Reason, ClosingSectionDto[]? Report);

    /// <param name="Report">Отпечаток перечня из предпросмотра — что человек видел, закрывая.</param>
    private record ClosePeriodRequest(
        string? Contour, Guid? ConstructionId, DateOnly? From, DateOnly? Through, string? IfMatch, string? Reason,
        string? Report);

    private record PreviewClosingRequest(string? Contour, Guid? ConstructionId, DateOnly? From, DateOnly? Through);

    private record ReopenPeriodRequest(string? Contour, Guid? ConstructionId, string? IfMatch, string? Reason);
}
