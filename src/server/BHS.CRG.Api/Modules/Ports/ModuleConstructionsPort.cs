using BHS.CRG.Application.Common;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Стройки и разделы ядра для модуля — из тех же таблиц, что читает дерево строек на экране
/// (задача F1, issue #1085). С <c>work-11-assignments</c> — и с действующим поясом (ТЗ CORE-5): пояс
/// компании порт берёт у настроек ядра сам, модулю их не видно.
/// </summary>
public sealed class ModuleConstructionsPort(
    IRepository<Construction> constructions, IRepository<Section> sections, IAppSettingsStore settings) : IModuleConstructions
{
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
