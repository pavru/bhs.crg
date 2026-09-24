using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using BHS.CRG.Api.Auth;
using BHS.CRG.Infrastructure.Http;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace BHS.CRG.Api.Configuration;

/// <summary>
/// Корень композиции, часть 1 — хост: пределы, клиенты, база, Identity, прокси, лимиты, JWT
/// (вынесено из <c>Program.cs</c>, issue #1030).
///
/// <para>Порядок вызовов задаёт <c>Program.cs</c>, и он значим; внутри методов порядок операторов
/// перенесён дословно.</para>
/// </summary>
internal static class HostRegistration
{
    /// <summary>
    /// Пределы тела запроса, разбор формы, умолчания исходящих клиентов и обязательные значения
    /// конфигурации. Порядок внутри значим и сохранён дословно: умолчания клиентов ставятся ДО
    /// любого <c>AddHttpClient</c>, проверка конфигурации — до регистрации того, что ею пользуется.
    /// </summary>
    internal static void AddProcessDefaults(this WebApplicationBuilder builder)
    {
    // Потолок тела запроса и разбора multipart — по НАШИМ пределам (issue #482). По умолчанию Kestrel
    // режет на 30 000 000 байт, из-за чего заявленные «50 МБ» были недостижимы, а пользователь получал
    // 500 с английским текстом фреймворка вместо внятного отказа.
    //
    // Глобально ставим ОБЫЧНЫЙ предел, а не наибольший: file.Length известен только после того, как
    // форма прочитана и часть выгружена во временный файл, поэтому глобальные 500 МБ позволили бы
    // любому пользователю заставить сервер выписать на диск сотни мегабайт перед отказом.
    // Восстановление бэкапа поднимает предел себе само.
    builder.WebHost.ConfigureKestrel(
        o => o.Limits.MaxRequestBodySize = BHS.CRG.Api.Endpoints.Common.UploadLimits.OrdinaryRequest);

    // Наибольший предел — архив восстановления, и он настраивается развёртыванием (issue #711).
    // Читаем его здесь же, до всего прочего: негодное значение обязано остановить запуск, а не
    // обнаружиться на первом восстановлении.
    var backupLimits = BHS.CRG.Api.Configuration.BackupSizeLimits.FromConfiguration(builder.Configuration);
    builder.Services.AddSingleton(backupLimits);
    builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
    {
        // Разбор формы — по наибольшему: он вторичный предохранитель, режет всё равно Kestrel.
        o.MultipartBodyLengthLimit = backupLimits.RequestBytes;
    });
    var cfg = builder.Configuration;

    // Как приложение открывает соединения наружу — один раз на всех клиентов фабрики. Платформа
    // перебирает адреса имени по очереди, и один недостижимый съедает весь бюджет: у health-пробы это
    // давало «недоступен/восстановлен» через раз, у распознавания и загрузки по ссылке — молчаливую
    // потерю секунд (issue #917). Настройка ставится ДО всех AddHttpClient — иначе клиент, собранный
    // раньше, останется со штатным последовательным перебором.
    //
    // Именно UseSocketsHttpHandler, а не ConfigurePrimaryHttpMessageHandler: первый ДОПОЛНЯЕТ
    // существующий обработчик, второй ЗАМЕНЯЕТ его целиком. С заменой порядок регистрации в DI решал
    // бы, что кого затрёт, — а среди затираемого есть проверка адреса (OutboundAddressPolicy).
    //
    // Здесь же — «напрямую» для всех (issue #936): внутреннее ходит мимо прокси по построению, а внешний
    // сервис получает прокси явно, своей регистрацией ниже (OutboundProxy.Route).
    var happyEyeballs = cfg.GetValue("Http:HappyEyeballs", true);
    builder.Services.ConfigureHttpClientDefaults(b => b.UseSocketsHttpHandler((h, _) =>
    {
        OutboundProxy.Direct(h);
        if (happyEyeballs) OutboundConnect.Apply(h);
    }));
    builder.Services.AddSingleton<OutboundProxyState>();

    builder.Services.ConfigureHttpJsonOptions(opt =>
        opt.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

    // Обязательные значения конфигурации проверяем ПЕРВЫМ делом — до регистрации чего бы то ни было,
    // которое ими пользуется. Проверка ключа подписи стоит ниже, у своей секции, и это не разнобой:
    // она читает значение оттуда же, откуда его берёт выдача токенов.
    StorageConfigGuard.Require(
        cfg.GetConnectionString("Postgres"),
        cfg["BlobStorage:AccessKey"],
        cfg["BlobStorage:SecretKey"],
        cfg["BlobStorage:Bucket"]);
    }

