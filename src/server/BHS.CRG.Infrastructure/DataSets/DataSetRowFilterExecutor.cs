using System.Text.Json;

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
/// <para>Отказ — здесь, в исполнителе, а не проверкой при сохранении настройки: проверка на входе не
/// покрывает то, что уже лежит в базе (условия сохранялись без проверки годами), а исполнитель —
/// единственное место, через которое проходят все пять путей чтения (предпросмотр, выгрузка,
/// генерация, MCP, сверка). Проверка при сохранении была бы удобством; гарантией она стать не может,
/// и заведись она первой — следующий правщик решил бы, что здесь проверять уже нечего.</para>
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
/// источника, принимает объект как есть и не проверяет ничего.</para>
///
/// <para><b>Чего отказом НЕ считаем.</b> Колонка, которой нет в строке, — обычное дело: строки из
/// распознавания и CSV бывают рваные, и такое условие сравнивает с пустым значением (см. тест
/// <c>MissingColumn_TreatedAsEmptyString</c>). Отличить переименованный заголовок от законно
/// отсутствующего исполнителю нечем: состава колонок он не объявляет — это появится вместе с
/// табличным представлением (CORE-33, этап 2). Пустое значение в условии тоже законно: с пустой
/// ячейкой сравнивают намеренно.</para>
/// </summary>
public static class DataSetRowFilterExecutor
{
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Операторы условий — ОДИН список на проверку и на выполнение. Раздельные списки разошлись бы
    /// при добавлении оператора, и разошлись бы в сторону молчания: проверка пропустила бы то, чего
    /// выполнение не умеет.
    /// </summary>
    static readonly Dictionary<string, Func<string, string, bool>> Ops = new(StringComparer.Ordinal)
    {
        ["eq"]           = (val, exp) => string.Equals(val, exp, StringComparison.OrdinalIgnoreCase),
        ["neq"]          = (val, exp) => !string.Equals(val, exp, StringComparison.OrdinalIgnoreCase),
        ["contains"]     = (val, exp) => val.Contains(exp, StringComparison.OrdinalIgnoreCase),
        ["not_contains"] = (val, exp) => !val.Contains(exp, StringComparison.OrdinalIgnoreCase),
        ["starts_with"]  = (val, exp) => val.StartsWith(exp, StringComparison.OrdinalIgnoreCase),
        ["ends_with"]    = (val, exp) => val.EndsWith(exp, StringComparison.OrdinalIgnoreCase),
        ["gt"]           = (val, exp) => CompareNumOrStr(val, exp) > 0,
        ["gte"]          = (val, exp) => CompareNumOrStr(val, exp) >= 0,
        ["lt"]           = (val, exp) => CompareNumOrStr(val, exp) < 0,
        ["lte"]          = (val, exp) => CompareNumOrStr(val, exp) <= 0,
        ["is_empty"]     = (val, _) => string.IsNullOrEmpty(val),
        ["is_not_empty"] = (val, _) => !string.IsNullOrEmpty(val),
    };

    /// <summary>
    /// Что исполнитель умеет. Наружу — ради сторожа: состав обязан совпадать с общим списком
    /// операторов таблиц (<c>TableOperators</c>, G1b), который предлагает условия экрану.
    /// </summary>
    public static IReadOnlyCollection<string> Operators => Ops.Keys;

    /// <param name="sourceName">
    /// Имя источника для текста отказа. У документа привязок бывает пять, и «отбор не разбирается»
    /// без имени не говорит, какую из них править.
    /// </param>
    public static List<IReadOnlyDictionary<string, string?>> Apply(
        string? rowFilterJson,
        List<IReadOnlyDictionary<string, string?>> rows,
        string? sourceName = null)
    {
        if (string.IsNullOrWhiteSpace(rowFilterJson)) return rows;

        FilterNode? root;
        try { root = JsonSerializer.Deserialize<FilterNode>(rowFilterJson, JsonOpts); }
        catch (JsonException ex)
        {
            // Исходная ошибка — во внутреннем исключении, а не в тексте: наружу дословно уходит
            // только наш текст (см. DomainException), а разбор столкновения без причины невозможен.
            throw Refuse(sourceName, "описание отбора не разбирается — текст условий испорчен.", ex);
        }

        if (root is null)
            throw Refuse(sourceName,
                "описание отбора записано значением «null»: условий в нём нет, и отсутствием отбора "
                + "это не считается.");

        Validate(root, "", sourceName);

        return rows.Where(row => Evaluate(root, row)).ToList();
    }

    /// <summary>
    /// Проверка дерева до отбора. Путь узла — номера по уровням от корня («2.1» — первый ребёнок
    /// второго узла корня): колонки в дереве повторяются, и одной колонки для «какое именно условие»
    /// не хватает.
    /// </summary>
    static void Validate(FilterNode node, string path, string? sourceName)
    {
        if (node.Type == "condition")
        {
            if (string.IsNullOrWhiteSpace(node.Column))
                throw Refuse(sourceName, $"{Place("условие", path)} не называет колонку.");

            // Оператор по умолчанию — «eq»: условия сохранялись без него, и менять им смысл нельзя.
            var op = node.Op ?? "eq";
            if (!Ops.ContainsKey(op))
                throw Refuse(sourceName,
                    $"{Place("условие", path)} по колонке «{node.Column}» задано оператором «{op}», "
                    + "которого нет.");
            return;
        }

        if (node.Type != "group")
            throw Refuse(sourceName,
                $"{Place("узел", path)} назван видом «{node.Type}», которого нет: бывают «condition» "
                + "и «group».");

        // Логику проверяем и у группы без условий: негодная логика — это опечатка в настройке, а не
        // повод молча применить «and».
        if (node.Logic is not ("and" or "or"))
            throw Refuse(sourceName,
                $"{Place("группа", path)} связывает условия логикой «{node.Logic}», которой нет: "
                + "бывают «and» и «or».");

        var children = node.Children ?? [];
        for (var i = 0; i < children.Length; i++)
            Validate(children[i], path.Length == 0 ? $"{i + 1}" : $"{path}.{i + 1}", sourceName);
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

    static bool Evaluate(FilterNode node, IReadOnlyDictionary<string, string?> row)
    {
        if (node.Type == "condition")
            return Match(node, row);

        // group — вид узла и логика уже проверены (Validate), так что иных ветвей здесь нет.
        var children = node.Children ?? [];
        if (children.Length == 0) return true;   // группа без условий ничего не ограничивает
        return node.Logic == "or"
            ? children.Any(c => Evaluate(c, row))
            : children.All(c => Evaluate(c, row));
    }

    static bool Match(FilterNode cond, IReadOnlyDictionary<string, string?> row)
    {
        var col = cond.Column ?? "";
        var val = row.TryGetValue(col, out var v) ? v ?? "" : "";
        var expected = cond.Value ?? "";

        return Ops[cond.Op ?? "eq"](val, expected);
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
