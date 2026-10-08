using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Заведение записи справочника ядра модулем (см. <see cref="IModuleCatalogIntake" />, issue #1077).
///
/// <para>Здесь — только граница: объявлен ли тип включённым модулем и лежит ли он в общей таблице.
/// Раскладку по схеме, замок и создание делает служба ядра (<see cref="ICatalogIntake" />).</para>
/// </summary>
public sealed class ModuleCatalogIntakePort(
    ModuleRegistry modules, ICatalogIntake intake, IRepository<DocumentType> types) : IModuleCatalogIntake
{
    public async Task<ModuleIntakeResult?> CreateAsync(
        string typeCode, string name, string uniqueValue, CancellationToken ct = default)
    {
        // Объявление — сначала: необъявленный тип не заводится, есть он в базе или нет. Код типа
        // модуль называет в своём коде, а не получает из запроса, поэтому это дефект, а не отказ.
        var declared = modules.Enabled.SelectMany(m => m.IntakeTypes).FirstOrDefault(t => t.TypeCode == typeCode)
            ?? throw new InvalidOperationException(
                $"Тип «{typeCode}» ни один включённый модуль не объявил в IntakeTypes — записи этого типа " +
                "портом заведения не создаются.");

        var found = await types.FindAsync(t => t.Code == typeCode, ct);
        if (found.Count == 0) return null;

        var type = found[0];
        if (!TypeStorageRules.KeptInCommonTable(type))
            throw new InvalidOperationException(
                $"Тип «{typeCode}» лежит не в общей таблице (носитель «{type.Storage}») — портом заведения " +
                "его записи не создаются.");

        var outcome = await intake.CreateAsync(
            new CatalogIntakeRequest(type.Id, declared.NameField, declared.UniqueField, name, uniqueValue), ct);

        // Код вида у найденных — код ИХ типа: совпасть мог и подтип.
        var codes = (await types.GetAllAsync(ct)).ToDictionary(t => t.Id, t => t.Code);
        return new ModuleIntakeResult(
            outcome.Created is { } created
                ? new ModuleCatalogRef(created.Id, type.Code, created.DisplayName, created.IsArchived)
                : null,
            [.. outcome.Existing.Select(r => new ModuleCatalogRef(
                r.Id, codes.GetValueOrDefault(r.CompositeTypeId, type.Code), r.DisplayName, r.Archived))],
            outcome.Refusals);
    }
}
