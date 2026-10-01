using BHS.CRG.Application.Tables;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Modules.Tables;

namespace BHS.CRG.Api.Modules.Tables;

/// <summary>
/// Запрос к таблице модуля из того, что прислал потребитель (ТЗ CORE-33; задача G1c, issue #1090).
///
/// <para><b>Битый отбор отказывает</b> — и проверяется он ЗДЕСЬ, до службы модуля: условие по
/// колонке, которой нет, оператор, не применимый к её виду, значение, которое не разбирается. Модуль
/// получает дерево уже годным и о проверках не думает — иначе каждая таблица проверяла бы своё, и
/// однажды какая-нибудь вернула бы все строки там, где обязана отказать.</para>
///
/// <para><b>По закрытой колонке не отбирают и не сортируют.</b> Значений её человек не видит, но
/// отбор «сумма больше миллиона» выдал бы их по одной: строка есть — значит больше. Отказ называет
/// причину теми же словами, что стоят у колонки.</para>
///
/// <para>Дерево разбирает исполнитель наборов данных (<see cref="DataSetRowFilterExecutor.Parse" />):
/// формат у экрана и у источника один, и второго разбора не заводим.</para>
/// </summary>
public static class ModuleTableQueries
{
    public static (ModuleTableQuery? Query, TableRefusal? Refusal) Build(
        string tableTitle, IReadOnlyList<TableColumnDto> columns, IReadOnlySet<string> open,
        IReadOnlySet<string> shown, TableRequest request, Guid userId)
    {
        var byKey = columns.ToDictionary(c => c.Key, StringComparer.Ordinal);

        TableRefusal? refusal = null;
        var filter = DataSetRowFilterExecutor.Parse(request.Filter, tableTitle) is { } root
            ? Filter(root, byKey, ref refusal)
            : null;
        if (refusal is not null) return (null, refusal);

        var sort = new List<TableSort>();
        foreach (var by in request.Sort ?? [])
        {
            if (Usable(by.Column, byKey, "сортировка", out var column) is { } problem) return (null, problem);
            sort.Add(new TableSort(column!.Key, TableKinds.Parse(column.Kind), by.Descending));
        }

        // Итог по колонке, которой нет или которая закрыта, не считается молча: сама колонка приходит
        // с причиной, и экран показывает её вместо итога.
        var totals = (request.Totals ?? [])
            .Distinct(StringComparer.Ordinal)
            .Where(open.Contains)
            .ToDictionary(key => key, key => TableKinds.Parse(byKey[key].Kind), StringComparer.Ordinal);

        return (new ModuleTableQuery(shown, userId, filter, sort, request.Offset, request.Limit, totals), null);
    }

    /// <summary>Итог модуля → итог для потребителя: с названной причиной неучтённых значений.</summary>
    public static TableTotalDto Total(TableTotal total, ModuleTableColumnKind kind) => new(
        total.Count, total.Skipped,
        total.Skipped == 0 ? null : kind == ModuleTableColumnKind.Date ? "не дата" : "не число",
        total.Sum, total.Average, total.Min, total.Max);

    private static TableFilter? Filter(
        FilterNode node, Dictionary<string, TableColumnDto> columns, ref TableRefusal? refusal)
    {
        if (node.Type == "group")
        {
            var children = new List<TableFilter>();
            foreach (var child in node.Children ?? [])
            {
                if (Filter(child, columns, ref refusal) is not { } converted) return null;
                children.Add(converted);
            }
            return new TableFilterGroup(node.Logic == "or", children);
        }

        if (Usable(node.Column!, columns, "условие отбора", out var column) is { } unusable)
        {
            refusal = unusable;
            return null;
        }

        var (kind, op, values) = (column!.Kind, node.Op ?? "eq", DataSetRowFilterExecutor.ValuesOf(node));
        if (TableConditions.Problem(kind, op, values) is { } problem)
        {
            refusal = new(StatusCodes.Status409Conflict,
                $"Отбор не применён: условие по колонке «{column.Label}» — {problem}. Строки не отданы вовсе: " +
                "отбор, который нельзя выполнить, отдал бы выдачу, неотличимую от правильной.");
            return null;
        }

        return new TableFilterCondition(
            column.Key, TableKinds.Parse(kind), op, values, TableConditions.Compile(kind, op, values));
    }

    /// <summary>Есть ли колонка и открыта ли она; null — годится.</summary>
    private static TableRefusal? Usable(
        string key, Dictionary<string, TableColumnDto> columns, string what, out TableColumnDto? column)
    {
        if (!columns.TryGetValue(key, out column))
            return new(StatusCodes.Status409Conflict,
                $"Не применено: {what} стоит на колонке «{key}», а такой колонки в таблице нет — поле удалено " +
                "из типа или переименовано. Строки не отданы вовсе: без этого условия выдача была бы другой.");

        return column.Unavailable is null
            ? null
            : new(StatusCodes.Status403Forbidden,
                $"Не применено: {what} стоит на колонке «{column.Label}», а она закрыта — {column.Reason}.");
    }
}

/// <summary>
/// Вид колонки в двух записях — перечислением у модуля и строкой у потребителя — и ОДНА таблица
/// соответствия на оба направления. Два отдельных перевода разошлись бы на новом виде: забытый в
/// одном из них молча стал бы текстом.
/// </summary>
public static class TableKinds
{
    private static readonly (ModuleTableColumnKind Kind, string Name)[] Map =
    [
        (ModuleTableColumnKind.Text, TableOperators.Text),
        (ModuleTableColumnKind.Number, TableOperators.Number),
        (ModuleTableColumnKind.Date, TableOperators.Date),
        (ModuleTableColumnKind.Boolean, TableOperators.Boolean),
    ];

    public static string Name(ModuleTableColumnKind kind) => Map.First(m => m.Kind == kind).Name;

    public static ModuleTableColumnKind Parse(string name) => Map.First(m => m.Name == name).Kind;
}
