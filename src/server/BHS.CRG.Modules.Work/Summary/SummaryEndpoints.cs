using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Modules.Work.Summary;

public static class SummaryEndpoints
{
    public const string Path = "/api/work/summary";
    public const int MaxRows = 100;

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(Path, GetAsync).WithTags("Отчёты")
            .RequireAuthorization(AppPolicies.AnyPermission(WorkModule.ReportReview, WorkModule.ReportRead));

    private static async Task<IResult> GetAsync(
        DateOnly from, DateOnly to, Guid? construction, int? offset, int? limit, IModuleReaders readers,
        SummaryQuery summary, CancellationToken ct)
    {
        var count = to.DayNumber - from.DayNumber + 1;
        if (count < 1) throw new InvalidRequestException("Начало периода (from) позже его конца (to).");
        if (count > 31) throw new InvalidRequestException("Период сводки — не больше 31 дня.");
        var skip = offset ?? 0;
        var take = limit ?? MaxRows;
        if (skip < 0) throw new InvalidRequestException("Сдвиг страницы (offset) не может быть отрицательным.");
        if (take is < 1 or > MaxRows)
            throw new InvalidRequestException($"Строк на странице (limit) — от 1 до {MaxRows}.");

        var reader = await readers.ForRequestAsync(ct);
        var days = Enumerable.Range(0, count).Select(from.AddDays).ToArray();
        return Results.Ok(await summary.ReadAsync(reader, days, construction, skip, take, ct));
    }
}
