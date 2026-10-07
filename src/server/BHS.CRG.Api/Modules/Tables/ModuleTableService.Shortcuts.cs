using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Tables;
using BHS.CRG.Modules.Tables;

namespace BHS.CRG.Api.Modules.Tables;

/// <content>Готовые отборы таблицы и число строк под каждым (issue #1186).</content>
public sealed partial class ModuleTableService
{
    /// <summary>
    /// Готовые отборы таблицы, которые спрашивающему предлагаются, — каждый с числом строк под ним.
    ///
    /// <para><b>Число — это число строк отбора</b>, посчитанное службой строк под ТЕМ ЖЕ условием, тем
    /// же путём, каким его поставит экран: условие собирается текстом отбора и проходит общий разбор
    /// (<see cref="ModuleTableQueries.Build" />). Страница при этом нулевая — строки не читаются.</para>
    ///
    /// <para>Ворота — те же, что у строк и у описания (<see cref="OpenAsync" />): готовые отборы не
    /// откроются тому, кому закрыта таблица. У выключенного модуля их нет: строк нет — считать нечего.</para>
    /// </summary>
    public async Task<(IReadOnlyList<TableShortcutDto>? Shortcuts, TableRefusal? Refusal)> ShortcutsAsync(
        string address, DataAccess access, CancellationToken ct)
    {
        var (opened, denied) = await OpenAsync(address, access, ct);
        if (opened is null) return (null, denied);

        var table = opened.Entry.Table;
        var offered = opened.Off is not null
            ? []
            : (table.Shortcuts ?? []).Where(s => s.Requires is null || access.Allows(s.Requires)).ToList();
        if (offered.Count == 0) return ([], null);

        var open = opened.Columns.Where(c => c.Unavailable is null).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var reader = (IModuleTableRows)services.GetRequiredService(table.Reader);

        var queries = new List<ModuleTableQuery>();
        foreach (var shortcut in offered)
        {
            // Колонка названа показанной: служба строк, считающая её по требованию, так узнаёт, что о
            // ней спросили, даже если бы условие по ней она разбирала иначе.
            var request = new TableRequest([shortcut.Column], Condition(shortcut), Limit: 0);
            var (query, refusal) = ModuleTableQueries.Build(
                table.Title, opened.Columns, open, new HashSet<string>([shortcut.Column], StringComparer.Ordinal),
                request, access.UserId!.Value);

            // Объявление проверено при старте: колонка есть, не закрыта правом, значение из её перечня.
            // Отказ здесь — ошибка сборки, а не человека, и молча выбросить отбор из списка значило бы
            // показать «наводить нечего» там, где не посчитано.
            if (query is null)
                throw new InvalidOperationException(
                    $"Готовый отбор «{shortcut.Code}» таблицы «{table.Title}» не разобрался: {refusal!.Error}");

            queries.Add(query);
        }

        var counts = await CountAsync(reader, queries, table, ct);
        return ([.. offered.Select((shortcut, i) => new TableShortcutDto(
            shortcut.Code, shortcut.Title, shortcut.Hint, shortcut.Column, ModuleTableShortcut.Op, shortcut.Value,
            counts[i].Count, counts[i].Doubts?.GetValueOrDefault(shortcut.Column), shortcut.Quiet))], null);
    }

    /// <summary>
    /// Числа под отборами: одним вызовом, если служба строк умеет считать без строк, иначе — чтением
    /// страницы нулевой длины на каждый отбор.
    /// </summary>
    private static async Task<IReadOnlyList<ModuleTableCount>> CountAsync(
        IModuleTableRows reader, List<ModuleTableQuery> queries, ModuleTable table, CancellationToken ct)
    {
        if (reader is not IModuleTableCounts counting)
        {
            var read = new List<ModuleTableCount>();
            foreach (var query in queries)
            {
                var page = await reader.ReadAsync(query, ct);
                read.Add(new(page.Count, page.Doubts));
            }
            return read;
        }

        var counts = await counting.CountAsync(queries, ct);
        // Ответов — сколько запросов: список короче сдвинул бы числа, и чип показал бы число соседа.
        if (counts.Count != queries.Count)
            throw new InvalidOperationException(
                $"Служба строк таблицы «{table.Title}» на запросов — {queries.Count} отдала чисел — {counts.Count}.");
        return counts;
    }

    /// <summary>Условие готового отбора — тем же текстом, каким отбор присылает экран.</summary>
    private static string Condition(ModuleTableShortcut shortcut) => JsonSerializer.Serialize(new
    {
        type = "group",
        logic = "and",
        children = new[]
        {
            new { type = "condition", column = shortcut.Column, op = ModuleTableShortcut.Op, value = shortcut.Value },
        },
    });
}
