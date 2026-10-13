using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Schema;

/// <summary>
/// Роль справочника нельзя снять с типа, пока на его записи ссылаются документы модулей
/// (решение владельца продукта от 13.10.2026, issue #1101).
///
/// <para><b>Почему.</b> Счёт хранит идентификатор поставщика, а название и саму принадлежность к
/// организациям читает по роли. Сними роль с «Организации» и поставь на другой тип — прежние
/// поставщики во всех заведённых счетах перестанут находиться: записи целы, а счета выглядят
/// потерявшими поставщика. Обратимо, но читается как потеря данных, и узнают о ней не при снятии
/// тэга, а открыв счёт.</para>
///
/// <para>Перенос роли — это снятие с одного типа и назначение другому, а второго без первого не
/// бывает: потолок «один носитель» не даст поставить роль, пока её несёт прежний тип. Поэтому
/// стеречь достаточно снятие.</para>
///
/// <para>⚠️ Чего проверка не видит: подтип, выведенный из справочника сменой родителя, — его записи
/// покидают справочник тем же способом. Случай редкий и чинится возвратом родителя.</para>
/// </summary>
public static class DirectoryRoleRelease
{
    public static async Task EnsureAllowedAsync(
        TagCatalog tags, IDomainObjectRepository objects, IRecordHolders holders,
        DocumentType type, JsonDocument incoming, IReadOnlyList<DocumentType> all, CancellationToken ct)
    {
        var released = tags.All
            .Where(t => tags.IsRole(t.Code))
            .Where(t => SchemaTags.SchemaHasTypeTag(type.Schema, t.Code) && !SchemaTags.SchemaHasTypeTag(incoming, t.Code))
            .ToList();
        if (released.Count == 0) return;

        // Справочник — носитель и его подтипы: записи подтипа покидают его вместе с ролью.
        var family = RoleResolution.Family(type, all).Keys.ToList();
        var ids = await objects.RecordIdsOfTypesAsync(family, ct);
        if (ids.Count == 0) return;

        var held = await holders.HeldAsync(ids, ct);
        var role = string.Join(", ", released.Select(t => $"«{t.Label}»"));

        if (!held.Verified)
            throw new ConflictException(
                $"Роль {role} с типа «{type.Name}» сейчас снять нельзя: не удалось проверить, ссылаются ли " +
                "на его записи документы модулей. Повторите позже; подробности — в журнале сервера.");

        if (held.Ids.Count > 0)
            throw new ConflictException(
                $"Роль {role} с типа «{type.Name}» снять нельзя: на его записи ссылаются документы модулей " +
                $"(записей со ссылками: {held.Ids.Count}). По роли код находит эти записи — без неё они " +
                "перестали бы находиться в уже заведённых документах, хотя сами остались бы на месте. " +
                "Роль можно снять, когда ссылок не останется.");
    }
}
