using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Schema;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Infrastructure.Generation;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Ports;
using Mediator;
using Microsoft.EntityFrameworkCore;
using RecordsFor = BHS.CRG.Application.Documents.RecordsFor;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Стройки и разделы ядра для модуля — из тех же таблиц, что читает дерево строек на экране
/// (задача F1, issue #1085). С <c>work-11-assignments</c> — и с действующим поясом (ТЗ CORE-5): пояс
/// компании порт берёт у настроек ядра сам, модулю их не видно.
/// </summary>
public sealed class ModuleConstructionsPort(
    IRepository<Construction> constructions, IRepository<Section> sections, IAppSettingsStore settings,
    AppDbContext db, IMediator mediator) : IModuleConstructions
{
    public Task<Guid[]> ExistingIdsAsync(Guid[] siteIds, CancellationToken ct = default) =>
        db.Constructions.AsNoTracking().Where(c => siteIds.Contains(c.Id)).Select(c => c.Id).ToArrayAsync(ct);

    public async Task<string> CompanyTimeZoneIdAsync(CancellationToken ct = default) =>
        SiteZones.IanaId(await settings.GetCompanyTimeZoneAsync(ct));

    public async Task<IReadOnlyDictionary<Guid, string>> AddressesAsync(IReadOnlyCollection<Guid> siteIds,
        CancellationToken ct = default)
    {
        var addresses = new Dictionary<Guid, string>();
        if (siteIds.Count == 0) return addresses;

        var allTypes = await db.DocumentTypes.AsNoTracking().ToListAsync(ct);
        if (LevelProfiles.ResolveProfileTypeId(allTypes, FunctionalTag.ProfileConstruction) is not { } profileType)
            return addresses;

        var ids = siteIds.ToArray();
        var profiles = await (from site in db.Constructions.AsNoTracking()
                              join profile in db.DomainObjects.AsNoTracking() on site.ProfileObjectId equals (Guid?)profile.Id
                              where ids.Contains(site.Id) && profile.ScopeLevel == CatalogScope.Construction
                                  && profile.ScopeId == site.Id && profile.ArchivedAt == null
                              select new { SiteId = site.Id, ProfileId = profile.Id, TypeId = profile.CompositeTypeId })
            .ToListAsync(ct);
        var typesById = allTypes.ToDictionary(t => t.Id);
        foreach (var group in profiles.GroupBy(p => p.TypeId))
        {
            if (!DocumentTypeSchemaReader.IsSameOrDescendant(group.Key, profileType, typesById)) continue;
            var keys = SchemaTags.TaggedFieldsInSchemaOrder(typesById[group.Key], allTypes)
                .Where(f => f.Tag.Code == FunctionalTag.ConstructionAddress).Select(f => f.Key).Distinct().ToArray();
            if (keys.Length != 1 || DocumentTypeSchemaReader.Field(group.Key, keys[0], typesById)
                    is not { Type: "string" or "text", Computed: false })
                continue;

            var sitesByProfile = group.ToDictionary(p => p.ProfileId, p => p.SiteId);
            var values = await mediator.Send(new CommonDataFieldValuesQuery([group.Key], keys[0], RecordsFor.Choice,
                Ids: [.. sitesByProfile.Keys]), ct);
            foreach (var address in values)
                if (!address.Unreadable && !string.IsNullOrWhiteSpace(address.Value))
                    addresses[sitesByProfile[address.Record.Id]] = address.Value.Trim();
        }
        return addresses;
    }

    public async Task<IReadOnlyList<ModuleConstruction>> ListAsync(CancellationToken ct = default)
    {
        var all = await constructions.GetAllAsync(ct);

        // Разделы — вторым запросом на все стройки сразу, а не навигацией: хранилище навигацию не
        // подгружает, и список строек пришёл бы без разделов вовсе — «разделов у стройки нет»
        // выглядело бы правдой.
        var bySite = (await sections.GetAllAsync(ct)).ToLookup(s => s.ConstructionId);
        var company = await settings.GetCompanyTimeZoneAsync(ct);

        // Порядок задаёт ПОРТ, как и у справочника: хранилище не сортирует вовсе, а список читает
        // человек.
        return [.. all
            .OrderBy(c => c.Name, StringComparer.CurrentCulture)
            .Select(c =>
            {
                var (zone, inherited) = SiteZones.Of(c.TimeZoneId, company);
                return new ModuleConstruction(c.Id, c.Name, [.. bySite[c.Id]
                    .OrderBy(s => s.Name, StringComparer.CurrentCulture)
                    .Select(s => new ModuleSection(s.Id, s.Name))], SiteZones.IanaId(zone), inherited);
            })];
    }
}
