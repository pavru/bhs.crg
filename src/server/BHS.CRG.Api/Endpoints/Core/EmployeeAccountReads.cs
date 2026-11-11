using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Endpoints.Core;

/// <summary>
/// Носители связи «учётная запись → сотрудник» (ТЗ WORK-47, D15 зонтика <c>work-00-epic</c>): что
/// показывает экран, считает СЕРВЕР — состояние связи, пометку «уволен, но связан», предложение
/// учётной записи по ФИО. Клиент ничего из этого не выводит из реквизитов: догадка на экране и правило
/// на сервере разошлись бы при первой же правке одного из них.
/// </summary>
public sealed class EmployeeAccountReads(AppDbContext db, IRepository<DocumentType> types, IAppSettingsStore settings)
{
    /// <summary>Сколько кандидатов отдаёт поиск за раз.</summary>
    public const int CandidatesMax = 50;

    /// <summary>Состояние связи каждого из сотрудников — одним запросом на всех.</summary>
    public async Task<IReadOnlyDictionary<Guid, EmployeeAccountDto>> ForAsync(
        IReadOnlyCollection<DomainObject> employees, CancellationToken ct)
    {
        var ids = employees.Select(e => e.Id).ToArray();
        var links = await (
            from a in db.EmployeeAccounts.AsNoTracking()
            where ids.Contains(a.EmployeeId)
            join u in db.Users.AsNoTracking() on a.UserId equals (Guid?)u.Id into us
            from u in us.DefaultIfEmpty()
            select new { a.EmployeeId, a.UserId, a.AccountName, u.DisplayName, u.UserName })
            .ToDictionaryAsync(l => l.EmployeeId, ct);

        var dismissal = await DismissalAsync(ct);
        var result = new Dictionary<Guid, EmployeeAccountDto>(employees.Count);
        foreach (var e in employees)
        {
            if (!links.TryGetValue(e.Id, out var link)) { result[e.Id] = EmployeeAccountDto.None; continue; }
            if (link.UserId is null)
            {
                result[e.Id] = new(EmployeeAccountDto.Deleted, link.AccountName, null, null);
                continue;
            }
            // Строка связи есть, а учётной записи нет — удаление в эту же секунду: внешний ключ
            // вот-вот обнулит связь. Сказать «удалена» честнее, чем показать связь с несуществующим.
            if (link.UserName is null)
            {
                result[e.Id] = new(EmployeeAccountDto.Deleted, link.AccountName, null, null);
                continue;
            }
            result[e.Id] = new(EmployeeAccountDto.Linked,
                EmployeeAccountLinks.AccountName(link.DisplayName, link.UserName), link.UserName,
                dismissal.DismissedOn(e));
        }
        return result;
    }

