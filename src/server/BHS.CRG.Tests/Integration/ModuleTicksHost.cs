using System.Threading.Channels;
using BHS.CRG.Api.Modules;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Tests.Configuration;
using BHS.CRG.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Хост для такта модульных заданий (work-08): модуль <c>costs</c>, своя база и пробный такт с его
/// обработчиком — так, как их объявит и зарегистрирует модуль. Каталог тактов подменён каталогом
/// пробного модуля: в поставке тактов пока нет (первый — автоподача, work-15).
///
/// <para>Исполнитель фоновых задач настоящий: задача такта проходит очередь, поиск исполнителя и
/// обработчик — иначе подпись журнала и параметр доступа проверялись бы мимо пути, которым идёт работа.</para>
///
/// <para>⚠️ Своя база по той же причине, что у <see cref="ModulePortsHost" />: состав модулей хоста иной,
/// а системные роли приводятся при старте к объявленному.</para>
/// </summary>
public sealed class ModuleTicksHost : ModuleOnlyHost
{
    protected override string ModuleCode => "costs";

    protected override string DatabaseSuffix => "ticks";

    /// <summary>
    /// Часы хоста: по ним и такт считает «сейчас», и очередь ставит <c>CreatedAt</c> задачи — одни часы
    /// на срок и на постановку, как в поставке. Тест двигает их сам.
    /// </summary>
    internal TestClock Clock { get; } = new(DateTimeOffset.UtcNow);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<ProbeTickGate>();
            services.AddScoped<IModuleJobHandler, ProbeTickWork>();
            services.AddSingleton<IModuleActivityActions, ProbeModuleActivity>();
            services.AddSingleton(ProbeTick.Catalog());
            services.AddSingleton<TimeProvider>(Clock);
        });
    }
}

/// <summary>Пробный такт модуля <c>costs</c>.</summary>
internal static class ProbeTick
{
    public const string Title = "Автоподача отчётов";

    public static readonly ModuleTick Tick = new("probe", Title, ProbeTickWork.Code, TimeSpan.FromMinutes(5));

    public static TickingModule Module() => new("costs", Tick);

    public static ModuleTickCatalog Catalog() => new([Module()]);

    public static Guid Target => ModuleTickCatalog.TargetOf("costs", Tick.Code);
}

/// <summary>
/// Что видел обработчик и задвижка, которой тест держит задачу «идущей». Одиночка: работа выполняется в
/// области фонового цикла, до которой у теста доступа нет.
/// </summary>
public sealed class ProbeTickGate
{
    private volatile TaskCompletionSource _open = Opened();

    public Channel<(ModuleJobRun Run, ModuleReader Reader)> Ran { get; private set; } =
        Channel.CreateUnbounded<(ModuleJobRun, ModuleReader)>();

    /// <summary>Отказ, которым упадёт следующая задача; <c>null</c> — не падать.</summary>
    public string? FailWith { get; set; }

    public Task Open => _open.Task;

    /// <summary>Следующие задачи ждут <see cref="Release" />.</summary>
    public void Hold() => _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _open.TrySetResult();

    /// <summary>Перед тестом: отпустить всё и забыть прежние выполнения.</summary>
    public void Reset()
    {
        Release();
        FailWith = null;
        Ran = Channel.CreateUnbounded<(ModuleJobRun, ModuleReader)>();
    }

    public async Task<(ModuleJobRun Run, ModuleReader Reader)> NextAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { return await Ran.Reader.ReadAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            throw new Xunit.Sdk.XunitException(
                "Задача такта не дошла до обработчика за 30 с: очередь, поиск исполнителя или постановка разошлись.");
        }
    }

    private static TaskCompletionSource Opened()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }
}

/// <summary>
/// Обработчик пробного такта — как его напишет модуль: параметр доступа берёт портом, пишет в журнал
/// портом. Scoped, как обработчики настоящих модулей.
/// </summary>
public sealed class ProbeTickWork(IModuleReaders readers, IModuleActivityLog journal, ProbeTickGate gate)
    : IModuleJobHandler
{
    public const string Code = "costs.такт-проба";

    public string Operation => Code;

    public async Task RunAsync(ModuleJobRun run, CancellationToken ct)
    {
        var reader = await readers.ForJobAsync(run, ct);
        await journal.RecordAsync(ProbeModuleActivity.InvoicePaid, targetId: run.JobId.ToString(),
            targetLabel: "Проба такта", ct: ct);
        gate.Ran.Writer.TryWrite((run, reader));

        await gate.Open.WaitAsync(ct);
        if (gate.FailWith is { } text) throw new BHS.CRG.Domain.Common.InvalidRequestException(text);
    }
}
