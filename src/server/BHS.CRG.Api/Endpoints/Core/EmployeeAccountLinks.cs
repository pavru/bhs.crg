using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Endpoints.Core;

/// <summary>
/// Связывание сотрудника с учётной записью и отвязывание (ТЗ WORK-47, CORE-15, CORE-28; change
/// <c>work-01-employee-account</c>). Один путь у одиночного адреса и у пачки: пачка — это те же
/// строки по очереди, и правило, расходящееся между ними, разошлось бы молча.
///
/// <para><b>Версия — версия карточки сотрудника.</b> Связь пишется вместе с <c>UpdatedAt</c> объекта
/// сотрудника одним сохранением под блокировкой его строки (<see cref="IDomainObjectRepository.SaveSeenAsync" />),
/// поэтому <c>xmin</c> карточки сдвигается. Отдельная версия у связи потребовала бы «версии
/// отсутствующей строки» для состояния «не связан». Форма карточки, открытая до связывания, получит
/// 409 — честно: карточка показывает связь.</para>
///
/// <para><b>Уникальность держит база</b> (частичный уникальный индекс по учётной записи). Проверка
/// «свободна ли» здесь нужна ради слов отказа — с именем занятого сотрудника, — а гонку двух
/// связываний ловит индекс, и проигравшая получает тот же отказ.</para>
/// </summary>
public sealed class EmployeeAccountLinks(
    AppDbContext db, IDomainObjectRepository objects, IRepository<DocumentType> types, IActivityLog journal)
{
    /// <summary>Сколько строк принимает пачка.</summary>
    public const int BatchMax = 200;

    public async Task<LinkResult> LinkAsync(Guid employeeId, Guid userId, string seen, CancellationToken ct)
    {
        // Строка пачки начинает с чистого листа: отказ соседней мог оставить в контексте сущность в
        // неудавшемся состоянии, и первое же сохранение повторило бы её попытку.
        db.ChangeTracker.Clear();

        if (await EmployeeAsync(employeeId, ct) is not { } employee) return LinkResult.NotFound;
        if (!Matches(employee.Version, seen)) return LinkResult.Stale;
        if (employee.IsArchived) return LinkResult.Archived;

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return LinkResult.AccountGone;
        var accountName = AccountName(user.DisplayName, user.UserName);

        var taken = await db.EmployeeAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == userId, ct);
        if (taken is not null)
            return taken.EmployeeId == employeeId
                ? LinkResult.Linked(employee) // уже так и связано — повтор безобиден, журнал молчит
                : await TakenAsync(taken.EmployeeId, ct);

        var current = await db.EmployeeAccounts.FirstOrDefaultAsync(a => a.EmployeeId == employeeId, ct);
        if (current is { IsLive: true })
            return LinkResult.EmployeeLinked(await LiveNameAsync(current, ct));

        // Поверх связи с УДАЛЁННОЙ учётной записью — можно: занимать там некого, а «сначала отвяжите»
        // было бы лишним шагом, который ничего не охраняет. Прежнее имя уходит в журнал.
        var before = current?.AccountName;
        if (current is null) db.EmployeeAccounts.Add(EmployeeAccount.Link(employeeId, userId, accountName));
        else current.Relink(userId, accountName);
        employee.TouchUpdatedAt();

        if (await SaveAsync(employee, seen, ct) is { } refused) return refused;

        await journal.RecordAsync(ActivityActions.EmployeeAccountLinked, employeeId.ToString(),
            employee.DisplayName, before: before, after: accountName, ct: ct);
        return LinkResult.Linked(employee);
    }

    /// <summary>
    /// Снять связь. Связи нет — успех без записи в журнал: повтор безобиден, и версия тогда не
    /// сверяется — менять нечего.
    /// </summary>
    public async Task<LinkResult> UnlinkAsync(Guid employeeId, string seen, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        if (await EmployeeAsync(employeeId, ct) is not { } employee) return LinkResult.NotFound;

        var current = await db.EmployeeAccounts.FirstOrDefaultAsync(a => a.EmployeeId == employeeId, ct);
        if (current is null) return LinkResult.Linked(employee);
        if (!Matches(employee.Version, seen)) return LinkResult.Stale;

        var before = await LiveNameAsync(current, ct);
        db.EmployeeAccounts.Remove(current);
        employee.TouchUpdatedAt();
        if (await SaveAsync(employee, seen, ct) is { } refused) return refused;

        await journal.RecordAsync(ActivityActions.EmployeeAccountUnlinked, employeeId.ToString(),
            employee.DisplayName, before: before, after: null, ct: ct);
        return LinkResult.Linked(employee);
    }

    /// <summary>
    /// Пачка: каждая строка — своё сохранение. Удачная строка не откатывается из-за отказа соседней:
    /// администратор, связавший девять из одиннадцати, обязан увидеть девять связанными, а две — с
    /// причиной, а не «ничего не вышло».
    /// </summary>
    public async Task<IReadOnlyList<(Guid EmployeeId, LinkResult Result)>> LinkManyAsync(
        IReadOnlyList<(Guid EmployeeId, Guid UserId, string Version)> rows, CancellationToken ct)
    {
        var results = new List<(Guid, LinkResult)>(rows.Count);
        foreach (var (employeeId, userId, version) in rows)
            results.Add((employeeId, await LinkAsync(employeeId, userId, version, ct)));
        return results;
    }

    private async Task<LinkResult?> SaveAsync(DomainObject employee, string seen, CancellationToken ct)
    {
        try
        {
            await objects.SaveSeenAsync(employee, seen, ct);
            return null;
        }
        // Версию сдвинули между ранней сверкой и блокировкой строки.
        catch (ConflictException) { db.ChangeTracker.Clear(); return LinkResult.Stale; }
        // Гонку двух связываний одной учётной записи поймал индекс — тот же отказ, что у проверки выше.
        catch (DbUpdateException ex) when (DbFailure.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return LinkResult.Conflict("taken", "Учётная запись тем временем связана с другим сотрудником. " +
                "Обновите список и выберите другую учётную запись.");
        }
    }

    private async Task<LinkResult> TakenAsync(Guid otherEmployeeId, CancellationToken ct)
    {
        var name = await db.DomainObjects.AsNoTracking().Where(o => o.Id == otherEmployeeId)
            .Select(o => o.DisplayName).FirstOrDefaultAsync(ct) ?? "";
        return LinkResult.Taken(otherEmployeeId, name);
    }

    /// <summary>Живое имя действующей связи — из учётной записи; у удалённой — снимок.</summary>
    private async Task<string> LiveNameAsync(EmployeeAccount link, CancellationToken ct)
    {
        if (link.UserId is not { } userId) return link.AccountName;
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.DisplayName, u.UserName }).FirstOrDefaultAsync(ct);
        return user is null ? link.AccountName : AccountName(user.DisplayName, user.UserName);
    }

    /// <summary>Карточка, если это сотрудник; иначе <c>null</c> — чужая запись за этой дверью не существует.</summary>
    private async Task<DomainObject?> EmployeeAsync(Guid id, CancellationToken ct)
    {
        var entry = await objects.GetByIdAsync(id, ct);
        return entry is not null && await EmployeeEndpoints.IsEmployeeAsync(entry, types) ? entry : null;
    }

    private static bool Matches(string stored, string seen) =>
        string.Equals(stored, seen.Trim(), StringComparison.Ordinal);

    /// <summary>Имя учётной записи для человека: отображаемое, а нет его — логин.</summary>
    internal static string AccountName(string? displayName, string? userName) =>
        string.IsNullOrWhiteSpace(displayName) ? userName ?? "" : displayName.Trim();
}

