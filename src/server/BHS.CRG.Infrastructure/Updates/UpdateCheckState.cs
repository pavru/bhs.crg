using System.Text.Json;
using System.Text.Json.Serialization;
using BHS.CRG.Domain.Settings;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Updates;

/// <summary>След проверки обновлений — то, что записала служба (issue #813).</summary>
public class UpdateCheckState
{
    /// <summary>Последняя выпущенная версия, о которой мы знаем.</summary>
    public string? LatestVersion { get; set; }

    /// <summary>Версия, о которой уже уведомили. Хранится, потому что «вышла новая версия» — факт,
    /// а не текущее состояние: держи мы его в памяти, каждый перезапуск api повторял бы уведомление
    /// (а при обновлении перезапуск происходит по определению).</summary>
    public string? NotifiedVersion { get; set; }

    public string? ReleaseUrl { get; set; }
    public string? ReleaseNotes { get; set; }

    /// <summary>Когда проверка последний раз УДАЛАСЬ. Неудачные попытки сюда не пишутся: иначе
    /// «проверено час назад» означало бы «час назад не смогли».</summary>
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>До этого момента GitHub просил не спрашивать (исчерпан лимит) — не спрашиваем.</summary>
    public DateTimeOffset? RateLimitedUntil { get; set; }
}

/// <summary>Чтение и запись следа службы одной строкой в <c>service_state</c>.</summary>
public class ServiceStateStore(AppDbContext db)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<T> LoadAsync<T>(string key, CancellationToken ct) where T : new()
    {
        var row = await db.ServiceState.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) return new T();
        try { return row.Data.Deserialize<T>(Json) ?? new T(); }
        catch (JsonException) { return new T(); }   // запись испорчена — начинаем заново, а не падаем
    }

    public async Task SaveAsync<T>(string key, T value, CancellationToken ct)
    {
        var json = JsonDocument.Parse(JsonSerializer.Serialize(value, Json));
        var row = await db.ServiceState.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.ServiceState.Add(ServiceStateEntity.Create(key, json));
        else row.Update(json);
        await db.SaveChangesAsync(ct);
    }
}
