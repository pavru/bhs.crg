using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Schema;
using BHS.CRG.Application.Tables;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Tables;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Modules.Tables;

/// <summary>
/// Таблицы модулей для обоих потребителей — экрана и набора данных (ТЗ CORE-33, CORE-24; задача G1b,
/// issue #1089).
///
/// <para><b>Одна служба на двух потребителей нарочно.</b> Состав колонок, причины недоступности и
/// вычистка закрытых значений живут здесь и только здесь: заведи набор данных свой путь, и права на
/// суммы проверялись бы в двух местах — то есть однажды в одном.</para>
///
/// <para><b>Сколько объявлено, столько и приходит.</b> Колонка без права приходит с причиной «нет права
/// на суммы», запрошенная, но исчезнувшая из типа — «поле удалено из типа», таблица выключенного
/// модуля — всеми колонками с «модуль выключен». Меньше колонок не приходит никогда: три отсутствия
/// иначе стали бы на экране одним дефисом.</para>
/// </summary>
public sealed class ModuleTableService(ModuleTableCatalog catalog, AppDbContext db, IServiceProvider services)
{
    /// <summary>Таблицы, которые спрашивающий может открыть, — включённых модулей и по его ключам.</summary>
    public IReadOnlyList<TableListItemDto> List(DataAccess access) =>
        [.. catalog.All
            .Where(e => !access.IsSystem && access.EnabledModules.Contains(e.Module) && access.Allows(e.Table.Requires))
            .Select(e => new TableListItemDto(e.Address, e.Table.Title, e.Table.Grain, e.Module))];

    /// <param name="request">Что просит потребитель. Колонка сохранённого представления, которой в
    /// таблице больше нет, приходит с причиной «поле удалено из типа». Отбор, сортировку и страницу
    /// исполняет служба модуля запросом к своей базе (G1c); итог считается по всему отбору.</param>
    /// <returns>Таблица либо отказ — кодом ответа и текстом. Отказ возвращается, а не бросается: слой
    /// Api отвечает кодами, а набор данных до этой службы доходит только через ворота набора, где те
    /// же проверки уже отказали своими словами.</returns>
    public async Task<(TableDto? Table, TableRefusal? Refusal)> ReadAsync(
        string address, DataAccess access, TableRequest request, CancellationToken ct)
    {
        var requested = request.Columns;
        var (opened, denied) = await OpenAsync(address, access, ct);
        if (opened is null) return (null, denied);
        var (entry, columns) = (opened.Entry, opened.Columns);
        var table = entry.Table;

        // Запрошенное поле схемы у выключенного модуля всё равно приходит: своим ключом, с той же
        // причиной, а не «удалено из типа» — про тип здесь не известно ничего.
        if (opened.Off is { } off)
            return (Dto(entry, [.. Mark(columns, requested).Select(c => c with
            {
                Unavailable = TableColumnReasons.ModuleOff, Reason = off,
            })], [], TableColumnReasons.ModuleOff), null);

        var marked = Mark(columns, requested).ToList();

        // Два множества, и путать их нельзя. ОТКРЫТЫЕ — всё, что человеку можно: по ним идут отбор,
        // сортировка и итог, даже если колонки нет на экране. ПОКАЗАННЫЕ — открытые из запрошенных:
        // только они едут в строках, иначе `?columns=Номер` тащил бы в каждой строке всю таблицу.
        var open = columns.Where(c => c.Unavailable is null).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var shown = marked.Where(c => c.Unavailable is null).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var (query, refusal) = ModuleTableQueries.Build(table.Title, columns, open, shown, request, access.UserId!.Value);
        if (query is null) return (null, refusal);

        var reader = (IModuleTableRows)services.GetRequiredService(table.Reader);
        var page = await reader.ReadAsync(query, ct);

        // Ключ — у каждой строки или ни у одной: список короче строк сдвинул бы ключи, и панель
        // открыла бы соседнюю строку под именем выбранной.
        if (page.Keys is { } keys && keys.Count != page.Rows.Count)
            throw new InvalidOperationException(
                $"Служба строк таблицы «{table.Title}» отдала строк — {page.Rows.Count}, а ключей — {keys.Count}.");

        if (query.Row is { } row)
        {
            if (page.Keys is null)
                return (null, new(StatusCodes.Status409Conflict,
                    $"Таблица «{table.Title}» строку по ключу не отдаёт: её служба ключей строк не называет."));

            // Служба, не заметившая ключа, отдала бы всю страницу — и первая её строка сошла бы за
            // запрошенную. Это ошибка модуля, а не человека: останавливаемся, а не отвечаем чужой строкой.
            if (page.Keys.Any(k => k != row))
                throw new InvalidOperationException(
                    $"Служба строк таблицы «{table.Title}» на запрос строки «{row}» отдала другие строки.");
        }

        // Подпись смысла — только колонке, объявленной зависящей от отбора, и только открытой: служба
        // строк не переименовывает чужие колонки и не подписывает то, чего человек не видит.
        var noted = marked.Select(c => c.DependsOnFilter && c.Unavailable is null
            && page.Notes is { } notes && notes.TryGetValue(c.Key, out var note) ? c with { Note = note } : c).ToList();

        // Вычистка — здесь, а не в службе модуля: служба вправе не считать закрытое, но гарантия
        // обязана стоять в одном месте. Забытое службой значение суммы иначе ушло бы наружу.
        return (Dto(entry, noted, [.. page.Rows.Select(r => Only(r, shown))]) with
        {
            Count = page.Count,
            Offset = request.Offset,
            Limit = request.Limit,
            Totals = page.Totals.Where(t => open.Contains(t.Key)).ToDictionary(
                t => t.Key, t => ModuleTableQueries.Total(t.Value, query.Totals![t.Key]), StringComparer.Ordinal),
            Keys = page.Keys,
        }, null);
    }

