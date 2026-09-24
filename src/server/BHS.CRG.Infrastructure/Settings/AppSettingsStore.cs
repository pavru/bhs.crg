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

    public async Task<TimeZoneInfo> GetCompanyTimeZoneAsync(CancellationToken ct = default)
    {
        var stored = await GetAsync(AppSettingKeys.CompanyTimeZone, ct);
        // Сохранённый пояс может не разбираться на ДРУГОЙ машине: база переезжает между системами,
        // а список поясов у них разный. Тихо подставить серверный — значит посчитать сутки не по
        // тому поясу и не сказать об этом; поэтому возвращаем серверный, но это видно в настройках.
        if (stored is not null && TimeZoneInfo.TryFindSystemTimeZoneById(stored, out var zone)) return zone;
        return TimeZoneInfo.Local;
    }
}
