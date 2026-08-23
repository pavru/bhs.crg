using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Settings;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Support;

/// <summary>Что получилось: номер и адрес заведённого issue.</summary>
public record CreatedIssue(int Number, string Url);

/// <summary>
/// Создание issue в GitHub из сообщения об ошибке (issue #834, часть 2).
///
/// Единственное место в системе, которое НАМЕРЕННО отправляет наружу текст, написанный внутри.
/// Поэтому отсюда уходит ровно то, что администратор увидел и отредактировал: заголовок и тело.
/// Ни снимка экрана (через API его к issue и не приложить), ни имени автора, ни адреса установки.
///
/// Отдельным классом от <c>BugReportService</c> ради проверяемости: подменив HTTP-обработчик, можно
/// прогнать разбор ответов и отказы, не выходя в сеть.
/// </summary>
public class GithubIssueClient(
    HttpClient http,
    IIntegrationSettings settings,
    ILogger<GithubIssueClient> logger)
{
    /// <summary>Метка, под которой сообщения приходят в трекер. Заводится в репозитории заранее.</summary>
    public const string Label = "bug";

    /// <summary>Срок запроса. Кнопку жмёт человек и ждёт ответа — минуты здесь неуместны.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Настроен ли перенос: токен есть. Репозиторий имеет умолчание, токен — нет.</summary>
    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
        => !string.IsNullOrWhiteSpace((await settings.GetEffectiveAsync(ct)).Github.Token);

    public async Task<CreatedIssue> CreateAsync(string title, string body, CancellationToken ct = default)
    {
        var github = (await settings.GetEffectiveAsync(ct)).Github;
        if (string.IsNullOrWhiteSpace(github.Token))
            throw new InvalidRequestException(
                "Токен GitHub не задан. Укажите его в настройках интеграций — раздел «Передача в GitHub».");

        // Вторая проверка того же: токен мог быть сохранён прежней версией, до отказа при вводе.
        // Без неё запрос падает сетевым исключением и отвечает «GitHub недоступен».
        if (!GithubSettings.IsTokenUsable(github.Token))
            throw new InvalidRequestException(
                "Сохранённый токен непригоден: в нём есть символы, недопустимые в заголовке HTTP. " +
                "Задайте его заново в настройках интеграций.");

        var repository = string.IsNullOrWhiteSpace(github.Repository)
            ? GithubSettings.DefaultRepository
            : github.Repository.Trim().Trim('/');

        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"https://api.github.com/repos/{repository}/issues");
        // User-Agent обязателен: без него GitHub отвечает 403, и выглядит это как отозванный токен —
        // причину искали бы не там (тот же урок, что в проверке обновлений, issue #813).
        req.Headers.UserAgent.ParseAdd("BHS.CRG-bug-reports");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", github.Token);
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        req.Content = JsonContent.Create(new { title, body, labels = new[] { Label } });

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Сообщение сетевого слоя называет прокси, адреса и таймауты — наружу не идёт.
            logger.LogWarning(ex, "Не удалось достучаться до GitHub при создании issue");
            throw new InvalidRequestException(
                "GitHub недоступен: запрос не дошёл. Проверьте сеть и попробуйте ещё раз — " +
                "сообщение осталось в системе.");
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode) throw Refusal(resp, repository);

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            // Номер обязателен: без него сообщение осталось бы без следа, а issue — заведённым.
            // Молчаливое «отправлено» с пустым номером хуже отказа: повторная отправка завела бы
            // второй issue о том же.
            if (!doc.RootElement.TryGetProperty("number", out var numberEl)
                || numberEl.ValueKind != JsonValueKind.Number
                || !numberEl.TryGetInt32(out var number))
            {
                logger.LogError("Ответ GitHub на создание issue без разбираемого number: {Body}", Head(json));
                throw new InvalidRequestException(
                    "GitHub ответил не так, как ожидалось: номер issue не разобран. " +
                    "Проверьте репозиторий — возможно, issue всё же заведён.");
            }

            var url = doc.RootElement.TryGetProperty("html_url", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString()!
                : $"https://github.com/{repository}/issues/{number}";
            return new CreatedIssue(number, url);
        }
    }

    /// <summary>
    /// Отказ GitHub — словами, обращёнными к администратору, и с названием следующего шага.
    ///
    /// Тело ответа наружу не отдаём: там бывают и адреса внутренних служб, и подсказки о правах
    /// токена. Но КОД различаем: «токен не тот» и «метки нет в репозитории» лечатся по-разному, и
    /// общее «не получилось» отправило бы администратора перебирать всё подряд.
    /// </summary>
    private InvalidRequestException Refusal(HttpResponseMessage resp, string repository)
    {
        logger.LogWarning("GitHub отказал в создании issue: {Status} для {Repository}",
            (int)resp.StatusCode, repository);

        return new InvalidRequestException(resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                "GitHub не принял токен (401). Похоже, он отозван или введён с ошибкой — " +
                "задайте его заново в настройках интеграций.",
            HttpStatusCode.Forbidden =>
                $"GitHub отказал в доступе (403) к «{repository}». Проверьте, что у токена есть " +
                "право issues: write именно на этот репозиторий.",
            HttpStatusCode.NotFound =>
                $"Репозиторий «{repository}» не найден (404). Так же отвечает GitHub, когда " +
                "репозиторий существует, но токен его не видит, — проверьте и адрес, и права токена.",
            HttpStatusCode.Gone =>
                $"В репозитории «{repository}» отключены issue (410). Передавать некуда.",
            HttpStatusCode.UnprocessableEntity =>
                "GitHub отклонил содержимое issue (422). Обычная причина — метки «" + Label +
                "» нет в репозитории; заведите её или уберите из кода.",
            _ => $"GitHub ответил кодом {(int)resp.StatusCode}. Сообщение осталось в системе — " +
                 "попробуйте позже.",
        });
    }

    private static string Head(string text) => text.Length <= 500 ? text : text[..500] + "…";
}
