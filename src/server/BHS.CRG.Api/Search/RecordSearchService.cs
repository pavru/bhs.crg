using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Search;

/// <summary>
/// Ответ одной группы: найденное либо отказ с причиной (<see cref="Refused" />). Группы, закрытой правами,
/// в ответе нет вовсе — ни записями, ни числом (ТЗ AUTH-16.1).
/// </summary>
public sealed record RecordSearchGroupAnswer(
    string Code, string Title, string Module, string RecordType,
    IReadOnlyList<RecordSearchHit> Items, bool More, string? Refused)
{
    public static RecordSearchGroupAnswer Found(RecordSearchSource s, IReadOnlyList<RecordSearchHit> items, bool more) =>
        new(s.Group.Code, s.Group.Title, s.Module, s.Group.RecordType, items, more, null);

    public static RecordSearchGroupAnswer Failed(RecordSearchSource s, string reason) =>
        new(s.Group.Code, s.Group.Title, s.Module, s.Group.RecordType, [], false, reason);
}

/// <summary>Запись, названная типом и идентификатором, — так её помнят «Недавние» (<c>shell-08</c>).</summary>
public sealed record RecordRef(string RecordType, Guid Id);

/// <summary>
/// Поиск записей по всем модулям в правах спрашивающего (ТЗ AUTH-16.1, CORE-34; shell-05, задачи 1.1, 1.5).
///
/// <para><b>Каждая группа — в своей области служб и со своим пределом времени.</b> Своя область — потому
/// что группы спрашиваются одновременно, а контекст базы одновременных запросов не терпит: две группы ядра
/// на одном <c>AppDbContext</c> уронили бы друг друга. Свой предел — потому что медленный поставщик не
/// держит ответ остальных: он отвечает отказом у своей группы (<see cref="RecordSearchLimits.PerGroup" />).
/// Отказ одной группы — исключение, таймаут — не роняет остальные (сценарий «Отказ одной группы»).</para>
///
/// <para><b>Права — дважды.</b> Группу, на ключ которой права нет, служба не спрашивает вовсе; ответ
/// поставщика «скрыто правами» тоже убирает группу. Первый пояс держит забывчивого поставщика, второй —
/// изоляцию строк, которую поставщик знает, а ядро нет (<c>work-07</c>).</para>
/// </summary>
public sealed class RecordSearchService(
    RecordSearchCatalog catalog, IServiceScopeFactory scopes, IModuleReaders readers, RecordSearchLimits limits,
    ILogger<RecordSearchService> log)
{
    /// <summary>Группы по словам запроса; <paramref name="only" /> — одна группа (Tab, чип области).</summary>
    public async Task<IReadOnlyList<RecordSearchGroupAnswer>> SearchAsync(
        IReadOnlyList<string> words, string? only, CancellationToken ct)
    {
        var reader = await readers.ForRequestAsync(ct);
        var query = new RecordSearchQuery(words, limits.Take);
        var asked = catalog.Sources
            .Where(s => only is null || string.Equals(s.Group.Code, only, StringComparison.Ordinal))
            .Where(s => reader.Allows(s.Group.Requires))
            .Select(s => AskAsync(s, "поиск",
                (p, token) => p.SearchAsync(s.Group.Code, query, reader, token),
                hits => RecordSearchGroupAnswer.Found(s, hits.Items, hits.More), ct));
        return [.. (await Task.WhenAll(asked)).OfType<RecordSearchGroupAnswer>()];
    }

    /// <summary>
    /// Карточки записей по типу и идентификатору (<c>shell-08</c>, «Недавние»). Тип выключенного модуля и
    /// группа без права — пропускаются: записей таких групп в ответе нет.
    /// </summary>
    public async Task<IReadOnlyList<RecordSearchGroupAnswer>> CardsAsync(
        IReadOnlyCollection<RecordRef> records, CancellationToken ct)
    {
        var reader = await readers.ForRequestAsync(ct);
        var asked = records
            .GroupBy(r => r.RecordType, StringComparer.Ordinal)
            .Select(g => (Source: catalog.ForRecordType(g.Key), Ids: (IReadOnlyCollection<Guid>)[.. g.Select(r => r.Id).Distinct()]))
            .Where(x => x.Source is not null && reader.Allows(x.Source.Group.Requires))
            .Select(x => AskAsync(x.Source!, "карточки",
                (p, token) => p.CardsAsync(x.Source!.Group.Code, x.Ids, reader, token),
                cards => RecordSearchGroupAnswer.Found(x.Source!, cards, false), ct));
        return [.. (await Task.WhenAll(asked)).OfType<RecordSearchGroupAnswer>()];
    }

    private async Task<RecordSearchGroupAnswer?> AskAsync<T>(
        RecordSearchSource source, string what,
        Func<IRecordSearch, CancellationToken, Task<ContractAnswer<T>>> ask, Func<T, RecordSearchGroupAnswer> found, CancellationToken ct)
    {
        var scope = scopes.CreateAsyncScope();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limits.PerGroup);
        Task<ContractAnswer<T>>? task = null;
        try
        {
            var provider = Provider(scope.ServiceProvider, source.Module);
            task = ask(provider, deadline.Token);
            // WaitAsync — на случай поставщика, который токен не слушает: предел держит ответ и тогда.
            var answer = await task.WaitAsync(limits.PerGroup, ct);
            return answer switch
            {
                ContractAnswer<T>.Shown shown => found(shown.Value),
                ContractAnswer<T>.Hidden => null,
                ContractAnswer<T>.SystemRefused refused => RecordSearchGroupAnswer.Failed(source, refused.Text),
                _ => throw new InvalidOperationException($"Неизвестный ответ контракта: {answer.GetType().Name}"),
            };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
                                   && ex is TimeoutException or OperationCanceledException)
        {
            log.LogWarning("Группа поиска «{Group}» модуля «{Module}» не ответила ({What}) за {Limit} мс.",
                source.Group.Code, source.Module, what, limits.PerGroup.TotalMilliseconds);
            return RecordSearchGroupAnswer.Failed(source,
                $"не ответила за {limits.PerGroup.TotalSeconds:0.#} с — запрос можно повторить");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Текст исключения — в журнал, не человеку: в нём бывают имена таблиц и строки запросов.
            log.LogError(ex, "Группа поиска «{Group}» модуля «{Module}» отказала ({What}).",
                source.Group.Code, source.Module, what);
            return RecordSearchGroupAnswer.Failed(source, "поиск в группе отказал — ошибка сервера, она записана в журнал");
        }
        finally
        {
            // Поставщик, не услышавший токена, ещё работает в этой области: убрать её можно только за ним.
            if (task is { IsCompleted: false }) _ = task.ContinueWith(_ => scope.Dispose(), TaskScheduler.Default);
            else await scope.DisposeAsync();
        }
    }

    private static IRecordSearch Provider(IServiceProvider services, string module) =>
        module == CoreRecordSearch.Module
            ? services.GetRequiredService<CoreRecordSearch>()
            : services.GetRequiredService<IModuleContracts>().GetAll<IRecordSearch>()
                  .Single(p => string.Equals(p.Module, module, StringComparison.Ordinal)).Contract;
}
