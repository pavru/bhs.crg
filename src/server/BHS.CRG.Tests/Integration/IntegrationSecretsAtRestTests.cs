using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Секреты интеграций (ключи движков, пароль SMTP) не должны лежать в БД открытым текстом: API их
/// и раньше маскировал при чтении, но дамп, реплика или копия базы отдавали значения как есть.
///
/// Проверяем именно СТРОКУ в базе, а не то, что вернул сервис: сервис отдаёт расшифрованное по
/// замыслу, и тест на нём прошёл бы и без всякого шифрования.
/// </summary>
[Collection("Integration")]
public class IntegrationSecretsAtRestTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    // integration_settings НЕ входит в список TRUNCATE у фикстуры (таблица одна на всю установку,
    // и её никто не чистил). Чистим сами — и после себя тоже, чтобы соседним классам не досталась
    // настроенная почта, которой они не ждут.
    public async Task InitializeAsync()
    {
        await fixture.ResetDatabaseAsync();
        await ClearSettingsAsync();
    }

    public async Task DisposeAsync() => await ClearSettingsAsync();

    private async Task ClearSettingsAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.IntegrationSettings.ExecuteDeleteAsync();
        // Действующие настройки кешируются в памяти — иначе следующий тест увидит прежние.
        scope.ServiceProvider.GetRequiredService<IIntegrationSettings>().Invalidate();
    }

    private const string ApiKeySecret = "sk-test-3f8a1c-НЕ-ДОЛЖЕН-ЛЕЖАТЬ-ОТКРЫТО";
    private const string SmtpSecret = "smtp-pass-9b2d-НЕ-ДОЛЖЕН-ЛЕЖАТЬ-ОТКРЫТО";

    private static async Task<string> StoredJsonAsync(IServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.IntegrationSettings.AsNoTracking().FirstAsync();
        return row.Data.RootElement.GetRawText();
    }

    [Fact]
    public async Task SavedSecrets_AreNotStoredInPlainText_AndComeBackDecrypted()
    {
        using (var scope = fixture.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IIntegrationSettings>();
            await settings.SaveAsync(new IntegrationSettingsModel
            {
                Recognition = { ["Anthropic"] = new IntegrationEngine { Enabled = true, ApiKey = ApiKeySecret } },
            });
            await settings.SaveSmtpAsync(new SmtpSettings { Enabled = true, Host = "smtp.example.test", User = "u", Password = SmtpSecret });
        }

        using (var scope = fixture.Services.CreateScope())
        {
            var json = await StoredJsonAsync(scope);
            Assert.DoesNotContain(ApiKeySecret, json);
            Assert.DoesNotContain(SmtpSecret, json);

            // Внутри приложения значения по-прежнему доступны — иначе распознавание и почта встанут.
            var eff = await scope.ServiceProvider.GetRequiredService<IIntegrationSettings>().GetEffectiveAsync();
            Assert.Equal(ApiKeySecret, eff.Recognition["Anthropic"].ApiKey);
            Assert.Equal(SmtpSecret, eff.Smtp.Password);
        }
    }

    /// <summary>
    /// Наследство: значения, записанные версиями до 0.92.0, лежат открытыми. Стартовый проход
    /// перешифровывает их, и приложение продолжает их понимать.
    /// </summary>
    [Fact]
    public async Task PlainSecretsFromOlderVersions_AreEncryptedOnStartupPass()
    {
        // Кладём открытые значения в обход сервиса — ровно так они и лежат у работающей установки.
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var json = System.Text.Json.JsonDocument.Parse(
                "{\"Recognition\":{\"Gemini\":{\"Enabled\":true,\"ApiKey\":\"" + ApiKeySecret + "\"}}," +
                "\"Smtp\":{\"Enabled\":true,\"Host\":\"smtp.example.test\",\"User\":\"u\",\"Password\":\"" + SmtpSecret + "\"}}");
            await db.IntegrationSettings.AddAsync(BHS.CRG.Domain.Settings.IntegrationSettingsEntity.Create(json));
            await db.SaveChangesAsync();
        }

        using (var scope = fixture.Services.CreateScope())
        {
            Assert.Contains(ApiKeySecret, await StoredJsonAsync(scope));   // до прохода — открыто

            var migrated = await scope.ServiceProvider
                .GetRequiredService<IntegrationSettingsService>().ProtectStoredSecretsAsync();
            Assert.Equal(2, migrated);
        }

        using (var scope = fixture.Services.CreateScope())
        {
            var json = await StoredJsonAsync(scope);
            Assert.DoesNotContain(ApiKeySecret, json);
            Assert.DoesNotContain(SmtpSecret, json);

            var eff = await scope.ServiceProvider.GetRequiredService<IIntegrationSettings>().GetEffectiveAsync();
            Assert.Equal(ApiKeySecret, eff.Recognition["Gemini"].ApiKey);
            Assert.Equal(SmtpSecret, eff.Smtp.Password);

            // Повторный проход работы не находит — иначе он перешифровывал бы при каждом старте.
            var again = await scope.ServiceProvider
                .GetRequiredService<IntegrationSettingsService>().ProtectStoredSecretsAsync();
            Assert.Equal(0, again);
        }
    }
}
