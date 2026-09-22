using System.Text.Json;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Documents;

/// <summary>
/// Происхождение поля схемы (ТЗ CORE-19.1). На нём держится весь замок: поле, созданное МОДУЛЕМ,
/// администратор не правит, а своё — правит свободно.
/// </summary>
public static class SchemaFieldOrigin
{
    /// <summary>Имя свойства в JSON поля.</summary>
    public const string Property = "origin";

    /// <summary>Поле завёл модуль при включении. На него опирается его код.</summary>
    public const string Module = "module";

    /// <summary>Поле добавил администратор. Значение по умолчанию: отсутствие свойства — оно.</summary>
    public const string Customer = "customer";
}

/// <summary>
/// Замок поля (issue #957, ТЗ CORE-20.2): значение кладёт код модуля, а не человек за формой.
/// Охрана записи требует оставить запертое поле бит-в-бит таким, как оно лежит.
///
/// <para>⚠️ Отдельная метка, а НЕ <see cref="SchemaFieldOrigin.Module"/>: происхождение говорит,
/// кто ОБЪЯВИЛ поле, а не кто пишет значение. Поле модуля «Табельный номер» человек как раз
/// заполняет руками, и «поле модуля ⇒ значение заперто» заперло бы форму на ровном месте.</para>
/// </summary>
public static class SchemaFieldLock
{
    /// <summary>Имя свойства в JSON поля.</summary>
    public const string Property = "locked";
}

