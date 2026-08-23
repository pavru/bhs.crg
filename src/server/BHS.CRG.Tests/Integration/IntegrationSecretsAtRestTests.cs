using BHS.CRG.Application.Common;
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
    // Сброс фикстуры чистит integration_settings и сбрасывает кеш настроек (issue #698), так что
    // перед прогоном руками делать нечего. А вот после себя убираем: не всякий класс начинается со
    // сброса, и соседям не должна достаться настроенная почта, которой они не ждут.
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();

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
    // Только ASCII: токен уезжает в заголовок HTTP, и негодный отвергается при сохранении.
    // «Не должен лежать открыто» здесь пишем латиницей — суть проверки от этого не меняется.
    private const string GithubSecret = "ghp_7c1e_MUST_NOT_BE_STORED_IN_PLAIN_TEXT";

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
    /// Смена почтового сервера обнуляет сохранённый пароль.
    ///
    /// Иначе форма настроек сама была бы способом его выгрузить: сохранить чужой хост с пустым
    /// полем пароля — и первое же письмо уйдёт туда с аутентификацией сохранённым паролем. Запрет
    /// подстановки в проверке связи этого не закрывает: проверка связи там не нужна.
    /// </summary>
    [Theory]
    [InlineData("smtp.attacker.test", 587, "u", true)]    // другой хост
    [InlineData("smtp.example.test", 2525, "u", true)]    // другой порт
    [InlineData("smtp.example.test", 587, "другой", true)] // другой пользователь
    [InlineData("smtp.example.test", 587, "u", false)]     // снято шифрование: пароль ушёл бы открытым
    public async Task ChangingMailServer_DropsSavedPassword(string host, int port, string user, bool useSsl)
    {
        using var scope = fixture.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IIntegrationSettings>();
        await settings.SaveSmtpAsync(new SmtpSettings
        {
            Enabled = true, Host = "smtp.example.test", Port = 587, User = "u", UseSsl = true, Password = SmtpSecret,
        });

        // Пароль в форме пустой — «оставить прежний». Прежний остаётся только на прежнем сервере.
        await settings.SaveSmtpAsync(new SmtpSettings
        {
            Enabled = true, Host = host, Port = port, User = user, UseSsl = useSsl, Password = null,
        });

        var eff = await settings.GetEffectiveAsync();
        Assert.True(string.IsNullOrEmpty(eff.Smtp.Password));
    }

    [Fact]
    public async Task SameMailServer_KeepsSavedPassword()
    {
        using var scope = fixture.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IIntegrationSettings>();
        var server = new SmtpSettings { Enabled = true, Host = "smtp.example.test", Port = 587, User = "u", UseSsl = true };

        await settings.SaveSmtpAsync(new SmtpSettings
        {
            Enabled = true, Host = server.Host, Port = server.Port, User = server.User, UseSsl = true, Password = SmtpSecret,
        });
        // Правка чего-то постороннего на том же сервере пароль не роняет — иначе почта отваливалась
        // бы от смены отображаемого имени отправителя.
        await settings.SaveSmtpAsync(new SmtpSettings
        {
            Enabled = true, Host = server.Host, Port = server.Port, User = server.User, UseSsl = true,
            FromName = "Исполнительная документация", Password = null,
        });

        var eff = await settings.GetEffectiveAsync();
        Assert.Equal(SmtpSecret, eff.Smtp.Password);
    }

    /// <summary>
    /// Смена репозитория обнуляет сохранённый токен GitHub — тот же урок, что с почтой (issue #834).
    ///
    /// Иначе форма настроек сама была бы способом опубликовать внутренний текст в чужом месте:
    /// указать чужой репозиторий с пустым полем токена — и первое же «Отправить в GitHub» уйдёт
    /// туда с нашим правом записи.
    /// </summary>
    [Fact]
    public async Task ChangingRepository_DropsSavedGithubToken()
    {
        using var scope = fixture.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IIntegrationSettings>();
        await settings.SaveGithubAsync(new GithubSettings { Repository = "pavru/bhs.crg", Token = GithubSecret });

        // Токен в форме пустой — «оставить прежний». Прежний остаётся только для прежнего места.
        await settings.SaveGithubAsync(new GithubSettings { Repository = "чужой/репозиторий", Token = null });

        var eff = await settings.GetEffectiveAsync();
        Assert.True(string.IsNullOrEmpty(eff.Github.Token));
    }

    [Fact]
    public async Task SameRepository_KeepsSavedGithubToken_AndStoresItEncrypted()
    {
        using var scope = fixture.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IIntegrationSettings>();
        await settings.SaveGithubAsync(new GithubSettings { Repository = "pavru/bhs.crg", Token = GithubSecret });
        // Правка чего-то постороннего в том же месте токен не роняет.
        await settings.SaveGithubAsync(new GithubSettings { Repository = "pavru/bhs.crg", Token = null });

        Assert.DoesNotContain(GithubSecret, await StoredJsonAsync(scope));
        Assert.Equal(GithubSecret, (await settings.GetEffectiveAsync()).Github.Token);
    }

    /// <summary>
    /// Негодный токен отвергается ПРИ ВВОДЕ, а не при отправке. Иначе отказ приходит не собой:
    /// запрос падает сетевым исключением, и человек читает «GitHub недоступен, проверьте сеть».
    /// </summary>
    [Fact]
    public async Task SavingUnusableGithubToken_IsRefused()
    {
        using var scope = fixture.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IIntegrationSettings>();

        var ex = await Assert.ThrowsAsync<InvalidRequestException>(() => settings.SaveGithubAsync(
            new GithubSettings { Repository = "pavru/bhs.crg", Token = "ghp_кириллица" }));
        Assert.Contains("Скопируйте токен заново", ex.Message);
    }

    /// <summary>
    /// Ключи Data Protection временно недоступны (том не примонтирован, путь переехал) — сохранение
    /// посторонней настройки НЕ должно затирать шифротекст. Иначе однократная ошибка развёртывания
    /// превращалась бы в безвозвратную потерю всех секретов: наружу они читаются как «не заданы», и
    /// первое же сохранение записало бы эту пустоту поверх целых значений.
    /// </summary>
    [Fact]
    public async Task SaveDoesNotWipeSecretsWhenTheyCannotBeDecrypted()
    {
        using (var scope = fixture.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IIntegrationSettings>();
            await settings.SaveAsync(new IntegrationSettingsModel
            {
                Recognition = { ["Anthropic"] = new IntegrationEngine { Enabled = true, ApiKey = ApiKeySecret } },
            });
        }

        // Подменяем шифротекст на нерасшифровываемый — это и есть «ключи сменились».
        string broken;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.IntegrationSettings.FirstAsync();
            broken = row.Data.RootElement.GetRawText()
                .Replace("enc:v1:", "enc:v1:ZZZ", StringComparison.Ordinal);
            row.Update(System.Text.Json.JsonDocument.Parse(broken));
            await db.SaveChangesAsync();
            scope.ServiceProvider.GetRequiredService<IIntegrationSettings>().Invalidate();
        }

        using (var scope = fixture.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IIntegrationSettings>();
            // Наружу — «ключ не задан»: с мусором в сеть не ходим.
            Assert.True(string.IsNullOrEmpty((await settings.GetEffectiveAsync()).Recognition["Anthropic"].ApiKey));

            // Сохраняем ПОСТОРОННЮЮ настройку, ключа не касаясь.
            await settings.SaveAsync(new IntegrationSettingsModel { FgisDomains = ["fsa.gov.ru"] });
        }

        using (var scope = fixture.Services.CreateScope())
        {
            // Шифротекст на месте — вернув ключи, значения снова прочитают.
            Assert.Contains("enc:v1:ZZZ", await StoredJsonAsync(scope));
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
