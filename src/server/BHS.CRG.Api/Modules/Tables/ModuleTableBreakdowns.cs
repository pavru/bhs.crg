using BHS.CRG.Application.Tables;
using BHS.CRG.Modules.Tables;

namespace BHS.CRG.Api.Modules.Tables;

/// <summary>
/// Расшифровка строки на пути от службы модуля к ответу (задача G4, issue #1097): ядро проверяет её и
/// вычищает закрытое — здесь, а не в модуле, как и у строк. Негодная расшифровка — ошибка модуля и
/// останавливает ответ; тихо отданная, она была бы утечкой сумм либо второй цифрой рядом с клеткой.
/// </summary>
internal static class ModuleTableBreakdowns
{
    /// <param name="columns">ВСЕ колонки таблицы с причинами закрытия — за ними следуют колонки расшифровки.</param>
    /// <param name="open">Открытые человеку колонки таблицы (не «показанные»: «Сумма» может быть убрана
    /// с экрана, а расшифровка законна).</param>
    /// <returns>null — служба расшифровки не отдала.</returns>
    public static TableBreakdownDto? Build(
        ModuleTable table, ModuleTableQuery query, ModuleTablePage page,
        IReadOnlyList<TableColumnDto> columns, IReadOnlySet<string> open)
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

        foreach (var row in data.Rows)
            foreach (var (key, value) in row.Values)
            {
                if (!kinds.TryGetValue(key, out var kind)) throw Broken($"колонки «{key}» в объявлении нет");
                if (!Fits(value, kind)) throw Broken($"в колонке «{key}» лежит не {TableKinds.Name(kind)}");
            }

        // Суммы — из строк, до вычистки; сверка с клеткой — по тому, что служба отдала в строке.
        var cell = page.Rows[0];
        var totals = new List<TableBreakdownTotalDto>();
        foreach (var sum in declared.Sums ?? [])
        {
            var whole = Sum(data.Rows, sum.Column);
            var named = data.Narrowed ? Sum(data.Rows.Where(r => r.Named), sum.Column) : null;

            Check(sum.Named, data.Narrowed ? named : whole);
            if (sum.Whole is not null) Check(sum.Whole, whole);
            if (visible.Contains(sum.Column)) totals.Add(new(sum.Column, whole, named));

            void Check(string column, decimal? expected)
            {
                if (!cell.TryGetValue(column, out var raw)) return;
                var actual = raw is null ? (decimal?)null : Convert.ToDecimal(raw);
                if (actual != expected)
                    throw Broken($"сумма колонки «{sum.Column}» — {Text(expected)}, а в клетке «{column}» — {Text(actual)}");
            }
        }

        return new(declared.Title, shown,
            [.. data.Rows.Select(r => new TableBreakdownRowDto(
                r.Values.Where(v => visible.Contains(v.Key)).ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal),
                data.Narrowed && r.Named))],
            data.Narrowed, totals,
            // Подпись — свободный текст модуля: вычистить из неё сумму нечем, поэтому при закрытой
            // колонке её нет вовсе.
            visible.Count == shown.Count ? data.Note : null);
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
