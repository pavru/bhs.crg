using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Tables;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Api.Modules.Tables;

/// <summary>
/// Что перенести в источник: то, что стоит на экране, — колонки, отбор и сортировку. Представления здесь
/// нет нарочно: источник — копия состояния экрана, и связи с представлением у него не остаётся.
/// </summary>
public sealed record TableDataSetBody(string? Name, string[]? Columns, string? Filter, TableSortRequest[]? Sort);

/// <summary>
/// Созданный источник. <c>Dropped</c> — подписи колонок экрана, не попавших в источник: их значение
/// зависит от отбора.
/// </summary>
public sealed record TableDataSetResult(Guid FileId, Guid SourceId, string Name, IReadOnlyList<string> Dropped);

/// <summary>
/// Мост «Сделать источником данных» (ТЗ CORE-33, COST-22; задача G3b, issue #1096): экран таблицы
/// превращается в обычный источник системного набора на поставщике <c>system:table:{адрес}</c>.
///
/// <para><b>Односторонний и однократный.</b> Колонки ложатся в маркер источника, отбор и сортировка —
/// в его обработку, значениями. Ни кода, ни идентификатора представления источник не получает: правка
/// представления после моста источник не меняет, и наоборот.</para>
///
/// <para><b>Строки источник читает правами читающего</b>, при каждом чтении, — тем же поставщиком,
/// что любой системный набор. Мост прав не расширяет: он только записывает, что читать.</para>
///
/// <para><b>Колонки, зависящие от отбора</b> («доля» разноски), в источник не едут: набор отбирает
/// строки после чтения, и доля посчиталась бы без отбора — полной суммой под именем доли. Отбирать и
/// сортировать по такой колонке не даёт уже экран (<c>ModuleTableQueries</c>), и мост получает тот же
/// отказ его же словами.</para>
/// </summary>
public sealed class ModuleTableBridge(ModuleTableService tables, IDataSetService datasets)
{
    public async Task<(TableDataSetResult? Result, TableRefusal? Refusal)> CreateAsync(
        string address, DataAccess access, TableDataSetBody body, CancellationToken ct)
    {
        // Отбор и сортировку проверяет экранный путь — страницей нулевой длины: закрытая колонка,
        // колонка с долей, негодное условие и ключ таблицы отказывают теми же словами, что на экране,
        // ещё до набора данных.
        var (screen, all, refusal) = await tables.ReadWithColumnsAsync(address, access,
            new TableRequest(body.Columns, body.Filter, body.Sort ?? [], 0, 0), ct);
        if (screen is null) return (null, refusal);
        if (screen.State is not null)
            return (null, new(StatusCodes.Status409Conflict,
                $"Таблица «{screen.Title}» сейчас недоступна — источник из неё не создать."));

        var byKey = all.ToDictionary(c => c.Key, StringComparer.Ordinal);
        bool Dependent(string key) => byKey.TryGetValue(key, out var c) && c.DependsOnFilter;

        var filtered = FilterColumns(DataSetRowFilterExecutor.Parse(body.Filter));
        var shown = body.Columns ?? [.. screen.Columns.Select(c => c.Key)];
        var sort = body.Sort ?? [];
        // В маркер — колонки экрана, а за ними те, по которым отбирают и сортируют: источник отбирает
        // строки сам, и колонки условия без значений у него не было бы — условие молча не нашло бы ничего.
        var columns = shown.Where(k => !Dependent(k))
            .Concat(filtered).Concat(sort.Select(s => s.Column))
            .Distinct(StringComparer.Ordinal).ToList();
        var dropped = shown.Where(Dependent).Select(k => byKey[k].Label).ToList();

        var sortSpec = sort.Length == 0 ? null : DataSetDtoMapper.SerializeJson(sort.Select(s =>
            new SortColumnDef { Column = s.Column, Direction = s.Descending ? "desc" : "asc" }).ToList());
        var source = await datasets.CreateTableCopyAsync(
            body.Name ?? "", SystemDataSets.TableMarker(screen.Address, columns),
            string.IsNullOrWhiteSpace(body.Filter) ? null : body.Filter, sortSpec, access, ct);
        return (new(source.FileId, source.Id, source.Name, dropped), null);
    }

    private static IEnumerable<string> FilterColumns(FilterNode? node) =>
        node is null ? []
        : node.Type == "condition" ? (node.Column is { } column ? [column] : [])
        : (node.Children ?? []).SelectMany(FilterColumns);
}
