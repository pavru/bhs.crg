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
}
