using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Tables;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Отбор строк источника по дереву условий (<see cref="FilterNode" />).
///
/// <para><b>Отбор, который нельзя выполнить, отказывает</b> (ТЗ CORE-33 частью, issue #966). До
/// 0.199.1 было наоборот, и оба случая выглядели удачей: испорченное описание возвращало ВСЕ строки —
/// как будто отбора нет вовсе, — а условие с неизвестным оператором считалось истиной, то есть
/// пропускало строки насквозь. Ошибиться было негде: выдача выглядела обычной, разница видна только
/// тому, кто помнит, сколько строк ожидал. В печатной форме такое расхождение вскрылось бы после
/// подписи.</para>
///
/// <para>Отказ — здесь, в исполнителе, а не только проверкой при сохранении настройки: проверка на
/// входе не покрывает то, что уже лежит в базе (условия сохранялись без проверки годами), а
/// исполнитель — единственное место, через которое проходят все пять путей чтения (предпросмотр,
/// выгрузка, генерация, MCP, сверка). Проверка при сохранении (issue #1137) — удобство: человек
/// узнаёт об ошибке там, где ошибся. Гарантией она стать не может, и убирать отказ отсюда на том
/// основании, что «на входе уже проверено», нельзя: мимо входа идут восстановление из копии, копия
/// источника и всё сохранённое раньше. Проверяет она тем же разбором (<see cref="Problem" />) —
/// второго списка правил нет.</para>
///
/// <para><b>Дерево проверяется целиком и ДО первой строки.</b> Проверка по ходу отбора молчала бы на
/// источнике, который сегодня вернул ноль строк, — то есть сторожа не было бы ровно в том состоянии,
/// где его труднее всего заметить глазами. Глубину вложенности ограничивает сам разбор JSON
/// (<c>MaxDepth</c> = 64 по умолчанию): дерево глубже разбор не примет, и рекурсия здесь ограничена
/// тем же числом.</para>
///
/// <para><b>Как битый отбор выглядит на живых данных.</b> Колонка — <c>jsonb</c>, поэтому ломаного
/// ТЕКСТА в базе не бывает: его не принимает сама база. Достижимы два вида — годный JSON чужой формы
/// (отбор, сохранённый строкой или массивом условий вместо корневой группы) и годное дерево с
/// негодным содержимым (оператор, вид узла или логика, которых нет). Служба, сохраняющая настройку
/// источника, до #1137 принимала объект как есть и не проверяла ничего.</para>
///
/// <para><b>Чего отказом НЕ считаем.</b> Колонка, которой нет в строке, — обычное дело: строки из
/// распознавания и CSV бывают рваные, и такое условие сравнивает с пустым значением (см. тест
/// <c>MissingColumn_TreatedAsEmptyString</c>). Отличить переименованный заголовок от законно
/// отсутствующего исполнителю нечем: состава колонок он не объявляет — это появится вместе с
/// табличным представлением (CORE-33, этап 2). Пустое значение в условии тоже законно: с пустой
/// ячейкой сравнивают намеренно.</para>
///
/// <para><b>Два режима сравнения</b> (G1c, issue #1090). У файловых наборов вида колонок нет, и
/// сравнение идёт по догадке — как было всегда, и менять его нельзя: на нём стоят сохранённые отборы.
/// У набора, чей поставщик объявил виды колонок (таблица модуля), условие по такой колонке сравнивает
/// ПО ВИДУ — правилами <see cref="TableConditions" />, теми же, что запрос к базе у экрана таблицы.
/// Иначе один сохранённый отбор давал бы на экране одни строки, а в наборе данных другие.</para>
/// </summary>
public static class DataSetRowFilterExecutor
{
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Операторы условий — ОДИН список на проверку и на выполнение. Раздельные списки разошлись бы
    /// при добавлении оператора, и разошлись бы в сторону молчания: проверка пропустила бы то, чего
    /// выполнение не умеет.
    /// </summary>
    static readonly Dictionary<string, Func<string, IReadOnlyList<string>, bool>> Ops = new(StringComparer.Ordinal)
    {
        ["eq"]           = (val, exp) => Same(val, exp[0]),
        ["neq"]          = (val, exp) => !Same(val, exp[0]),
        ["contains"]     = (val, exp) => val.Contains(exp[0], StringComparison.OrdinalIgnoreCase),
        ["not_contains"] = (val, exp) => !val.Contains(exp[0], StringComparison.OrdinalIgnoreCase),
        ["starts_with"]  = (val, exp) => val.StartsWith(exp[0], StringComparison.OrdinalIgnoreCase),
        ["ends_with"]    = (val, exp) => val.EndsWith(exp[0], StringComparison.OrdinalIgnoreCase),
        ["gt"]           = (val, exp) => CompareNumOrStr(val, exp[0]) > 0,
        ["gte"]          = (val, exp) => CompareNumOrStr(val, exp[0]) >= 0,
        ["lt"]           = (val, exp) => CompareNumOrStr(val, exp[0]) < 0,
        ["lte"]          = (val, exp) => CompareNumOrStr(val, exp[0]) <= 0,
        // Узлы G1c — у файловых наборов той же догадкой, что и остальные сравнения.
        ["between"]      = (val, exp) => CompareNumOrStr(val, exp[0]) >= 0 && CompareNumOrStr(val, exp[1]) <= 0,
        ["in"]           = (val, exp) => exp.Any(e => Same(val, e)),
        ["not_in"]       = (val, exp) => !exp.Any(e => Same(val, e)),
        ["is_empty"]     = (val, _) => string.IsNullOrEmpty(val),
        ["is_not_empty"] = (val, _) => !string.IsNullOrEmpty(val),
        ["is_null"]      = (val, _) => string.IsNullOrEmpty(val),
        ["is_not_null"]  = (val, _) => !string.IsNullOrEmpty(val),
    };

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Что исполнитель умеет. Наружу — ради сторожа: состав обязан совпадать с общим списком
    /// операторов таблиц (<c>TableOperators</c>, G1b), который предлагает условия экрану.
    /// </summary>
    public static IReadOnlyCollection<string> Operators => Ops.Keys;