    /// <summary>
    /// База, Identity, ключи Data Protection, каталог резервных копий и время жизни токенов.
    /// Каталоги проверяются на запись ЗДЕСЬ, на старте: узнать о недоступном каталоге при попытке
    /// снять копию значит узнать в тот единственный раз, когда копия была нужна.
    /// </summary>
    internal static void AddPersistenceAndIdentity(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
    // ── Database ──────────────────────────────────────────────────────────────────
    builder.Services.AddDbContext<AppDbContext>(opt =>
        opt.UseNpgsql(cfg.GetConnectionString("Postgres")));

    // ── Identity ──────────────────────────────────────────────────────────────────
    builder.Services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(opt =>
        {
            // Парольная политика (issue #148 follow-up): ≥8 символов, буквы разного регистра + цифра.
            // Спецсимволы не обязательны (меньше трения). Затрагивает только установку нового пароля.
            opt.Password.RequiredLength = 8;
            opt.Password.RequireDigit = true;
            opt.Password.RequireLowercase = true;
            opt.Password.RequireUppercase = true;
            opt.Password.RequireNonAlphanumeric = false;
            // Защита от перебора пароля (issue #148 follow-up): блокировка на 15 минут после 5 неудач.
            opt.Lockout.AllowedForNewUsers = true;
            opt.Lockout.MaxFailedAccessAttempts = 5;
            opt.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            // Подтверждение/смена email — на отдельном 24-часовом провайдере (issue #148),
            // сброс пароля остаётся на дефолтном (1 час).
            opt.Tokens.EmailConfirmationTokenProvider = "EmailConfirmDP";
            opt.Tokens.ChangeEmailTokenProvider = "EmailConfirmDP";
        })
        .AddEntityFrameworkStores<AppDbContext>()
        .AddErrorDescriber<RuIdentityErrorDescriber>()
        .AddDefaultTokenProviders()
        .AddTokenProvider<EmailConfirmTokenProvider>("EmailConfirmDP");

    // Токены сброса/подтверждения (issue #148) шифруются ключами Data Protection. БЕЗ персистентных
    // ключей они инвалидируются при каждом рестарте API (и ломаются при >1 инстанса). Персистим на диск
    // (в контейнере — том); путь настраивается, дефолт — рядом с приложением.
    var dpKeysPath = cfg["DataProtection:KeysPath"];
    if (string.IsNullOrWhiteSpace(dpKeysPath))
        dpKeysPath = Path.Combine(builder.Environment.ContentRootPath, "dp-keys");
    Directory.CreateDirectory(dpKeysPath);
    // Каталог должен быть ЗАПИСЫВАЕМ, и проверяем это здесь. Data Protection при невозможности записи
    // не падает, а молча переходит на ключи в памяти — снаружи это выглядит как «ссылки сброса пароля
    // протухают сами собой», причём заново после каждого рестарта, и ищется такое долго. Случай не
    // умозрительный: том dp_keys, созданный до перехода контейнера на непривилегированного
    // пользователя, остаётся принадлежать root (лечится разовым chown, см. docs/DEPLOYMENT.md §8).
    try
    {
        var probe = Path.Combine(dpKeysPath, ".write-probe");
        File.WriteAllText(probe, string.Empty);
        File.Delete(probe);
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException(
            $"Каталог ключей Data Protection «{dpKeysPath}» недоступен для записи ({ex.Message}). " +
            "Без него токены сброса пароля и подтверждения почты будут инвалидироваться при каждом " +
            "перезапуске. В поставке Docker выполните (--entrypoint обязателен, иначе аргументы " +
            "достанутся серверу приложений и chown не выполнится): docker compose -f " +
            "deploy/docker-compose.yml run --rm --user root --entrypoint chown api -R app:app " +
            "/app/dp-keys", ex);
    }
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dpKeysPath))
        .SetApplicationName("BHS.CRG");

    // Каталог резервных копий (issue #831) — здесь же и по той же причине, что каталог ключей выше:
    // контейнер работает не от root, и каталог, смонтированный с хоста с чужим владельцем, — самый
    // вероятный отказ подсистемы. Узнать о нём при попытке снять копию значит узнать в тот
    // единственный раз, когда копия была нужна.
    var backupStorage = BHS.CRG.Application.Backup.BackupStorageOptions.Parse(
        cfg["Backup:Directory"], cfg["Backup:KeepCount"],
        Path.Combine(builder.Environment.ContentRootPath, "backups"));
    backupStorage.EnsureUsable();
    builder.Services.AddSingleton(backupStorage);

    // Время жизни токенов сброса пароля / подтверждения (дефолтный token-провайдер) — 1 час.
    builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(1));
    }

    /// <summary>
    /// Доверенные сети обратного прокси и ограничение частоты запросов. Список сетей разбирается
    /// здесь, а не внутри <c>Configure</c>: делегат выполняется лениво, и опечатка в CIDR валила бы
    /// каждый запрос при внешне успешном старте.
    /// </summary>
    internal static void AddForwardedHeadersAndRateLimiting(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
    // За обратным прокси (в поставке это nginx) Connection.RemoteIpAddress — адрес контейнера прокси,
    // один и тот же для всех. Партиционирование лимитов по нему вырождается в общую корзину: десяти
    // запросов «забыли пароль» хватало, чтобы выключить сброс пароля всей организации. Реальный адрес
    // берём из X-Forwarded-For, но ТОЛЬКО когда запрос пришёл от доверенной сети — иначе заголовок
    // подделает кто угодно и лимит обходится сменой строки.
    // Список разбираем ЗДЕСЬ, а не внутри Configure: делегат выполняется лениво, при первом запросе, и
    // опечатка в CIDR валила бы каждый запрос на первом же middleware при внешне успешном старте. Ключ
    // конфигурации негодного вида должен останавливать запуск — как с ключом подписи ниже.
    var trustedProxyNetworks = ParseTrustedNetworks(cfg["ForwardedHeaders:TrustedNetworks"]);

    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        // Дефолт фреймворка — только петля; в Compose прокси приходит из сети моста, поэтому список
        // задаём сами. Настраивается ForwardedHeaders:TrustedNetworks (CIDR через запятую) — сузить
        // до конкретной сети прокси, если API доступен ещё откуда-то.
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
        foreach (var n in trustedProxyNetworks)
            o.KnownIPNetworks.Add(n);

        // Разбираем СТОЛЬКО записей, сколько пришло из доверенных сетей, а не одну. Прокси между
        // клиентом и нами обычно два: терминатор TLS (обязателен, см. DEPLOYMENT §2) и наш nginx.
        // С дефолтным ForwardLimit = 1 разобралась бы только запись, добавленная nginx, и адресом
        // клиента стал бы ТЕРМИНАТОР — один и тот же у всех, то есть корзина лимита снова одна на
        // всю организацию: ровно та беда, ради которой заголовок и разбирается.
        // Обход останавливает не счётчик, а первая запись вне доверенных сетей.
        //
        // Плата: клиент, сам находящийся в доверенной сети, может дописать в заголовок выдуманные
        // адреса и выбрать себе корзину лимита. Он и так может прийти с нескольких адресов, а больше
        // разобранный адрес у нас никем не используется; кому это важно — сужает TrustedNetworks до
        // сети прокси, и подделка перестаёт проходить.
        o.ForwardLimit = null;
    });

    static System.Net.IPNetwork[] ParseTrustedNetworks(string? configured)
    {
        string[] raw = string.IsNullOrWhiteSpace(configured)
            ? ["127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"]
            : configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return [.. raw.Select(n =>
        {
            try { return System.Net.IPNetwork.Parse(n); }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    $"ForwardedHeaders:TrustedNetworks — «{n}» не является сетью в записи CIDR " +
                    "(ожидается вид 10.0.0.0/8; биты адреса за маской должны быть нулевыми).", ex);
            }
        })];
    }

    // Rate limiting чувствительных анонимных эндпоинтов — по адресу клиента (см. UseForwardedHeaders).
    // Пределы разные, потому что разные профили обращения: за NAT организации адрес общий на всех, и
    // предел, годный для «забыли пароль», выключил бы вход целому офису.
    static Func<HttpContext, RateLimitPartition<string>> PerClient(int permitLimit, TimeSpan window) =>
        ctx => RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = window, QueueLimit = 0 });

    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        // Парольные и почтовые операции: редки по своей природе, предел жёсткий.
        o.AddPolicy("auth", PerClient(10, TimeSpan.FromMinutes(15)));
        // Вход и регистрация первого администратора. Подбор пароля отдельно ограничен блокировкой
        // учётной записи (5 неудач), лимит здесь — против перебора адресов и распыления пароля.
        o.AddPolicy("login", PerClient(30, TimeSpan.FromMinutes(5)));
        // Обновление пары токенов: у каждого пользователя раз в час, подбирать 256-битный refresh-токен
        // смысла нет — предел только против шторма из зациклившегося клиента.
        o.AddPolicy("refresh", PerClient(120, TimeSpan.FromMinutes(5)));
        // Сообщения об ошибках (issue #834): по ПОЛЬЗОВАТЕЛЮ, а не по адресу. Эндпоинт закрыт входом,
        // и за общим адресом офиса сидят разные люди — предел по адресу заткнул бы рот всем, кроме
        // первого. Десяти сообщений за десять минут хватает и человеку в плохой день, и от заливки
        // формой в цикле защищает.
        o.AddPolicy("bug-report", ctx => RateLimitPartition.GetFixedWindowLimiter(
            ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? ctx.User.FindFirst("sub")?.Value
                ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10, Window = TimeSpan.FromMinutes(10), QueueLimit = 0,
            }));
    });
    }

    /// <summary>Выдача и проверка JWT плюс политики авторизации.</summary>
    internal static void AddJwtAuth(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
    // ── JWT ───────────────────────────────────────────────────────────────────────
    var jwtSection = cfg.GetSection("Jwt");
    // Проверка ключа — здесь, чтобы негодная конфигурация останавливала запуск сразу (см. JwtKeyGuard),
    // а не при первом обращении к защищённому эндпоинту.
    JwtKeyGuard.Require(jwtSection["Key"]);
    builder.Services.AddAuthentication(opt =>
        {
            opt.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            opt.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(opt =>
        {
            // Не переименовывать claim-типы во внутренние URI — оставляем "sub"/"role" как есть.
            opt.MapInboundClaims = false;
            opt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwtSection["Issuer"],
                ValidateAudience = true,
                ValidAudience = jwtSection["Audience"],
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                // Значение читаем ЗДЕСЬ, а не подставляем прочитанное выше: этот делегат выполняется
                // позже, на готовой конфигурации, и её слои к тому моменту могут отличаться от тех, что
                // были на строке выше (так тестовый хост подкладывает свой ключ). Подставь мы сюда
                // раннее значение — подписывали бы одним ключом, а проверяли другим.
                // Проверку повторяем: ключ, которым реально пользуются, обязан быть годным.
                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(JwtKeyGuard.Require(jwtSection["Key"]))),
                RoleClaimType = "role",
            };
            opt.Events = new JwtBearerEvents
            {
                // Токен принимаем ТОЛЬКО из заголовка Authorization. Приём из query-строки существовал
                // ради рукопожатия SignalR (#486) — вместе с хабом убран: токен в строке запроса
                // оседает в логах доступа и заголовке Referer, а нужды в нём больше нет.
                // Проверка SecurityStamp (issue #148 follow-up): токен со «старым» стампом
                // (после сброса/смены пароля или logout-all) отклоняется, даже не истёкший.
                OnTokenValidated = async ctx =>
                {
                    var principal = ctx.Principal;
                    var userId = principal?.FindFirst("sub")?.Value;
                    var tokenStamp = principal?.FindFirst(JwtTokens.SecurityStampClaim)?.Value;
                    if (userId is null || tokenStamp is null) { ctx.Fail("Недействительный токен."); return; }

                    var userManager = ctx.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
                    var user = await userManager.FindByIdAsync(userId);
                    if (user is null) { ctx.Fail("Пользователь не найден."); return; }

                    var currentStamp = await userManager.GetSecurityStampAsync(user);
                    if (!string.Equals(tokenStamp, currentStamp, StringComparison.Ordinal))
                        ctx.Fail("Сессия недействительна — войдите заново.");
                }
            };
        });
    // Политики «по имени роли» здесь больше НЕТ и заводить её снова нельзя: доступ разграничивают
    // права (ТЗ AUTH-8), а имя роли не значит ничего — состав роли меняют в редакторе, имя остаётся
    // прежним. Последняя такая политика — "Admin" — убрана вместе с последними двумя дверьми на ней
    // (issue #989). Оставленная зарегистрированной, она была приглашением: следующий автор ворот взял
    // бы готовое имя, не заметив, что это откат к прежнему устройству.
    builder.Services.AddAuthorization();
    }
}

/// <summary>
/// Клиент внешнего сервиса: прокси — если у сервиса стоит галка (issue #936). Одна функция на все
/// регистрации, чтобы «чей это клиент» нельзя было пропустить молча — сторожит
/// <c>OutboundClientRoutingTests</c> (он проверяет собранное приложение, а не текст).
///
/// <para>Жила локальной функцией в <c>Program.cs</c>; при разрезе (issue #1030) стала членом класса,
/// потому что зовут её из трёх файлов регистрации. Вызовы не менялись: файлы подключают её через
/// <c>using static</c>.</para>
/// </summary>
internal static class OutboundClients
{
    internal static void RouteVia(IHttpClientBuilder b, OutboundService service) =>
        b.UseSocketsHttpHandler((h, sp) => OutboundProxy.Route(h, service, sp.GetRequiredService<OutboundProxyState>()));
}