/// <summary>
/// Что администратору можно сделать со схемой типа — таблица ТЗ CORE-19.1, выраженная кодом.
///
/// Проверка живёт НА СЕРВЕРЕ и при сохранении СХЕМЫ: старая схема сравнивается с новой по полям
/// модуля, отказ называет поле и причину. Клиент может (и будет) прятать запрещённые действия, но
/// прятать — не значит запрещать: у схемы есть адрес, и он обязан отвечать отказом сам.
///
/// ⚠️ Переименование поля неотличимо от «удалил одно, добавил другое»: в сохранённой схеме нет
/// личности поля, только ключ. Поэтому оба случая ловятся одним правилом — «поле модуля исчезло», —
/// и отказ говорит «удалено или переименовано», а не гадает, что именно случилось.
///
/// ⚠️ Чего эта проверка НЕ делает: не разбирает <c>excludedFields</c> по происхождению. Правка
/// набора исключений считается правкой состава полей ПЕССИМИСТИЧНО — на уровнях «расширяемый» и
/// «закрытый» запрещено и добавить исключение, и снять его: первое убирает поле из эффективной
/// схемы, второе добавляет. Разбирать чужие поля по происхождению пришлось бы через всю цепочку
/// наследования, а цена ошибки здесь несимметрична: лишний отказ виден и обсуждаем, пропущенное
/// удаление поля модуля — нет.
/// </summary>
public static class SchemaEditPolicy
{
    /// <summary>
    /// Чем правка запрещена — словами, готовыми к показу человеку. Пусто — правка разрешена.
    /// </summary>
    public static IReadOnlyList<string> Refusals(JsonDocument before, JsonDocument after, SchemaEditLevel level)
    {
        var problems = new List<string>();
        var old = Snapshot(before);
        var now = Snapshot(after);

        // ── Замок: проверяется НА ЛЮБОМ УРОВНЕ, до раннего выхода ─────────────
        //
        // ⚠️ Здесь, а не ниже, потому что замок действует в ДАННЫХ (issue #957), и уровень правки
        // схемы ему не указ: поставив «locked» своему полю в открытом типе, администратор запер бы
        // себе форму без способа снять замок — метку снимает та же проверка, под которую он попал.
        // Происхождение поля, наоборот, остаётся НИЖЕ раннего выхода намеренно: оно значит что-то
        // только там, где уровень запирает, а над выходом отказывало бы законному переименованию
        // поля модуля в открытом типе (чинилось в #956).
        //
        // Цена, вслух: запертое поле нельзя и переименовать — в сохранённой схеме личности поля
        // нет, и переименование неотличимо от «снял замок с одного, повесил на другое».
        foreach (var field in now.Fields.Values)
            if (field.Locked && (!old.Fields.TryGetValue(field.Key, out var wasLocked) || !wasLocked.Locked))
                problems.Add($"поле «{Name(field)}» нельзя запереть: замок ставит модуль на свои поля, " +
                             "а не редактор типов");

        foreach (var field in old.Fields.Values)
            if (field.Locked && now.Fields.TryGetValue(field.Key, out var nowLocked) && !nowLocked.Locked)
                problems.Add($"с поля «{Name(field)}» нельзя снять замок: его значение кладёт код модуля");

        // На открытом уровне администратор «меняет схему как хочет» — и это вся проверка.
        // Происхождение поля там тоже ни от чего не защищает: запирать в открытом типе нечего.
        if (level == SchemaEditLevel.Open) return problems;

        var closed = level == SchemaEditLevel.Closed;

        // ⚠️ Происхождение — основа замка, поэтому оно проверяется ОТДЕЛЬНО от остальных свойств
        // поля. Убери его администратор у поля модуля — и поле стало бы «своим», то есть открытым
        // для всего сразу; объяви он своё поле полем модуля — запер бы себе то, о чём модуль не
        // знает, и снять замок было бы нечем.
        //
        // Переименование поля модуля сюда не попадает: в сохранённой схеме личности поля нет,
        // переименование выглядит как «удалил одно, добавил другое», и ловит его правило удаления.
        foreach (var field in now.Fields.Values)
            if (field.FromModule
                && (!old.Fields.TryGetValue(field.Key, out var previously) || !previously.FromModule))
                problems.Add($"поле «{Name(field)}» нельзя объявить полем модуля: поля модуля заводит " +
                             "модуль при включении, а не редактор типов");

        foreach (var field in old.Fields.Values)
            if (field.FromModule && now.Fields.TryGetValue(field.Key, out var stillThere) && !stillThere.FromModule)
                problems.Add($"у поля «{Name(field)}» нельзя убрать происхождение «поле модуля»: " +
                             "замок снимается вместе с ним");

        // ── Добавление полей ──────────────────────────────────────────────────
        foreach (var field in now.Fields.Values.Where(f => !old.Fields.ContainsKey(f.Key)))
        {
            if (closed)
                problems.Add($"поле «{Name(field)}» нельзя добавить: схему закрытого типа задаёт модуль");
            else if (field.Required)
                problems.Add($"поле «{Name(field)}» нельзя добавить обязательным: прежние записи его " +
                             "не заполнят, а старые устройства о нём не знают — добавьте необязательным");
        }

        // ── Удаление (и переименование) полей ─────────────────────────────────
        foreach (var field in old.Fields.Values.Where(f => !now.Fields.ContainsKey(f.Key)))
        {
            if (field.FromModule)
                problems.Add($"поле модуля «{Name(field)}» удалено или переименовано: на него опирается код модуля");
            else if (closed)
                problems.Add($"поле «{Name(field)}» удалено: в закрытом типе администратору остаётся только подпись");
        }

        // ── Правка существующих полей ─────────────────────────────────────────
        foreach (var (key, was) in old.Fields)
        {
            if (!now.Fields.TryGetValue(key, out var became)) continue;

            if (was.FromModule)
            {
                if (was.Type != became.Type || was.TypeId != became.TypeId)
                    problems.Add($"у поля модуля «{Name(was)}» нельзя сменить вид: по нему модуль читает значение");
                // Расчётное поле не вводится и не хранится вовсе (issue #368). Сделать таким поле
                // модуля — не «сменить вид», а увести значение из-под модуля совсем; поэтому
                // отдельной строкой, а не внутри проверки вида. Найдено ревью PR #1004.
                if (was.Computed != became.Computed || was.Expression != became.Expression)
                    problems.Add($"поле модуля «{Name(was)}» нельзя сделать расчётным: " +
                                 "вычисленное значение не вводится и не хранится, а модуль его ждёт");
                if (!was.Tags.SequenceEqual(became.Tags))
                    problems.Add($"тэги поля модуля «{Name(was)}» ставит модуль: по ним его находит код");
                if (was.Required != became.Required)
                    problems.Add($"обязательность поля модуля «{Name(was)}» задаёт модуль");
                if (!JsonEquals(was.DefaultValue, became.DefaultValue))
                    problems.Add($"значение по умолчанию у поля модуля «{Name(was)}» задаёт модуль");
                if (closed && !was.Options.SequenceEqual(became.Options))
                    problems.Add($"варианты поля «{Name(was)}» задаёт модуль");
                else if (!closed && was.Options.Any(o => !became.Options.Contains(o)))
                    problems.Add($"из вариантов поля модуля «{Name(was)}» нельзя убирать: " +
                                 "прежние записи ссылаются на них — добавлять можно");
            }
            else if (closed)
            {
                if (was.Type != became.Type || was.TypeId != became.TypeId || was.Required != became.Required
                    || was.Computed != became.Computed || was.Expression != became.Expression
                    || !JsonEquals(was.DefaultValue, became.DefaultValue)
                    || !was.Tags.SequenceEqual(became.Tags) || !was.Options.SequenceEqual(became.Options))
                    problems.Add($"поле «{Name(was)}» правке не подлежит: в закрытом типе " +
                                 "администратору остаётся только подпись");
            }
            else if (was.Required != became.Required && became.Required)
            {
                // Строки «добавить обязательное поле» в таблице касается и это: сделать
                // существующее поле обязательным — то же самое по последствиям, прежние записи его
                // так же не заполнят.
                problems.Add($"поле «{Name(was)}» нельзя сделать обязательным: прежние записи его не заполнят");
            }
        }

        // ── Порядок, группы, переопределения, исключения ──────────────────────
        if (closed && !old.Order.SequenceEqual(now.Order))
            problems.Add("порядок полей закрытого типа задаёт модуль: администратору остаётся подпись");
        if (closed && !JsonEquals(old.Groups, now.Groups))
            problems.Add("группы полей закрытого типа задаёт модуль: администратору остаётся подпись");
        if (closed && !JsonEquals(old.TypeTags, now.TypeTags))
            problems.Add("тэги закрытого типа ставит модуль: по ним его находит код");

        // Переопределения касаются УНАСЛЕДОВАННЫХ полей — то есть чужих. В закрытом типе это та же
        // правка схемы, только через заднюю дверь: значение по умолчанию и обязательность меняются
        // не у своего поля.
        if (closed && !JsonEquals(old.Overrides, now.Overrides))
            problems.Add("переопределения унаследованных полей в закрытом типе задаёт модуль");

        // ⚠️ Сравнивается НАБОР исключений целиком, в обе стороны. Одностороннюю проверку («нельзя
        // добавить исключение») обходит снятие: вернув исключённое поле, администратор добавляет
        // поле в эффективную схему — ровно то, что запрещено. Найдено ревью PR #1004.
        foreach (var excluded in now.Excluded.Where(e => !old.Excluded.Contains(e)))
            problems.Add($"унаследованное поле «{excluded}» нельзя исключить: оно принадлежит родительскому типу");
        foreach (var returned in old.Excluded.Where(e => !now.Excluded.Contains(e)))
            problems.Add($"унаследованное поле «{returned}» нельзя вернуть: это то же добавление поля, " +
                         "только из родительского типа");

        return problems;
    }

