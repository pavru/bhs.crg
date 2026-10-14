using System.Collections;
using System.Globalization;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Settings;
using BHS.CRG.Application.Tables;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Modules.Tables;

/// <summary>Итог под колонкой выгрузки: колонка и слово итога (<c>sum</c>, <c>count</c>…), как на экране.</summary>
public sealed record TableExportTotal(string Column, string Aggregate);

/// <summary>
/// Что выгрузить: то, что стоит на экране, — колонки, отбор, сортировку и итоги. Страницы нет: в файл
/// идёт ВЕСЬ отбор. <c>View</c> — название представления для шапки и имени файла; на строки оно не
/// влияет никак.
/// </summary>
public sealed record TableExportBody(
    string[]? Columns, string? Filter, TableSortRequest[]? Sort, TableExportTotal[]? Totals, string? View = null);

/// <summary>Готовый файл выгрузки.</summary>
public sealed record TableExportFile(byte[] Bytes, string ContentType, string FileName);

/// <summary>
/// Выгрузка таблицы в XLSX — «то, что на экране» (ТЗ CORE-33; задача G3b, issue #1096).
///
/// <para><b>Строки берёт только служба таблиц</b> (<see cref="ModuleTableService.ReadAsync" />) — тем
/// же путём, что экран: ключ таблицы, закрытые колонки, вычистка значений. Своего чтения у выгрузки нет,
/// и обойти права ей нечем: здесь только «ответ таблицы → лист».</para>
///
/// <para><b>Весь отбор или ничего.</b> Больше <see cref="MaxRows" /> строк — отказ «сузьте отбор», а не
/// файл по пределу: обрезанный файл выглядел бы полным, и сумма под ним была бы молча меньше.</para>
/// </summary>
public sealed class ModuleTableExport(
    ModuleTableService tables, IAppSettingsStore settings, IActivityLog journal, AppDbContext db)
{
    /// <summary>
    /// Потолок строк одной выгрузки (решение владельца 14.10.2026). Замер экспортёра на 50 000 строк по
    /// 20 колонок — 2 с и 250 МБ на пике: синхронно, без фоновой задачи.
    /// </summary>
    public const int MaxRows = 50_000;

    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public Task<(TableExportFile? File, TableRefusal? Refusal)> ExportAsync(
        string address, DataAccess access, TableExportBody body, CancellationToken ct) =>
        ExportAsync(address, access, body, MaxRows, ct);

    /// <param name="maxRows">Потолок строк — параметром ради теста: пятьдесят тысяч счетов на стенде
    /// не заводят, а отказ сверх потолка проверить обязаны.</param>
    public async Task<(TableExportFile? File, TableRefusal? Refusal)> ExportAsync(
        string address, DataAccess access, TableExportBody body, int maxRows, CancellationToken ct)
    {
        var totals = body.Totals ?? [];

        // Сначала — сколько строк, и только потом сами строки: отказ «сузьте отбор» не должен стоить
        // чтения пятидесяти тысяч строк со всеми их подзапросами (ревью PR #1326). Страница нулевой
        // длины отдаёт число строк отбора — тем же счётом, каким его отдаёт таблица.
        var (counted, early) = await tables.ReadAsync(address, access,
            new TableRequest(body.Columns, body.Filter, body.Sort ?? [], 0, 0), ct);
        if (counted is null) return (null, early);
        if (Unexportable(counted, maxRows) is { } before) return (null, before);

        var request = new TableRequest(body.Columns, body.Filter, body.Sort ?? [], 0, maxRows,
            [.. totals.Select(t => t.Column).Distinct(StringComparer.Ordinal)]);
        var (table, all, refusal) = await tables.ReadWithColumnsAsync(address, access, request, ct);
        if (table is null) return (null, refusal);
        // Между двумя чтениями строк могло стать больше: проверяем и то, что прочли на самом деле.
        if (Unexportable(table, maxRows) is { } after) return (null, after);

        var shown = table.Columns.Where(c => c.Unavailable is null).ToList();
        var money = shown.Where(c => IsMoney(c, table.Rows)).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);

        var zone = await settings.GetCompanyTimeZoneAsync(ct);
        // Кто выгрузил — именем из учётной записи, как автор представления в списке: в токене имени
        // может не быть, и тогда в шапке файла стоял бы идентификатор.
        var who = await db.Users.AsNoTracking().Where(u => u.Id == access.UserId)
            .Select(u => string.IsNullOrWhiteSpace(u.DisplayName) ? u.UserName : u.DisplayName)
            .FirstOrDefaultAsync(ct) ?? access.Who;
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        var currency = money.Count == 0 ? null : AccountingCurrencies.Effective(
            (await settings.GetManyAsync([AppSettingKeys.AccountingCurrency], ct)).GetValueOrDefault(AppSettingKeys.AccountingCurrency));

        var sheet = new SpreadsheetExporter.Sheet(
            table.Title,
            // Подпись смысла — в том же заголовке, в скобках: вторая строка шапки сломала бы автофильтр
            // и сводную таблицу (решение владельца 14.10.2026).
            [.. shown.Select(c => c.Note is null ? c.Label : $"{c.Label} ({c.Note})")],
            [.. table.Rows.Select(row => (IReadOnlyList<object?>)[.. shown.Select(c =>
                Cell(row.GetValueOrDefault(c.Key), c.Kind, money.Contains(c.Key), zone))])],
            Preamble(table, all, body, who, now, currency, Unshown(totals, shown)),
            Footer(table, shown, money, totals, zone));

        var (bytes, _, _) = SpreadsheetExporter.ExportSheets(SpreadsheetFormat.Xlsx, [sheet]);

        // В журнал — кто, какая таблица и сколько строк; ни сумм, ни условий отбора (решение владельца
        // 14.10.2026): журнал читают по праву ядра, а суммы и то, по чему отбирали, закрыты правом модуля.
        await journal.RecordAsync(ActivityActions.TableExported, table.Address, table.Title,
            after: $"строк: {Count(table.Count)}", ct: CancellationToken.None);

        return (new TableExportFile(bytes, Xlsx, FileName(table.Title, body.View, now)), null);
    }

    /// <summary>Почему таблицу нельзя выгрузить целиком; null — можно.</summary>
    private static TableRefusal? Unexportable(TableDto table, int maxRows)
    {
        if (table.State is not null)
            return new(StatusCodes.Status409Conflict,
                $"Таблица «{table.Title}» не выгружена: {table.Columns.FirstOrDefault()?.Reason ?? "модуль выключен"} — строк нет.");
        return table.Count > maxRows
            ? new(StatusCodes.Status409Conflict,
                $"Таблица «{table.Title}» не выгружена: под отбором {Count(table.Count)} строк, а выгрузка берёт " +
                $"не больше {Count(maxRows)}. Сузьте отбор — файл, обрезанный по пределу, выглядел бы полным.")
            : null;
    }

    /// <summary>
    /// Итоги, которых в файле не будет, — словами. Неизвестное слово итога и итог, который у колонки
    /// такого вида не считается («сумма» текста), иначе пропали бы молча: на экране под колонкой что-то
    /// стоит, а в файле строки нет, и читатель решил бы, что итогов не просили (ревью PR #1326).
    /// Итог по колонке, которой в файле нет, здесь не назван: о самой колонке говорит строка «Не выгружено».
    /// </summary>
    private static List<string> Unshown(
        IReadOnlyList<TableExportTotal> totals, List<TableColumnDto> shown)
    {
        var lines = new List<string>();
        foreach (var total in totals)
        {
            var column = shown.FirstOrDefault(c => c.Key == total.Column);
            if (column is null) continue;
            if (!Aggregates.TryGetValue(total.Aggregate, out var word))
                lines.Add($"Итог по «{column.Label}» не выгружен: слова итога «{total.Aggregate}» выгрузка не знает");
            else if (!Applies(total.Aggregate, column.Kind))
                lines.Add($"Итог «{word}» по «{column.Label}» не выгружен: у колонки этого вида он не считается");
        }
        return lines;
    }

    /// <summary>Какие итоги у колонки считаются — то же правило, что у экрана (<c>aggregatesFor</c>).</summary>
    private static bool Applies(string aggregate, string kind) => aggregate switch
    {
        "count" => true,
        "min" or "max" => kind is TableOperators.Number or TableOperators.Date,
        _ => kind == TableOperators.Number,
    };

    /// <summary>
    /// Строки над таблицей — всё, без чего файл через неделю не прочесть: что за таблица, кто и когда
    /// выгрузил, что отобрано, сколько строк, чего в файле нет и почему.
    /// </summary>
    private static List<string> Preamble(
        TableDto table, IReadOnlyList<TableColumnDto> all, TableExportBody body, string who,
        DateTimeOffset now, string? currency, List<string> unshown)
    {
        var view = string.IsNullOrWhiteSpace(body.View) ? null : body.View.Trim();
        var label = all.ToDictionary(c => c.Key, c => c.Label, StringComparer.Ordinal);
        var lines = new List<string>
        {
            view is null ? table.Title : $"{table.Title} — {view}",
            $"Выгружено {now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)}, {who}",
            $"Отбор: {TableFilterWords.Describe(DataSetRowFilterExecutor.Parse(body.Filter), all) ?? "нет — все строки таблицы"}",
        };

        if (body.Sort is { Length: > 0 } sort)
            lines.Add("Порядок: " + string.Join(", ", sort.Select(s =>
                $"{label.GetValueOrDefault(s.Column, s.Column)}{(s.Descending ? " по убыванию" : "")}")));

        lines.Add($"Строк: {Count(table.Count)}");
        if (currency is not null) lines.Add($"Суммы — в валюте учёта: {currency}");

        // Чего в файле нет — названо, а не пропало: колонка без права и удалённое поле иначе выглядели бы
        // так, будто представление их и не просило.
        foreach (var group in table.Columns.Where(c => c.Unavailable is not null).GroupBy(c => c.Reason))
            lines.Add($"Не выгружено — {group.Key}: {string.Join(", ", group.Select(c => c.Label))}");

        foreach (var doubt in table.Doubts ?? [])
            lines.Add($"Неполно: «{doubt.Label}» — {doubt.Reason}. Строк под отбором по этой колонке может быть больше.");

        foreach (var (key, total) in table.Totals ?? new Dictionary<string, TableTotalDto>())
            if (total.Skipped > 0)
                lines.Add($"Итог по «{label.GetValueOrDefault(key, key)}»: не учтено {Count(total.Skipped)} — {total.SkippedReason}");

        lines.AddRange(unshown);
        return lines;
    }

    /// <summary>
    /// Строки итогов — по одной на слово итога, под своими колонками. Подпись («Сумма») стоит под первой
    /// колонкой, а если под ней сам итог — за последней: в одну клетку два значения не лягут.
    /// </summary>
    private static List<IReadOnlyList<object?>> Footer(
        TableDto table, List<TableColumnDto> shown, HashSet<string> money, IReadOnlyList<TableExportTotal> totals,
        TimeZoneInfo zone)
    {
        var rows = new List<IReadOnlyList<object?>>();
        foreach (var (aggregate, word) in AggregateWords)
        {
            var row = new object?[shown.Count + 1];
            var any = false;
            foreach (var total in totals.Where(t => t.Aggregate == aggregate))
            {
                var index = shown.FindIndex(c => c.Key == total.Column);
                if (index < 0 || table.Totals?.GetValueOrDefault(total.Column) is not { } value) continue;
                var column = shown[index];
                if (!Applies(aggregate, column.Kind)) continue; // назван в шапке — см. Unshown
                var cell = aggregate switch
                {
                    "count" => (object)(decimal)value.Count,
                    "sum" => Cell(value.Sum, column.Kind, money.Contains(column.Key), zone),
                    "avg" => Cell(value.Average, column.Kind, money.Contains(column.Key), zone),
                    "min" => Cell(value.Min, column.Kind, money.Contains(column.Key), zone),
                    _ => Cell(value.Max, column.Kind, money.Contains(column.Key), zone),
                };
                if (cell is null) continue;
                row[index] = cell;
                any = true;
            }
            if (!any) continue;
            row[row[0] is null ? 0 : shown.Count] = word;
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Слова итогов — те же, что под таблицей на экране (<c>tableTotals.ts</c>), и в том же порядке.</summary>
    private static readonly (string Aggregate, string Word)[] AggregateWords =
        [("sum", "Сумма"), ("avg", "Среднее"), ("min", "Наименьшее"), ("max", "Наибольшее"), ("count", "Количество")];

    private static readonly Dictionary<string, string> Aggregates =
        AggregateWords.ToDictionary(a => a.Aggregate, a => a.Word, StringComparer.Ordinal);

    /// <summary>
    /// Значение таблицы → значение клетки. Вид клетки задаёт ВИД КОЛОНКИ, а не вид значения: строка
    /// «0012» в текстовой колонке так и остаётся строкой.
    ///
    /// <para>⚠️ Момент переводится в пояс КОМПАНИИ: экспортёр пояса не знает и пишет те часы, что
    /// записаны в значении, — у момента из базы это UTC, и «22:30 14-го» по Москве было бы уже 15-м
    /// (ревью PR #1326). Дата без времени — день, ей переводить нечего.</para>
    /// </summary>
    private static object? Cell(object? value, string kind, bool money, TimeZoneInfo zone) => value switch
    {
        null => null,
        bool flag => flag ? "да" : "нет",
        DateTimeOffset moment => TimeZoneInfo.ConvertTime(moment, zone),
        DateTime { Kind: DateTimeKind.Utc } moment => TimeZoneInfo.ConvertTime(new DateTimeOffset(moment), zone),
        string text when kind == TableOperators.Date => Day(text, zone) is { } day ? day : text,
        string text => text,
        // Перечень — строкой через запятую, как на экране: в клетку список не ложится.
        IEnumerable list => string.Join(", ", list.Cast<object?>().Select(v => Convert.ToString(v, CultureInfo.InvariantCulture))),
        decimal number when money => new SpreadsheetExporter.Money(number),
        _ => value,
    };

    /// <summary>
    /// Дата из поля схемы: «2026-10-14» и «2026-10-14T…» — днём; иное — не дата. Момент СО СМЕЩЕНИЕМ
    /// («…T22:30:00Z») — днём в поясе компании, а не первыми десятью знаками: обрезка дала бы день UTC.
    /// </summary>
    public static DateOnly? Day(string text, TimeZoneInfo zone)
    {
        if (text.Length > 10 && (text.EndsWith('Z') || text.LastIndexOfAny(['+', '-']) > 10)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment))
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, zone).DateTime);
        return text.Length >= 10 && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var day) ? day : null;
    }

    /// <summary>
    /// Деньги ли колонка — решается по ВСЕЙ колонке, а не по клетке (ревью PR #1325): числовая колонка,
    /// где каждое значение — дробь не длиннее сотых и хоть одно записано с копейками. Сумма из базы
    /// приходит с двумя знаками и у круглых рублей («1000.00»), количество дней — целым.
    /// </summary>
    private static bool IsMoney(TableColumnDto column, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (column.Kind != TableOperators.Number) return false;
        var numbers = rows.Select(r => r.GetValueOrDefault(column.Key)).Where(v => v is not null).ToList();
        return numbers.Count > 0
            && numbers.All(v => v is decimal d && d.Scale <= 2)
            && numbers.Any(v => ((decimal)v!).Scale > 0);
    }

    /// <summary>«Счета — Мои счета 2026-10-14.xlsx»: знаки, запрещённые в именах файлов, — пробелом.</summary>
    private static string FileName(string title, string? view, DateTimeOffset now)
    {
        var name = string.IsNullOrWhiteSpace(view) ? title : $"{title} — {view.Trim()}";
        var bad = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var safe = new string([.. name.Select(ch => bad.Contains(ch) || char.IsControl(ch) ? ' ' : ch)]).Trim();
        if (safe.Length > 100) safe = safe[..100].TrimEnd();
        return $"{safe} {now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.xlsx";
    }

    private static string Count(long count) => count.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"));
}
