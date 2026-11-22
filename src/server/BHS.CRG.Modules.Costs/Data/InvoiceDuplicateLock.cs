using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Data;

public enum InvoiceDuplicateWrite { None, Payment, Change }

/// <summary>Упорядочивает внесение оплаты и записи, меняющие блокирующие пары (D2).</summary>
internal static class InvoiceDuplicateLock
{
    // Отдельный ключ в пространстве bigint, общий для всех счетов схемы costs.
    internal const long Key = 0x434F53544455504C;

    internal static Task AcquireAsync(CostsDbContext db, InvoiceDuplicateWrite mode, CancellationToken ct)
    {
        if (mode == InvoiceDuplicateWrite.None) return Task.CompletedTask;
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Проверку дублей при записи защищает транзакционный замок.");

        return mode == InvoiceDuplicateWrite.Payment
            ? db.Database.SqlQuery<int>($"SELECT 1 AS \"Value\" FROM pg_advisory_xact_lock_shared({Key})").ToListAsync(ct)
            : db.Database.SqlQuery<int>($"SELECT 1 AS \"Value\" FROM pg_advisory_xact_lock({Key})").ToListAsync(ct);
    }

    internal static async Task AcquireForRestoreAsync(DbTransaction transaction, CancellationToken ct)
    {
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_advisory_xact_lock(@key)";
        var key = command.CreateParameter();
        key.ParameterName = "key";
        key.Value = Key;
        command.Parameters.Add(key);
        await command.ExecuteNonQueryAsync(ct);
    }
}
