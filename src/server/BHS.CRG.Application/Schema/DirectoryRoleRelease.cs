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
/// <para><b>Дверей две, и стерегутся обе.</b> Вторая — смена родителя подтипа
/// (<see cref="EnsureMayLeaveAsync" />): записи «ОрганизацииСРО», переставленной под другой тип,
/// покидают справочник тем же способом, хотя схему никто не трогал (ревью PR #1317).</para>
///
/// <para>⚠️ Чего проверка не видит: восстановление копии. Оно типы переписывает целиком и о
/// держателях не спрашивает — зато говорит о необеспеченной роли в своём отчёте.</para>
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

        var role = string.Join(", ", released.Select(t => $"«{t.Label}»"));
        await EnsureNotHeldAsync(objects, holders, type, all,
            $"Роль {role} с типа «{type.Name}» сейчас снять нельзя",
            $"Роль {role} с типа «{type.Name}» снять нельзя: ",
            "Роль можно снять, когда ссылок не останется.", ct);
    }

    /// <summary>
    /// Смена родителя: тип, входивший в справочник подтипом, не выводится из него, пока на записи
    /// его и его потомков ссылаются документы модулей.
    /// </summary>
    /// <param name="newParentId">Родитель, которого типу назначают. Проверку зовут ДО перестановки:
    /// семья справочника считается по прежней цепочке.</param>
    public static async Task EnsureMayLeaveAsync(
        TagCatalog tags, IDomainObjectRepository objects, IRecordHolders holders,
        DocumentType type, Guid? newParentId, IReadOnlyList<DocumentType> all, CancellationToken ct)
    {
        var left = new List<TagDefinition>();
        foreach (var tag in tags.All.Where(t => tags.IsRole(t.Code)))
        {
            var directory = RoleResolution.Role(tag.Code, all);

            // Сам носитель справочник не покидает, куда бы его ни переставили: роль на нём.
            if (directory.Root is not { } root || root.Id == type.Id || !directory.Family.ContainsKey(type.Id)) continue;
            if (newParentId is { } parent && directory.Family.ContainsKey(parent)) continue;
            left.Add(tag);
        }

        if (left.Count == 0) return;

        var role = string.Join(", ", left.Select(t => $"«{t.Label}»"));
        await EnsureNotHeldAsync(objects, holders, type, all,
            $"Тип «{type.Name}» сейчас нельзя вывести из справочника {role}",
            $"Тип «{type.Name}» нельзя вывести из справочника {role}: новый родитель в этот справочник не входит, а ",
            "Родителя можно сменить, когда ссылок не останется.", ct);
    }

    private static async Task EnsureNotHeldAsync(
        IDomainObjectRepository objects, IRecordHolders holders, DocumentType type, IReadOnlyList<DocumentType> all,
        string unverified, string refused, string wayOut, CancellationToken ct)
    {
        // Тип и его подтипы: их записи покидают справочник вместе с ним.
        var family = RoleResolution.Family(type, all).Keys.ToList();
        var ids = await objects.RecordIdsOfTypesAsync(family, ct);
        if (ids.Count == 0) return;

        var held = await holders.HeldAsync(ids, ct);

        if (!held.Verified)
            throw new ConflictException(
                $"{unverified}: не удалось проверить, ссылаются ли на его записи документы модулей. " +
                "Повторите позже; подробности — в журнале сервера.");

        if (held.Ids.Count > 0)
            throw new ConflictException(
                $"{refused}на его записи ссылаются документы модулей " +
                $"(записей со ссылками: {held.Ids.Count}). По роли код находит эти записи — вне справочника " +
                "они перестали бы находиться в уже заведённых документах, хотя сами остались бы на месте. " +
                wayOut);
    }
}