    /// <summary>Имя поля для человека: подпись, а если её нет — ключ.</summary>
    private static string Name(FieldSnapshot field) =>
        string.IsNullOrWhiteSpace(field.Title) ? field.Key : field.Title!;

    /// <summary>Ровно то, что сравнивается, и ничего больше.</summary>
    private sealed record FieldSnapshot(
        string Key, string Type, string? TypeId, bool Required, string? Title,
        IReadOnlyList<string> Tags, IReadOnlyList<string> Options, bool FromModule,
        bool Computed, string? Expression, JsonElement? DefaultValue, bool Locked);

    private sealed record SchemaSnapshot(
        IReadOnlyDictionary<string, FieldSnapshot> Fields, IReadOnlyList<string> Order,
        IReadOnlyList<string> Excluded, JsonElement? Groups, JsonElement? Overrides, JsonElement? TypeTags);

    /// <summary>
    /// ⚠️ Куски JSON сравниваются ПО СОДЕРЖАНИЮ, а не текстом. Старая схема приходит из
    /// <c>jsonb</c>, который Postgres отдаёт со своими пробелами, новая — из тела запроса, где
    /// клиент шлёт компактную запись: текстом они не совпадут НИКОГДА, и закрытый тип с группами
    /// отказывал бы на любом сохранении. Тесты этого не видели — в них обе схемы разбирались из
    /// одинаково отформатированных литералов. Найдено ревью PR #1004.
    /// </summary>
    private static bool JsonEquals(JsonElement? a, JsonElement? b) => (a, b) switch
    {
        (null, null) => true,
        ({ } x, { } y) => JsonElement.DeepEquals(x, y),
        _ => false,
    };

