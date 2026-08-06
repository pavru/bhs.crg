using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Common;
using BHS.CRG.Api.Configuration;
using BHS.CRG.Api.Endpoints.Account;
using BHS.CRG.Api.Endpoints.Attachments;
using BHS.CRG.Api.Endpoints.Auth;
using BHS.CRG.Api.Endpoints.Backup;
using BHS.CRG.Api.Endpoints.Maintenance;
using BHS.CRG.Api.Endpoints.Catalog;
using BHS.CRG.Api.Endpoints.Recognition;
using BHS.CRG.Api.Endpoints.DataSets;
using BHS.CRG.Api.Endpoints.Documents;
using BHS.CRG.Api.Endpoints.Email;
using BHS.CRG.Api.Endpoints.Subscriptions;
using BHS.CRG.Api.Endpoints.Generation;
using BHS.CRG.Api.Endpoints.QualityDocs;
using BHS.CRG.Api.Endpoints.Reconciliation;
using BHS.CRG.Api.Endpoints.Resolution;
using BHS.CRG.Api.Endpoints.Templates;
using BHS.CRG.Application.Catalog;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Templates;
using BHS.CRG.Infrastructure.Templates;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Resolution;
using BHS.CRG.Application.Settings;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Api.Endpoints.Settings;
using BHS.CRG.Api.Endpoints.Users;
using BHS.CRG.Api.Endpoints.Schema;
using BHS.CRG.Api.Endpoints.Notifications;
using BHS.CRG.Api.Endpoints.Jobs;
using BHS.CRG.Api.Notifications;
using BHS.CRG.Application.Jobs;
using BHS.CRG.Domain.Jobs;
using BHS.CRG.Infrastructure.Jobs;
using BHS.CRG.Infrastructure.Notifications;
using BHS.CRG.Infrastructure.Recognition;
using BHS.CRG.Infrastructure.Search;
using BHS.CRG.Infrastructure.Settings;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Generation;
using BHS.CRG.Infrastructure.Http;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Plugins;
using BHS.CRG.Infrastructure.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Minio;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dpKeysPath))
    .SetApplicationName("BHS.CRG");

// Время жизни токенов сброса пароля / подтверждения (дефолтный token-провайдер) — 1 час.
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(1));

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
});

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
builder.Services.AddAuthorization(opt =>
    opt.AddPolicy("Admin", p => p.RequireRole("Admin")));

// ── MediatR ───────────────────────────────────────────────────────────────────
builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssemblies(
        typeof(CatalogHandlers).Assembly,
        typeof(GenerateDocumentHandler).Assembly));

// ── Repositories ──────────────────────────────────────────────────────────────
builder.Services.AddScoped<IRepository<CatalogEntity>, Repository<CatalogEntity>>();
builder.Services.AddScoped<IRepository<BHS.CRG.Domain.DataSets.DataSetBinding>,
    Repository<BHS.CRG.Domain.DataSets.DataSetBinding>>();
builder.Services.AddScoped<IRepository<PrimitiveType>, Repository<PrimitiveType>>();
builder.Services.AddScoped<IRepository<EnumType>, Repository<EnumType>>();
builder.Services.AddScoped<IRepository<BHS.CRG.Domain.Recognition.RecognitionProfile>, Repository<BHS.CRG.Domain.Recognition.RecognitionProfile>>();
builder.Services.AddScoped<IRepository<DocumentType>, Repository<DocumentType>>();
builder.Services.AddScoped<IRepository<Construction>, ConstructionRepository>();
builder.Services.AddScoped<IRepository<Section>, Repository<Section>>();
builder.Services.AddScoped<IRepository<DocumentSet>, DocumentSetRepository>();
builder.Services.AddScoped<DomainObjectRepository>();
builder.Services.AddScoped<IRepository<BHS.CRG.Domain.Objects.DomainObject>>(sp => sp.GetRequiredService<DomainObjectRepository>());
builder.Services.AddScoped<IDomainObjectRepository>(sp => sp.GetRequiredService<DomainObjectRepository>());
builder.Services.AddScoped<IRepository<Template>, Repository<Template>>();
builder.Services.AddScoped<IRepository<TemplateAsset>, Repository<TemplateAsset>>();
builder.Services.AddScoped<BHS.CRG.Application.Templates.IDocumentTemplateInvalidator,
    BHS.CRG.Application.Templates.DocumentTemplateInvalidator>();
