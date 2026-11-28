using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace BHS.CRG.Modules.Work.Devices;

internal sealed class FieldReferencePages(IDataProtectionProvider protection)
{
    internal const int DefaultLimit = 100;
    internal const int MaxLimit = 200;

    private readonly ITimeLimitedDataProtector _cursor = protection
        .CreateProtector("BHS.CRG.Field.ReferencePage.v1").ToTimeLimitedDataProtector();

    private sealed record Position(string Scope, string Revision, int Offset);

    internal object Page<T>(string scope, string? cursor, int limit, string state, IReadOnlyList<T> items,
        object? metadata = null)
    {
        // A continuation can only advance through the exact same ordered projection.
        var revision = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { state, items, metadata })));
        var previous = Read(cursor);
        var replace = previous is null || previous.Scope != scope || previous.Revision != revision;
        var offset = replace ? 0 : previous!.Offset;
        var page = items.Skip(offset).Take(limit).ToArray();
        var next = offset + page.Length;
        return new
        {
            items = page, state, metadata, replace, limit, more = next < items.Count,
            cursor = !replace && next == offset ? cursor
                : _cursor.Protect(JsonSerializer.Serialize(new Position(scope, revision, next)), TimeSpan.FromHours(24)),
        };
    }

    private Position? Read(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return null;
        try
        {
            return JsonSerializer.Deserialize<Position>(_cursor.Unprotect(cursor));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