    /// <summary>
    /// Таблица без строк: все её колонки с причинами (G1e, issue #1092). Те же ворота, что у строк, —
    /// проверки стоят в <see cref="OpenAsync" /> один раз, и описание не может открыться тому, кому
    /// закрыты строки.
    /// </summary>
    public async Task<(TableDeclarationDto? Table, TableRefusal? Refusal)> DescribeAsync(
        string address, DataAccess access, CancellationToken ct)
    {
        var (opened, denied) = await OpenAsync(address, access, ct);
        if (opened is null) return (null, denied);

        var (entry, table) = (opened.Entry, opened.Entry.Table);
        return (new(entry.Address, table.Title, table.Grain, table.Boundary, opened.Columns,
            opened.Off is null ? null : TableColumnReasons.ModuleOff,
            opened.Off is null ? Views(table) : []), null);
    }

    /// <summary>
    /// Готовые представления таблицы — как объявил модуль. Правами не сужаются: представление называет
    /// колонки, а закрыта ли колонка этому человеку, говорит сама колонка — и экран покажет её причину
    /// так же, как у представления, собранного руками.
    /// </summary>
    private static IReadOnlyList<TableViewDto> Views(ModuleTable table) => [.. (table.Views ?? [])
        .Select(v => new TableViewDto(
            v.Code, v.Title, v.Columns,
            [.. (v.Sort ?? []).Select(s => new TableSortRequest(s.Column, s.Descending))],
            [.. (v.Totals ?? []).Select(t => new TableViewTotalDto(t.Column, t.Aggregate))],
            v.Pinned, v.Filters ?? []))];

    /// <summary>Таблица, открытая спрашивающему: её колонки с причинами и, если модуль выключен, чем.</summary>
    /// <param name="Columns">ВСЕ колонки таблицы, закрытые — с причиной.</param>
    /// <param name="Off">Причина «модуль выключен» словами; null — модуль включён.</param>
    private sealed record Opened(ModuleTableEntry Entry, List<TableColumnDto> Columns, string? Off);

    /// <summary>Ворота таблицы — одни на строки и на описание.</summary>
    private async Task<(Opened? Table, TableRefusal? Refusal)> OpenAsync(
        string address, DataAccess access, CancellationToken ct)
    {
        if (catalog.Find(address) is not { } entry)
            return (null, new(StatusCodes.Status404NotFound, $"Таблицы «{address}» нет ни у одного модуля сборки."));
        var table = entry.Table;

        // Система — первой, как в воротах наборов: у неё нет ни ключей, ни списка модулей, и любая
        // другая проверка ответила бы ей неверной причиной.
        if (access.IsSystem)
            return (null, new(StatusCodes.Status409Conflict,
                $"Таблица «{table.Title}» отдаёт строки только по правам человека, а их здесь нет " +
                $"({access.SystemReason})."));

        // Модуль выключен — не отказ, а состояние: колонки приходят все, с этой причиной, строк нет.
        // Объявление — не данные модуля, и показать, ЧТО выключено, честнее, чем пустой 404.
        //
        // ⚠️ Только ОБЪЯВЛЕННЫЕ колонки, без полей схемы типа. Ключа таблицы здесь не проверить — у
        // выключенного модуля его нет ни у кого, — поэтому отвечаем тем, что и так лежит в коде
        // модуля, а не схемой заказчика (ревью PR #1130).
        if (!access.EnabledModules.Contains(entry.Module))
        {
            var off = TableColumnReasons.ModuleOffText(entry.ModuleTitle);
            return (new(entry, [.. Declared(table).Select(c => c with
            {
                Unavailable = TableColumnReasons.ModuleOff, Reason = off,
            })], off), null);
        }

        if (!access.Allows(table.Requires))
            return (null, new(StatusCodes.Status403Forbidden,
                $"Таблица «{table.Title}» открывается ключом «{table.Requires}», а у «{access.Who}» его нет."));

        // Причина «нет права» ставится ВСЕМ колонкам таблицы, а не только запрошенным: отбирать и
        // сортировать можно и по колонке, которой на экране нет, — и по закрытой нельзя всё равно.
        var columns = (await ColumnsAsync(table, ct))
            .Select(c => Closed(table, c.Key, access) is { } closed
                ? c with
                {
                    Unavailable = TableColumnReasons.NoRight,
                    Reason = TableColumnReasons.NoRightText(closed.Hides!),
                    Requires = closed.Requires,
                }
                : c)
            .ToList();
        return (new(entry, columns, null), null);
    }

