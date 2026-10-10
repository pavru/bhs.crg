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
}
