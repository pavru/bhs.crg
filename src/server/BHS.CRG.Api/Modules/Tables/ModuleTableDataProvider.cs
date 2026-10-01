using System.Globalization;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Tables;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Modules.Tables;

namespace BHS.CRG.Api.Modules.Tables;

/// <summary>
/// Таблица модуля как системный набор данных — второй потребитель того же объявления (ТЗ CORE-33,
/// CORE-24; задача G1b, issue #1089).
///
/// <para><b>Поставщик на КАЖДУЮ таблицу</b>, а не один на все: объявление набора — свойство
/// поставщика (<see cref="ISystemDataProvider.Declaration" />), и у таблиц оно разное — свой модуль,
/// свой ключ, своя граница выдачи. Один поставщик со «средним» объявлением открыл бы все таблицы по
/// ключу самой открытой.</para>
///
/// <para>Строки берёт у <see cref="ModuleTableService" />, то есть тем же путём, что экран: колонка
/// без права приходит в набор колонкой без значений, а причина — оговоркой к данным. Исчезни она
/// из набора, разметка источника на неё выглядела бы как «поле удалено».</para>
/// </summary>
public sealed class ModuleTableDataProvider(ModuleTableEntry entry, ModuleTableService tables) : ISystemDataProvider
{
    /// <summary>Маркер источника: <c>system:table:costs.invoices</c>.</summary>
    public static string MarkerOf(ModuleTableEntry entry) => SystemDataSets.TableMarkerPrefix + entry.Address;

    public SystemDataSetDeclaration Declaration { get; } = new(
        entry.Module,
        entry.Table.Requires,
        entry.Table.Isolation switch
        {
            ModuleTableIsolation.None => SystemDataSetIsolation.None,
            ModuleTableIsolation.PerUser => SystemDataSetIsolation.PerUser,
            _ => SystemDataSetIsolation.Unset,
        },
        [entry.Table.Boundary]);

    public bool Handles(string marker) => marker == MarkerOf(entry);

    /// <summary>
    /// Таблица модуля живёт на уровне системы: её строки не зависят от места набора. Предлагать её
    /// на стройке значило бы обещать отбор по стройке, которого здесь нет (он — G1c).
    /// </summary>
    public async Task<IReadOnlyList<DataSetSourceInfo>> GetCandidatesAsync(
        CatalogScope scope, Guid? scopeId, DataAccess access, CancellationToken ct)
    {
        if (scope != CatalogScope.System) return [];
        var provided = await ProvideAsync(MarkerOf(entry), scope, scopeId, access, ct);
        return [new DataSetSourceInfo(entry.Table.Title, MarkerOf(entry), provided.Columns, provided.Rows.Count,
            Warning: provided.Warning)];
    }

    public async Task<DataSetParseResult> ProvideAsync(
        string marker, CatalogScope scope, Guid? scopeId, DataAccess access, CancellationToken ct)
    {
        // Отказ здесь — дефект, а не ответ пользователю: до поставщика доходят только через ворота
        // набора (SystemDataSetGate), а они проверяют то же самое — систему, модуль и ключ таблицы.
        var (table, refusal) = await tables.ReadAsync(entry.Address, access, null, ct);
        if (table is null)
            throw new InvalidOperationException(
                $"Ворота набора пропустили к таблице «{entry.Address}», а служба таблиц отказала: {refusal!.Error}");
        var rows = table.Rows.Select(Strings).ToList();

        var columns = table.Columns
            .Select(c => new DataSetColumnInfo(c.Key, [.. rows
                .Select(r => r.GetValueOrDefault(c.Key))
                .OfType<string>().Where(v => v.Length > 0).Distinct().Take(3)]))
            .ToList();

        // Закрытые колонки остаются колонками; почему они пусты — говорит оговорка. Перечисляем
        // заголовками: ключ «ВТомЧислеНДС» человеку ничего не говорит.
        var closed = table.Columns.Where(c => c.Unavailable is not null).ToList();
        var warning = closed.Count == 0
            ? null
            : string.Join("; ", closed.GroupBy(c => c.Reason).Select(g =>
                $"{(g.Count() == 1 ? "колонка" : "колонки")} {string.Join(", ", g.Select(c => $"«{c.Label}»"))} " +
                $"без значений: {g.Key}"));

        return new DataSetParseResult(columns, rows, warning is null ? null : Capitalize(warning));
    }

    private static IReadOnlyDictionary<string, string?> Strings(IReadOnlyDictionary<string, object?> row) =>
        row.ToDictionary(p => p.Key, p => Text(p.Value), StringComparer.Ordinal);

    /// <summary>Значения наборов — строки; числа и даты — в неизменном виде, а не в виде сервера.</summary>
    private static string? Text(object? value) => value switch
    {
        null => null,
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static string Capitalize(string text) => char.ToUpperInvariant(text[0]) + text[1..];
}
