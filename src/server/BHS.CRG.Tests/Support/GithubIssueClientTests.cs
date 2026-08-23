using System.Net;
using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Support;

/// <summary>
/// Отправка сообщения об ошибке в GitHub (issue #834, часть 2).
///
/// Это единственное место системы, которое НАМЕРЕННО отправляет наружу текст, написанный внутри, —
/// и потому проверяется по двум осям: что уходит в запросе (ровно заголовок, тело и метка) и что
/// возвращается человеку при отказе (наши слова с названием следующего шага, а не тело ответа
/// GitHub, где бывают подсказки о правах токена).
///
/// Сеть подменена обработчиком: без него проверить разбор отказов было бы нечем — 401 и 422 от
/// живого GitHub на глаз не устроить.
/// </summary>
public class GithubIssueClientTests
{
    private sealed class StubHandler(
        HttpStatusCode status, string body, Action<HttpRequestMessage, string>? capture = null)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            capture?.Invoke(request, await (request.Content?.ReadAsStringAsync(ct) ?? Task.FromResult("")));
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubSettings(GithubSettings github) : IIntegrationSettings
    {
        public Task<IntegrationSettingsModel> GetEffectiveAsync(CancellationToken ct = default)
            => Task.FromResult(new IntegrationSettingsModel { Github = github });
        public Task SaveAsync(IntegrationSettingsModel u, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveSmtpAsync(SmtpSettings s, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveUpdatesAsync(UpdateCheckSettings u, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveBackupScheduleAsync(BackupScheduleSettings b, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveGithubAsync(GithubSettings g, CancellationToken ct = default) => Task.CompletedTask;
        public void Invalidate() { }
    }

    private static GithubIssueClient Client(
        HttpStatusCode status, string body, GithubSettings? github = null,
        Action<HttpRequestMessage, string>? capture = null)
        => new(new HttpClient(new StubHandler(status, body, capture)),
               new StubSettings(github ?? new GithubSettings { Token = "ghp_test", Repository = "pavru/bhs.crg" }),
               NullLogger<GithubIssueClient>.Instance);

    private const string Created = """{"number":842,"html_url":"https://github.com/pavru/bhs.crg/issues/842"}""";

    [Fact]
    public async Task Create_SendsExactlyTitleBodyAndLabel()
    {
        HttpRequestMessage? sent = null;
        string payload = "";
        var client = Client(HttpStatusCode.Created, Created,
            capture: (req, body) => { sent = req; payload = body; });

        var issue = await client.CreateAsync("Не сохраняется акт", "Тело, уже отредактированное администратором.");

        Assert.Equal(842, issue.Number);
        Assert.Equal("https://github.com/pavru/bhs.crg/issues/842", issue.Url);
        Assert.Equal("https://api.github.com/repos/pavru/bhs.crg/issues", sent!.RequestUri!.ToString());
        // User-Agent обязателен: без него GitHub отвечает 403, и выглядит это как отозванный токен.
        Assert.Contains("BHS.CRG", sent.Headers.UserAgent.ToString());
        Assert.Equal("Bearer", sent.Headers.Authorization!.Scheme);

        using var doc = JsonDocument.Parse(payload);
        Assert.Equal("Не сохраняется акт", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal("Тело, уже отредактированное администратором.", doc.RootElement.GetProperty("body").GetString());
        Assert.Equal(GithubIssueClient.Label, doc.RootElement.GetProperty("labels")[0].GetString());
        // Ничего сверх этих трёх полей наружу не уходит: ни автора, ни адреса установки, ни снимка.
        Assert.Equal(3, doc.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task Create_WithoutToken_RefusesAndNamesWhereToSetIt()
    {
        var client = Client(HttpStatusCode.Created, Created, new GithubSettings { Token = null });

        var ex = await Assert.ThrowsAsync<InvalidRequestException>(
            () => client.CreateAsync("Заголовок", "Тело"));
        Assert.Contains("настройках интеграций", ex.Message);
    }

    /// <summary>
    /// Отказ называет ПРИЧИНУ по коду: «токен не тот», «нет прав на этот репозиторий», «метки нет».
    /// Общее «не получилось» отправило бы администратора перебирать всё подряд.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "токен")]
    [InlineData(HttpStatusCode.Forbidden, "issues: write")]
    [InlineData(HttpStatusCode.NotFound, "не найден")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "метки")]
    public async Task Create_Refusal_NamesTheNextStep(HttpStatusCode status, string expected)
    {
        const string secretBody = """{"message":"Bad credentials","documentation_url":"https://внутренний.хост/подсказка"}""";
        var client = Client(status, secretBody);

        var ex = await Assert.ThrowsAsync<InvalidRequestException>(
            () => client.CreateAsync("Заголовок", "Тело"));

        Assert.Contains(expected, ex.Message);
        // Тело ответа GitHub наружу НЕ уходит: там бывают адреса и подсказки о правах токена.
        Assert.DoesNotContain("внутренний.хост", ex.Message);
        Assert.DoesNotContain("Bad credentials", ex.Message);
    }

    /// <summary>
    /// Ответ без разбираемого номера — отказ, а не тихий успех. Иначе issue оказался бы заведён, а
    /// в системе следа не осталось: повторная отправка завела бы второй о том же.
    /// </summary>
    [Theory]
    [InlineData("""{"html_url":"https://github.com/pavru/bhs.crg/issues/842"}""")]
    [InlineData("""{"number":"842"}""")]
    [InlineData("{}")]
    public async Task Create_WithoutUsableNumber_Refuses(string body)
    {
        var client = Client(HttpStatusCode.Created, body);

        var ex = await Assert.ThrowsAsync<InvalidRequestException>(
            () => client.CreateAsync("Заголовок", "Тело"));
        Assert.Contains("номер issue", ex.Message);
    }

    /// <summary>Адрес issue не пришёл — собираем сами: ссылка автору нужна в любом случае.</summary>
    [Fact]
    public async Task Create_WithoutHtmlUrl_BuildsLinkFromRepository()
    {
        var client = Client(HttpStatusCode.Created, """{"number":7}""");

        var issue = await client.CreateAsync("Заголовок", "Тело");

        Assert.Equal("https://github.com/pavru/bhs.crg/issues/7", issue.Url);
    }

    /// <summary>
    /// Токен с кириллицей, пробелом или невидимым знаком — отказ ПРО ТОКЕН, а не «GitHub
    /// недоступен». Найдено живой проверкой: заголовки HTTP не несут таких символов, .NET роняет
    /// запрос сетевым исключением, и администратор шёл чинить сеть вместо того, чтобы скопировать
    /// токен заново.
    /// </summary>
    [Theory]
    [InlineData("ghp_кириллица")]
    [InlineData("ghp_с пробелом")]
    [InlineData("ghp_\u00a0неразрывный")]
    public async Task Create_WithUnusableToken_SaysItIsAboutTheToken(string token)
    {
        var client = Client(HttpStatusCode.Created, Created, new GithubSettings { Token = token });

        var ex = await Assert.ThrowsAsync<InvalidRequestException>(
            () => client.CreateAsync("Заголовок", "Тело"));

        Assert.Contains("токен", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("недоступен", ex.Message);
    }

    [Fact]
    public async Task IsConfigured_FollowsTheToken_NotTheRepository()
    {
        Assert.False(await Client(HttpStatusCode.OK, "{}", new GithubSettings { Token = "   " }).IsConfiguredAsync());
        Assert.True(await Client(HttpStatusCode.OK, "{}", new GithubSettings { Token = "ghp_x" }).IsConfiguredAsync());
    }
}