builder.Services.AddScoped<IRepository<GeneratedFile>, Repository<GeneratedFile>>();

// Сверка на непротиворечивость (issue #433).
builder.Services.AddScoped<IRepository<BHS.CRG.Domain.Reconciliation.ReconciliationDefinition>,
    Repository<BHS.CRG.Domain.Reconciliation.ReconciliationDefinition>>();
builder.Services.AddScoped<IRepository<BHS.CRG.Domain.Reconciliation.ReconciliationRun>,
    Repository<BHS.CRG.Domain.Reconciliation.ReconciliationRun>>();
builder.Services.AddScoped<IRepository<BHS.CRG.Domain.Reconciliation.ReconciliationFinding>,
    Repository<BHS.CRG.Domain.Reconciliation.ReconciliationFinding>>();
builder.Services.AddScoped<IRepository<BHS.CRG.Domain.Reconciliation.ReconciliationDecision>,
    Repository<BHS.CRG.Domain.Reconciliation.ReconciliationDecision>>();
builder.Services.AddScoped<IRepository<BHS.CRG.Domain.Reconciliation.AgentObservation>,
    Repository<BHS.CRG.Domain.Reconciliation.AgentObservation>>();
builder.Services.AddScoped<IRepository<BHS.CRG.Domain.Reconciliation.ReconciliationAlias>,
    Repository<BHS.CRG.Domain.Reconciliation.ReconciliationAlias>>();
builder.Services.AddScoped<IRepository<DocumentSetOutput>, Repository<DocumentSetOutput>>();
builder.Services.AddScoped<IRepository<TypstUserLib>, Repository<TypstUserLib>>();
builder.Services.AddScoped<IRepository<TypstUserLibFile>, Repository<TypstUserLibFile>>();
builder.Services.AddScoped<IRepository<QualityDocument>, Repository<QualityDocument>>();
builder.Services.AddScoped<IRepository<MaterialQualityLink>, Repository<MaterialQualityLink>>();
builder.Services.AddScoped<IRepository<QualityAuditRun>, Repository<QualityAuditRun>>();

// ── Backup ────────────────────────────────────────────────────────────────────
builder.Services.AddScoped<BackupService>();
builder.Services.AddScoped<BHS.CRG.Infrastructure.Maintenance.ImageBlobMigration>();
builder.Services.AddScoped<BHS.CRG.Infrastructure.Maintenance.MaterialLabelBackfill>();

// ── Снимок данных для внешних потребителей + MCP (issue #415) ─────────────────
builder.Services.AddScoped<BHS.CRG.Application.DataSnapshots.IDataSnapshotService,
    BHS.CRG.Infrastructure.DataSets.DataSnapshotService>();
builder.Services.AddScoped<BHS.CRG.Application.DataSnapshots.IDomainSnapshotService,
    BHS.CRG.Infrastructure.Generation.DomainSnapshotService>();
// MCP-инструментам домена нужен ClaimsPrincipal (act-as-user): агент работает от имени пользователя.
builder.Services.AddHttpContextAccessor();
// MCP-сервер: ВТОРОЙ тонкий адаптер над тем же ядром, in-process с API — переиспользует ту же
// аутентификацию, DI и scoping DbContext. Только чтение: инструментов записи в этом срезе нет.
builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "bhs-crg", Version = BHS.CRG.Api.Mcp.McpContract.ServerVersion };
        o.ServerInstructions = BHS.CRG.Api.Mcp.McpContract.Instructions;
    })
    // Без сессий (issue #599). Сессия жила в памяти процесса, и первый вызов после паузы регулярно
    // получал 404 «Session not found»: клиент вынужден был закладывать слепой ретрай, а для
    // неидемпотентного вызова это небезопасно. Каждый запрос теперь самодостаточен — терять нечего.
    //
    // Плата: сервер не может слать клиенту сообщения по своей инициативе (sampling, elicitation,
    // подписки на ресурсы) и не поднимает legacy /sse. Мы ничем из этого не пользуемся: сервер здесь
    // отвечает на вопросы и не задаёт своих.
    .WithHttpTransport(o => o.Stateless = true)
    // Кодировщик передаётся каждой регистрации: домен русскоязычный, а по умолчанию System.Text.Json
    // раздувает кириллицу в \uXXXX вчетверо и упирает ответы в лимит клиента (#576, McpSerialization).
    .WithTools<BHS.CRG.Api.Mcp.DataSnapshotTools>(BHS.CRG.Api.Mcp.McpSerialization.ToolOptions)
    .WithTools<BHS.CRG.Api.Mcp.DomainSnapshotTools>(BHS.CRG.Api.Mcp.McpSerialization.ToolOptions)
    .WithTools<BHS.CRG.Api.Mcp.DocumentActionTools>(BHS.CRG.Api.Mcp.McpSerialization.ToolOptions)
    .WithTools<BHS.CRG.Api.Mcp.ObservationTools>(BHS.CRG.Api.Mcp.McpSerialization.ToolOptions)
    .WithTools<BHS.CRG.Api.Mcp.ReconciliationTools>(BHS.CRG.Api.Mcp.McpSerialization.ToolOptions)
    // Ресурсы сериализуем сами (McpJsonResource) — SDK принимает от них уже готовый текст.
    .WithResources<BHS.CRG.Api.Mcp.DataSnapshotResources>()
    .WithResources<BHS.CRG.Api.Mcp.DomainSnapshotResources>()
    .WithPrompts<BHS.CRG.Api.Mcp.ReconciliationPrompts>(BHS.CRG.Api.Mcp.McpSerialization.ToolOptions)
    // Шаблоны уезжают в resources/templates/list; здесь — реальные объекты для прикрепления (#427).
    .WithListResourcesHandler(BHS.CRG.Api.Mcp.McpResourceCatalog.ListAsync);

