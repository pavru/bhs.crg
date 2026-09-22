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
/// ⚠️ Чего эта проверка НЕ делает: не разбирает <c>excludedFields</c> по происхождению. Исключение
/// унаследованного поля в производном типе считается удалением ПЕССИМИСТИЧНО — на уровнях
/// «расширяемый» и «закрытый» запрещено любое новое исключение. Разбирать чужие поля по
/// происхождению пришлось бы через всю цепочку наследования, а цена ошибки здесь несимметрична:
/// лишний отказ виден и обсуждаем, пропущенное удаление поля модуля — нет.
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
                if (!was.Tags.SequenceEqual(became.Tags))
                    problems.Add($"тэги поля модуля «{Name(was)}» ставит модуль: по ним его находит код");
                if (was.Required != became.Required)
                    problems.Add($"обязательность поля модуля «{Name(was)}» задаёт модуль");
                if (closed && !was.Options.SequenceEqual(became.Options))
                    problems.Add($"варианты поля «{Name(was)}» задаёт модуль");
                else if (!closed && was.Options.Any(o => !became.Options.Contains(o)))
                    problems.Add($"из вариантов поля модуля «{Name(was)}» нельзя убирать: " +
                                 "прежние записи ссылаются на них — добавлять можно");
            }
            else if (closed)
            {
                if (was.Type != became.Type || was.TypeId != became.TypeId || was.Required != became.Required
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

        // ── Порядок, группы, исключения ───────────────────────────────────────
        if (closed && !old.Order.SequenceEqual(now.Order))
            problems.Add("порядок полей закрытого типа задаёт модуль: администратору остаётся подпись");
        if (closed && old.Groups != now.Groups)
            problems.Add("группы полей закрытого типа задаёт модуль: администратору остаётся подпись");

        foreach (var excluded in now.Excluded.Where(e => !old.Excluded.Contains(e)))
            problems.Add($"унаследованное поле «{excluded}» нельзя исключить: оно принадлежит родительскому типу");

        return problems;
    }

    /// <summary>Имя поля для человека: подпись, а если её нет — ключ.</summary>
    private static string Name(FieldSnapshot field) =>
        string.IsNullOrWhiteSpace(field.Title) ? field.Key : field.Title!;

    /// <summary>Ровно то, что сравнивается, и ничего больше.</summary>
    private sealed record FieldSnapshot(
        string Key, string Type, string? TypeId, bool Required, string? Title,
        IReadOnlyList<string> Tags, IReadOnlyList<string> Options, bool FromModule);

    private sealed record SchemaSnapshot(
        IReadOnlyDictionary<string, FieldSnapshot> Fields, IReadOnlyList<string> Order,
        IReadOnlyList<string> Excluded, string Groups);

    private static SchemaSnapshot Snapshot(JsonDocument schema)
    {
        var fields = new Dictionary<string, FieldSnapshot>(StringComparer.Ordinal);
        var order = new List<string>();
        var excluded = new List<string>();
        var groups = string.Empty;
        var root = schema.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return new SchemaSnapshot(fields, order, excluded, groups);

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
                    string.Equals(Str(f, SchemaFieldOrigin.Property), SchemaFieldOrigin.Module, StringComparison.Ordinal));
                fields[key] = snapshot;
                order.Add(key);
            }

        if (root.TryGetProperty("excludedFields", out var ex) && ex.ValueKind == JsonValueKind.Array)
            excluded.AddRange(ex.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!));

        // Группы сравниваются целиком, текстом: их состав, порядок и состав полей внутри —
        // одинаково «оформление», и различать их тремя правилами значило бы придумывать оттенки,
        // которых в таблице ТЗ нет.
        if (root.TryGetProperty("groups", out var gr)) groups = gr.GetRawText();

        return new SchemaSnapshot(fields, order, excluded, groups);
    }

    private static string? Str(JsonElement node, string name) =>
        node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyList<string> Strings(JsonElement node, string name) =>
        node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
            : [];
}
