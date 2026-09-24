using BHS.CRG.Application.Catalog;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Templates;
using BHS.CRG.Infrastructure.Templates;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Resolution;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Recognition;
using BHS.CRG.Infrastructure.Settings;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Generation;
using BHS.CRG.Infrastructure.Http;
using BHS.CRG.Infrastructure.Persistence;
using static BHS.CRG.Api.Configuration.OutboundClients;

namespace BHS.CRG.Api.Configuration;

/// <summary>
/// Корень композиции, часть 2 — предметные службы: MediatR, репозитории, копии и обслуживание,
/// прикладные службы генерации, распознавания, настроек и почты (issue #1030).
/// </summary>
internal static class DomainRegistration
{
    /// <summary>MediatR и репозитории доменных сущностей.</summary>
    internal static void AddMediatRAndRepositories(this WebApplicationBuilder builder)
    {
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
    // План по документам (issue #796): строки живут на комплекте, уровни выше считаются.
    builder.Services.AddScoped<IRepository<DocumentSetPlanItem>, Repository<DocumentSetPlanItem>>();
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
    }

    /// <summary>Сообщения об ошибках, резервные копии и задачи обслуживания.</summary>
    internal static void AddSupportBackupAndMaintenance(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
    // ── Сообщения об ошибках (issue #834) ────────────────────────────────────────
    builder.Services.AddScoped<BHS.CRG.Application.Support.IBugReportService,
        BHS.CRG.Infrastructure.Support.BugReportService>();
    RouteVia(builder.Services.AddHttpClient<BHS.CRG.Infrastructure.Support.GithubIssueClient>()
        .ConfigureHttpClient(c => c.Timeout = BHS.CRG.Infrastructure.Support.GithubIssueClient.Timeout), OutboundService.Github);

    // ── Backup ────────────────────────────────────────────────────────────────────
    builder.Services.AddScoped<BackupService>();
    builder.Services.AddSingleton<BHS.CRG.Infrastructure.Backup.BackupFileStore>();
    builder.Services.AddScoped<BHS.CRG.Infrastructure.Backup.BackupJobRunner>();
    // Плановое копирование (issue #832): служба ставит ту же задачу, что и кнопка в интерфейсе, —
    // одна дорога снятия копии на систему.
    //
    // Выключатель нужен ровно одному потребителю — тестовому хосту, который поднимает приложение
    // целиком, вместе с фоновыми службами. Расписание включено по умолчанию, и через две минуты
    // прогона служба сняла бы копию ТЕСТОВОЙ базы в каталог рядом с исходниками: гигабайты мусора,
    // чтение базы под чужим TRUNCATE и записи в notifications посреди чужого теста. В поставке эту
    // переменную не задают — расписанием управляет администратор из интерфейса.
    if (cfg.GetValue("Backup:SchedulerEnabled", true))
        builder.Services.AddHostedService<BHS.CRG.Infrastructure.Backup.BackupScheduleService>();
    builder.Services.AddScoped<BHS.CRG.Infrastructure.Maintenance.ImageBlobMigration>();
    builder.Services.AddScoped<BHS.CRG.Infrastructure.Maintenance.MaterialLabelBackfill>();
    builder.Services.AddScoped<BHS.CRG.Infrastructure.Maintenance.OrphanObjectCleanup>();
    builder.Services.AddScoped<BHS.CRG.Infrastructure.Maintenance.LiveBlobPathScan>();
    builder.Services.AddScoped<BHS.CRG.Infrastructure.Maintenance.OrphanBlobCleanup>();

    // ── Снимок данных для внешних потребителей + MCP (issue #415) ─────────────────
    builder.Services.AddScoped<BHS.CRG.Application.DataSnapshots.IDataSnapshotService,
        BHS.CRG.Infrastructure.DataSets.DataSnapshotService>();
    builder.Services.AddScoped<BHS.CRG.Application.DataSnapshots.IDomainSnapshotService,
        BHS.CRG.Infrastructure.Generation.DomainSnapshotService>();
    }

