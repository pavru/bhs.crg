using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Settings;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Settings;

/// <inheritdoc cref="IAppSettingsStore" />
public class AppSettingsStore(AppDbContext db) : IAppSettingsStore
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        (await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct))?.Value;

    public async Task<IReadOnlyDictionary<string, string>> GetManyAsync(
        IReadOnlyCollection<string> keys, CancellationToken ct = default) =>
        keys.Count == 0
            ? new Dictionary<string, string>()
            : await db.AppSettings.AsNoTracking().Where(s => keys.Contains(s.Key))
                .ToDictionaryAsync(s => s.Key, s => s.Value, StringComparer.Ordinal, ct);

    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (value is null)
        {
            if (row is not null) db.AppSettings.Remove(row);
        }
        else if (row is null) db.AppSettings.Add(AppSetting.Create(key, value));
        else row.SetValue(value);

        await db.SaveChangesAsync(ct);
    }

    public async Task<TimeZoneInfo> GetCompanyTimeZoneAsync(CancellationToken ct = default) =>
        CompanyZone.Of(await GetAsync(AppSettingKeys.CompanyTimeZone, ct));
}