// ── Профили распознавания (issue #406) ────────────────────────────────────────
builder.Services.AddScoped<BHS.CRG.Application.Recognition.IRecognitionProfileProvider,
    BHS.CRG.Infrastructure.Recognition.RecognitionProfileProvider>();

// ── Generation ────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<BHS.CRG.Application.Generation.IExpressionEvaluator,
    BHS.CRG.Infrastructure.Generation.JintExpressionEvaluator>();
builder.Services.AddScoped<IEntityResolver, EntityResolver>();

// Сверка на непротиворечивость (issue #431): арифметику считает код, ИИ в пути сравнения нет.
builder.Services.AddScoped<BHS.CRG.Application.Reconciliation.IReconciliationRunner,
    BHS.CRG.Infrastructure.Reconciliation.ReconciliationRunner>();
builder.Services.AddScoped<BHS.CRG.Application.Reconciliation.IProblemAttribution,
    BHS.CRG.Infrastructure.Reconciliation.ProblemAttributionService>();
builder.Services.AddScoped<BHS.CRG.Application.Documents.ILevelProfileService, BHS.CRG.Infrastructure.Generation.LevelProfileService>();
// Спуск «уровень → комплекты поддерева» (issue #625): слою приложения нужен через контракт —
// AppDbContext ему недоступен, а копия обхода была у него своя.
builder.Services.AddScoped<BHS.CRG.Application.Common.IScopeSubtree, BHS.CRG.Infrastructure.Common.ScopeSubtreeService>();
builder.Services.AddScoped<IMetadataExtractor, MetadataExtractor>();
builder.Services.AddScoped<IDataSetResolver, DataSetResolver>();
builder.Services.AddScoped<IObjectResolver, ObjectResolver>();
builder.Services.AddScoped<IQualityLinkResolver, QualityLinkResolver>();
// Проверка резолва экземпляра как СЕРВИС, а не только запрос MediatR: пакетным вызывающим нужно
// читать справочники схемы один раз на прогон, а не на документ (issue #628).
builder.Services.AddScoped<BHS.CRG.Application.Generation.IInstanceResolutionValidator,
    BHS.CRG.Application.Generation.InstanceResolutionValidator>();
builder.Services.AddScoped<BHS.CRG.Application.QualityDocs.IQualitySetAuditRunner,
    BHS.CRG.Application.QualityDocs.QualitySetAuditRunner>();
