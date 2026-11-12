using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Общий помощник опроса поставщиков (<see cref="ModuleContributions" />; shell-05 и shell-08): каждый
/// поставщик — в своей области служб, медленный и упавший отвечают отказом у своего вклада и не держат
/// остальных, текст исключения человеку не уходит, отмена запроса доходит до вызывающего.
/// </summary>
public class ModuleContributionsTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);
    private static readonly ModuleReader Reader = ModuleReader.Of(Guid.NewGuid(), "probe", []);

    public interface IProbeQueue
    {
        Task<Guid> AskAsync(CancellationToken ct);
    }

    /// <summary>Метка области служб: у двух поставщиков из одной области она одна.</summary>
    private sealed class ScopeMark
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    private sealed class Fast(ScopeMark mark) : IProbeQueue
    {
        public Task<Guid> AskAsync(CancellationToken ct) => Task.FromResult(mark.Id);
    }

    /// <summary>Поставщик, который токена не слушает: предел обязан держать ответ и тогда.</summary>
    private sealed class Deaf : IProbeQueue
    {
        public Task<Guid> AskAsync(CancellationToken ct) => new TaskCompletionSource<Guid>().Task;
    }

    private sealed class Broken : IProbeQueue
    {
        public Task<Guid> AskAsync(CancellationToken ct) =>
            throw new InvalidOperationException("relation \"probe_secret\" does not exist");
    }

    /// <summary>Порт контрактов на пробных поставщиках: реализации — областные, как настоящие.</summary>
    private sealed class ProbeContracts(ScopeMark mark) : IModuleContracts
    {
        public ModuleContractAnswer<T> Get<T>() where T : class => throw new NotSupportedException();

        public IReadOnlyList<ProvidedContract<T>> GetAll<T>() where T : class =>
        [
            new("fast1", (T)(object)new Fast(mark)),
            new("deaf", (T)(object)new Deaf()),
            new("broken", (T)(object)new Broken()),
            new("fast2", (T)(object)new Fast(mark)),
        ];
    }

    private static ModuleContributions Contributions()
    {
        var services = new ServiceCollection()
            .AddScoped<ScopeMark>()
            .AddScoped<IModuleContracts, ProbeContracts>()
            .BuildServiceProvider();
        return new ModuleContributions(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ModuleContributions>.Instance);
    }

    [Fact]
    public async Task Каждый_поставщик_в_своей_области_и_отказ_у_своего_вклада()
    {
        var answers = await Contributions().AskEachAsync<IProbeQueue, Guid>(
            Reader, Short, (q, _, ct) => q.AskAsync(ct), CancellationToken.None);

        Assert.Equal(["fast1", "deaf", "broken", "fast2"], answers.Select(a => a.Module));
        var (fast1, deaf, broken, fast2) = (answers[0], answers[1], answers[2], answers[3]);

        Assert.Null(fast1.Refused);
        Assert.Null(fast2.Refused);
        // Одна область на двоих дала бы одну метку — и один контекст базы на два одновременных запроса.
        Assert.NotEqual(fast1.Value, fast2.Value);

        Assert.Contains("нет ответа за 0", deaf.Refused);
        Assert.Contains("ошибка сервера", broken.Refused);
        Assert.DoesNotContain("probe_secret", broken.Refused);
    }

    [Fact]
    public async Task Отмена_запроса_не_отказ_а_отмена()
    {
        using var gone = new CancellationTokenSource();
        await gone.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Contributions().AskEachAsync<IProbeQueue, Guid>(
            Reader, TimeSpan.FromSeconds(5), (q, _, ct) => q.AskAsync(ct), gone.Token));
    }

    // Предел вышел — токен поставщика отменён, а не дожит до естественного конца: иначе слушающий токен
    // запрос EF держал бы область и соединение пула (ревью #1913: WaitAsync может опередить CancelAfter).
    [Fact]
    public async Task По_пределу_токен_поставщика_отменяется()
    {
        var heard = new TaskCompletionSource();
        var answer = await Contributions().AskAsync<Guid>("listening", "проба", Short, async (_, token) =>
        {
            // Отмену отмечает сам поставщик, а не token.Register: обработчики зовутся в обратном порядке, и
            // первым шёл бы Task.Delay — его продолжение, исполнившись на том же потоке, снимало бы
            // регистрацию `heard` раньше её вызова (тест мигал на CI).
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                heard.TrySetResult();
                throw;
            }
            return Guid.Empty;
        }, CancellationToken.None);

        Assert.Contains("нет ответа", answer.Refused);
        await heard.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Обработчики отмены — поставщика (Npgsql шлёт по ним CancelRequest по сети): отказ по пределу их не
    // ждёт, иначе «нет ответа за 3 с» приходил бы позже трёх секунд (ревью #1915).
    [Fact]
    public async Task Отказ_по_пределу_не_ждёт_обработчиков_отмены()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var answer = await Contributions().AskAsync<Guid>("slow-cancel", "проба", Short, (_, token) =>
        {
            token.Register(() => Thread.Sleep(TimeSpan.FromSeconds(3)));
            return new TaskCompletionSource<Guid>().Task;
        }, CancellationToken.None);

        Assert.Contains("нет ответа", answer.Refused);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"ответ ждал {watch.Elapsed}");
    }

    // Своя отмена поставщика в пределе (HTTP-клиент плагина сдался через 100 мс) — его ошибка, а не наш
    // таймаут: человеку «ошибка сервера», причина — в журнал.
    [Fact]
    public async Task Своя_отмена_поставщика_в_пределе_ошибка_а_не_таймаут()
    {
        var answer = await Contributions().AskAsync<Guid>("plugin", "проба", TimeSpan.FromSeconds(5),
            (_, _) => throw new TaskCanceledException("HttpClient.Timeout"), CancellationToken.None);

        Assert.Contains("ошибка сервера", answer.Refused);
    }

    // Человек ушёл, а поставщик в ту же минуту упал своей ошибкой: это отмена, а не отказ модуля.
    [Fact]
    public async Task Ошибка_поставщика_при_отменённом_запросе_отмена()
    {
        using var gone = new CancellationTokenSource();
        await gone.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Contributions().AskAsync<Guid>(
            "broken", "проба", TimeSpan.FromSeconds(5),
            (_, _) => throw new InvalidOperationException("npgsql"), gone.Token));
    }
}
