using BHS.CRG.Application.Tables;
using BHS.CRG.Modules.Tables;

namespace BHS.CRG.Api.Modules.Tables;

/// <summary>
/// Расшифровка строки на пути от службы модуля к ответу (задача G4, issue #1097): ядро проверяет её и
/// вычищает закрытое — здесь, а не в модуле, как и у строк.
///
/// <para>Два вида негодности, и отвечают они по-разному. <b>Нарушение формы</b> (незнакомый ключ, не
/// тот вид значения, расшифровка со страницей) — ошибка кода модуля: исключение, её ловят тесты.
/// <b>Сумма не сошлась с клеткой</b> — может всплыть на живых данных, которых тесты не видели: блок не
/// показывается, на его месте отказ, расхождение — в журнале. Пятисотый ответ унёс бы и поля строки,
/// а две разные цифры рядом — то, от чего сверка и защищает (ревью PR #1197).</para>
/// </summary>
public static class ModuleTableBreakdowns
{
    /// <summary>Что видит человек на месте блока, не сошедшегося со строкой.</summary>
    public const string Mismatch = "Блок не показан: его сумма не сошлась с суммой строки. Сообщите администратору.";

    /// <param name="columns">ВСЕ колонки таблицы с причинами закрытия — за ними следуют колонки расшифровки.</param>
    /// <param name="open">Открытые человеку колонки таблицы (не «показанные»: «Сумма» может быть убрана
    /// с экрана, а расшифровка законна).</param>
    /// <returns>null — служба расшифровки не отдала.</returns>
    public static TableBreakdownDto? Build(
        ModuleTable table, ModuleTableQuery query, ModuleTablePage page,
        IReadOnlyList<TableColumnDto> columns, IReadOnlySet<string> open, ILogger log)
    {
        if (page.Breakdown is not { } data) return null;
        InvalidOperationException Broken(string what) =>
            new($"Служба строк таблицы «{table.Title}» отдала негодную расшифровку строки: {what}.");

        if (table.Breakdown is not { } declared) throw Broken("таблица расшифровки не объявляет");
        // Не «проигнорировать»: модуль, считающий расшифровку на каждую страницу, должен об этом узнать.
        if (query.Row is null) throw Broken("её отдают только с одной строкой, а запрошена страница");
        if (page.Rows.Count != 1) throw Broken("строки, которую она расшифровывает, в ответе нет");

        var byKey = columns.ToDictionary(c => c.Key, StringComparer.Ordinal);
        var shown = declared.Columns.Select(c =>
        {
            var closed = c.Follows is { } follows && !open.Contains(follows) ? byKey.GetValueOrDefault(follows) : null;
            // Закрыта та, за которой следует, — причина та же; а если её причина не названа (колонки нет
            // в ответе вовсе) — закрыта без слов, но закрыта.
            var unavailable = c.Follows is { } f && !open.Contains(f) ? closed?.Unavailable ?? TableColumnReasons.NoRight : null;
            return new TableBreakdownColumnDto(c.Key, c.Title, TableKinds.Name(c.Kind), unavailable, closed?.Reason);
        }).ToList();
        var kinds = declared.Columns.ToDictionary(c => c.Key, c => c.Kind, StringComparer.Ordinal);
        var visible = shown.Where(c => c.Unavailable is null).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var own = table.Columns.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var row in data.Rows)
        {
            if (row.Follows is { } follows && !own.Contains(follows))
                throw Broken($"строка следует за колонкой «{follows}», которой в таблице нет");
            foreach (var (key, value) in row.Values)
            {
                if (!kinds.TryGetValue(key, out var kind)) throw Broken($"колонки «{key}» в объявлении нет");
                if (!Fits(value, kind)) throw Broken($"в колонке «{key}» лежит не {TableKinds.Name(kind)}");
            }
        }

        // Суммы — из ВСЕХ строк, до вычистки; сверка с клеткой — по тому, что служба отдала в строке.
        var cell = page.Rows[0];
        var totals = new List<TableBreakdownTotalDto>();
        foreach (var sum in declared.Sums ?? [])
        {
            var whole = Sum(data.Rows, sum.Column);
            var named = data.Narrowed ? Sum(data.Rows.Where(r => r.Named), sum.Column) : null;

            var mismatch = Differs(sum.Named, data.Narrowed ? named : whole)
                           ?? (sum.Whole is null ? null : Differs(sum.Whole, whole));
            if (mismatch is not null)
            {
                log.LogError(
                    "Расшифровка строки «{Row}» таблицы «{Table}» не сошлась со строкой и не показана: сумма колонки «{Column}» — {Mismatch}",
                    query.Row, table.Title, sum.Column, mismatch);
                return new(declared.Title, [], [], false, [], Refusal: Mismatch);
            }
            if (visible.Contains(sum.Column)) totals.Add(new(sum.Column, whole, named));
        }

        // Строка, существующая только из-за денег, не приходит тому, кому они закрыты.
        var rows = data.Rows.Where(r => r.Follows is null || open.Contains(r.Follows)).ToList();
        return new(declared.Title, shown,
            [.. rows.Select(r => new TableBreakdownRowDto(
                r.Values.Where(v => visible.Contains(v.Key)).ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal),
                data.Narrowed && r.Named))],
            data.Narrowed, totals,
            // Подпись — свободный текст модуля: вычистить из неё сумму нечем, поэтому при закрытой
            // колонке её нет вовсе.
            visible.Count == shown.Count ? data.Note : null);

        // «Денег нет» и «ноль» — одно число: у счёта с нулевой суммой в клетке стоит 0, а строк с
        // деньгами в расшифровке может не быть вовсе.
        string? Differs(string column, decimal? expected)
        {
            if (!cell.TryGetValue(column, out var raw)) return null;
            var actual = raw is null ? (decimal?)null : Convert.ToDecimal(raw);
            return (actual ?? 0) == (expected ?? 0) ? null : $"{Text(expected)}, а в клетке «{column}» — {Text(actual)}";
        }
    }

    private static string Text(decimal? value) => value?.ToString("0.##") ?? "пусто";

    /// <summary>Сумма колонки по строкам; null — ни в одной строке числа нет.</summary>
    private static decimal? Sum(IEnumerable<TableBreakdownRow> rows, string column)
    {
        var values = rows.Select(r => r.Values.GetValueOrDefault(column)).Where(v => v is not null)
            .Select(Convert.ToDecimal).ToList();
        return values.Count == 0 ? null : values.Sum();
    }

    private static bool Fits(object? value, ModuleTableColumnKind kind) => value is null || kind switch
    {
        ModuleTableColumnKind.Number => value is decimal or int or long or double,
        ModuleTableColumnKind.Date => value is DateOnly or DateTime or DateTimeOffset,
        _ => value is string,
    };
}
