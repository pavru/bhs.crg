using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Settings;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Settings;

/// <inheritdoc cref="IUserSettingsStore" />
public class UserSettingsStore(AppDbContext db) : IUserSettingsStore
{
    public async Task<IReadOnlyDictionary<string, string>> GetAsync(Guid userId, CancellationToken ct = default) =>
        await db.UserSettings
            .Where(s => s.UserId == userId)
            .ToDictionaryAsync(s => s.Key, s => s.Value, StringComparer.Ordinal, ct);

    public async Task ApplyAsync(
        Guid userId, IReadOnlyDictionary<string, string?> patch, CancellationToken ct = default)
    {
        if (patch.Count == 0) return;

        var keys = patch.Keys.ToArray();
        var existing = await db.UserSettings
            .Where(s => s.UserId == userId && keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, StringComparer.Ordinal, ct);

        foreach (var (key, value) in patch)
        {
            var row = existing.GetValueOrDefault(key);
            if (value is null)
            {
                if (row is not null) db.UserSettings.Remove(row);
            }
            else if (row is null)
            {
                db.UserSettings.Add(UserSetting.Create(userId, key, value));
            }
            else
            {
                row.SetValue(value);
            }
        }

        // Одно сохранение на весь набор — см. IUserSettingsStore.ApplyAsync.
        await db.SaveChangesAsync(ct);
    }
}