builder.Services.AddScoped<ITemplateAssetResolver, TemplateAssetResolver>();
builder.Services.AddHttpClient<AnthropicRecognizerEngine>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(2));
builder.Services.AddHttpClient<GeminiRecognizerEngine>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(2));
builder.Services.AddHttpClient<OllamaRecognizerEngine>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(3));
builder.Services.AddScoped<IRecognizerEngine>(sp => sp.GetRequiredService<AnthropicRecognizerEngine>());
builder.Services.AddScoped<IRecognizerEngine>(sp => sp.GetRequiredService<GeminiRecognizerEngine>());
builder.Services.AddScoped<IRecognizerEngine>(sp => sp.GetRequiredService<OllamaRecognizerEngine>());
builder.Services.AddScoped<IDocumentRecognizer, ChainDocumentRecognizer>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<SettingsSecretProtector>();
builder.Services.AddScoped<IntegrationSettingsService>();
builder.Services.AddScoped<IIntegrationSettings>(sp => sp.GetRequiredService<IntegrationSettingsService>());
builder.Services.AddScoped<BHS.CRG.Application.Email.IEmailSender, BHS.CRG.Infrastructure.Email.MailKitEmailSender>();
builder.Services.AddScoped<BHS.CRG.Infrastructure.Email.AccountEmailService>();
builder.Services.AddScoped<RefreshTokenService>();

// ── Фоновые задачи (долгие операции: распознавание набора/таблицы) ──────────────
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddScoped<IJobService, JobService>();
builder.Services.AddHostedService<JobBackgroundService>();

// ── Notifications + health monitoring ───────────────────────────────────────────
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddSingleton<HealthMonitorService>();
builder.Services.AddSingleton<IHealthState>(sp => sp.GetRequiredService<HealthMonitorService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<HealthMonitorService>());
builder.Services.AddHttpClient<SerperEngine>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient<YandexEngine>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<IWebSearchEngine>(sp => sp.GetRequiredService<SerperEngine>());
builder.Services.AddScoped<IWebSearchEngine>(sp => sp.GetRequiredService<YandexEngine>());
// Автоследование за перенаправлениями выключено намеренно: переходы проходит SafeHttpGet, проверяя
// цель каждого. С автоследованием проверка исходного адреса ничего не стоит — ответ общедоступного
// хоста уводит куда угодно.
builder.Services.AddHttpClient<TieredWebSearch>()
    .ConfigurePrimaryHttpMessageHandler(OutboundAddressPolicy.CreateGuardedHandler)
    .ConfigureHttpClient(c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
});
builder.Services.AddScoped<IQualityDocSearch>(sp => sp.GetRequiredService<TieredWebSearch>());
builder.Services.AddHttpClient<IFileUrlFetcher, HttpFileUrlFetcher>()
    .ConfigurePrimaryHttpMessageHandler(OutboundAddressPolicy.CreateGuardedHandler)
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton<TypstGenerator>();
builder.Services.AddSingleton<IDocumentGeneratorFactory, DocumentGeneratorFactory>();
builder.Services.AddSingleton<ITypstSyntaxChecker, TypstSyntaxChecker>();
builder.Services.AddSingleton<BHS.CRG.Application.Templates.IUserLibChecker, UserLibChecker>();
// Единая точка чтения библиотеки (issue #473) — раньше три места читали её каждое по-своему.
builder.Services.AddScoped<BHS.CRG.Application.Templates.IUserLibProvider,
    BHS.CRG.Application.Templates.UserLibProvider>();

// ── DataSets ──────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<IDataSetParser, CsvDataSetParser>();
builder.Services.AddSingleton<IDataSetParser, XlsxDataSetParser>();
builder.Services.AddSingleton<IDataSetParser, XmlDataSetParser>();
builder.Services.AddSingleton<IDataSetParser, JsonDataSetParser>();
builder.Services.AddSingleton<IDataSetParser, ZipDataSetParser>();
builder.Services.AddSingleton<IDataSetParser, PdfDataSetParser>();
builder.Services.AddSingleton<DataSetParserFactory>();
builder.Services.AddScoped<ISystemDataProvider, SetDocumentsProvider>();
builder.Services.AddScoped<ISystemDataProvider, QualityDocumentsProvider>();
builder.Services.AddScoped<ISystemDataProvider, MaterialQualityProvider>();
builder.Services.AddScoped<ISystemDataProvider, SubtreeDocumentsProvider>();
builder.Services.AddScoped<ISystemDataProvider, DomainObjectsProvider>();
builder.Services.AddScoped<SystemDataProviderRegistry>();
builder.Services.AddScoped<IDataSetRowLoader, DataSetRowLoader>();
builder.Services.AddScoped<SystemSourceCounter>();
builder.Services.AddScoped<DataSetProcessingTemplateService>();
builder.Services.AddScoped<DataSetBindingTemplateService>();
builder.Services.AddScoped<DataSetPdfRecognitionService>();
builder.Services.AddScoped<DataSetFileService>();
builder.Services.AddScoped<DataSetSourceService>();
builder.Services.AddScoped<DataSetBindingService>();
builder.Services.AddScoped<BHS.CRG.Infrastructure.Generation.DocumentSetAssemblyService>();
builder.Services.AddScoped<BHS.CRG.Infrastructure.Email.DocumentSetEmailService>();
builder.Services.AddScoped<BHS.CRG.Application.Documents.IDocumentSearch, BHS.CRG.Infrastructure.Documents.DocumentSearchService>();
builder.Services.AddScoped<BHS.CRG.Application.Subscriptions.ISubscriptionService, BHS.CRG.Infrastructure.Subscriptions.SubscriptionService>();
builder.Services.AddScoped<IDataSetService, DataSetService>();

