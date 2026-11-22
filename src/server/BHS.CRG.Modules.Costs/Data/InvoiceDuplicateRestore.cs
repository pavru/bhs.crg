using System.Data.Common;
using System.Text.Json;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>Согласует восстановление счетов и подтверждений с обычными записями модуля.</summary>
internal static class InvoiceDuplicateRestore
{
    internal static async Task PrepareAsync(
        DbTransaction transaction, IDictionary<string, JsonElement[]> tables, CancellationToken ct)
    {
        await InvoiceDuplicateLock.AcquireForRestoreAsync(transaction, ct);

        if (!tables.TryGetValue("invoices", out var invoices)) return;
        var revisions = new Dictionary<(Guid Invoice, Guid Revision), Guid>();
        tables["invoices"] = [.. invoices.Select(row =>
        {
            if (!TryGuid(row, "id", out var id) || !TryGuid(row, "duplicate_revision", out var revision))
                return row;

            // Старую редакцию не возвращаем: в живой базе к ней может оставаться решение, которого в копии нет.
            var fresh = Guid.NewGuid();
            revisions.Add((id, revision), fresh);
            return Replace(row, ("duplicate_revision", fresh));
        })];

        if (revisions.Count == 0 || !tables.TryGetValue("invoice_duplicate_confirmations", out var confirmations))
            return;

        tables["invoice_duplicate_confirmations"] = [.. confirmations.Select(row =>
        {
            // Только действовавшая в копии пара получает обе новые редакции. Исторические записи остаются историческими.
            if (!TryGuid(row, "first_invoice_id", out var first) || !TryGuid(row, "first_revision", out var firstRevision)
                || !TryGuid(row, "second_invoice_id", out var second) || !TryGuid(row, "second_revision", out var secondRevision)
                || !revisions.TryGetValue((first, firstRevision), out var freshFirst)
                || !revisions.TryGetValue((second, secondRevision), out var freshSecond))
                return row;

            return Replace(row, ("first_revision", freshFirst), ("second_revision", freshSecond));
        })];
    }

    private static bool TryGuid(JsonElement row, string name, out Guid guid)
    {
        guid = default;
        return row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetGuid(out guid);
    }

    private static JsonElement Replace(JsonElement row, params (string Name, Guid Value)[] fields)
    {
        var values = row.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        foreach (var (name, value) in fields)
            values[name] = JsonSerializer.SerializeToElement(value);

        return JsonSerializer.SerializeToElement(values);
    }
}
