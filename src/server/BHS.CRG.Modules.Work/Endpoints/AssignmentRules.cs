using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Endpoints;

/// <summary>
/// Правила назначения (ТЗ WORK-21, WORK-27, WORK-46; change <c>work-11-assignments</c>): годен ли сотрудник на
/// весь период и с чем назначение пересекается. Одни и те же — для одиночного заведения, правки, пачки и
/// кандидатов: кандидат «годен» ровно тогда, когда заведение его не отвергнет.
/// </summary>
internal static class AssignmentRules
{
    /// <summary>
    /// Почему сотрудника нельзя назначить на этот период; <c>null</c> — можно. Причина — словами для человека:
    /// её показывают и у кандидата, и в отказе.
    /// </summary>
    public static string? Unfit(ModuleEmployee employee, AssignmentKind kind, DateOnly from, DateOnly? to)
    {
        if (employee.Archived) return "в архиве";
        if (employee.EmployedFrom is { } hired && hired > from) return $"принят с {Assignment.Day(hired)}";
        // «Уволен с» — первый день, когда сотрудник уже не работает: он входит в период, если не позже конца,
        // а бессрочному назначению мешает любая дата увольнения.
        if (employee.EmployedTo is { } gone && (to is not { } end || gone <= end)) return $"уволен с {Assignment.Day(gone)}";
        // «Свои стройки» менеджера вычисляются через учётную запись: без неё назначение не дало бы ничего.
        if (kind == AssignmentKind.Manager && !employee.Account.IsLinked) return "сотрудник не связан с учётной записью";
        return null;
    }

    /// <summary>
    /// Назначение того же сотрудника на той же стройке того же вида, пересекающееся с периодом. На другой
    /// стройке пересечение допустимо: монтажник утром на одной, вечером на другой.
    /// </summary>
    public static async Task<Assignment?> OverlapAsync(WorkDbContext db, Guid employeeId, Guid constructionId,
        AssignmentKind kind, DateOnly from, DateOnly? to, Guid? except, CancellationToken ct)
    {
        var same = await db.Assignments.AsNoTracking()
            .Where(a => a.EmployeeId == employeeId && a.ConstructionId == constructionId && a.Kind == kind
                && a.Id != except)
            .ToListAsync(ct);
        return same.Where(a => a.Overlaps(from, to)).OrderBy(a => a.DateFrom).FirstOrDefault();
    }

    /// <summary>Отказ «уже назначен» — с периодом существующего назначения, как требует спецификация.</summary>
    public static ConflictException Overlapping(Assignment existing, string employee) =>
        new($"{employee} уже назначен на эту стройку ({Assignment.KindText(existing.Kind)}) на {existing.Period}: " +
            "периоды одного вида на одной стройке не пересекаются. Измените период существующего назначения " +
            "или выберите другие даты.");

    /// <summary>Причина кандидата для уже назначенного: «уже назначен 01.11.2026–15.11.2026».</summary>
    public static string AlreadyAssigned(Assignment existing) => $"уже назначен {existing.Period}";

    /// <summary>
    /// «Справочник не заведён» (порт сотрудников ответил <c>null</c>) — 409 с кодом, а не пустой список
    /// (ТЗ CORE-20.4, <c>work-10</c> отложил перевод сюда).
    /// </summary>
    public static IResult DirectoryMissing() =>
        Results.Conflict(new { code = IModuleEmployees.NotSetUpCode, error = IModuleEmployees.NotSetUp });

    /// <summary>Сотрудник негоден на период — 422 с причиной (ТЗ WORK-27).</summary>
    public static IResult Unprocessable(ModuleEmployee employee, string reason) =>
        Results.UnprocessableEntity(new
        {
            error = $"Назначить {employee.FullName} нельзя: {reason}.",
            reason,
            refused = new[] { new Refused(employee.Id, employee.FullName, reason) },
        });

    /// <summary>Отказавший в назначении — в ответе пачки и одиночного заведения.</summary>
    public sealed record Refused(Guid EmployeeId, string Name, string Reason);

    /// <summary>Период из запроса: начало обязательно, конец — по правилу вида (<see cref="Assignment" />).</summary>
    public static (DateOnly From, DateOnly? To) Period(AssignmentKind kind, DateOnly? from, DateOnly? to)
    {
        if (from is not { } start)
            throw new InvalidRequestException("Не задано начало периода (dateFrom).");
        // Сущность проверит ещё раз при записи; здесь — до похода в справочник и замка.
        _ = Assignment.Create(Guid.Empty, Guid.Empty, kind, start, to, DateTimeOffset.UnixEpoch);
        return (start, to);
    }
}
