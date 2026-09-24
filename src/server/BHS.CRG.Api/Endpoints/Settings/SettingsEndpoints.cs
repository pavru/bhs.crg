using BHS.CRG.Modules;
using BHS.CRG.Api.Auth;
using System.Text.Json;
using BHS.CRG.Application.Email;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Http;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Endpoints.Settings;

public static class SettingsEndpoints
{
    // Курируемые списки моделей для облачных движков: их каталог не перечисляем по сети — там лежат
    // и эмбеддинги, и синтез речи, а выбирать надо из vision-моделей.
    //
    // У курирования есть цена, и она уже была заплачена (issue #799): к августу 2026 из четырёх
    // предлагавшихся моделей Gemini три отвечали «нет такой», а работавшая в списке отсутствовала, —
    // пользователь не мог выбрать ни одной пригодной, и выяснилось это только разбором. Поэтому
    // рядом живёт проверка выбранной модели (IRecognitionModelCatalog): список всё так же может
    // протухнуть, но молча — уже нет.
    //
    // Список Gemini сверен пробой 2026-08-20; список Anthropic сверить НЕ удалось: на ключе нет
    // средств, и любое имя модели, включая заведомо несуществующее, получает 400 раньше проверки.
    private static readonly string[] AnthropicModels =
        ["claude-opus-4-8", "claude-sonnet-4-6", "claude-haiku-4-5-20251001"];
    private static readonly string[] GeminiModels =
        ["gemini-3.1-pro-preview", "gemini-3.6-flash", "gemini-3.5-flash", "gemini-3.5-flash-lite", "gemini-2.5-flash"];

    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Настройки компании (ТЗ CORE-25.3, issue #960) ──────────────────────
        // Отдельной группой, а не полем в настройках интеграций: там секреты, и они шифруются.
        // Часовой пояс — открытое значение, которое читает код подсчёта суток.
        var company = app.MapGroup("/api/settings/company")
            .RequireAuthorization(AppPolicies.Permission(CorePermissions.SystemManage));

        company.MapGet("/", async (IAppSettingsStore store, CancellationToken ct) =>
        {
            var stored = await store.GetAsync(AppSettingKeys.CompanyTimeZone, ct);
            var effective = await store.GetCompanyTimeZoneAsync(ct);
            // Три значения, а не одно, и это не избыточность: «что выбрали», «что действует» и «что
            // на сервере». Совпадать они перестают ровно тогда, когда базу перенесли на машину, где
            // выбранного пояса нет, — и администратор обязан увидеть это здесь, а не по сдвинутым
            // датам в документах.
            return Results.Ok(new
            {
                timeZoneId = stored,
                effectiveTimeZoneId = effective.Id,
                serverTimeZoneId = TimeZoneInfo.Local.Id,
                resolved = stored is null || string.Equals(stored, effective.Id, StringComparison.Ordinal),
            });
        });

        company.MapPut("/", async (CompanyTimeZoneRequest req, IAppSettingsStore store, CancellationToken ct) =>
        {
            // Пусто — снять настройку, вернуться к поясу сервера. Непустое обязано разбираться.
            if (!string.IsNullOrWhiteSpace(req.TimeZoneId) && !AppSettingKeys.IsKnownTimeZone(req.TimeZoneId))
                return Results.BadRequest(new { error = $"Часовой пояс «{req.TimeZoneId}» этой системе неизвестен." });

            await store.SetAsync(AppSettingKeys.CompanyTimeZone,
                string.IsNullOrWhiteSpace(req.TimeZoneId) ? null : req.TimeZoneId.Trim(), ct);
            return Results.NoContent();
        });

        var g = app.MapGroup("/api/settings/integrations").RequireAuthorization(AppPolicies.Permission(CorePermissions.SystemManage));

        // Чтение: ключи НЕ возвращаем, только признак «ключ задан». Сюда НЕ добавляем проверок,
        // ходящих в сеть: этот запрос рисует страницу настроек, и секунда ожидания поставщика — это
        // секунда пустого экрана. Что известно про модели, отдаёт /models, отдельным запросом.
        g.MapGet("/", async (IIntegrationSettings settings, OutboundProxyState proxyState, CancellationToken ct) =>
        {
            var m = await settings.GetEffectiveAsync(ct);
            return Results.Ok(new
            {
                recognitionOrder = m.RecognitionOrder,
                // «Чего не хватает» считает сервер (EngineReadiness) — тем же правилом, по которому
                // цепочка распознавания и веб-поиск решают, брать движок в работу (issue #797).
                recognition = m.Recognition.ToDictionary(kv => kv.Key,
                    kv => Mask(kv.Value, EngineReadiness.MissingForRecognition(kv.Key, kv.Value))),
                webSearch = m.WebSearch.ToDictionary(kv => kv.Key,
                    kv => Mask(kv.Value, EngineReadiness.MissingForWebSearch(kv.Key, kv.Value))),
                fgisDomains = m.FgisDomains,
                manufacturerDomains = m.ManufacturerDomains,
                smtp = MaskSmtp(m.Smtp),
                // Токен наружу не отдаём — только признак «задан», как у ключей движков и пароля
                // SMTP. Репозиторий не секрет и виден как есть: без него признак «задан» ничего не
                // говорит — токен всегда выдаётся на конкретный репозиторий.
                github = new
                {
                    repository = string.IsNullOrWhiteSpace(m.Github.Repository)
                        ? GithubSettings.DefaultRepository
                        : m.Github.Repository,
                    hasToken = !string.IsNullOrWhiteSpace(m.Github.Token),
                    useProxy = m.Github.UseProxy,
                },
                // Прокси (issue #936): пароль не отдаём — только признак «задан», как у остальных секретов.
                proxy = new
                {
                    url = m.Proxy.Url,
                    user = m.Proxy.User,
                    hasPassword = !string.IsNullOrWhiteSpace(m.Proxy.Password),
                    // Кого можно проверить кнопкой (issue #937): сервисы с галкой и постоянным
                    // адресом. Считает сервер — у клиента нет и не должно быть списка этих адресов.
                    checkable = ProxyCheck.Checkable(proxyState, m)
                        .Select(s => new { service = s.ToString(), name = OutboundProxy.DisplayName(s) }),
                },
                externalLinksUseProxy = m.ExternalLinksUseProxy,
            });
        });

        // Сохранение только настроек GitHub. Пустой токен = оставить прежний, но ТОЛЬКО для того же
        // репозитория (см. MergeGithub): смена репозитория с пустым полем обнуляет токен, иначе форма
        // сама была бы способом отправить внутренний текст в чужое место нашим правом записи.
        g.MapPut("/github", async (GithubSettings github, IIntegrationSettings settings) =>
        {
            await settings.SaveGithubAsync(github);
            return Results.NoContent();
        });

        // Сохранение только прокси (issue #936). Пустой пароль = оставить прежний, но ТОЛЬКО для того же
        // прокси и логина (см. MergeProxy): иначе форма была бы способом унести пароль на чужой прокси.
        g.MapPut("/proxy", async (ProxySettings proxy, IIntegrationSettings settings) =>
        {
            await settings.SaveProxyAsync(proxy);
            return Results.NoContent();
        });

        // Проверка прокси по значениям ФОРМЫ (issue #937): соединение с прокси и туннель до одного из
        // сервисов с галкой. Ключ API не отправляется — ответ поставщика 401/403 значит «дошли».
        g.MapPost("/proxy/test", async (ProxyTestRequest req, IIntegrationSettings settings,
            OutboundProxyState state, CancellationToken ct) =>
        {
            var proxy = new ProxySettings { Url = req.Url, User = req.User, Password = req.Password };
            var model = await settings.GetEffectiveAsync(ct);

            // Пустое поле пароля при СОХРАНЁННОМ пароле значит «взять прежний», и вот тут же —
            // тот же урок, что у проверки SMTP: подставляется он ТОЛЬКО тому же прокси и тому же
            // логину. Иначе кнопка «Проверить» была бы способом отправить сохранённый пароль на
            // чужой адрес: достаточно вписать свой прокси и не трогать поле пароля.
            //
            // Когда сохранённого пароля нет, отправлять нечего — и требовать его нельзя: прокси без
            // входа это обычное дело, а отказ «введите пароль» на пустом месте выглядел бы поломкой.
            if (string.IsNullOrWhiteSpace(proxy.Password) && !string.IsNullOrWhiteSpace(model.Proxy.Password))
            {
                if (!proxy.SameProxyAs(model.Proxy))
                    return Results.Ok(new
                    {
                        ok = false,
                        problem = nameof(OutboundProblem.None),
                        message = "Проверка другого прокси или под другим логином требует ввести пароль: сохранённый на чужой адрес не отправляется.",
                    });
                proxy.Password = model.Proxy.Password;
            }

            // Какой сервис туннелировать: названный, иначе первый с галкой. Проверять сервис БЕЗ
            // галки бессмысленно — он и в работе пойдёт напрямую, и удачная проверка обещала бы не то.
            var checkable = ProxyCheck.Checkable(state, model);
            OutboundService? service =
                Enum.TryParse<OutboundService>(req.Service, ignoreCase: true, out var named) && checkable.Contains(named)
                    ? named
                    : checkable.Count > 0 ? checkable[0] : null;

            var result = await ProxyCheck.RunAsync(proxy, service,
                service is { } s ? ProxyCheck.TargetFor(s, model) : null, ct);
            return Results.Ok(new
            {
                ok = result.Ok,
                problem = result.Problem.ToString(),
                message = result.Message,
                service = result.Service?.ToString(),
                serviceName = result.Service is { } used ? OutboundProxy.DisplayName(used) : null,
                target = result.Target,
                ms = result.Milliseconds,
            });
        });

        // Сохранение только SMTP (отдельно от распознавания/поиска — формы не затирают друг друга).
        // Пустой пароль = оставить прежний (как ключи движков).
        g.MapPut("/email", async (SmtpSettings smtp, IIntegrationSettings settings) =>
        {
            await settings.SaveSmtpAsync(smtp);
            return Results.NoContent();
        });

        // Тест-отправка: проверяет, что SMTP настроен и письмо уходит. Возвращает понятную ошибку, не 500.
        g.MapPost("/email/test", async (EmailTestRequest req, IEmailSender email) =>
        {
            if (string.IsNullOrWhiteSpace(req.To))
                return Results.BadRequest(new { ok = false, error = "Укажите адрес получателя." });
            try
            {
                await email.SendAsync(new EmailMessage([req.To],
                    "BHS.CRG — тестовое письмо",
                    "Это тестовое письмо из системы исполнительной документации BHS.CRG. SMTP настроен верно."));
                return Results.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { ok = false, error = ex.Message });
            }
        });

