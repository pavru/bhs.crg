using BHS.CRG.Domain.Auth;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Api.Auth;

/// <summary>
/// Приводит таблицу прав в соответствие с тем, что объявил код (ТЗ AUTH-1).
///
/// Живёт в корне композиции, а не в слое доступа к данным: ему нужны и контекст базы, и каталог
/// прав из контрактов модулей, а слой доступа к данным про модули знать не должен — направление
/// ссылок одностороннее (CORE-2). Работа разовая, на старте, и корню композиции она по размеру.
///
/// Направление одностороннее и здесь: код — источник истины, база — его отражение. Поэтому сверка идёт при
/// каждом старте и молча: появление нового права и уточнение формулировки — обычные события,
/// сообщать о них некому.
///
/// А вот исчезновение права — событие: на него могли ссылаться роли. Такое право не удаляется, а
/// перестаёт числиться объявленным, и это пишется в лог с перечислением кодов. Удаление оборвало бы
/// ссылки, и администратор увидел бы роль, молча потерявшую часть состава.
/// </summary>
public static class PermissionSynchronizer
{
    public static async Task SyncAsync(
        AppDbContext db, PermissionCatalog catalog, ILogger logger, CancellationToken ct = default)
    {
        var stored = await db.Permissions.ToDictionaryAsync(p => p.Code, StringComparer.OrdinalIgnoreCase, ct);
        var declared = catalog.All.ToDictionary(p => p.Code, StringComparer.OrdinalIgnoreCase);

        foreach (var (code, permission) in declared)
        {
            if (stored.TryGetValue(code, out var existing))
                existing.Redeclare(permission.Gives, permission.Opens, permission.UsuallyWith);
            else
                db.Permissions.Add(
                    Permission.Declare(code, permission.Gives, permission.Opens, permission.UsuallyWith));
        }

        var vanished = stored.Values.Where(p => p.IsDeclared && !declared.ContainsKey(p.Code)).ToList();
        foreach (var permission in vanished) permission.MarkUndeclared();

        if (vanished.Count > 0)
            logger.LogWarning(
                "Права больше не объявлены кодом и не выдаются: {Codes}. Роли, где они стояли, сохранены — " +
                "снимите их в редакторе ролей, если это насовсем",
                string.Join(", ", vanished.Select(p => p.Code)));

        await db.SaveChangesAsync(ct);
    }
}
