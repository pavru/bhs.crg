using BHS.CRG.Api.Endpoints.Core;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Ports;
using MediatR;
using Microsoft.EntityFrameworkCore;
using RecordsFor = BHS.CRG.Application.Documents.RecordsFor;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Справочник сотрудников для модуля (ТЗ PLAN-16, CORE-7) — те же записи, что отдаёт дверь
/// <c>/api/employees</c>. Табельный номер и период работы — по тэгам <c>employee.*</c> у типа самой
/// карточки: ключи заказчик вправе переименовать.
///
/// <para><b>Читается весь справочник одним показом</b>, выбор порт отделяет сам — как классификатор
/// видов работ: сотрудников у компании десятки и сотни, а искать надо и по табельному номеру, который
/// лежит в данных карточки.</para>
///
/// <para>Связь с учётной записью (<c>work-11-assignments</c>) — из таблицы связи <c>work-01</c>, одним
/// запросом на весь справочник: поле карточки <c>УчётнаяЗапись</c> связь не заполняет, и прочитанное
/// оттуда расходилось бы с тем, по чему считаются «свои» отчёты.</para>
/// </summary>
public sealed class ModuleEmployeesPort(IMediator mediator, IRepository<DocumentType> types, AppDbContext db) : IModuleEmployees
{
    public async Task<ModuleEmployee?> OfAccountAsync(Guid userId, CancellationToken ct = default)
    {
        var employeeId = await db.EmployeeAccounts.AsNoTracking()
            .Where(a => a.UserId == userId).Select(a => (Guid?)a.EmployeeId).FirstOrDefaultAsync(ct);
        if (employeeId is not { } id) return null;
        return (await GetAsync([id], ct))?.SingleOrDefault();
    }

    public async Task<IReadOnlyList<ModuleEmployee>?> GetAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (await AllAsync(ct) is not { } all) return null;
        var wanted = ids.ToHashSet();
        return [.. all.Where(e => wanted.Contains(e.Id))];
    }

    public async Task<ModuleChoice<ModuleEmployee>?> SearchAsync(
        string? query, DateOnly onDate, int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (await AllAsync(ct) is not { } all) return null;

        var found = all.Where(e => Matches(e, query)).ToList();
        // Не работающие на дату — после работающих, а не вон из выдачи: уволенный, пропавший молча,
        // выглядел бы ошибкой справочника, а исполнитель прошлой работы обязан находиться.
        return new ModuleChoice<ModuleEmployee>(
            [.. found.Where(e => !e.Archived).OrderBy(e => e.NotWorkingOn(onDate) is null ? 0 : 1).Take(limit)],
            found.Count(e => e.Archived));
    }

    private async Task<List<ModuleEmployee>?> AllAsync(CancellationToken ct)
    {
        var all = await types.GetAllAsync(ct);
        if (CoreDirectories.Family(all, CoreRecordTypes.EmployeeCode) is not { } family) return null;

        var accounts = await AccountsAsync(ct);
        var employees = new List<ModuleEmployee>();
        foreach (var (typeId, type) in family)
        {
            // Ключи — раз на тип, а не на карточку: схема разбирается по цепочке наследования.
            var number = CoreDirectories.TaggedKey(type, all, FunctionalTag.EmployeePersonnelNumber);
            var from = CoreDirectories.TaggedKey(type, all, FunctionalTag.EmployeeEmployedFrom);
            var to = CoreDirectories.TaggedKey(type, all, FunctionalTag.EmployeeEmployedTo);
            foreach (var e in await mediator.Send(new ListCommonDataEntriesQuery(RecordsFor.Display, CompositeTypeId: typeId), ct))
            {
                var data = e.Data.RootElement;
                employees.Add(new ModuleEmployee(
                    e.Id, e.DisplayName ?? "", CoreDirectories.Text(data, number),
                    CoreDirectories.Date(data, from), CoreDirectories.Date(data, to), e.IsArchived,
                    accounts.GetValueOrDefault(e.Id, ModuleEmployeeAccount.None)));
            }
        }
        return [.. employees.OrderBy(e => e.FullName, StringComparer.CurrentCulture)];
    }

    /// <summary>
    /// Связи всех сотрудников. Строка связи без учётной записи — «удалена» (внешний ключ обнулил её,
    /// <c>work-01</c>); строка, чья учётная запись исчезла в эту секунду, — тоже «удалена»: показать связь
    /// с несуществующим хуже. Правило то же, что у экрана (<see cref="EmployeeAccountReads" />).
    /// </summary>
    private async Task<Dictionary<Guid, ModuleEmployeeAccount>> AccountsAsync(CancellationToken ct)
    {
        var links = await (
            from a in db.EmployeeAccounts.AsNoTracking()
            join u in db.Users.AsNoTracking() on a.UserId equals (Guid?)u.Id into us
            from u in us.DefaultIfEmpty()
            select new { a.EmployeeId, a.UserId, a.AccountName, FoundId = (Guid?)u.Id, u.DisplayName, u.UserName })
            .ToListAsync(ct);
        return links.ToDictionary(l => l.EmployeeId, l => l.UserId is { } userId && l.FoundId is not null
            ? new ModuleEmployeeAccount(ModuleAccountState.Linked, userId,
                EmployeeAccountLinks.AccountName(l.DisplayName, l.UserName))
            : new ModuleEmployeeAccount(ModuleAccountState.Deleted, null, l.AccountName));
    }

    private static bool Matches(ModuleEmployee e, string? query) =>
        string.IsNullOrWhiteSpace(query)
        || new[] { e.FullName, e.PersonnelNumber }
            .Any(text => text?.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase) == true);
}
