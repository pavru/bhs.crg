using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Home;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Endpoints.Core;

/// <summary>
/// Карточка очереди Главной («Ждут вас», shell-08, ТЗ UI-34).
/// </summary>
/// <param name="Module">Код модуля — вместе с <paramref name="Id" /> это адрес экрана очереди на
/// клиенте: адресов клиента сервер не знает.</param>
/// <param name="ModuleTitle">Название модуля для строки под подписью.</param>
/// <param name="Count">Сколько; <c>null</c> — не посчитано, и тогда назван <paramref name="Refused" />.</param>
/// <param name="Doubt">Почему числу нельзя верить как полному. ⚠️ Ноль с ней — не «пусто».</param>
public sealed record HomeQueueView(
    string Module, string ModuleTitle, string Id, string Label, string Hint, bool Quiet,
    int? Count, string? Doubt, string? Refused);

/// <summary>
/// Адрес очередей Главной (shell-08, ТЗ UI-34): открытые спрашивающему очереди включённых модулей с
/// числами.
///
/// <para><b>На адресе — только вход, права — здесь</b>: у каждой очереди свои ключи — модуль и ВСЕ права
/// объявления, формула ворот адреса модуля. Строка в корзине «ворота внутри» переписи адресов
/// называет сторожа (<c>HomeQueuesTests</c>). Модулю приходят только открытые коды.</para>
///
/// <para><b>Ничего не открыто — пустой список, а не отказ</b>: Главная открыта каждому вошедшему, и
/// без очередей она показывает остальные блоки (ТЗ UI-33). Выключенный модуль просто не отвечает.</para>
/// </summary>
public static class HomeEndpoints
{
    /// <summary>Что спрашивали — для журнала отказа поставщика.</summary>
    private const string Label = "Очереди Главной";

    public static void MapHomeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/home/queues", async (IModuleReaders readers, HomeQueueCatalog catalog,
            ModuleRegistry registry, ModuleContributions contributions, CancellationToken ct) =>
            TypedResults.Ok(await QueuesAsync(await readers.ForRequestAsync(ct), catalog, registry, contributions, ct)))
            .RequireAuthorization()
            .WithTags("Главная");
    }

    /// <summary>Очереди, открытые спрашивающему, — в порядке состава поставки и объявления.</summary>
    internal static async Task<IReadOnlyList<HomeQueueView>> QueuesAsync(
        ModuleReader reader, HomeQueueCatalog catalog, ModuleRegistry registry,
        ModuleContributions contributions, CancellationToken ct)
    {
        var offered = registry.Enabled
            .SelectMany(m => catalog.All.Where(e => string.Equals(e.Module, m.Code, StringComparison.Ordinal)))
            .Where(e => Open(e, reader))
            .ToList();
        if (offered.Count == 0) return [];

        // Спрашиваются только модули с открытыми очередями и только об открытых кодах: право проверено
        // здесь, до вызова, — поставщику отдать лишнее нечем. Поэтому по вкладу (AskAsync), а не всех
        // поставщиков разом.
        var codes = offered.GroupBy(e => e.Module, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(e => e.Queue.Code)], StringComparer.Ordinal);
        var asked = codes.Select(pair => contributions.AskAsync(pair.Key, Label, ModuleContributions.DefaultTimeout,
            (services, token) => services.GetRequiredService<IModuleContracts>().GetAll<IHomeQueueCounts>()
                .Single(p => string.Equals(p.Module, pair.Key, StringComparison.Ordinal)).Contract
                .CountAsync(reader, pair.Value, token), ct));
        var answers = (await Task.WhenAll(asked)).ToDictionary(a => a.Module, StringComparer.Ordinal);

        return [.. offered.Select(e =>
        {
            var q = e.Queue;
            HomeQueueView View(int? count, string? doubt, string? refused) =>
                new(e.Module, e.ModuleTitle, q.Code, q.Title, q.Hint, q.Quiet, count, doubt, refused);

            if (!answers.TryGetValue(e.Module, out var answer))
                return View(null, null, $"модуль «{e.ModuleTitle}» не ответил");
            if (answer.Refused is { } refused) return View(null, null, refused);
            // Число без причины отказа и без числа — не «ноль»: молчание модуля о коде названо словами.
            var found = answer.Value?.FirstOrDefault(c => string.Equals(c.Code, q.Code, StringComparison.Ordinal));
            return found is null
                ? View(null, null, $"модуль «{e.ModuleTitle}» не назвал число этой очереди")
                : found.Count is null
                    ? View(null, found.Doubt, found.Refused ?? "не посчитано")
                    : View(found.Count, found.Doubt, null);
        })];
    }

    /// <summary>Открыта ли очередь: модуль открыт спрашивающему и у него есть все права очереди.</summary>
    internal static bool Open(HomeQueueEntry entry, ModuleReader reader) =>
        reader.Allows(entry.Module) && entry.Queue.Requires.All(reader.Allows);
}
