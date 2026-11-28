using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;

namespace BHS.CRG.Modules.Work.Devices;

internal sealed class FieldReferencePages(IDataProtectionProvider protection) : IDisposable
{
    internal const int DefaultLimit = 100;
    internal const int MaxLimit = 200;

    private readonly ITimeLimitedDataProtector _cursor = protection
        .CreateProtector("BHS.CRG.Field.ReferencePage.v1").ToTimeLimitedDataProtector();

    private sealed record Position(string Scope, string Revision, int Offset);
    private readonly MemoryCache _snapshots = new(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
    private readonly SemaphoreSlim[] _loadLocks = [.. Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1))];

    internal async Task<object?> ReadAsync(string snapshotKey, string revision, string scope, string? cursor, int limit,
        Func<Task<FieldReferenceSnapshot>> load, Func<Task<string>> readRevision, CancellationToken ct)
    {
        if (!_snapshots.TryGetValue<CachedSnapshot>(snapshotKey, out var cached) || cached!.SourceRevision != revision)
        {
            var gate = _loadLocks[(uint)StringComparer.Ordinal.GetHashCode(snapshotKey) % _loadLocks.Length];
            await gate.WaitAsync(ct);
            try
            {
                if (!_snapshots.TryGetValue<CachedSnapshot>(snapshotKey, out cached) || cached!.SourceRevision != revision)
                {
                    if (await readRevision() != revision) return null;
                    var snapshot = await load();
                    if (await readRevision() != revision) return null;
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        state = snapshot.State, items = snapshot.Items, metadata = snapshot.Metadata,
                    });
                    cached = new CachedSnapshot(revision, Convert.ToHexString(SHA256.HashData(bytes)), snapshot);
                    _snapshots.Set(snapshotKey, cached, new MemoryCacheEntryOptions()
                        .SetSize(bytes.LongLength + 64L * snapshot.Items.Count + 1024)
                        .SetSlidingExpiration(TimeSpan.FromMinutes(10)));
                }
            }
            finally { gate.Release(); }
        }

        var (state, items, metadata) = cached!.Snapshot;
        var previous = Read(cursor);
        var replace = previous is null || previous.Scope != scope || previous.Revision != cached.ContentHash;
        var offset = replace ? 0 : previous!.Offset;
        var page = items.Skip(offset).Take(limit).ToArray();
        var next = offset + page.Length;
        return new
        {
            items = page, state, metadata, replace, limit, more = next < items.Count,
            cursor = !replace && next == offset ? cursor
                : _cursor.Protect(JsonSerializer.Serialize(new Position(scope, cached.ContentHash, next)), TimeSpan.FromHours(24)),
        };
    }

    private sealed record CachedSnapshot(string SourceRevision, string ContentHash, FieldReferenceSnapshot Snapshot);

    public void Dispose()
    {
        _snapshots.Dispose();
        foreach (var gate in _loadLocks) gate.Dispose();
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

internal sealed record FieldReferenceSnapshot(string State, IReadOnlyList<object> Items, object? Metadata = null)
{
    internal static FieldReferenceSnapshot Of<T>(IEnumerable<T>? items, Func<T, Guid> key, object? metadata = null) =>
        items is null ? new("not-configured", [], metadata) : new("ok", [.. items.OrderBy(key).Cast<object>()], metadata);
}
