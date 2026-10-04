using System.Globalization;
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

        close.MapGet("/history", async (int? take, IPeriodClosures closures, CancellationToken ct) =>
            Results.Ok((await closures.HistoryAsync(take ?? 50, ct)).Select(r => new ClosureDto(
                r.Id, r.Kind.ToString(), r.Contour.ToString(), r.ConstructionId, r.From, r.Through,
                r.At, r.ByName, r.Reason))));

        close.MapPost("/close", async (ClosePeriodRequest req, IPeriodClosures closures, CancellationToken ct) =>
        {
            if (Problem(req.Contour, req.ConstructionId, req.IfMatch, out var contour, out var seen) is { } problem)
                return Results.BadRequest(new { error = problem });
            if (req.From is not { } from || req.Through is not { } through)
                return Results.BadRequest(new { error = "Назовите период: первый и последний день." });

            var row = await closures.CloseAsync(new ClosePeriod(contour, from, through, seen, req.Reason), ct);
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
            reopenable is null ? null : new ReopenableDto(reopenable.From, reopenable.Through));
    }

    /// <summary>
    /// Разбор контура и увиденной границы. <c>ifMatch</c> обязателен: без него «закрыть» подтверждает
    /// человек, а закрывается состояние, которого он не видел.
    /// </summary>
    private static string? Problem(
        string? kind, Guid? constructionId, string? ifMatch, out PeriodContour contour, out DateOnly? seen)
    {
        contour = PeriodContour.Company;
        seen = null;

        switch (kind?.ToLowerInvariant())
        {
            case "company":
                if (constructionId is not null) return "У контура «компания» стройки нет.";
                break;
            case "construction":
                if (constructionId is not { } id) return "Назовите стройку, чей период закрывается.";
                contour = PeriodContour.Construction(id);
                break;
            default:
                return "Назовите контур: «company» — компания или «construction» — стройка.";
        }

        if (string.IsNullOrWhiteSpace(ifMatch))
            return "Не названа граница периода, которую вы видели (ifMatch): дата «закрыто по» или " +
                   $"«{NothingClosed}», если не закрыто ничего.";
        if (ifMatch == NothingClosed) return null;
        if (!DateOnly.TryParseExact(ifMatch, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return $"Граница периода (ifMatch) — дата вида 2026-09-30 или «{NothingClosed}».";

        seen = date;
        return null;
    }

    private record PeriodsDto(DateOnly Today, ContourDto Company, ContourDto[] Constructions);

    /// <param name="ConstructionId">Стройка; у компании — <c>null</c>.</param>
    /// <param name="ClosedThrough">Действующая граница: у стройки — позднейшая из своей и компании.</param>
    /// <param name="OwnClosedThrough">Собственная граница контура — чтобы показать, чьим закрытием он закрыт.</param>
    /// <param name="ExpectedFrom">С какого дня начнётся следующее закрытие; <c>null</c> — закрытий не было.</param>
    /// <param name="Reopenable">Закрытие, которое отменит отмена; <c>null</c> — отменять нечего.</param>
    private record ContourDto(
        Guid? ConstructionId, DateOnly? ClosedThrough, DateOnly? OwnClosedThrough, DateOnly? ExpectedFrom,
        ReopenableDto? Reopenable);

    private record ReopenableDto(DateOnly From, DateOnly Through);

    private record ClosureDto(
        Guid Id, string Kind, string Contour, Guid? ConstructionId, DateOnly From, DateOnly Through,
        DateTimeOffset At, string ByName, string? Reason);

    private record ClosePeriodRequest(
        string? Contour, Guid? ConstructionId, DateOnly? From, DateOnly? Through, string? IfMatch, string? Reason);

    private record ReopenPeriodRequest(string? Contour, Guid? ConstructionId, string? IfMatch, string? Reason);
}
