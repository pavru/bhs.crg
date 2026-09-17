using BHS.CRG.Domain.Notifications;
using BHS.CRG.Infrastructure.Notifications;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// След вытеснения уведомлений в журнале (issue #919): поток мониторинга вытеснил две недели истории
/// без следа, и обрезанную таблицу приняли за начало сбоев. Возраст берётся из записей в базе —
/// поэтому корзины здесь засеяны с нужными датами, а не накоплены ожиданием.
/// </summary>
[Collection("Integration")]
public class NotificationEvictionTraceTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const int MaxKept = 300;

    private sealed class CapturingLogger : ILogger<NotificationService>
    {
        public readonly List<(LogLevel Level, string Text)> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
        public List<string> Warnings => Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Text).ToList();
    }

    // Пользователь на каждый тест — своя корзина: ограничение частоты помнит корзину весь процесс.
    private async Task<Guid> SeedFullBucketAsync(TimeSpan age)
    {
        using var scope = fixture.Services.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"{Guid.NewGuid():N}@test.local";
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Тест" };
        Assert.True((await um.CreateAsync(user, "Passw0rd!")).Succeeded);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        for (var i = 0; i < MaxKept; i++)
            db.Notifications.Add(Notification.Create(NotificationSeverity.Info, $"Старое {i}", "текст", "Тест", user.Id));
        await db.SaveChangesAsync();

        var at = DateTimeOffset.UtcNow - age;
        await db.Notifications.Where(n => n.UserId == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.CreatedAt, at));
        return user.Id;
    }

    private async Task PublishAsync(Guid userId, CapturingLogger logger, int count)
    {
        using var scope = fixture.Services.CreateScope();
        var svc = new NotificationService(scope.ServiceProvider.GetRequiredService<AppDbContext>(), logger);
        for (var i = 0; i < count; i++)
            await svc.PublishAsync(NotificationSeverity.Info, $"Новое {i}", "текст", "Тест", userId);
    }

    [Fact]
    public async Task Ротация_старой_истории_молчит()
    {
        var user = await SeedFullBucketAsync(TimeSpan.FromDays(365));
        var logger = new CapturingLogger();

        await PublishAsync(user, logger, 1);

        Assert.Empty(logger.Warnings);
        using var scope = fixture.Services.CreateScope();
        Assert.Equal(MaxKept, await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Notifications.CountAsync(n => n.UserId == user));
    }

    [Fact]
    public async Task Поток_предупреждает_один_раз_и_называет_настоящий_горизонт()
    {
        var user = await SeedFullBucketAsync(TimeSpan.FromHours(1));
        var logger = new CapturingLogger();

        // Каждая публикация в полной корзине вытесняет одну запись: строка на каждую — удвоенный шум.
        await PublishAsync(user, logger, 20);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("вытесняются потоком", warning);

        // Горизонт в строке — первого предупреждения; после него вытеснялись записи той же даты
        // засева, поэтому он совпадает с самой ранней записью, оставшейся в таблице сейчас.
        using var scope = fixture.Services.CreateScope();
        var earliest = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Notifications.Where(n => n.UserId == user).MinAsync(n => n.CreatedAt);
        Assert.Contains(earliest.ToString("u"), warning);
    }
}
