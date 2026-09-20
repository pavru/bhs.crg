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

    /// <summary>Изменён состав включённых модулей (ТЗ AUTH-17) — замечается при старте.</summary>
    public static readonly ActivityAction ModulesChanged = new("core.modules.changed", "Изменён состав модулей");

    public static IReadOnlyList<ActivityAction> All =>
        [UserCreated, UserRoleChanged, UserDeleted, TypeSchemaChanged, ModulesChanged];

    /// <summary>
    /// Название по коду. Неизвестный код возвращается как есть: он приходит из записей, сделанных
    /// другой версией или приехавших копией, и прятать такую строку нельзя — в журнале пропуск
    /// хуже непонятного названия.
    /// </summary>
    public static string Title(string code) =>
        All.FirstOrDefault(a => a.Code == code)?.Title ?? code;
}