    /// <summary>
    /// Прикладные службы: разрешение сущностей, генерация, распознавание, настройки, почта.
    /// </summary>
    internal static void AddApplicationServices(this WebApplicationBuilder builder)
    {
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
        // Ворота инструментов (issue #948, ТЗ AUTH-12.1). Адрес /mcp один на все инструменты, и
        // потребовать на нём можно только «пользователь вошёл» — значит, права стоят ВНУТРИ, у каждого
        // инструмента (McpGates). Этот вызов включает разбор их политик: список собирается по правам
        // спрашивающего, а прямой вызов недоступного инструмента получает отказ, а не пустой результат.
        //
        // ⚠️ Забыть эту строку — не тихая беда, и это проверено её снятием: без неё сервер отказывается
        // перечислять инструменты ВООБЩЕ («An error occurred» на tools/list), то есть MCP перестаёт
        // работать целиком, а не открывается всем. Из двух исходов этот несравнимо лучше — потому и
        // записан: следующий, кто увидит здесь лишний вызов, не станет его убирать «на пробу».
        .AddAuthorizationFilters()
        // Кодировщик передаётся каждой регистрации: домен русскоязычный, а по умолчанию System.Text.Json
        // раздувает кириллицу в \uXXXX вчетверо и упирает ответы в лимит клиента (#576, McpSerialization).
        .WithTools<BHS.CRG.Api.Mcp.DataSnapshotTools>(BHS.CRG.Api.Mcp.McpSerialization.ToolOptions)
        .WithTools<BHS.CRG.Api.Mcp.DomainSnapshotTools>(BHS.CRG.Api.Mcp.McpSerialization.ToolOptions)
        .WithTools<BHS.CRG.Api.Mcp.DocumentActionTools>(BHS.CRG.Api.Mcp.McpSerialization.ToolOptions)
        .WithTools<BHS.CRG.Api.Mcp.ObservationTools>(BHS.CRG.Api.Mcp.McpSerialization.ToolOptions)
        .WithTools<BHS.CRG.Api.Mcp.ReconciliationTools>(BHS.CRG.Api.Mcp.McpSerialization.ToolOptions)
        .WithTools<BHS.CRG.Api.Mcp.JobTools>(BHS.CRG.Api.Mcp.McpSerialization.ToolOptions)
        .WithTools<BHS.CRG.Api.Mcp.OperationTools>(BHS.CRG.Api.Mcp.McpSerialization.ToolOptions)
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
    builder.Services.AddScoped<BHS.CRG.Application.Common.IScopeChildren, BHS.CRG.Application.Common.ScopeChildren>();
    builder.Services.AddScoped<BHS.CRG.Application.Objects.IScopeCascade, BHS.CRG.Application.Objects.ScopeCascade>();
    builder.Services.AddScoped<BHS.CRG.Application.Objects.IReferenceIndex,
        BHS.CRG.Infrastructure.Persistence.ReferenceIndex>();
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
    // Сроки ответа — константами на самих движках (issue #797): движок называет своё число
    // пользователю в сообщении о таймауте, и число из регистрации разъехалось бы с текстом.
    RouteVia(builder.Services.AddHttpClient<AnthropicRecognizerEngine>().ConfigureHttpClient(c => c.Timeout = AnthropicRecognizerEngine.Timeout), OutboundService.Anthropic);
    RouteVia(builder.Services.AddHttpClient<GeminiRecognizerEngine>().ConfigureHttpClient(c => c.Timeout = GeminiRecognizerEngine.Timeout), OutboundService.Gemini);
    RouteVia(builder.Services.AddHttpClient<OllamaRecognizerEngine>().ConfigureHttpClient(c => c.Timeout = OllamaRecognizerEngine.Timeout), OutboundService.Ollama);
    builder.Services.AddScoped<IRecognizerEngine>(sp => sp.GetRequiredService<AnthropicRecognizerEngine>());
    builder.Services.AddScoped<IRecognizerEngine>(sp => sp.GetRequiredService<GeminiRecognizerEngine>());
    builder.Services.AddScoped<IRecognizerEngine>(sp => sp.GetRequiredService<OllamaRecognizerEngine>());
    // Отбор движков — один на цепочку и на предполётную проверку (issue #801): разъехавшись, они дали
    // бы задачу, которую разрешили поставить и тут же отказались выполнять.
    builder.Services.AddScoped<RecognitionEngineSelector>();
    builder.Services.AddScoped<IDocumentRecognizer, ChainDocumentRecognizer>();
    builder.Services.AddScoped<BHS.CRG.Application.QualityDocs.IRecognitionPreflight, RecognitionPreflight>();
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton<SettingsSecretProtector>();
    builder.Services.AddScoped<IntegrationSettingsService>();
    builder.Services.AddScoped<IIntegrationSettings>(sp => sp.GetRequiredService<IntegrationSettingsService>());
    // Предпочтения пользователя на сервере (issue #953, ТЗ CORE-25.3): тема и язык переживают смену
    // компьютера, потому что лежат не в браузере.
    builder.Services.AddScoped<BHS.CRG.Application.Settings.IUserSettingsStore,
        BHS.CRG.Infrastructure.Settings.UserSettingsStore>();
    // Каталог моделей движков (issue #799). Сам он без состояния — кэш ответов живёт в IMemoryCache,
    // то есть переживает запрос, а HTTP-клиент берётся у фабрики, как у движков распознавания.
    // Клиентов у каталога три — по одному на движок: он спрашивает и Gemini, и Anthropic, и Ollama, а
    // прокси выбирается по сервису, не по адресу (issue #936).
    foreach (var service in new[] { OutboundService.Gemini, OutboundService.Anthropic, OutboundService.Ollama })
        RouteVia(builder.Services.AddHttpClient(OutboundProxy.ClientName(BHS.CRG.Infrastructure.Settings.RecognitionModelCatalog.ClientPurpose, service))
            .ConfigureHttpClient(c => c.Timeout = BHS.CRG.Infrastructure.Settings.RecognitionModelCatalog.Timeout), service);
    builder.Services.AddScoped<BHS.CRG.Infrastructure.Settings.RecognitionModelCatalog>();
    builder.Services.AddScoped<BHS.CRG.Application.Settings.IRecognitionModelCatalog>(
        sp => sp.GetRequiredService<BHS.CRG.Infrastructure.Settings.RecognitionModelCatalog>());
    builder.Services.AddScoped<BHS.CRG.Application.Email.IEmailSender, BHS.CRG.Infrastructure.Email.MailKitEmailSender>();
    builder.Services.AddScoped<BHS.CRG.Infrastructure.Email.AccountEmailService>();
    builder.Services.AddScoped<RefreshTokenService>();
    }
}