    /// <summary>Колонки таблицы: системные модуля, затем поля схемы типа, которых среди системных нет.</summary>
    private async Task<List<TableColumnDto>> ColumnsAsync(ModuleTable table, CancellationToken ct)
    {
        var columns = Declared(table);
        if (table.RecordType is null) return columns;

        // Типов немного, а эффективная схема требует цепочки предков — грузим все разом.
        var types = await db.DocumentTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        var type = types.Values.FirstOrDefault(t => t.Code == table.RecordType);
        if (type is null) return columns;

        var system = columns.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var fields = DocumentTypeSchemaReader.EffectiveFields(type.Id, types)
            // Расчётное поле в данных не лежит, нескалярное в клетку не ложится.
            .Where(f => !system.Contains(f.Key) && !f.Computed && SchemaFieldKinds.IsScalar(f.Type))
            .ToList();

        // Примитив («Деньги» на базе числа) — колонка своей базы: иначе сумма пришла бы текстом, с
        // «содержит» вместо «больше», и итог G1c посчитал бы строки вместо суммы (ревью PR #1130).
        var primitiveIds = fields.Where(f => f.Type == "primitive" && f.TypeId is not null)
            .Select(f => f.TypeId!.Value).Distinct().ToList();
        var bases = primitiveIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.PrimitiveTypes.AsNoTracking().Where(p => primitiveIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.BaseType, ct);

        columns.AddRange(fields.Select(f =>
        {
            var kind = KindOf(f.Type == "primitive" && f.TypeId is { } id && bases.TryGetValue(id, out var b) ? b : f.Type);
            return new TableColumnDto(
                f.Key, string.IsNullOrWhiteSpace(f.Title) ? f.Key : f.Title, kind, TableOperators.For(kind), false);
        }));
        return columns;
    }

    /// <summary>Системные колонки модуля — то, что лежит в его коде, без схемы заказчика.</summary>
    private static List<TableColumnDto> Declared(ModuleTable table) => [.. table.Columns
        .Select(c => new TableColumnDto(
            c.Key, c.Title, TableKinds.Name(c.Kind),
            // Колонке, чьё значение зависит от отбора, операторов не положено: по ней не отбирают.
            c.DependsOnFilter ? [] : TableOperators.For(TableKinds.Name(c.Kind)), true,
            DependsOnFilter: c.DependsOnFilter, Options: c.Options))];

    /// <summary>
    /// Запрошенные колонки в запрошенном порядке; ключ, которого нет, — колонка с причиной «поле
    /// удалено из типа». Ничего не просили — все колонки таблицы.
    ///
    /// <para>Пустой список — тоже «ничего не просили» (<c>?columns=,</c>): таблица без единой колонки
    /// нарушила бы главное обещание — меньше объявленного не приходит никогда (ревью PR #1130).</para>
    /// </summary>
    private static IEnumerable<TableColumnDto> Mark(List<TableColumnDto> columns, IReadOnlyList<string>? requested)
    {
        if (requested is null or { Count: 0 }) return columns;

        var byKey = columns.ToDictionary(c => c.Key, StringComparer.Ordinal);
        return requested.Distinct(StringComparer.Ordinal).Select(key => byKey.TryGetValue(key, out var column)
            ? column
            : new TableColumnDto(key, key, TableOperators.Text, TableOperators.For(TableOperators.Text), false,
                TableColumnReasons.Removed, TableColumnReasons.RemovedText));
    }

    /// <summary>
    /// Объявление колонки, закрытой правом, которого у спрашивающего нет; null — колонка открыта. Из
    /// него берутся и слова причины («суммы»), и код права — тот, по которому его ищет администратор.
    /// </summary>
    private static ModuleTableColumn? Closed(ModuleTable table, string key, DataAccess access) =>
        table.Columns.FirstOrDefault(c => c.Key == key) is { Requires: { } requires, Hides: not null } column
        && !access.Allows(requires)
            ? column
            : null;

    private static IReadOnlyDictionary<string, object?> Only(IReadOnlyDictionary<string, object?> row, ISet<string> open) =>
        row.Where(p => open.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    private static TableDto Dto(
        ModuleTableEntry entry, IReadOnlyList<TableColumnDto> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string? state = null) =>
        new(entry.Address, entry.Table.Title, entry.Table.Grain, entry.Table.Boundary, columns, rows, state);

    /// <summary>
    /// Вид поля схемы (или базы примитива) → вид колонки. Перечисление, строка, текст — текстом: в
    /// данных у перечисления код, и отбор идёт по коду; подпись значения — дело экрана (G1e).
    /// </summary>
    private static string KindOf(string fieldType) => fieldType switch
    {
        "number" => TableOperators.Number,
        "date" => TableOperators.Date,
        "bool" or "boolean" => TableOperators.Boolean,
        _ => TableOperators.Text,
    };
}

/// <summary>Отказ таблицы — код ответа и текст для человека.</summary>
public sealed record TableRefusal(int Status, string Error);
