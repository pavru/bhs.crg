using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Persistence;

/// <summary>
/// Выпуск/ротация/ревокация refresh-токенов (issue #148 follow-up). Сырой токен возвращается
/// клиенту один раз; в БД лежит только SHA-256 хэш. Ротация: при обмене старый ревокается,
/// выдаётся новый. Срок жизни — <see cref="Lifetime"/>.
/// </summary>
public class RefreshTokenService(AppDbContext db)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    public async Task<string> IssueAsync(Guid userId, CancellationToken ct = default)
    {
        var raw = GenerateRaw();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = Hash(raw),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(Lifetime),
        });
        await db.SaveChangesAsync(ct);
        return raw;
    }

    /// <summary>Проверяет и ротирует токен. Возвращает (userId, новый сырой токен) или null, если
    /// токен неизвестен / отозван / истёк.</summary>
    public async Task<(Guid UserId, string NewToken)?> RotateAsync(string rawToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;
        var hash = Hash(rawToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null || !token.IsActive) return null;

        token.RevokedAt = DateTime.UtcNow;
        var raw = GenerateRaw();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = token.UserId,
            TokenHash = Hash(raw),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(Lifetime),
        });
        await db.SaveChangesAsync(ct);
        return (token.UserId, raw);
    }

    /// <summary>Отзывает конкретный токен (logout). Молча игнорирует неизвестный.</summary>
    public async Task RevokeAsync(string rawToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return;
        var hash = Hash(rawToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.RevokedAt == null, ct);
        if (token is null) return;
        token.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Отзывает все активные токены пользователя (смена/сброс пароля, «выйти со всех устройств»).</summary>
    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    private static string GenerateRaw()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
}
