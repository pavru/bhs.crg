using BHS.CRG.Api.Configuration;

// Прокси по умолчанию для процесса — «никакого» (issue #936). Иначе .NET на Linux сам берёт
// HTTP(S)_PROXY из окружения, и туда ушли бы все клиенты, не спросив галок: SDK хранилища со своим
// HttpClient к garage:3900, Ollama рядом, плагины. Прокси внешних сервисов задаётся в настройках и
// действует только у сервиса с галкой (OutboundProxy). Ставится ДО всего, что может создать клиента.
HttpClient.DefaultProxy = new System.Net.WebProxy();

var builder = WebApplication.CreateBuilder(args);

// ── Корень композиции (issue #1030) ───────────────────────────────────────────
// Ниже — та же последовательность, что была здесь операторами: 887 строк разошлись по методам в
// Configuration/, а порядок вызовов остался буквально прежним. ⚠️ Он значим: умолчания исходящих
// клиентов ставятся до любого AddHttpClient, проверка конфигурации — до тех, кто ею пользуется,
// модули — последними среди регистраций. Переставить строки ниже нельзя, даже если кажется, что
// они независимы.
builder.AddProcessDefaults();
builder.AddPersistenceAndIdentity();
builder.AddForwardedHeadersAndRateLimiting();
builder.AddJwtAuth();
builder.AddMediatRAndRepositories();
builder.AddSupportBackupAndMaintenance();
builder.AddApplicationServices();
builder.AddBackgroundWorkAndNotifications();
builder.AddDataSetsStorageAndPlugins();
builder.AddCorsAndModules();

var app = builder.Build();

// Сначала работа при старте (миграции, реестр тэгов, первичные данные) — приложение обязано упасть
// здесь, а не на первом запросе. Затем конвейер, затем маршруты.
await app.RunStartupTasksAsync();
app.UseAppPipeline();
app.MapAppEndpoints();

app.Run();

// Needed for WebApplicationFactory<Program> in integration tests
public partial class Program { }
