using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Ports;
using MediatR;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Заведение записи справочника ядра модулем (см. <see cref="IModuleCatalogIntake" />, issue #1077).
///
/// <para>Здесь — только граница: объявлен ли справочник включённым модулем, обеспечена ли роль,
/// сопоставлены ли поля и лежит ли тип в общей таблице. Раскладку по схеме, замок и создание делает
/// служба ядра (<see cref="ICatalogIntake" />).</para>
/// </summary>
public sealed class ModuleCatalogIntakePort(
    ModuleRegistry modules, ICatalogIntake intake, IRepository<DocumentType> types, TagCatalog tags,
    IMediator mediator)
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

        // Годен ли носитель в справочники (составной, в общих данных), решает сам расчёт роли: тип
        // с собственной таблицей носителем не считается, и сюда не доходит.

        // Ключи полей — по тэгам. Несопоставленное поле — отказ человеку, а не дефект: тэг снимает
        // администратор, и причина обязана называть поле его словами, а не ключом.
        var refusals = new List<string>();
        var nameKey = KeyOf(resolved, declared.NameTag, all, refusals);
        var uniqueKey = KeyOf(resolved, declared.UniqueTag, all, refusals);
        if (nameKey is null || uniqueKey is null) return new ModuleIntakeResult(null, [], refusals);

        var outcome = await intake.CreateAsync(
            new CatalogIntakeRequest(type.Id, nameKey, uniqueKey, name, uniqueValue), ct);

        // Код вида у найденных — код ИХ типа: совпасть мог и подтип. Название — для показа, как у
        // читающего порта (issue #1473): найденная по ИНН организация называется сокращённым, если оно
        // есть. Новая сокращённого не имеет — заведение его не выдумывает.
        var codes = all.ToDictionary(t => t.Id, t => t.Code);
        var names = outcome.Existing.Count == 0
            ? null
            : await RecordDisplayNames.LoadAsync(mediator, tags, resolved, all, ct, [.. outcome.Existing.Select(r => r.Id)]);
        return new ModuleIntakeResult(
            outcome.Created is { } created
                ? new ModuleCatalogRef(created.Id, type.Code, created.DisplayName, created.IsArchived)
                : null,
            [.. outcome.Existing.Select(r => names!.Apply(new ModuleCatalogRef(
                r.Id, codes.GetValueOrDefault(r.CompositeTypeId, type.Code), r.DisplayName, r.Archived)))],
            outcome.Refusals);
    }

    private string? KeyOf(ResolvedRole role, string tagCode, IReadOnlyList<DocumentType> all, List<string> refusals)
    {
        var tag = tags.Find(tagCode) ?? throw new InvalidOperationException(
            $"Тэга «{tagCode}», названного в IntakeTypes, в реестре нет.");
        var field = RoleResolution.Field(role, tag, all);
        if (field.DirectKey is not null) return field.DirectKey;

        // Тэг на части составного (issue #1473): заведение пишет значение в поле целиком и в часть не
        // попало бы — ключ составного означал бы «записать название не туда» (ревью PR #1898).
        refusals.Add(field.Describe(tag)
            ?? $"поле «{tag.Label}» назначено части составного поля «{field.Key}» — заводить запись по нему нельзя");
        return null;
    }
}