// ── MinIO ─────────────────────────────────────────────────────────────────────
var blobOpts = cfg.GetSection("BlobStorage").Get<BlobStorageOptions>() ?? new();
builder.Services.AddSingleton(blobOpts);
builder.Services.AddMinio(c => c
    .WithEndpoint(blobOpts.Endpoint)
    .WithCredentials(blobOpts.AccessKey, blobOpts.SecretKey)
    .WithSSL(blobOpts.UseSSL));
// Хранилище отдаётся наружу ТОЛЬКО обёрнутым (issue #672): обёртка ведёт реестр созданных
// приложением объектов и отказывает в выдаче тому, чего в реестре нет.
//
// Настоящее хранилище в контейнер НЕ кладётся вовсе — оно создаётся здесь и живёт только внутри
// обёртки. Разница не косметическая: будь оно зарегистрировано, любой мог бы попросить
// GetRequiredService<MinIOBlobStorage>() и записать мимо реестра, а типы взаимозаменяемы по
// сигнатурам — ни компилятор, ни ревью такого не заметят. Теперь такой запрос просто не
// разрешается.
builder.Services.AddSingleton<IBlobStorage>(sp => new RegisteredBlobStorage(
    new MinIOBlobStorage(sp.GetRequiredService<IMinioClient>(), sp.GetRequiredService<BlobStorageOptions>()),
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<ILogger<RegisteredBlobStorage>>()));
builder.Services.AddScoped<BHS.CRG.Infrastructure.Maintenance.BlobRegistryBackfill>();

// ── Plugins ───────────────────────────────────────────────────────────────────
var pluginOpts = cfg.GetSection("Plugins").Get<PluginHostOptions>() ?? new();
builder.Services.AddSingleton(pluginOpts);
builder.Services.AddSingleton<IPluginHost, PluginHost>();