    /// <param name="sourceName">
    /// Имя источника для текста отказа. У документа привязок бывает пять, и «отбор не разбирается»
    /// без имени не говорит, какую из них править.
    /// </param>
    /// <param name="types">Виды колонок, если поставщик их объявил; null — сравнение по догадке.</param>
    public static List<IReadOnlyDictionary<string, string?>> Apply(
        string? rowFilterJson,
        List<IReadOnlyDictionary<string, string?>> rows,
        string? sourceName = null,
        DataSetColumnTypes? types = null)
    {
        if (Parse(rowFilterJson, sourceName, types) is not { } root) return rows;
        var test = Compile(root, types);
        return rows.Where(test).ToList();
    }

    /// <summary>
    /// Разобранный и ПРОВЕРЕННЫЙ отбор; null — отбора нет. Наружу — для экрана таблицы: он исполняет
    /// то же дерево запросом к базе, и разбирать его вторым способом значило бы завести второй язык
    /// отборов (ТЗ CORE-33).
    /// </summary>
    public static FilterNode? Parse(string? rowFilterJson, string? sourceName = null, DataSetColumnTypes? types = null)
    {
        var (root, problem, cause) = Read(rowFilterJson, types);
        return problem is null ? root : throw Refuse(sourceName, problem, cause);
    }

    /// <summary>
    /// Что не так с отбором; null — годен либо его нет. Для сохранения настройки (issue #1137): там
    /// отказ звучит иначе («не сохранён», а не «строки не отданы»), а правила обязаны быть теми же,
    /// что при чтении, — поэтому это тот же разбор, а не вторая проверка.
    /// </summary>
    public static string? Problem(string? rowFilterJson, DataSetColumnTypes? types = null) =>
        Read(rowFilterJson, types).Problem;

    static (FilterNode? Root, string? Problem, Exception? Cause) Read(string? rowFilterJson, DataSetColumnTypes? types)
    {
        if (string.IsNullOrWhiteSpace(rowFilterJson)) return (null, null, null);

        FilterNode? root;
        try { root = JsonSerializer.Deserialize<FilterNode>(rowFilterJson, JsonOpts); }
        catch (JsonException ex)
        {
            // Исходная ошибка — во внутреннем исключении, а не в тексте: наружу дословно уходит
            // только наш текст (см. DomainException), а разбор столкновения без причины невозможен.
            return (null, "описание отбора не разбирается — текст условий испорчен.", ex);
        }

        if (root is null)
            return (null,
                "описание отбора записано значением «null»: условий в нём нет, и отсутствием отбора "
                + "это не считается.", null);

        return (root, Check(root, "", types), null);
    }

    /// <summary>
    /// Значения условия. У оператора с одним значением оно лежит в <c>value</c> (и пустое законно —
    /// с пустой ячейкой сравнивают намеренно); у «in» и «between» — списком в <c>values</c>.
    /// </summary>
    public static IReadOnlyList<string> ValuesOf(FilterNode condition)
    {
        if (condition.Values is { Length: > 0 } list) return list;
        return TableOperators.Arity(condition.Op ?? "eq") == 1 ? [condition.Value ?? ""] : [];
    }