    private static SchemaSnapshot Snapshot(JsonDocument schema)
    {
        var fields = new Dictionary<string, FieldSnapshot>(StringComparer.Ordinal);
        var order = new List<string>();
        var excluded = new List<string>();
        var root = schema.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return new SchemaSnapshot(fields, order, excluded, null, null, null);

        if (root.TryGetProperty("fields", out var fs) && fs.ValueKind == JsonValueKind.Array)
            foreach (var f in fs.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Object) continue;
                var key = Str(f, "key");
                if (string.IsNullOrEmpty(key)) continue;
                var snapshot = new FieldSnapshot(
                    key,
                    Str(f, "type") ?? "string",
                    Str(f, "typeId"),
                    f.TryGetProperty("required", out var rq) && rq.ValueKind == JsonValueKind.True,
                    Str(f, "title"),
                    Strings(f, "tags"),
                    Strings(f, "options"),
                    string.Equals(Str(f, SchemaFieldOrigin.Property), SchemaFieldOrigin.Module, StringComparison.Ordinal),
                    f.TryGetProperty("computed", out var cp) && cp.ValueKind == JsonValueKind.True,
                    Str(f, "expression"),
                    Element(f, "defaultValue"),
                    f.TryGetProperty(SchemaFieldLock.Property, out var lk) && lk.ValueKind == JsonValueKind.True);
                fields[key] = snapshot;
                order.Add(key);
            }

        if (root.TryGetProperty("excludedFields", out var ex) && ex.ValueKind == JsonValueKind.Array)
            excluded.AddRange(ex.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!));

        // Группы, переопределения и тэги ТИПА сравниваются целиком: их состав, порядок и
        // содержимое — одинаково «оформление» либо одинаково «разметка модуля», и различать их
        // отдельными правилами значило бы придумывать оттенки, которых в таблице ТЗ нет.
        return new SchemaSnapshot(fields, order, excluded,
            Element(root, "groups"), Element(root, "fieldOverrides"), Element(root, "tags"));
    }

    /// <summary>Кусок JSON как значение: <c>Clone()</c>, потому что документ живёт короче снимка.</summary>
    private static JsonElement? Element(JsonElement node, string name) =>
        node.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Undefined ? v.Clone() : null;

    private static string? Str(JsonElement node, string name) =>
        node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyList<string> Strings(JsonElement node, string name) =>
        node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
            : [];
}
