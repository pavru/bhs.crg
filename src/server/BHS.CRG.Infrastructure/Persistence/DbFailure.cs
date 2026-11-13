using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BHS.CRG.Infrastructure.Persistence;

/// <summary>Что значит отказ базы при сохранении — одним местом, а не выражением у каждого, кто ловит.</summary>
public static class DbFailure
{
    /// <summary>
    /// Нарушение уникальности — «такая запись уже есть». Там, где две записи одного ключа
    /// возможны по построению (повтор запроса, два процесса), это штатный ход, а не сбой.
    /// </summary>
    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>
    /// Нарушение уникальности ИМЕННО этих ограничений. Чужое столкновение, объявленное «такая запись
    /// уже есть», увело бы разбирательство в сторону — поэтому ловящий называет свои индексы.
    /// </summary>
    public static bool IsUniqueViolationOf(DbUpdateException ex, params string[] constraints) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && constraints.Contains(pg.ConstraintName);

    /// <summary>
    /// Нарушение внешнего ключа — «того, на что ссылаются, уже нет»: строку, на которую указывает
    /// запись, удалили между проверкой и сохранением.
    /// </summary>
    public static bool IsForeignKeyViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation };
}
