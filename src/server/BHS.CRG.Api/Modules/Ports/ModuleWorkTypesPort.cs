using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Modules.Ports;
using MediatR;
using RecordsFor = BHS.CRG.Application.Documents.RecordsFor;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Классификатор видов работ для модуля — через те же запросы, что и общие данные (ТЗ CORE-20.4,
/// PLAN-6.1). Части вида работы находит <see cref="WorkTypeKeys" />: по ключу идентичности, цели поля и
/// тэгам, а не по ключам полей.
///
/// <para><b>Читается весь классификатор, с архивом, одним показом</b>, а выбор порт отделяет сам: живые
/// — в выдачу, архивные — числом. Видов работ у компании сотни, а не тысячи, картинок у них нет, и
/// искать по коду — частью ключа в данных записи — запрос по названию не умеет.</para>
/// </summary>
public sealed class ModuleWorkTypesPort(IMediator mediator, IRepository<DocumentType> types) : IModuleWorkTypes
{
    public async Task<IReadOnlyList<ModuleWorkType>?> GetAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (await AllAsync(ct) is not { } all) return null;
        var wanted = ids.ToHashSet();
        return [.. all.Where(w => wanted.Contains(w.Item.Id)).Select(w => w.Item)];
    }

    public async Task<ModuleChoice<ModuleWorkType>?> SearchAsync(string? query, int limit, CancellationToken ct = default)
    {
        // Предел ноль — дефект вызывающего: «архивных под запрос N» без единого живого ничего не значит.
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (await AllAsync(ct) is not { } all) return null;

        var found = all.Where(w => Matches(w.Item, w.Aliases, query)).Select(w => w.Item).ToList();
        return new ModuleChoice<ModuleWorkType>(
            [.. found.Where(w => !w.Archived).Take(limit)], found.Count(w => w.Archived));
    }

    /// <summary>Весь классификатор по коду, с псевдонимами для поиска, — или <c>null</c>, если его нет.</summary>
    private async Task<List<(ModuleWorkType Item, IReadOnlyList<string> Aliases)>?> AllAsync(CancellationToken ct)
    {
        var all = await types.GetAllAsync(ct);
        if (CoreDirectories.Family(all, CoreRecordTypes.WorkTypeCode) is not { } family) return null;
        var units = CoreDirectories.Family(all, CoreRecordTypes.UnitCode) ?? new Dictionary<Guid, DocumentType>();

        var entries = new List<DomainObject>();
        foreach (var typeId in family.Keys)
            entries.AddRange(await mediator.Send(new ListCommonDataEntriesQuery(RecordsFor.Display, CompositeTypeId: typeId), ct));

        var keys = family.ToDictionary(t => t.Key, t => WorkTypeKeys.Of(t.Value, all, [.. units.Keys]));
        var read = entries.Select(e => (Entry: e, Keys: keys[e.CompositeTypeId],
            Unit: CoreDirectories.Reference(e.Data.RootElement, keys[e.CompositeTypeId].Unit))).ToList();

        // Название единицы — по самой записи единицы, а не по снимку в ссылке: единицу переименовывают,
        // а снимок в данных вида работы остаётся прежним.
        var unitIds = read.Select(r => r.Unit).OfType<Guid>().Distinct().ToList();
        var unitNames = unitIds.Count == 0 || units.Count == 0
            ? new Dictionary<Guid, string?>()
            : (await mediator.Send(new CommonDataRefsByIdsQuery([.. units.Keys], unitIds), ct))
                .ToDictionary(u => u.Id, u => u.DisplayName);

        // Порядок — по коду: так классификатор читает сметчик. Сравнение культурное, как у списков ядра.
        return [.. read
            .Select(r =>
            {
                var data = r.Entry.Data.RootElement;
                return (Item: new ModuleWorkType(
                    r.Entry.Id, CoreDirectories.Text(data, r.Keys.Code), CoreDirectories.Text(data, r.Keys.Name),
                    r.Entry.DisplayName ?? "", r.Unit, r.Unit is { } u ? unitNames.GetValueOrDefault(u) : null,
                    CoreDirectories.Flag(data, r.Keys.RequiresLocation), CoreDirectories.Flag(data, r.Keys.RequiresMaterials),
                    CoreDirectories.Flag(data, r.Keys.ProducesId), r.Entry.IsArchived),
                    Aliases: (IReadOnlyList<string>)[.. r.Entry.Aliases]);
            })
            .OrderBy(w => w.Item.Code ?? w.Item.DisplayName, StringComparer.CurrentCulture)
            .ThenBy(w => w.Item.DisplayName, StringComparer.CurrentCulture)];
    }

    private static bool Matches(ModuleWorkType w, IReadOnlyList<string> aliases, string? query) =>
        string.IsNullOrWhiteSpace(query)
        || new[] { w.Code, w.Name, w.DisplayName }.Concat(aliases)
            .Any(text => text?.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase) == true);
}
