using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Modules.Ports;
using MediatR;
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
/// </summary>
public sealed class ModuleEmployeesPort(IMediator mediator, IRepository<DocumentType> types) : IModuleEmployees
{
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
                    CoreDirectories.Date(data, from), CoreDirectories.Date(data, to), e.IsArchived));
            }
        }
        return [.. employees.OrderBy(e => e.FullName, StringComparer.CurrentCulture)];
    }

    private static bool Matches(ModuleEmployee e, string? query) =>
        string.IsNullOrWhiteSpace(query)
        || new[] { e.FullName, e.PersonnelNumber }
            .Any(text => text?.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase) == true);
}
