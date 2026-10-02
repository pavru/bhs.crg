using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Хост для проверки портов ядра (задача M2 этапа 2, issue #1069): оба модуля включены, база своя, и
/// в нём зарегистрирован обработчик фоновой операции — так же, как его регистрирует модуль.
///
/// <para>Наследуется от <see cref="IntegrationTestFixture" />, чтобы не разойтись с ним в том, чем
/// тестовый хост держится (подставное хранилище, снятые расписания, ослабленные пределы частоты);
/// переопределения добавляются ПОСЛЕ базовых — последний слой конфигурации выигрывает.</para>
///
/// <para>⚠️ Своя база, как и у <see cref="CostsOnlyHost" />: состав системных ролей приводится при
/// старте к объявленному, и хост с другим набором модулей менял бы права ролям у соседних классов —
/// а падали бы они, и причину искали бы у них.</para>
/// </summary>
public sealed class ModulePortsHost : IntegrationTestFixture
{
    private static string ConnectionString { get; } = Dedicated();

    private static string Dedicated()
    {
        var builder = new NpgsqlConnectionStringBuilder(TestConnectionString);
        builder.Database += "_ports";
        return builder.ConnectionString;
    }

    protected override string HostConnectionString => ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        var overrides = new Dictionary<string, string?>
        {
            // Оба модуля: порт состава поставки проверяется двумя кодами, а журнал — префиксом
            // модуля, которого в умолчательной сборке нет.
            ["Modules:Enabled"] = "id,costs",
            ["ConnectionStrings:Postgres"] = ConnectionString,
        };

        foreach (var (key, value) in overrides) builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(overrides));

        // Обработчик фоновой операции и объявление действий журнала — ровно те регистрации, которые
        // делает модуль в своём RegisterServices. Подделки в переходниках при этом нет: очередь,
        // цикл, поиск исполнителя и каталог действий работают настоящие, поддельны только сама работа
        // и её объявление.
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<ProbeModuleWork>();
            services.AddSingleton<IModuleJobHandler>(sp => sp.GetRequiredService<ProbeModuleWork>());
            services.AddSingleton<IModuleActivityActions, ProbeModuleActivity>();
        });
    }
}

/// <summary>
/// Объявление действий журнала, как его делает модуль. Одно действие — больше для проверки пути не
/// нужно: названием оно отличается от кода, и именно название обязано доехать до экрана.
/// </summary>
public sealed class ProbeModuleActivity : IModuleActivityActions
{
    public static readonly ModuleActivityAction InvoicePaid =
        new("costs.invoice.paid", "Счёт отмечен оплаченным");

    public IReadOnlyList<ModuleActivityAction> Actions => [InvoicePaid];
}

/// <summary>
/// Поддельная фоновая работа модуля: докладывает ход и запоминает, что ей досталось.
///
/// Одиночка, потому что тест обязан дождаться её ВНЕ области запроса: работа выполняется в своей
/// области служб, созданной фоновым циклом, и до неё у теста доступа нет.
/// </summary>
public sealed class ProbeModuleWork : IModuleJobHandler
{
    /// <summary>Код операции. С префиксом модуля — так его напишет и настоящий модуль.</summary>
    public const string Code = "costs.проба";

    private readonly TaskCompletionSource<ModuleJobRun> _ran =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Operation => Code;

    public async Task RunAsync(ModuleJobRun run, CancellationToken ct)
    {
        // Доклад с последним шагом ядро пишет всегда (прореживание по времени пропускает только
        // промежуточные), поэтому итоговый ход задачи предсказуем.
        await run.Report("строк", 3, 3);
        _ran.TrySetResult(run);
    }

    /// <summary>
    /// Дождаться выполнения. Предел с запасом — работа мгновенная, но между постановкой и ею лежит
    /// фоновый цикл; не дождались — падаем словами, а не молчаливым таймаутом теста.
    /// </summary>
    public async Task<ModuleJobRun> WaitAsync(TimeSpan timeout)
    {
        var done = await Task.WhenAny(_ran.Task, Task.Delay(timeout));

        Assert.True(done == _ran.Task,
            $"Фоновая работа модуля не дошла до обработчика за {timeout.TotalSeconds:0} с. " +
            "Значит, задача осталась в очереди: вид задачи, аргументы с кодом операции или поиск " +
            "исполнителя разошлись между постановкой и фоновым циклом.");

        return await _ran.Task;
    }
}
