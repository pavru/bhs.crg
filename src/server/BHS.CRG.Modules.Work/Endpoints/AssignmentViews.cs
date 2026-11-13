using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Access;
using BHS.CRG.Modules.Work.Data;

namespace BHS.CRG.Modules.Work.Endpoints;

/// <summary>Назначение в ответе — с признаками действий от ворот стройки (D15 зонтика).</summary>
/// <param name="Active">Действует сегодня на стройке — по её поясу; клиент «сегодня» не вычисляет.</param>
/// <param name="Version">Версия строки — значение <c>If-Match</c> правки и снятия.</param>
public sealed record AssignmentDto(
    Guid Id, Guid EmployeeId, string EmployeeName, string? PersonnelNumber, Guid ConstructionId, string Kind,
    DateOnly DateFrom, DateOnly? DateTo, bool Active, string Version,
    bool CanChange, string? ChangeReason, bool CanRemove, string? RemoveReason);

/// <summary>Стройка в ответе чтения назначений: можно ли на ней назначать, и почему нет.</summary>
public sealed record AssignmentSiteDto(
    Guid Id, string Name, string TimeZoneId, bool TimeZoneInherited, DateOnly? Today, bool CanAssign, string? Reason);

/// <summary>
/// Сборка ответа назначений. Признаки <c>canChange</c> и <c>canRemove</c> считает <see cref="AssignmentGate" /> —
/// тот же, что отказывает пишущему адресу: кнопка и отказ не расходятся (D15 зонтика).
/// </summary>
internal static class AssignmentViews
{
    public static async Task<AssignmentDto> OneAsync(
        WorkDbContext db, AssignmentGate gate, Assignment assignment, ModuleEmployee? employee, DateOnly? today,
        CancellationToken ct)
    {
        var change = await gate.RefusalAsync(assignment.ConstructionId, ct);
        var (remove, _) = await gate.RemovalAsync(assignment, ct);
        return new AssignmentDto(
            assignment.Id, assignment.EmployeeId, employee?.FullName ?? "сотрудник удалён", employee?.PersonnelNumber,
            assignment.ConstructionId, Assignment.KindCode(assignment.Kind), assignment.DateFrom, assignment.DateTo,
            today is { } day && assignment.Covers(day), db.VersionOf(assignment),
            change is null, change, remove is null, remove);
    }

    public static async Task<IReadOnlyList<AssignmentDto>> ManyAsync(
        WorkDbContext db, AssignmentGate gate, IReadOnlyList<Assignment> assignments,
        IReadOnlyDictionary<Guid, ModuleEmployee> employees, DateOnly? today, CancellationToken ct)
    {
        var result = new List<AssignmentDto>(assignments.Count);
        foreach (var a in assignments)
            result.Add(await OneAsync(db, gate, a, employees.GetValueOrDefault(a.EmployeeId), today, ct));
        // Менеджеры — первыми; дальше по ФИО и началу — порядок задаёт сервер, клиент не сортирует.
        return [.. result
            .OrderBy(a => a.Kind == "manager" ? 0 : 1)
            .ThenBy(a => a.EmployeeName, StringComparer.CurrentCulture)
            .ThenBy(a => a.DateFrom)];
    }
}