        // Проверка подключения: соединение + аутентификация по значениям ФОРМЫ (без отправки письма и
        // без сохранения). Пустой пароль = взять сохранённый (форма не присылает существующий).
        g.MapPost("/email/test-connection", async (SmtpSettings smtp, IIntegrationSettings settings, IEmailSender email, CancellationToken ct) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(smtp.Password))
                {
                    // Сохранённый пароль подставляем ТОЛЬКО на сохранённый же сервер (сравнение —
                    // общее с путём сохранения, см. SmtpSettings.SameServerAs). Иначе проверка связи
                    // превращалась в выгрузку пароля: хост берётся из формы, пароль из базы — и
                    // достаточно указать свой сервер, чтобы он пришёл на него сам. Роль Admin по
                    // модели угроз всесильна, но украденная сессия администратора — нет, а здесь
                    // секрет уходил без единого «покажи пароль».
                    var saved = (await settings.GetEffectiveAsync(ct)).Smtp;
                    if (!smtp.SameServerAs(saved))
                        return Results.Ok(new { ok = false, error = "Проверка на другом сервере, под другим пользователем или без шифрования требует ввести пароль: сохранённый на чужой адрес не отправляется." });
                    smtp.Password = saved.Password;
                }
                await email.TestConnectionAsync(smtp, ct);
                return Results.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { ok = false, error = ex.Message });
            }
        });

        // Проверка email пользователей: у кого задан/валиден адрес (для рассылок/подписок).
        g.MapGet("/email/user-status", async (AppDbContext db, CancellationToken ct) =>
        {
            var users = await db.Set<ApplicationUser>().AsNoTracking()
                .Select(u => new { u.DisplayName, u.Email }).ToListAsync(ct);
            return Results.Ok(users.Select(u => new
            {
                displayName = u.DisplayName,
                email = u.Email,
                valid = EmailValidation.IsValid(u.Email),
            }));
        });

        // Модели для выпадающих списков: облачные — курируемым списком, Ollama — только реально
        // скачанные. Плюс то, что поставщик отвечает про сами модели (issue #799): курируемый список
        // протухает молча, и без этого «модель больше не обслуживается» выясняется разбором логов.
        g.MapGet("/models", async (IIntegrationSettings settings, IRecognitionModelCatalog catalog, CancellationToken ct) =>
        {
            var m = await settings.GetEffectiveAsync(ct);
            var installed = await catalog.GetInstalledAsync("Ollama", m.Rec("Ollama"), ct);

            // Что предлагаем выбрать. Текущее значение — наравне с курируемыми: именно оно и протухает.
            IEnumerable<string> Offered(string engine, string[] curated) =>
                curated.Concat([m.Rec(engine).Model ?? string.Empty])
                    .Where(o => !string.IsNullOrWhiteSpace(o))
                    .Distinct(StringComparer.OrdinalIgnoreCase);

            // К поставщику ходим ТОЛЬКО за выбранной моделью — той, на которой всё и стоит; про
            // остальные пункты отвечаем тем, что уже лежит в кэше. Проверять пробой весь список
            // заманчиво, но замерено: тринадцать моделей — 28 секунд на открытии настроек.
            // Сравнение «что просили» с «что подтвердили» делает СЕРВЕР: своя копия на клиенте
            // разъехалась бы на первом же частном случае (Ollama пишет «qwen3-vl:latest» там, где в
            // настройках стоит «qwen3-vl»), и вышло бы худшее — бейдж говорит «модель на месте»,
            // а в списке она помечена недоступной.
            async Task<(string Engine, object[] Gone, string? Issue, string? Blind)> CheckAsync(string engine, string[] curated)
            {
                var cfg = m.Rec(engine);
                var selected = cfg.Model ?? string.Empty;
                var checks = await Task.WhenAll(Offered(engine, curated).Select(async o =>
                    (Model: o, Status: await catalog.GetStatusAsync(engine, cfg, o,
                        probe: o.Equals(selected, StringComparison.OrdinalIgnoreCase)
                               && EngineReadiness.IsUsableForRecognition(engine, cfg)
                            ? ModelProbe.IfUnknown : ModelProbe.CacheOnly, ct))));
                var gone = checks.Where(c => c.Status.State == ModelState.Gone)
                    .Select(object (c) => new { model = c.Model, advice = c.Status.Advice })
                    .ToArray();
                var status = checks.FirstOrDefault(c => c.Model.Equals(selected, StringComparison.OrdinalIgnoreCase)).Status
                             ?? ModelStatus.Unknown;
                // Зрение — ТОЛЬКО из кэша (CacheOnly). Канарейка стоит секунд, а на холодной
                // модели — минуты: страница настроек столько не ждёт. Проверку запускает человек
                // кнопкой, и он же видит, что она идёт.
                var vision = await catalog.GetVisionAsync(engine, cfg, selected, VisionProbe.CacheOnly, ct);
                return (engine, gone, EngineReadiness.ModelIssue(engine, cfg, status),
                        EngineReadiness.VisionIssue(engine, cfg, vision));
            }

            var checked_ = await Task.WhenAll(
                CheckAsync("Gemini", GeminiModels),
                CheckAsync("Anthropic", AnthropicModels),
                CheckAsync("Ollama", []));

            return Results.Ok(new
            {
                anthropic = AnthropicModels,
                gemini = GeminiModels,
                ollama = installed ?? [],
                // Только те, про которые ТОЧНО известно «нет такой»; «не проверили» сюда не попадает.
                unavailable = checked_.ToDictionary(c => c.Engine, c => c.Gone),
                // Беда с выбранной моделью, одной строкой — для бейджа рядом с движком.
                issues = checked_.Where(c => c.Issue is not null).ToDictionary(c => c.Engine, c => c.Issue),
                // Слепота — отдельно от issues, а не строкой в общей куче: она запрещает работу, и
                // интерфейс красит её иначе (issue #801). Пусто здесь значит «не уличён», а не
                // «проверен»: вердикт берётся из кэша, проверку запускает человек кнопкой.
                blind = checked_.Where(c => c.Blind is not null).ToDictionary(c => c.Engine, c => c.Blind),
                // Пустой список моделей Ollama значит одно, если её спросили, и совсем другое, если
                // спросить не вышло. Без этого признака интерфейс советовал бы «скачайте модель»
                // остановленной Ollama.
                ollamaChecked = installed is not null,
            });
        });

        // Проверка зрения модели по кнопке (issue #801). Отдельно от GET /models намеренно: канарейка
        // ходит к движку и на холодной модели ждёт минуты, а страница настроек рисуется сразу.
        // Ответ «не проверено» — не отказ и не приговор: остановленная Ollama не делает модель слепой.
        g.MapPost("/vision-check", async (VisionCheckRequest req, IIntegrationSettings settings,
            IRecognitionModelCatalog catalog, CancellationToken ct) =>
        {
            var engine = req.Engine ?? "Ollama";
            var m = await settings.GetEffectiveAsync(ct);
            var cfg = m.Rec(engine);
            if (EngineReadiness.MissingForRecognition(engine, cfg) is { } missing)
                return Results.Ok(new { state = "unknown", error = $"Движок не настроен: {missing}." });

            // Заново, а не «если неизвестно»: сюда приходят по нажатию кнопки, то есть именно затем,
            // чтобы перепроверить — чаще всего после того, как послушались нашего же совета.
            var vision = await catalog.GetVisionAsync(engine, cfg, cfg.Model ?? "", VisionProbe.Refresh, ct);
            return Results.Ok(new
            {
                state = vision.State switch
                {
                    VisionState.Sighted => "sighted",
                    VisionState.Blind => "blind",
                    _ => "unknown",
                },
                issue = EngineReadiness.VisionIssue(engine, cfg, vision),
                detail = vision.Detail,
                error = vision.State == VisionState.Unknown
                    ? $"Проверить не удалось: {engine} не ответила ({(string.IsNullOrWhiteSpace(cfg.BaseUrl) ? "http://localhost:11434" : cfg.BaseUrl)}). " +
                      "Это не приговор модели — повторите, когда сервис поднимется."
                    : null,
            });
        });

        // Сохранение: ключ перезаписывается только при непустом значении (пустой = оставить прежний).
        g.MapPut("/", async (IntegrationSettingsModel model, IIntegrationSettings settings) =>
        {
            await settings.SaveAsync(model);
            return Results.NoContent();
        });
    }

    /// <param name="missing">Чего не хватает движку, чтобы участвовать в работе; <c>null</c> — настроен.</param>
    private static object Mask(IntegrationEngine e, string? missing) => new
    {
        enabled = e.Enabled,
        hasKey = !string.IsNullOrWhiteSpace(e.ApiKey),
        model = e.Model,
        baseUrl = e.BaseUrl,
        folderId = e.FolderId,
        host = e.Host,
        missing,
        useProxy = e.UseProxy,
    };

    // Пароль SMTP не возвращаем — только признак «задан» (как ключи движков).
    private static object MaskSmtp(SmtpSettings s) => new
    {
        enabled = s.Enabled,
        host = s.Host,
        port = s.Port,
        user = s.User,
        hasPassword = !string.IsNullOrWhiteSpace(s.Password),
        from = s.From,
        fromName = s.FromName,
        useSsl = s.UseSsl,
        useProxy = s.UseProxy,
    };

    private record CompanyTimeZoneRequest(string? TimeZoneId);
    private record EmailTestRequest(string? To);

    /// <param name="Service">Какой сервис туннелировать; пусто — первый с галкой «Через прокси».</param>
    private record ProxyTestRequest(string? Url, string? User, string? Password, string? Service);

    /// <summary>Какой движок проверять канарейкой; по умолчанию — Ollama, единственный, кого спрашиваем.</summary>
    private record VisionCheckRequest(string? Engine);
}
