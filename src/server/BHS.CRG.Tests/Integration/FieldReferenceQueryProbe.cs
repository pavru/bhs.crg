using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BHS.CRG.Tests.Integration;

internal sealed class FieldReferenceQueryProbe : DbCommandInterceptor
{
    private int _catalogReads;
    internal int CatalogReads => Volatile.Read(ref _catalogReads);

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        var sql = command.CommandText;
        if (sql.Contains("FROM domain_objects", StringComparison.Ordinal)
            && sql.Contains("ORDER BY", StringComparison.Ordinal)
            && sql.Contains("LIMIT", StringComparison.Ordinal))
            Interlocked.Increment(ref _catalogReads);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