// ── CORS ──────────────────────────────────────────────────────────────────────
builder.Services.AddCors(opt => opt.AddDefaultPolicy(p =>
    p.WithOrigins(cfg["AllowedOrigins"]?.Split(',') ?? ["http://localhost:5173"])
     .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.Services.AddOpenApi();

var app = builder.Build();

// ExcelDataReader требует регистрации кодировок для .xls файлов
System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();

    // Встроенные профили распознавания (issue #406) — идемпотентно; правленые пользователем не трогает.
    await BHS.CRG.Infrastructure.Recognition.RecognitionProfileSeeder.SeedAsync(db);

    // Секреты интеграций до 0.92.0 лежали в БД открытым текстом. Перешифровываем оставшиеся —
    // идемпотентно: уже зашифрованные пропускаются, второй прогон работы не находит.
    var protectedCount = await scope.ServiceProvider
        .GetRequiredService<IntegrationSettingsService>().ProtectStoredSecretsAsync();
    if (protectedCount > 0)
        app.Logger.LogInformation("Секретов настроек интеграций зашифровано при старте: {Count}", protectedCount);

    // Разовый перенос размеров изображений из схем типов в значения инстансов (issue #246).
    // Идемпотентно: после первого прогона схемы очищены, карта пустеет — обход не запускается.
    await BHS.CRG.Infrastructure.DataFixups.ImageSizeToInstanceFixup.RunAsync(db);

    // Зависшие фоновые задачи (in-process очередь потеряна при рестарте) — помечаем Failed, чтобы
    // индикатор не «висел» вечно (тот же приём восстановления, что и сид ролей ниже).
    var stuckJobs = await db.Jobs.Where(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running).ToListAsync();
    foreach (var job in stuckJobs) job.MarkAbandoned();
    if (stuckJobs.Count > 0) await db.SaveChangesAsync();

    // ── Роли + миграция существующих пользователей ──────────────────────────────
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
    foreach (var role in new[] { "Admin", "User" })
        if (!await roleManager.RoleExistsAsync(role))
            await roleManager.CreateAsync(new IdentityRole<Guid>(role));

    // Существующие аккаунты без роли получают Admin (раньше у всех был полный доступ).
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    foreach (var u in userManager.Users.ToList())
        if ((await userManager.GetRolesAsync(u)).Count == 0)
            await userManager.AddToRoleAsync(u, "Admin");

    // Прогрев плагинов: HTTP-плагины отдают схемы только по запросу (GET /schemas) — best-effort.
    await scope.ServiceProvider.GetRequiredService<IPluginHost>().WarmUpAsync();
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

// Первым в конвейере: дальше адрес клиента читают и лимитер, и логи.
app.UseForwardedHeaders();

app.UseExceptionHandler(exApp => exApp.Run(async ctx =>
{
    var feature = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    var ex = feature?.Error;
    ctx.Response.ContentType = "application/json";

    // Само правило — в ApiErrorMapping: оно проверяется тестами, а здесь только конвейер (issue #691).
    var (status, message) = ApiErrorMapping.Describe(ex, ctx.TraceIdentifier);
    ctx.Response.StatusCode = status;

    // В лог уходит именно то, чего не увидел клиент: по идентификатору запроса администратор
    // находит запись целиком. Доменные отказы не логируем как ошибки — это штатный ответ.
    if (status == StatusCodes.Status500InternalServerError)
        ctx.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("BHS.CRG.UnhandledException")
            .LogError(ex, "Необработанное исключение, запрос {TraceId}", ctx.TraceIdentifier);

    await ctx.Response.WriteAsJsonAsync(new { error = message });
}));

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapAttachmentEndpoints();
app.MapAuthEndpoints();
app.MapAccountEndpoints();
app.MapUserEndpoints();
app.MapBackupEndpoints();
app.MapMaintenanceEndpoints();
app.MapCatalogEndpoints();
app.MapPrimitiveTypeEndpoints();
app.MapEnumTypeEndpoints();
app.MapRecognitionProfileEndpoints();

// MCP-эндпоинт (issue #415) — под той же аутентификацией, что и REST: агент действует ОТ ИМЕНИ
// пользователя своим JWT, поэтому отдельной модели доступа не заводим.
app.MapMcp("/mcp").RequireAuthorization();
app.MapDocumentTypeEndpoints();
app.MapCommonDataEndpoints();
app.MapTemplateEndpoints();
app.MapTemplateAssetEndpoints();
app.MapTypstUserLibEndpoints();
app.MapPrintFormEndpoints();
app.MapDocumentSetEndpoints();
app.MapGenerationEndpoints();
app.MapDataSetEndpoints();
app.MapDataSetBindingEndpoints();
app.MapDataSetBindingTemplateEndpoints();
app.MapQualityDocEndpoints();
app.MapReconciliationEndpoints();
app.MapObservationEndpoints();
app.MapObjectResolveEndpoints();
app.MapSettingsEndpoints();
app.MapEmailEndpoints();
app.MapSubscriptionEndpoints();

// Версия приложения (для отображения в UI и трассировки сборок). Анонимно — виден и на странице входа.
// Сам номер версии анонимен намеренно, а git-хеш и дата сборки — нет: репозиторий публичный, и по
// хешу сборка сопоставляется с историей до конкретного коммита. Вошедшему пользователю они видны.
app.MapGet("/api/version", (HttpContext ctx) =>
{
    var asm = System.Reflection.Assembly.GetEntryAssembly()!;
    var info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0";
    var parts = info.Split('+', 2); // "0.1.0+<sha>"
    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Ok(new { version = parts[0], commit = "", buildDate = (DateTimeOffset?)null });

    var commit = parts.Length > 1 ? parts[1][..Math.Min(7, parts[1].Length)] : ""; // короткий sha для показа
    DateTimeOffset? buildDate = null;
    try { buildDate = File.GetLastWriteTimeUtc(asm.Location); } catch { /* single-file/unknown */ }
    return Results.Ok(new { version = parts[0], commit, buildDate });
}).AllowAnonymous();
app.MapNotificationsEndpoints();
app.MapJobsEndpoints();
app.MapTagsEndpoints();

app.Run();

// Needed for WebApplicationFactory<Program> in integration tests
public partial class Program { }