    /// <summary>
    /// Проверка дерева до отбора: первое, что в нём не так, либо null. Путь узла — номера по уровням
    /// от корня («2.1» — первый ребёнок второго узла корня): колонки в дереве повторяются, и одной
    /// колонки для «какое именно условие» не хватает.
    /// </summary>
    static string? Check(FilterNode node, string path, DataSetColumnTypes? types)
    {
        if (node.Type == "condition")
        {
            if (string.IsNullOrWhiteSpace(node.Column))
                return $"{Place("условие", path)} не называет колонку.";

            // Оператор по умолчанию — «eq»: условия сохранялись без него, и менять им смысл нельзя.
            var op = node.Op ?? "eq";
            if (!Ops.ContainsKey(op))
                return $"{Place("условие", path)} по колонке «{node.Column}» задано оператором «{op}», "
                    + "которого нет.";

            // Значение — в одном месте: «value» и «values» разом — это два разных условия в одном
            // узле, и какое из них имел в виду автор, исполнитель не знает.
            if (node.Value is not null && node.Values is { Length: > 0 })
                return $"{Place("условие", path)} по колонке «{node.Column}» несёт значение дважды — и в "
                    + "«value», и в «values».";

            var values = ValuesOf(node);
            if (TableOperators.ArityProblem(op, values) is { } arity)
                return $"{Place("условие", path)} по колонке «{node.Column}»: {arity}.";

            // Колонка, пришедшая без значений (нет права на суммы): у пустых клеток отбор вернул бы
            // «ничего не нашлось» — человек без права получил бы пустой набор вместо отказа.
            if (types is not null && types.Closed.TryGetValue(node.Column, out var reason))
                return $"{Place("условие", path)} стоит на колонке «{node.Column}», а она пришла без "
                    + $"значений: {reason}.";

            if (types is not null && types.Kinds.TryGetValue(node.Column, out var kind)
                && TableConditions.Problem(kind, op, values) is { } problem)
                return $"{Place("условие", path)} по колонке «{node.Column}»: {problem}.";
            return null;
        }

        if (node.Type != "group")
            return $"{Place("узел", path)} назван видом «{node.Type}», которого нет: бывают «condition» "
                + "и «group».";

        // Логику проверяем и у группы без условий: негодная логика — это опечатка в настройке, а не
        // повод молча применить «and».
        if (node.Logic is not ("and" or "or"))
            return $"{Place("группа", path)} связывает условия логикой «{node.Logic}», которой нет: "
                + "бывают «and» и «or».";

        var children = node.Children ?? [];
        for (var i = 0; i < children.Length; i++)
        {
            var childPath = path.Length == 0 ? $"{i + 1}" : $"{path}.{i + 1}";
            // «null» среди узлов разбор пропускает как значение — и без этой строки отказом был бы
            // NullReferenceException: 500 без текста вместо названной причины и на сохранении, и на
            // чтении отбора, приехавшего из копии.
            if (children[i] is null)
                return $"{Place("узел", childPath)} записан значением «null»: ни условия, ни группы в нём нет.";
            if (Check(children[i], childPath, types) is { } problem) return problem;
        }
        return null;
    }

    static string Place(string kind, string path)
        => path.Length == 0 ? $"{kind} в корне отбора" : $"{kind} {path}";

    static ConflictException Refuse(string? sourceName, string what, Exception? inner = null)
    {
        var named = string.IsNullOrWhiteSpace(sourceName) ? "" : $"«{sourceName}» ";
        return new ConflictException(
            $"Отбор строк источника {named}не применён: {what} Строки не отданы вовсе — отбор, который "
            + "нельзя выполнить, отдал бы выдачу, неотличимую от правильной. Исправьте условия отбора.",
            inner);
    }

    /// <summary>
    /// Дерево, готовое к строкам: колонка, оператор и значения каждого условия разобраны ОДИН раз, а
    /// не на каждой строке. Дерево обязано быть проверенным (<see cref="Check" />).
    /// </summary>
    static Func<IReadOnlyDictionary<string, string?>, bool> Compile(FilterNode node, DataSetColumnTypes? types)
    {
        if (node.Type == "condition")
        {
            var column = node.Column ?? "";
            var op = node.Op ?? "eq";
            var values = ValuesOf(node);

            // Колонка с объявленным видом — по виду; остальные (файл, вычисляемая колонка) — по догадке.
            Func<string?, bool> test = types is not null && types.Kinds.TryGetValue(column, out var kind)
                ? TableConditions.Compile(kind, op, values)
                : cell => Ops[op](cell ?? "", values);
            return row => test(row.TryGetValue(column, out var cell) ? cell : null);
        }

        // group — вид узла и логика уже проверены (Check), так что иных ветвей здесь нет.
        var children = (node.Children ?? []).Select(c => Compile(c, types)).ToArray();
        if (children.Length == 0) return _ => true;   // группа без условий ничего не ограничивает
        return node.Logic == "or"
            ? row => children.Any(c => c(row))
            : row => children.All(c => c(row));
    }

    static int CompareNumOrStr(string a, string b)
    {
        if (double.TryParse(a, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var da) &&
            double.TryParse(b, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var db))
            return da.CompareTo(db);
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
