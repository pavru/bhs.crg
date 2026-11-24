using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Calendars;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Modules.Plan.Endpoints;

internal static class CalendarEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var calendars = endpoints.MapGroup("/api/plan/calendars").WithTags("Календари планирования");
        var read = AppPolicies.AnyPermission(PlanModule.ScheduleRead, PlanModule.CalendarEdit);
        var companyEdit = AppPolicies.Permission(PlanModule.CalendarEdit);
        calendars.MapGet("/company/{year:int}", GetCompanyAsync).RequireAuthorization(read);
        calendars.MapPut("/company/{year:int}", PutCompanyAsync).RequireAuthorization(companyEdit);
        // WritesNothing: предпросмотр читает файл и год, запись происходит отдельным PUT с If-Match.
        calendars.MapPost("/company/{year:int}/import", PreviewAsync).RequireAuthorization(companyEdit)
            .DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(ProductionCalendarFile.MaxBytes + 4096));
        calendars.MapGet("/constructions/{id:guid}", GetConstructionAsync).RequireAuthorization(read);
        calendars.MapGet("/constructions/{id:guid}/non-working-days", GetNonWorkingDaysAsync)
            .RequireAuthorization(AppPolicies.Permission(PlanModule.ScheduleRead));
        calendars.MapPut("/constructions/{id:guid}", PutConstructionAsync)
            .RequireAuthorization(AppPolicies.Permission(PlanModule.ScheduleEdit));
    }

    private static async Task<IResult> GetCompanyAsync(int year, CalendarStore store, CancellationToken ct) =>
        Results.Ok(await store.CompanyAsync(year, ct));

    private static async Task<IResult> PutCompanyAsync(
        int year, CompanyCalendarInput input, HttpRequest request, CalendarStore store, CancellationToken ct) =>
        Results.Ok(await store.SaveCompanyAsync(year, input, request, ct));

    private static async Task<IResult> PreviewAsync(
        int year, IFormFile file, CalendarStore store, CancellationToken ct)
    {
        CalendarViews.EnsureYear(year);
        if (file.Length is < 1 or > ProductionCalendarFile.MaxBytes)
            throw new InvalidRequestException("Выберите непустой файл календаря до 128 КБ.");
        var name = CalendarStore.CleanFileName(file.FileName);
        await using var stream = file.OpenReadStream();
        var proposed = await ProductionCalendarFile.ReadAsync(stream, year, ct);
        var current = await store.CompanyAsync(year, ct);
        return Results.Ok(CalendarImportPreview.Compare(current, proposed, name));
    }

    private static async Task<IResult> GetConstructionAsync(Guid id, CalendarStore store, CancellationToken ct) =>
        Results.Ok(await store.ConstructionAsync(id, ct));

    private static async Task<IResult> GetNonWorkingDaysAsync(
        Guid id, DateOnly from, DateOnly to, CalendarStore store, CancellationToken ct) =>
        Results.Ok(await store.NonWorkingDaysAsync(id, from, to, ct));

    private static async Task<IResult> PutConstructionAsync(
        Guid id, ConstructionCalendarInput input, HttpRequest request, CalendarStore store, CancellationToken ct) =>
        Results.Ok(await store.SaveConstructionAsync(id, input, request, ct));
}
