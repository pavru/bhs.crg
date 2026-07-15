using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Endpoints.Account;
using BHS.CRG.Api.Endpoints.Attachments;
using BHS.CRG.Api.Endpoints.Auth;
using BHS.CRG.Api.Endpoints.Backup;
using BHS.CRG.Api.Endpoints.Catalog;
using BHS.CRG.Api.Endpoints.DataSets;
using BHS.CRG.Api.Endpoints.Documents;
using BHS.CRG.Api.Endpoints.Email;
using BHS.CRG.Api.Endpoints.Subscriptions;
using BHS.CRG.Api.Endpoints.Generation;
using BHS.CRG.Api.Endpoints.QualityDocs;
using BHS.CRG.Api.Endpoints.Templates;
using BHS.CRG.Api.Hubs;
using BHS.CRG.Application.Catalog;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Templates;
using BHS.CRG.Infrastructure.Templates;
using BHS.CRG.Application.QualityDocs;
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
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Plugins;
using BHS.CRG.Infrastructure.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Minio;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

builder.Services.ConfigureHttpJsonOptions(opt =>
    opt.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// ── Database ──────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseNpgsql(cfg.GetConnectionString("Postgres")));

// ── Identity ──────────────────────────────────────────────────────────────────
builder.Services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(opt =>
    {
        opt.Password.RequireDigit = false;
        opt.Password.RequireNonAlphanumeric = false;
        opt.Password.RequiredLength = 6;
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

// Rate limiting для чувствительных анонимных эндпоинтов сброса пароля (по IP).
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(15), QueueLimit = 0 }));
});

// ── JWT ───────────────────────────────────────────────────────────────────────
var jwtSection = cfg.GetSection("Jwt");
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
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtSection["Key"]!)),
            RoleClaimType = "role",
        };
        opt.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) &&
                    ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    ctx.Token = token;
                return Task.CompletedTask;
            },
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
builder.Services.AddScoped<IRepository<PrimitiveType>, Repository<PrimitiveType>>();
builder.Services.AddScoped<IRepository<EnumType>, Repository<EnumType>>();
builder.Services.AddScoped<IRepository<DocumentType>, Repository<DocumentType>>();
builder.Services.AddScoped<IRepository<Construction>, ConstructionRepository>();
builder.Services.AddScoped<IRepository<Section>, Repository<Section>>();
builder.Services.AddScoped<IRepository<DocumentSet>, DocumentSetRepository>();
builder.Services.AddScoped<DomainObjectRepository>();
builder.Services.AddScoped<IRepository<BHS.CRG.Domain.Objects.DomainObject>>(sp => sp.GetRequiredService<DomainObjectRepository>());
builder.Services.AddScoped<IDomainObjectRepository>(sp => sp.GetRequiredService<DomainObjectRepository>());
builder.Services.AddScoped<IRepository<Template>, Repository<Template>>();
builder.Services.AddScoped<IRepository<TemplateAsset>, Repository<TemplateAsset>>();
builder.Services.AddScoped<IRepository<GeneratedFile>, Repository<GeneratedFile>>();
builder.Services.AddScoped<IRepository<DocumentSetOutput>, Repository<DocumentSetOutput>>();
builder.Services.AddScoped<IRepository<TypstUserLib>, Repository<TypstUserLib>>();
builder.Services.AddScoped<IRepository<QualityDocument>, Repository<QualityDocument>>();
builder.Services.AddScoped<IRepository<MaterialQualityLink>, Repository<MaterialQualityLink>>();

// ── Backup ────────────────────────────────────────────────────────────────────
builder.Services.AddScoped<BackupService>();

