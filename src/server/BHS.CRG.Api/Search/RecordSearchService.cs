using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Modules.Search;
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
/// <para><b>Каждая группа — в своей области служб и со своим пределом времени</b>
/// (<see cref="ModuleContributions" />, <see cref="RecordSearchLimits.PerGroup" />): медленный или упавший
/// поставщик отвечает отказом у своей группы и не держит остальные (сценарий «Отказ одной группы»).
/// Спрашивается группа, а не модуль: групп у модуля бывает несколько, и предел — у каждой.</para>
///
/// <para><b>Права — дважды</b> (правило соглашения shell-05 и shell-08 от 12.11.2026). Право, известное до
/// вызова, модуль объявляет ключом группы, и ядро группу без права не спрашивает вовсе; право, зависящее от
/// данных поставщика, — его ответ «скрыто правами», и группа тоже пропадает. Первый пояс держит
/// забывчивого поставщика, второй — изоляцию строк, которую поставщик знает, а ядро нет (<c>work-07</c>).</para>
/// </summary>
public sealed class RecordSearchService(
    RecordSearchCatalog catalog, ModuleContributions contributions, IModuleReaders readers, RecordSearchLimits limits)
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
        Func<IRecordSearch, CancellationToken, Task<ContractAnswer<T>>> ask, Func<T, RecordSearchGroupAnswer> found,
        CancellationToken ct)
    {
        // Разбор ответа — внутри опроса: неожиданный ответ поставщика (null, новый вариант) — отказ его
        // группы, а не 500 на весь поиск.
        var answer = await contributions.AskAsync<RecordSearchGroupAnswer?>(source.Module, $"группа поиска «{source.Group.Code}», {what}",
            limits.PerGroup, async (services, token) =>
                await ask(Provider(services, source.Module), token) switch
                {
                    ContractAnswer<T>.Shown shown => found(shown.Value),
                    ContractAnswer<T>.Hidden => null,
                    ContractAnswer<T>.SystemRefused refused => RecordSearchGroupAnswer.Failed(source, refused.Text),
                    var other => throw new InvalidOperationException(
                        $"Неизвестный ответ контракта: {other?.GetType().Name ?? "null"}"),
                }, ct);
        return answer.Refused is { } failed ? RecordSearchGroupAnswer.Failed(source, failed) : answer.Value;
    }

    private static IRecordSearch Provider(IServiceProvider services, string module) =>
        module == CoreRecordSearch.Module
            ? services.GetRequiredService<CoreRecordSearch>()
            : services.GetRequiredService<IModuleContracts>().GetAll<IRecordSearch>()
                  .Single(p => string.Equals(p.Module, module, StringComparison.Ordinal)).Contract;
}
