using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Заведение записи справочника ядра модулем (см. <see cref="IModuleCatalogIntake" />, issue #1077).
///
/// <para>Здесь — только граница: объявлен ли справочник включённым модулем, обеспечена ли роль,
/// сопоставлены ли поля и лежит ли тип в общей таблице. Раскладку по схеме, замок и создание делает
/// служба ядра (<see cref="ICatalogIntake" />).</para>
/// </summary>
public sealed class ModuleCatalogIntakePort(
    ModuleRegistry modules, ICatalogIntake intake, IRepository<DocumentType> types, TagCatalog tags)
    : IModuleCatalogIntake
{
    public async Task<ModuleIntakeResult?> CreateAsync(
        string role, string name, string uniqueValue, CancellationToken ct = default)
    {
        // Объявление — сначала: в необъявленный справочник не заводится, обеспечена роль или нет.
        // Роль модуль называет в своём коде, а не получает из запроса, поэтому это дефект, а не отказ.
        var declared = modules.Enabled.SelectMany(m => m.IntakeTypes).FirstOrDefault(t => t.Role == role)
            ?? throw new InvalidOperationException(
                $"Справочник с ролью «{role}» ни один включённый модуль не объявил в IntakeTypes — записи " +
                "в нём портом заведения не создаются.");

        // Справочник находит тот же расчёт, что и читающий порт: иначе сопоставление находило бы
        // тип и отвечало «организации нет», а заведение тут же отвечало бы «справочника нет».
        var all = await types.GetAllAsync(ct);
        var resolved = RoleResolution.Role(role, all);
        if (resolved.Root is not { } type) return null;

        if (!TypeStorageRules.KeptInCommonTable(type))
            throw new InvalidOperationException(
                $"Тип «{type.Code}» лежит не в общей таблице (носитель «{type.Storage}») — портом заведения " +
                "его записи не создаются.");

        // Ключи полей — по тэгам. Несопоставленное поле — отказ человеку, а не дефект: тэг снимает
        // администратор, и причина обязана называть поле его словами, а не ключом.
        var refusals = new List<string>();
        var nameKey = KeyOf(resolved, declared.NameTag, all, refusals);
        var uniqueKey = KeyOf(resolved, declared.UniqueTag, all, refusals);
        if (nameKey is null || uniqueKey is null) return new ModuleIntakeResult(null, [], refusals);

        var outcome = await intake.CreateAsync(
            new CatalogIntakeRequest(type.Id, nameKey, uniqueKey, name, uniqueValue), ct);

        // Код вида у найденных — код ИХ типа: совпасть мог и подтип.
        var codes = all.ToDictionary(t => t.Id, t => t.Code);
        return new ModuleIntakeResult(
            outcome.Created is { } created
                ? new ModuleCatalogRef(created.Id, type.Code, created.DisplayName, created.IsArchived)
                : null,
            [.. outcome.Existing.Select(r => new ModuleCatalogRef(
                r.Id, codes.GetValueOrDefault(r.CompositeTypeId, type.Code), r.DisplayName, r.Archived))],
            outcome.Refusals);
    }

    private string? KeyOf(ResolvedRole role, string tagCode, IReadOnlyList<DocumentType> all, List<string> refusals)
    {
        var tag = tags.Find(tagCode) ?? throw new InvalidOperationException(
            $"Тэга «{tagCode}», названного в IntakeTypes, в реестре нет.");
        var field = RoleResolution.Field(role, tag, all);
        if (field.Key is not null) return field.Key;

        refusals.Add(field.Fault switch
        {
            RoleFieldFault.Several =>
                $"поле «{tag.Label}» назначено дважды ({string.Join(", ", field.Keys.Select(k => $"«{k}»"))}) — в какое класть значение, не понять",
            RoleFieldFault.WrongKind => $"поле «{tag.Label}» назначено полю неподходящего вида",
            RoleFieldFault.DiffersInSubtype =>
                $"поле «{tag.Label}» у подтипа «{field.Subtype!.Name}» назначено иначе, чем у самого справочника",
            _ => $"в справочнике не назначено поле «{tag.Label}»",
        });
        return null;
    }
}
