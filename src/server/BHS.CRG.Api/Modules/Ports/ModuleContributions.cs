using System.Reflection;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Вклад одного поставщика: значение либо отказ с причиной для человека (<see cref="Refused" />). Отказ —
/// не пустота: экран пишет его на месте вклада, а не «данных нет» (ТЗ OVW-9).
/// </summary>
public sealed record ModuleContribution<R>(string Module, R? Value, string? Refused);

/// <summary>
/// Опрос поставщиков контракта со многими поставщиками — каждого в своей области служб и со своим пределом
/// времени (ТЗ CORE-34; общий помощник <c>shell-05</c> — поиск записей — и <c>shell-08</c> — очереди Главной,
/// соглашение владельца 12.11.2026).
///
/// <para><b>Своя область</b> — потому что поставщиков спрашивают одновременно, а контекст базы одновременных
/// запросов не терпит: два поставщика на одном <c>AppDbContext</c> (через порты ядра) уронили бы друг друга.
/// <b>Свой предел</b> — потому что медленный поставщик не держит ответ остальных: он отвечает отказом у
/// своего вклада. Исключение поставщика — тоже отказ у его вклада, а текст исключения — в журнал, не
/// человеку: в нём бывают имена таблиц и строки запросов.</para>
///
/// <para>Отмена запроса (человек ушёл) — не отказ: она доходит до вызывающего как
/// <see cref="OperationCanceledException" />.</para>
/// </summary>
public sealed class ModuleContributions(IServiceScopeFactory scopes, ILogger<ModuleContributions> log)
{
    /// <summary>Предел по умолчанию — один у поиска и у Главной: человек ждёт ответа экрана, а не модуля.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Каждый включённый поставщик контракта <typeparamref name="T" /> — в порядке состава поставки.
    /// <paramref name="ask" /> получает реализацию, читателя и токен, отменяемый пределом.
    /// </summary>
    public async Task<IReadOnlyList<ModuleContribution<R>>> AskEachAsync<T, R>(
        ModuleReader reader, TimeSpan timeout, Func<T, ModuleReader, CancellationToken, Task<R>> ask,
        CancellationToken ct) where T : class
    {
        var label = typeof(T).GetCustomAttribute<ModuleContractAttribute>()?.Title ?? typeof(T).Name;
        // Перепись поставщиков — в своей области: реализации областные, и взятая здесь не годится для
        // одновременного опроса. Каждый вклад ниже берёт СВОЮ реализацию в своей области.
        IReadOnlyList<string> modules;
        await using (var census = scopes.CreateAsyncScope())
            modules = [.. census.ServiceProvider.GetRequiredService<IModuleContracts>().GetAll<T>().Select(p => p.Module)];

        var asked = modules.Select(module => AskAsync(module, label, timeout, (services, token) =>
        {
            var contract = services.GetRequiredService<IModuleContracts>().GetAll<T>()
                .Single(p => string.Equals(p.Module, module, StringComparison.Ordinal)).Contract;
            return ask(contract, reader, token);
        }, ct));
        return await Task.WhenAll(asked);
    }

    /// <summary>
    /// Один вклад: <paramref name="ask" /> в своей области служб и в пределе <paramref name="timeout" />.
    /// <paramref name="label" /> — что спрашивали, для журнала.
    /// </summary>
    public async Task<ModuleContribution<R>> AskAsync<R>(
        string module, string label, TimeSpan timeout, Func<IServiceProvider, CancellationToken, Task<R>> ask,
        CancellationToken ct)
    {
        var scope = scopes.CreateAsyncScope();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        Task<R>? task = null;
        try
        {
            task = ask(scope.ServiceProvider, deadline.Token);
            // WaitAsync — на случай поставщика, который токен не слушает: предел держит ответ и тогда.
            return new(module, await task.WaitAsync(timeout, ct), null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
                                   && ex is TimeoutException or OperationCanceledException)
        {
            log.LogWarning("Модуль «{Module}» не ответил ({Label}) за {Limit} мс.",
                module, label, timeout.TotalMilliseconds);
            return new(module, default, $"нет ответа за {timeout.TotalSeconds:0.#} с — запрос можно повторить");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Модуль «{Module}» отказал ({Label}).", module, label);
            return new(module, default, "ошибка сервера — она записана в журнал");
        }
        finally
        {
            // Поставщик, не услышавший токена, ещё работает в этой области: убрать её можно только за ним.
            if (task is { IsCompleted: false }) _ = task.ContinueWith(_ => scope.Dispose(), TaskScheduler.Default);
            else await scope.DisposeAsync();
        }
    }
}