// ── Generation ────────────────────────────────────────────────────────────────
builder.Services.AddScoped<IEntityResolver, EntityResolver>();
builder.Services.AddScoped<IMetadataExtractor, MetadataExtractor>();
builder.Services.AddScoped<IDataSetResolver, DataSetResolver>();
builder.Services.AddScoped<IQualityLinkResolver, QualityLinkResolver>();
builder.Services.AddScoped<ITemplateAssetResolver, TemplateAssetResolver>();
builder.Services.AddHttpClient<AnthropicRecognizerEngine>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(2));
builder.Services.AddHttpClient<GeminiRecognizerEngine>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(2));
builder.Services.AddHttpClient<OllamaRecognizerEngine>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(3));
builder.Services.AddScoped<IRecognizerEngine>(sp => sp.GetRequiredService<AnthropicRecognizerEngine>());
builder.Services.AddScoped<IRecognizerEngine>(sp => sp.GetRequiredService<GeminiRecognizerEngine>());
builder.Services.AddScoped<IRecognizerEngine>(sp => sp.GetRequiredService<OllamaRecognizerEngine>());
builder.Services.AddScoped<IDocumentRecognizer, ChainDocumentRecognizer>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IIntegrationSettings, IntegrationSettingsService>();
builder.Services.AddScoped<BHS.CRG.Application.Email.IEmailSender, BHS.CRG.Infrastructure.Email.MailKitEmailSender>();
builder.Services.AddScoped<BHS.CRG.Infrastructure.Email.AccountEmailService>();

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
builder.Services.AddHttpClient<TieredWebSearch>().ConfigureHttpClient(c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
});
builder.Services.AddScoped<IQualityDocSearch>(sp => sp.GetRequiredService<TieredWebSearch>());
builder.Services.AddHttpClient<IFileUrlFetcher, HttpFileUrlFetcher>()
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton<TypstGenerator>();
builder.Services.AddSingleton<IDocumentGeneratorFactory, DocumentGeneratorFactory>();

// ── DataSets ──────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<IDataSetParser, CsvDataSetParser>();
builder.Services.AddSingleton<IDataSetParser, XlsxDataSetParser>();
builder.Services.AddSingleton<IDataSetParser, XmlDataSetParser>();
builder.Services.AddSingleton<IDataSetParser, JsonDataSetParser>();
builder.Services.AddSingleton<IDataSetParser, ZipDataSetParser>();
builder.Services.AddSingleton<IDataSetParser, PdfDataSetParser>();
builder.Services.AddSingleton<DataSetParserFactory>();
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
builder.Services.AddSingleton<IBlobStorage, MinIOBlobStorage>();

// ── Plugins ───────────────────────────────────────────────────────────────────
var pluginOpts = cfg.GetSection("Plugins").Get<PluginHostOptions>() ?? new();
builder.Services.AddSingleton(pluginOpts);
builder.Services.AddSingleton<IPluginHost, PluginHost>();

// ── SignalR + CORS ────────────────────────────────────────────────────────────
builder.Services.AddSignalR();
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

app.UseExceptionHandler(exApp => exApp.Run(async ctx =>
{
    var feature = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    var ex = feature?.Error;
    ctx.Response.ContentType = "application/json";
    ctx.Response.StatusCode = ex switch
    {
        KeyNotFoundException => 404,
        UnauthorizedAccessException => 403,
        ArgumentException => 400,
        _ => 500,
    };
    await ctx.Response.WriteAsJsonAsync(new { error = ex?.Message ?? "Internal server error" });
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
app.MapCatalogEndpoints();
app.MapPrimitiveTypeEndpoints();
app.MapEnumTypeEndpoints();
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
app.MapSettingsEndpoints();
app.MapEmailEndpoints();
app.MapSubscriptionEndpoints();

// Версия приложения (для отображения в UI и трассировки сборок). Анонимно — виден и на странице входа.
app.MapGet("/api/version", () =>
{
    var asm = System.Reflection.Assembly.GetEntryAssembly()!;
    var info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0";
    var parts = info.Split('+', 2); // "0.1.0+<sha>"
    var commit = parts.Length > 1 ? parts[1][..Math.Min(7, parts[1].Length)] : ""; // короткий sha для показа
    DateTimeOffset? buildDate = null;
    try { buildDate = File.GetLastWriteTimeUtc(asm.Location); } catch { /* single-file/unknown */ }
    return Results.Ok(new { version = parts[0], commit, buildDate });
}).AllowAnonymous();
app.MapNotificationsEndpoints();
app.MapJobsEndpoints();
app.MapTagsEndpoints();
app.MapHub<GenerationHub>("/hubs/generation");

app.Run();

// Needed for WebApplicationFactory<Program> in integration tests
public partial class Program { }
