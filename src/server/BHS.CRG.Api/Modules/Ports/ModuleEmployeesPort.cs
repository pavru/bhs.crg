using BHS.CRG.Api.Endpoints.Core;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Ports;
using Mediator;
using Microsoft.EntityFrameworkCore;
using RecordsFor = BHS.CRG.Application.Documents.RecordsFor;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Справочник сотрудников для модуля (ТЗ PLAN-16, CORE-7) — те же записи, что отдаёт дверь
/// <c>/api/employees</c>. Табельный номер и период работы — по тэгам <c>employee.*</c> у типа самой
/// карточки: ключи заказчик вправе переименовать.
///
/// <para>Поиск читает весь справочник: искать надо и по табельному номеру в данных карточки.
/// Явный список идентификаторов ограничивает чтение карточек и связей.</para>
///
/// <para>Связь с учётной записью (<c>work-11-assignments</c>) — из таблицы связи <c>work-01</c>, одним
/// запросом на весь справочник: поле карточки <c>УчётнаяЗапись</c> связь не заполняет, и прочитанное
/// оттуда расходилось бы с тем, по чему считаются «свои» отчёты.</para>
///
/// <para><b>Справочник читается раз на область</b> (порт — scoped, то есть на запрос): ворота стройки
/// спрашивают сотрудника учётной записи, адрес следом — назначаемого, и каждый вопрос иначе читал бы весь
/// справочник заново. Модули сотрудников не пишут, а запись ядра и чтение порта в одном запросе не
/// встречаются.</para>
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
        var records = _loaded ? _all : await LoadAsync(ids, ct);
        if (records is null) return null;
        var wanted = ids.ToHashSet();
        return [.. records.Where(e => wanted.Contains(e.Id))];
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

    private List<ModuleEmployee>? _all;
    private bool _loaded;

    private async Task<List<ModuleEmployee>?> AllAsync(CancellationToken ct)
    {
        if (!_loaded)
        {
            _all = await LoadAsync(null, ct);
            _loaded = true;
        }
        return _all;
    }

    private async Task<List<ModuleEmployee>?> LoadAsync(IReadOnlyCollection<Guid>? ids, CancellationToken ct)
    {
        var all = await types.GetAllAsync(ct);
        if (CoreDirectories.Family(all, CoreRecordTypes.EmployeeCode) is not { } family) return null;

        if (ids is { Count: 0 }) return [];
        var accounts = await AccountsAsync(ids, ct);
        var records = ids is null ? null : await mediator.Send(new ListCommonDataEntriesQuery(RecordsFor.Display, Ids: ids), ct);
        var byType = records?.ToLookup(e => e.CompositeTypeId);
        var employees = new List<ModuleEmployee>();
        foreach (var (typeId, type) in family)
        {
            // Ключи — раз на тип, а не на карточку: схема разбирается по цепочке наследования.
            var number = CoreDirectories.TaggedKey(type, all, FunctionalTag.EmployeePersonnelNumber);
            var from = CoreDirectories.TaggedKey(type, all, FunctionalTag.EmployeeEmployedFrom);
            var to = CoreDirectories.TaggedKey(type, all, FunctionalTag.EmployeeEmployedTo);
            var entries = byType?[typeId] ?? await mediator.Send(new ListCommonDataEntriesQuery(RecordsFor.Display, CompositeTypeId: typeId), ct);
            foreach (var e in entries)
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
    private async Task<Dictionary<Guid, ModuleEmployeeAccount>> AccountsAsync(IReadOnlyCollection<Guid>? ids, CancellationToken ct)
    {
        var links = await (
            from a in db.EmployeeAccounts.AsNoTracking()
            join u in db.Users.AsNoTracking() on a.UserId equals (Guid?)u.Id into us
            from u in us.DefaultIfEmpty()
            where ids == null || ids.Contains(a.EmployeeId)
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
