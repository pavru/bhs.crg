using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BHS.CRG.Tests.Integration;

internal sealed class ReportReplayProbe : DbCommandInterceptor
{
    private Func<Task>? _beforeLookup;

    internal void BeforeNextLookup(Func<Task>? action) => Interlocked.Exchange(ref _beforeLookup, action);

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("FROM work.report_changes", StringComparison.Ordinal)
            && command.CommandText.Contains(".change_id =", StringComparison.Ordinal)
            && Interlocked.Exchange(ref _beforeLookup, null) is { } action)
            await action();
        return result;
    }
}
