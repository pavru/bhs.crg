using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Стройки и разделы ядра для модуля — из тех же таблиц, что читает дерево строек на экране
/// (задача F1, issue #1085).
/// </summary>
public sealed class ModuleConstructionsPort(
    IRepository<Construction> constructions, IRepository<Section> sections) : IModuleConstructions
{
    public async Task<IReadOnlyList<ModuleConstruction>> ListAsync(CancellationToken ct = default)
    {
        var all = await constructions.GetAllAsync(ct);

        // Разделы — вторым запросом на все стройки сразу, а не навигацией: хранилище навигацию не
        // подгружает, и список строек пришёл бы без разделов вовсе — «разделов у стройки нет»
        // выглядело бы правдой.
        var bySite = (await sections.GetAllAsync(ct)).ToLookup(s => s.ConstructionId);

        // Порядок задаёт ПОРТ, как и у справочника: хранилище не сортирует вовсе, а список читает
        // человек.
        return [.. all
            .OrderBy(c => c.Name, StringComparer.CurrentCulture)
            .Select(c => new ModuleConstruction(c.Id, c.Name, [.. bySite[c.Id]
                .OrderBy(s => s.Name, StringComparer.CurrentCulture)
                .Select(s => new ModuleSection(s.Id, s.Name))]))];
    }
}
