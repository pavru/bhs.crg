using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Updates;
using BHS.CRG.Application.Updates;
using BHS.CRG.Infrastructure.Updates;
using BHS.CRG.Api.Modules;
using BHS.CRG.Modules;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Application.Activity;
using BHS.CRG.Api.Notifications;
using BHS.CRG.Application.Jobs;
using BHS.CRG.Infrastructure.Jobs;
using BHS.CRG.Infrastructure.Notifications;
using BHS.CRG.Infrastructure.Search;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Generation;
using BHS.CRG.Infrastructure.Http;
using BHS.CRG.Infrastructure.Plugins;
using BHS.CRG.Infrastructure.Storage;
using Minio;
using static BHS.CRG.Api.Configuration.OutboundClients;

namespace BHS.CRG.Api.Configuration;

/// <summary>
/// Корень композиции, часть 3 — фоновая работа и внешние хранилища: очередь задач, уведомления и
/// health-мониторинг, наборы данных, S3, плагины, CORS и модули (issue #1030).
/// </summary>
internal static class WorkerRegistration
{
    /// <summary>Очередь фоновых задач, уведомления, проверка обновлений и health-мониторинг.</summary>
    internal static void AddBackgroundWorkAndNotifications(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
    // ── Фоновые задачи (долгие операции: распознавание набора/таблицы) ──────────────
    builder.Services.AddSingleton<JobQueue>();
    builder.Services.AddScoped<IJobService, JobService>();
    // Запуск долгих операций вместе с их защитами — одно ядро для HTTP и MCP (issue #898).
    builder.Services.AddScoped<BHS.CRG.Application.Jobs.IOperationLauncher, OperationLauncher>();
    builder.Services.AddHostedService<JobBackgroundService>();

    // ── Notifications + health monitoring ───────────────────────────────────────────
    builder.Services.AddScoped<INotificationService, NotificationService>();
    // Проверка новых версий (issue #813): служба — singleton (её же зовёт кнопка «Проверить сейчас»),
    // чтение состояния — scoped, потому что ходит в базу.
    builder.Services.AddScoped<ServiceStateStore>();
    builder.Services.AddScoped<IUpdateCheck, UpdateCheckReader>();
    builder.Services.AddScoped<UpdateNotifier>();
    builder.Services.AddSingleton<UpdateCheckService>();
    RouteVia(builder.Services.AddHttpClient(UpdateCheckService.ClientName), OutboundService.UpdateCheck);
    foreach (var service in new[] { OutboundService.Gemini, OutboundService.Ollama })
        RouteVia(builder.Services.AddHttpClient(OutboundProxy.ClientName(HealthMonitorService.ClientPurpose, service)), service);
    builder.Services.AddSingleton<HealthMonitorService>();
    builder.Services.AddSingleton<IHealthState>(sp => sp.GetRequiredService<HealthMonitorService>());
    // Расписание проверки обновлений и мониторинга — выключаемое, по той же причине, что и плановое
    // копирование выше: под тестовым хостом обе службы пишут в базу по своему расписанию (уведомления,
    // service_state) и встречаются с TRUNCATE соседнего класса (issue #928). Сами службы остаются в
    // контейнере — кнопке «Проверить сейчас» и снимку состояния расписание не нужно. В поставке эти
    // переменные не задают.
    if (cfg.GetValue("Updates:CheckerEnabled", true))
        builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateCheckService>());
    if (cfg.GetValue("Health:MonitorEnabled", true))
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HealthMonitorService>());
    RouteVia(builder.Services.AddHttpClient<SerperEngine>().ConfigureHttpClient(c => c.Timeout = SerperEngine.Timeout), OutboundService.Serper);
    RouteVia(builder.Services.AddHttpClient<YandexEngine>().ConfigureHttpClient(c => c.Timeout = YandexEngine.Timeout), OutboundService.Yandex);
    builder.Services.AddScoped<IWebSearchEngine>(sp => sp.GetRequiredService<SerperEngine>());
    builder.Services.AddScoped<IWebSearchEngine>(sp => sp.GetRequiredService<YandexEngine>());
    // Автоследование за перенаправлениями выключено намеренно: переходы проходит SafeHttpGet, проверяя
    // цель каждого. С автоследованием проверка исходного адреса ничего не стоит — ответ общедоступного
    // хоста уводит куда угодно.
    builder.Services.AddHttpClient<TieredWebSearch>()
        .UseSocketsHttpHandler((h, sp) => OutboundAddressPolicy.ApplyGuard(h, sp.GetRequiredService<OutboundProxyState>()))
        .ConfigureHttpClient(c =>
    {
        c.Timeout = TimeSpan.FromSeconds(15);
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
    });
    builder.Services.AddScoped<IQualityDocSearch>(sp => sp.GetRequiredService<TieredWebSearch>());
    builder.Services.AddHttpClient<IFileUrlFetcher, HttpFileUrlFetcher>()
        .UseSocketsHttpHandler((h, sp) => OutboundAddressPolicy.ApplyGuard(h, sp.GetRequiredService<OutboundProxyState>()))
        .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(60));
    builder.Services.AddSingleton<TypstGenerator>();
    builder.Services.AddSingleton<IDocumentGeneratorFactory, DocumentGeneratorFactory>();
    builder.Services.AddSingleton<ITypstSyntaxChecker, TypstSyntaxChecker>();
    builder.Services.AddSingleton<BHS.CRG.Application.Templates.IUserLibChecker, UserLibChecker>();
    // Единая точка чтения библиотеки (issue #473) — раньше три места читали её каждое по-своему.
    builder.Services.AddScoped<BHS.CRG.Application.Templates.IUserLibProvider,
        BHS.CRG.Application.Templates.UserLibProvider>();
    }

    /// <summary>Наборы данных, блоб-хранилище и плагины.</summary>
    internal static void AddDataSetsStorageAndPlugins(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
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
    }

    /// <summary>
    /// CORS и состав модулей. Модули идут последними среди регистраций: их службы опираются на
    /// зарегистрированное выше.
    /// </summary>
    internal static void AddCorsAndModules(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
    // ── CORS ──────────────────────────────────────────────────────────────────────
    builder.Services.AddCors(opt => opt.AddDefaultPolicy(p =>
        p.WithOrigins(cfg["AllowedOrigins"]?.Split(',') ?? ["http://localhost:5173"])
         .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

    builder.Services.AddOpenApi();

    // ── Модули ────────────────────────────────────────────────────────────────────
    // Единственное место, где ядро знает имена модулей, — и это намеренно корень композиции, а не
    // сканер сборок рядом с приложением: набор модулей на экземпляре обязан быть решением поставки
    // (Modules__Enabled, AUTH-17), а не следствием того, какие DLL кто-то скопировал.
    builder.Services.AddAppModules(builder.Configuration, CorePermissions.All, new IdModule());
    // Реестр функциональных тэгов: ядро + тэги ВКЛЮЧЁННЫХ модулей (ТЗ TYPE-22, issue #959). Сразу за
    // регистрацией модулей — он собирается из их объявлений.
    builder.Services.AddTagCatalog();
    builder.Services.AddScoped<EffectivePermissions>();
    // Чем ворота модулей и прав отвечают на вопрос «что этому пользователю можно» (AUTH-6).
    builder.Services.AddSingleton<IUserPermissions, PermissionCache>();
    // Кому адресовано уведомление (AUTH-13): тем же правам, что и двери, — и считается это в одном
    // месте, а не перебором ролей у каждого издателя.
    builder.Services.AddSingleton<BHS.CRG.Application.Notifications.INotificationAudience,
        BHS.CRG.Api.Notifications.PermissionAudience>();
    // Журнал действий — одна служба на весь продукт (ТЗ CORE-28). Scoped: пишет через тот же контекст
    // базы, что и само действие, и живёт ровно столько же.
    builder.Services.AddScoped<IActivityLog, BHS.CRG.Infrastructure.Activity.ActivityLog>();
    // Редактор матрицы ролей (ТЗ AUTH-5): правит роли Identity и пишет в журнал — scoped, как и они.
    builder.Services.AddScoped<BHS.CRG.Api.Auth.RoleEditor>();
    builder.Services.AddSingleton<IActivityActor, BHS.CRG.Api.Activity.HttpContextActivityActor>();
    }
}