    /// <summary>
    /// Учётные записи под строку поиска: имя, логин и с кем уже связана. Роли и почту для уведомлений
    /// адрес не отдаёт: связывающему они не нужны, а узкий ответ не раскрывает лишнего (ТЗ WORK-47).
    ///
    /// <para>Пустая строка — все по порядку имени: так администратор, не помнящий, как назвали запись,
    /// видит список, а не пустоту. Отбор — в памяти: учётных записей у компании десятки и сотни.</para>
    /// </summary>
    public async Task<(IReadOnlyList<AccountCandidateDto> Items, bool More)> CandidatesAsync(string? q, CancellationToken ct)
    {
        var users = await UsersAsync(ct);
        var linkedTo = await LinkedToAsync(ct);
        var query = (q ?? "").Trim();
        var found = users
            .Where(u => query.Length == 0
                || u.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || u.Login.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(u => u.Name, StringComparer.CurrentCulture)
            .ToList();
        return ([.. found.Take(CandidatesMax).Select(u => new AccountCandidateDto(
                u.Id, u.Name, u.Login, linkedTo.TryGetValue(u.Id, out var who) ? who : null))],
            found.Count > CandidatesMax);
    }

    /// <summary>
    /// Строки режима «Сопоставить»: сотрудники без действующей связи — не в архиве и не уволенные на
    /// сегодня — с предложенной СВОБОДНОЙ учётной записью того же ФИО. Предлагается только
    /// единственная: две подходящие, или одна, подходящая двум сотрудникам, — <c>ambiguous</c>, и
    /// выбирает человек. Занятая учётная запись не предлагается никогда.
    /// </summary>
    public async Task<IReadOnlyList<AccountMatchDto>?> MatchesAsync(CancellationToken ct)
    {
        if (await EmployeeEndpoints.EmployeeTypeIdAsync(types) is not { } typeId) return null;

        var employees = await db.DomainObjects.AsNoTracking()
            .Where(o => o.CompositeTypeId == typeId && o.ArchivedAt == null)
            .ToListAsync(ct);
        var accounts = await ForAsync(employees, ct);
        var dismissal = await DismissalAsync(ct);
        var linkedTo = await LinkedToAsync(ct);
        var free = (await UsersAsync(ct)).Where(u => !linkedTo.ContainsKey(u.Id)).ToList();

        var rows = employees
            .Where(e => accounts[e.Id].State != EmployeeAccountDto.Linked && !dismissal.IsDismissed(e))
            .Select(e => (Employee: e, Fits: free.Where(u => PersonName.Same(e.DisplayName, u.Name)).ToList()))
            .ToList();
        // Одна учётная запись, подходящая двум сотрудникам, — тоже неоднозначность: предложи её обоим,
        // и вторая строка пачки получила бы «занята» за решение, которого человек не принимал.
        var contested = rows.Where(r => r.Fits.Count == 1).GroupBy(r => r.Fits[0].Id)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();

        return [.. rows
            .OrderBy(r => r.Employee.DisplayName, StringComparer.CurrentCulture)
            .Select(r =>
            {
                var single = r.Fits.Count == 1 && !contested.Contains(r.Fits[0].Id) ? r.Fits[0] : null;
                return new AccountMatchDto(
                    new AccountMatchEmployee(r.Employee.Id, r.Employee.DisplayName ?? "", r.Employee.Version),
                    accounts[r.Employee.Id],
                    single is null ? null : new AccountSuggestion(single.Id, single.Name, single.Login),
                    single is not null ? null : r.Fits.Count == 0 ? AccountMatchDto.NoMatch : AccountMatchDto.Ambiguous);
            })];
    }

    /// <summary>
    /// Сотрудник вошедшего — для <c>/api/account/access</c> (ТЗ AUTH-14). Адрес зовут каждые полминуты,
    /// поэтому связь читается одним запросом, а тип справочника спрашивается только когда связи нет:
    /// «вас не связали» и «справочник не заведён» — разные ответы.
    /// </summary>
    public async Task<(object? Employee, string? Reason)> OfUserAsync(Guid userId, CancellationToken ct)
    {
        var own = await (
            from a in db.EmployeeAccounts.AsNoTracking()
            where a.UserId == userId
            join o in db.DomainObjects.AsNoTracking() on a.EmployeeId equals o.Id
            select new { o.Id, o.DisplayName, o.ArchivedAt })
            .FirstOrDefaultAsync(ct);
        if (own is not null)
            return (new { id = own.Id, name = own.DisplayName ?? "", archived = own.ArchivedAt != null }, null);
        return (null, await EmployeeEndpoints.EmployeeTypeIdAsync(types) is null ? DirectoryMissing : NotLinked);
    }

    public const string NotLinked = "not-linked";
    public const string DirectoryMissing = "directory-missing";

    private sealed record UserRow(Guid Id, string Name, string Login);

    private async Task<List<UserRow>> UsersAsync(CancellationToken ct) =>
        [.. (await db.Users.AsNoTracking().Select(u => new { u.Id, u.DisplayName, u.UserName }).ToListAsync(ct))
            .Select(u => new UserRow(u.Id, EmployeeAccountLinks.AccountName(u.DisplayName, u.UserName), u.UserName ?? ""))];

    /// <summary>Действующие связи: учётная запись → сотрудник, с именем сотрудника.</summary>
    private async Task<Dictionary<Guid, LinkedEmployee>> LinkedToAsync(CancellationToken ct) =>
        (await (
            from a in db.EmployeeAccounts.AsNoTracking()
            where a.UserId != null
            join o in db.DomainObjects.AsNoTracking() on a.EmployeeId equals o.Id
            select new { UserId = a.UserId!.Value, o.Id, o.DisplayName })
            .ToListAsync(ct))
        .ToDictionary(l => l.UserId, l => new LinkedEmployee(l.Id, l.DisplayName ?? ""));

    /// <summary>
    /// «Уволен с» по тэгу <c>employee.employedTo</c> против сегодняшнего дня по поясу компании. Ключ —
    /// раз на тип: заказчик вправе переименовать поле, а тэг остаётся.
    /// </summary>
    private async Task<Dismissal> DismissalAsync(CancellationToken ct)
    {
        var all = await types.GetAllAsync(ct);
        var keys = all.ToDictionary(t => t.Id, t => CoreDirectories.TaggedKey(t, all, FunctionalTag.EmployeeEmployedTo));
        var zone = await settings.GetCompanyTimeZoneAsync(ct);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        return new Dismissal(keys, today);
    }

    private sealed record Dismissal(IReadOnlyDictionary<Guid, string?> Keys, DateOnly Today)
    {
        public DateOnly? DismissedOn(DomainObject e) =>
            Keys.TryGetValue(e.CompositeTypeId, out var key)
            && CoreDirectories.Date(e.Data.RootElement, key) is { } to && to <= Today ? to : null;

        public bool IsDismissed(DomainObject e) => DismissedOn(e) is not null;
    }
}

/// <summary>
/// Состояние связи у сотрудника: <c>linked</c>, <c>none</c> или <c>deleted</c>. <see cref="DismissedOn" />
/// — «Уволен с», когда связь действует, а дата наступила: пометка «уволен, но связан».
/// </summary>
public sealed record EmployeeAccountDto(string State, string? Name, string? Login, DateOnly? DismissedOn)
{
    public const string Linked = "linked";
    public const string NoneState = "none";
    public const string Deleted = "deleted";

    public static readonly EmployeeAccountDto None = new(NoneState, null, null, null);
}

public sealed record LinkedEmployee(Guid Id, string Name);

public sealed record AccountCandidateDto(Guid UserId, string Name, string Login, LinkedEmployee? LinkedTo);

public sealed record AccountMatchEmployee(Guid Id, string Name, string Version);

public sealed record AccountSuggestion(Guid UserId, string Name, string Login);

public sealed record AccountMatchDto(
    AccountMatchEmployee Employee, EmployeeAccountDto Account, AccountSuggestion? Suggested, string? Reason)
{
    public const string NoMatch = "no-match";
    public const string Ambiguous = "ambiguous";
}
