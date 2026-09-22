namespace BHS.CRG.Application.Activity;

/// <summary>Действие, которое журнал умеет записывать: код в базе и название для человека.</summary>
/// <param name="Code">Код вида <c>ядро.объект.действие</c> — он и хранится в записи.</param>
/// <param name="Title">Название на экране журнала: «Изменена роль пользователя».</param>
public sealed record ActivityAction(string Code, string Title);

/// <summary>
/// Что журнал записывает (ТЗ CORE-28). Этап 1: состав модулей, роли и права, схемы типов.
///
/// Каталог существует не ради порядка, а вместо проверки: служба принимает
/// <see cref="ActivityAction" />, а не строку, поэтому опечатка в коде действия невыразима. Код с
/// опечаткой записался бы молча и всплыл бы на экране журнала строкой без названия — то есть тогда,
/// когда запись уже сделана и переписать её нельзя.
///
/// ⚠️ Названия читает человек, коды — база. Переименование названия безопасно, переименование КОДА
/// осиротит уже записанное: старые записи останутся со старым кодом, и <see cref="Title" /> покажет
/// его как есть. Так и задумано — переписывать прошлое журнал не умеет.
/// </summary>
public static class ActivityActions
{
    /// <summary>Заведён пользователь — вместе с ролью, то есть это и выдача прав.</summary>
    public static readonly ActivityAction UserCreated = new("core.user.created", "Заведён пользователь");

    /// <summary>Смена роли: то самое «с автором, временем и прежним значением» из issue #950.</summary>
    public static readonly ActivityAction UserRoleChanged = new("core.user.role.changed", "Изменена роль пользователя");

    /// <summary>Удалён пользователь — снятие всех прав разом.</summary>
    public static readonly ActivityAction UserDeleted = new("core.user.deleted", "Удалён пользователь");

    /// <summary>Изменена схема типа документа: состав полей, от которого зависят данные и печать.</summary>
    public static readonly ActivityAction TypeSchemaChanged = new("core.type.schema.changed", "Изменена схема типа");

    /// <summary>
    /// Тип передан другому владельцу (ТЗ CORE-30). В журнал — потому что передача меняет судьбу
    /// типа при выключении модуля: вчера он предлагался в редакторе, сегодня нет, и спросить
    /// «кто и когда» будет не у кого.
    /// </summary>
    public static readonly ActivityAction TypeOwnerChanged = new("core.type.owner.changed", "Изменён владелец типа");

    /// <summary>Изменён состав включённых модулей (ТЗ AUTH-17) — замечается при старте.</summary>
    public static readonly ActivityAction ModulesChanged = new("core.modules.changed", "Изменён состав модулей");

    /// <summary>Заведена роль (ТЗ AUTH-5): новый набор прав, который теперь можно кому-то выдать.</summary>
    public static readonly ActivityAction RoleCreated = new("core.role.created", "Заведена роль");

    /// <summary>
    /// Изменён состав прав роли (ТЗ AUTH-5.1). Действует немедленно у ВСЕХ, кто эту роль носит, —
    /// то есть это выдача или снятие доступа сразу многим, и без записи спросить потом не у кого.
    /// </summary>
    public static readonly ActivityAction RolePermissionsChanged =
        new("core.role.permissions.changed", "Изменён состав прав роли");

    /// <summary>Переименована роль: кто и когда решил, что «Кладовщик» теперь называется иначе.</summary>
    public static readonly ActivityAction RoleRenamed = new("core.role.renamed", "Переименована роль");

    /// <summary>Удалена роль — вместе со всем, что она давала.</summary>
    public static readonly ActivityAction RoleDeleted = new("core.role.deleted", "Удалена роль");

    public static IReadOnlyList<ActivityAction> All =>
        [UserCreated, UserRoleChanged, UserDeleted, TypeSchemaChanged, TypeOwnerChanged, ModulesChanged,
         RoleCreated, RolePermissionsChanged, RoleRenamed, RoleDeleted];

    /// <summary>
    /// Название по коду. Неизвестный код возвращается как есть: он приходит из записей, сделанных
    /// другой версией или приехавших копией, и прятать такую строку нельзя — в журнале пропуск
    /// хуже непонятного названия.
    /// </summary>
    public static string Title(string code) =>
        All.FirstOrDefault(a => a.Code == code)?.Title ?? code;
}