/// <summary>
/// Итог одной строки связывания. <see cref="Outcome" /> — код для клиента, <see cref="Error" /> —
/// слова для человека; у удачи <see cref="Employee" /> — карточка с новой версией.
/// </summary>
public sealed record LinkResult(string Outcome, string? Error, DomainObject? Employee = null,
    object? LinkedTo = null, string? AccountName = null)
{
    public bool Succeeded => Outcome == "linked";

    public static LinkResult Linked(DomainObject employee) => new("linked", null, employee);

    public static readonly LinkResult NotFound = new("not-found", "Сотрудник не найден.");

    public static readonly LinkResult Stale = new("stale", RecordSeen.Moved);

    public static readonly LinkResult Archived = new("archived",
        "Сотрудник в архиве — связывать его не с чем: верните карточку из архива, если он снова работает.");

    public static readonly LinkResult AccountGone = new("account-gone",
        "Такой учётной записи больше нет — её удалили. Выберите другую.");

    public static LinkResult Taken(Guid employeeId, string employeeName) => new("taken",
        $"Учётная запись уже связана с сотрудником «{employeeName}». Учётная запись — один человек: " +
        "сначала отвяжите её у него.", LinkedTo: new { id = employeeId, name = employeeName });

    public static LinkResult EmployeeLinked(string accountName) => new("employee-linked",
        $"Сотрудник уже связан с учётной записью «{accountName}». Сначала отвяжите её.",
        AccountName: accountName);

    public static LinkResult Conflict(string outcome, string error) => new(outcome, error);
}
