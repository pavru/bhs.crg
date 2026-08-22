using System.Net;
using System.Text.Json;
using BHS.CRG.Application.Settings;
using BHS.CRG.Application.Updates;
using BHS.CRG.Infrastructure.Updates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Api.Updates;

/// <summary>
/// Периодически спрашивает у GitHub последний выпуск и сообщает администраторам, что вышла новая
/// версия (issue #813).
///
/// Это первая функция системы, которая ходит наружу без действия человека, поэтому граница проведена
/// явно: наружу уходит ЗАПРОС и ничего больше — ни адреса установки в теле, ни телеметрии, ни
/// содержимого. Выключенная настройка означает «не ходить», а не «сходить и промолчать».
///
/// От <c>HealthMonitorService</c> отличается тем, что здесь состояние не текущее, а исторический
/// ФАКТ: «версия 0.138.0 вышла» не перестаёт быть правдой оттого, что сеть моргнула или api
/// перезапустили. Поэтому «о чём уже уведомили» лежит в базе, а не в памяти процесса, и
/// недоступность GitHub ничего не гасит.
/// </summary>
public class UpdateCheckService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpFactory,
    ILogger<UpdateCheckService> logger
) : BackgroundService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/pavru/bhs.crg/releases/latest";
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);

    /// <summary>Сколько неудач подряд уже случилось — чтобы не писать в журнал одно и то же каждые
    /// шесть часов. Предупреждение, повторяющееся вечно, перестают замечать вместе со всем журналом.</summary>
    private int _consecutiveFailures;

    /// <summary>Текст последней неудачи — для ответа на явную проверку. В журнал он уходит по своим
    /// правилам (первая неудача подряд предупреждением, дальше отладкой), а человеку у кнопки нужен
    /// всегда: без него нажатие выглядит удавшимся.</summary>
    private string? _lastError;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Пауза на старте — из тех же соображений, что у health-мониторинга: при запуске системе
        // есть чем заняться, а обновление не срочно.
        try { await Task.Delay(StartDelay, ct); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await CheckAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Сбой цикла проверки обновлений"); }
        }
        while (await SafeWait(timer, ct));
    }

    private static async Task<bool> SafeWait(PeriodicTimer t, CancellationToken ct)
    {
        try { return await t.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>Одна проверка. Публичная — её же выполняет кнопка «Проверить сейчас».</summary>
    public async Task<UpdateStatus> CheckAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<ServiceStateStore>();
        var settings = await sp.GetRequiredService<IIntegrationSettings>().GetEffectiveAsync(ct);
        var state = await store.LoadAsync<UpdateCheckState>(UpdateCheckStateKeys.UpdateCheck, ct);
        var installed = AppVersion.SplitInformational(AppVersion.InformationalOfEntryAssembly()).Version;

        if (!settings.Updates.Enabled)
            return StatusOf(state, installed, enabled: false);

        // GitHub попросил подождать — ждём: долбить в исчерпанный лимит по расписанию бессмысленно.
        if (state.RateLimitedUntil is { } until && DateTimeOffset.UtcNow < until)
            return StatusOf(state, installed, enabled: true) with
            {
                JustChecked = false,
                LastError = $"GitHub ограничил частоту запросов, следующая попытка после {until.ToLocalTime():HH:mm}.",
            };

        var release = await FetchLatestAsync(state, ct);
        if (release is null)
        {
            // Неудача НИЧЕГО не гасит: уже известная новая версия остаётся известной. Но и выдать её
            // за свежий ответ нельзя — иначе кнопка «Проверить сейчас» отвечает успехом на неудачу.
            await store.SaveAsync(UpdateCheckStateKeys.UpdateCheck, state, ct);
            return StatusOf(state, installed, enabled: true) with
            {
                JustChecked = false,
                LastError = _lastError ?? "Не удалось получить сведения о выпусках.",
            };
        }

        state.LatestVersion = release.Value.Tag;
        state.ReleaseUrl = release.Value.HtmlUrl;
        state.ReleaseNotes = release.Value.Body;
        state.LastCheckedAt = DateTimeOffset.UtcNow;

        if (UpdateNotifier.ShouldNotify(state.LatestVersion, installed, state.NotifiedVersion))
        {
            await sp.GetRequiredService<UpdateNotifier>().NotifyAsync(state.LatestVersion!, installed, ct);
            state.NotifiedVersion = state.LatestVersion;
        }
        else if (!AppVersion.IsNewer(state.LatestVersion, installed) && state.NotifiedVersion is not null)
        {
            // Обновились — сообщение о том, что «доступна версия», стало неправдой. Убираем его и
            // забываем, о чём уведомляли: следующий выпуск начнёт разговор заново.
            await sp.GetRequiredService<UpdateNotifier>().ClearAsync(ct);
            state.NotifiedVersion = null;
        }

        await store.SaveAsync(UpdateCheckStateKeys.UpdateCheck, state, ct);
        return StatusOf(state, installed, enabled: true) with { JustChecked = true };
    }

    private static UpdateStatus StatusOf(UpdateCheckState s, string installed, bool enabled) => new(
        installed,
        AppVersion.Normalize(s.LatestVersion),
        AppVersion.IsNewer(s.LatestVersion, installed),
        s.ReleaseUrl,
        s.ReleaseNotes,
        s.LastCheckedAt,
        enabled);

    private async Task<(string Tag, string? HtmlUrl, string? Body)?> FetchLatestAsync(
        UpdateCheckState state, CancellationToken ct)
    {
        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(20);
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        // User-Agent обязателен: без него GitHub отвечает 403, и выглядит это в точности как
        // исчерпанный лимит — причину искали бы не там.
        req.Headers.UserAgent.ParseAdd("BHS.CRG-update-check");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");

        try
        {
            using var resp = await http.SendAsync(req, ct);
            // Именно ИСЧЕРПАННЫЙ лимит, а не любой 403: заголовок X-RateLimit-Reset приходит почти
            // с каждым ответом GitHub, и по одному его наличию мы объявляли бы лимитом и запрет по
            // User-Agent, и вторичное ограничение — вплоть до часа тишины и неверной подсказки в
            // журнале, то есть ровно того, от чего предостерегает комментарий выше.
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                && RateLimitExhausted(resp) && RateLimitReset(resp) is { } reset)
            {
                state.RateLimitedUntil = reset;
                Fail("лимит запросов GitHub исчерпан, следующая попытка после {Reset}", reset);
                return null;
            }
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            // tag_name, а не name: имя выпуска человек правит свободно, тег производит workflow.
            if (!doc.RootElement.TryGetProperty("tag_name", out var tagEl) || tagEl.GetString() is not { } tag)
            {
                Fail("ответ GitHub без tag_name", null);
                return null;
            }
            if (!AppVersion.TryParse(tag, out _))
            {
                // Отказ разбора обязан быть ВИДЕН: иначе служба тихо перестанет работать.
                Fail("тег выпуска {Tag} не разобран как версия", tag);
                return null;
            }

            _consecutiveFailures = 0;
            _lastError = null;
            state.RateLimitedUntil = null;
            return (tag,
                doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null,
                doc.RootElement.TryGetProperty("body", out var b) ? b.GetString() : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Fail("не удалось получить сведения о выпусках — {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>Первую неудачу подряд — предупреждением, дальше — отладкой. Предупреждение, которое
    /// повторяется каждые шесть часов месяцами, читать перестают.</summary>
    private void Fail(string template, object? arg)
    {
        _consecutiveFailures++;
        _lastError = arg is null ? template : template.Replace("{Reset}", "{0}")
            .Replace("{Tag}", "{0}").Replace("{Message}", "{0}")
            .Replace("{0}", arg.ToString() ?? "");
        if (_consecutiveFailures == 1)
            logger.LogWarning("Проверка обновлений: " + template, arg!);
        else
            logger.LogDebug("Проверка обновлений (неудача {N} подряд): " + template, _consecutiveFailures, arg!);
    }

    private static bool RateLimitExhausted(HttpResponseMessage resp)
        => resp.Headers.TryGetValues("X-RateLimit-Remaining", out var vals)
           && int.TryParse(vals.FirstOrDefault(), out var left)
           && left <= 0;

    private static DateTimeOffset? RateLimitReset(HttpResponseMessage resp)
        => resp.Headers.TryGetValues("X-RateLimit-Reset", out var vals)
           && long.TryParse(vals.FirstOrDefault(), out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : null;

}
