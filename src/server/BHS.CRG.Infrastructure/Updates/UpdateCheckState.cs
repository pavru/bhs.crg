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

    /// <summary>
    /// Записать след службы. Писателей ДВА — фоновый цикл и кнопка «Проверить сейчас», — и на свежей
    /// установке они оба могут увидеть «строки ещё нет» и оба попытаться её создать. Уникальный
    /// индекс по ключу тогда отдаёт 23505, и проигравший получал бы 500 вместо результата проверки.
    /// Поэтому конфликт вставки не ошибка, а ожидаемый исход: перечитываем и обновляем существующую.
    /// </summary>
    public async Task SaveAsync<T>(string key, T value, CancellationToken ct)
    {
        var json = JsonDocument.Parse(JsonSerializer.Serialize(value, Json));
        var row = await db.ServiceState.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is not null)
        {
            row.Update(json);
            await db.SaveChangesAsync(ct);
            return;
        }

        db.ServiceState.Add(ServiceStateEntity.Create(key, json));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Нас опередили. Отцепляем свою вставку и дописываем в чужую строку — состояние службы
            // одно на систему, и «кто последний, того и запись» здесь верное поведение. Если строки
            // всё же нет, дело было не в гонке: пробрасываем, молчать нельзя.
            db.ChangeTracker.Clear();
            var existing = await db.ServiceState.FirstOrDefaultAsync(s => s.Key == key, ct);
            if (existing is null) throw;
            existing.Update(json);
            await db.SaveChangesAsync(ct);
        }
    }
}
