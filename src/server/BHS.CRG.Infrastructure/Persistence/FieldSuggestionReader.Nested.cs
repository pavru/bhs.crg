using System.Globalization;
using BHS.CRG.Application.Objects.FieldSuggestions;
using Npgsql;
using NpgsqlTypes;

namespace BHS.CRG.Infrastructure.Persistence;

/// <summary>
/// Окно подсказок по путям внутри записей-владельцев (issue #1532): строки массивов и составные поля.
///
/// <para><b>Окно — то же, что у верхнего уровня</b> (ветка на тип над частичным индексом, слияние по
/// свежести, нарастающая сумма размеров), <b>плюс второй предел — строки массивов.</b> Цена массива —
/// разбор строк, а не объём: тысяча документов по 40 строк укладывается в 8 МБ, а стоит 300 мс (замер
/// #1528). Число строк записи — <c>.size()</c> читаемых массивов, без разбора самих строк, и считается
/// только у записей, прошедших предел объёма. Нарастающая сумма строк режет окно второй раз; самая
/// свежая запись остаётся в нём всегда — по той же причине, что и с объёмом.</para>
///
/// <para><b>Значения разворачиваются только у записей окна</b> — подзапросом по первичному ключу, как у
/// верхнего уровня, и по одной строке на разное значение записи: частота — по записям-владельцам, и
/// пятьдесят строк одного журнала с одним значением весят одну запись.</para>
///
/// <para>Пути — <c>jsonpath</c> по ключам схемы, а не поиск ключа в данных. Пути и их «счётчики строк» у
/// ветки — параметрами-массивами: у типа-владельца путей бывает несколько, и запись читается однажды,
/// сколько бы их ни было.</para>
/// </summary>
public sealed partial class FieldSuggestionReader
{
    /// <summary>Ветка окна одного типа-владельца; пути и счётчики строк — его, параметрами.</summary>
    private const string NestedBranch = """
        (SELECT d."Id", d."ScopeLevel", d."ScopeId", d."UpdatedAt", pg_column_size(d."Data") AS "Size",
                @p{0}::text[] AS "Paths", @s{0}::text[] AS "Sizes"
         FROM domain_objects d
         WHERE d."CompositeTypeId" = @o{0} AND d."ArchivedAt" IS NULL
         ORDER BY d."UpdatedAt" DESC
         LIMIT @rows)
        """;

    /// <summary>
    /// Запрос окна для <paramref name="owners" /> типов-владельцев. Параметры: <c>@o0…</c> — типы,
    /// <c>@p0…</c> — их пути, <c>@s0…</c> — счётчики строк, <c>@rows</c>, <c>@bytes</c>, <c>@lines</c> —
    /// пределы. Открыт для теста плана: он объясняет ровно этот текст.
    /// </summary>
    public static string NestedSql(int owners) => $$"""
        SELECT w."Id", w."ScopeLevel", w."ScopeId", w."UpdatedAt", x."Value"
        FROM (
            SELECT c.*, sum(c."Lines") OVER (ORDER BY c."UpdatedAt" DESC, c."Id") AS "LineTotal",
                   row_number() OVER (ORDER BY c."UpdatedAt" DESC, c."Id") AS "N"
            FROM (
                SELECT b.*,
                       (SELECT coalesce(sum(n::numeric), 0)
                        FROM domain_objects o, unnest(b."Sizes") s, jsonb_path_query(o."Data", s::jsonpath) n
                        WHERE o."Id" = b."Id") AS "Lines"
                FROM (
                    SELECT r.*, sum(r."Size") OVER (ORDER BY r."UpdatedAt" DESC, r."Id") AS "Total"
                    FROM (
                        SELECT l.* FROM (
                            {{string.Join("\nUNION ALL\n", Enumerable.Range(0, owners).Select(i => string.Format(CultureInfo.InvariantCulture, NestedBranch, i)))}}
                        ) l
                        ORDER BY l."UpdatedAt" DESC
                        LIMIT @rows
                    ) r
                ) b
                WHERE b."Total" <= @bytes OR b."Total" = b."Size"
            ) c
        ) w
        CROSS JOIN LATERAL (
            SELECT DISTINCT v #>> '{}' AS "Value"
            FROM domain_objects o, unnest(w."Paths") p, jsonb_path_query(o."Data", p::jsonpath) v
            WHERE o."Id" = w."Id"
        ) x
        WHERE w."LineTotal" <= @lines OR w."N" = 1
        """;

    /// <summary>Параметры <see cref="NestedSql" /> — общие у чтения и у теста плана.</summary>
    public static void AddNestedParameters(NpgsqlCommand cmd, IReadOnlyList<NestedSource> nested, SuggestionWindow window)
    {
        foreach (var (source, i) in nested.Select((s, i) => (s, i)))
        {
            cmd.Parameters.Add(new NpgsqlParameter($"o{i}", NpgsqlDbType.Uuid) { Value = source.OwnerTypeId });
            cmd.Parameters.Add(new NpgsqlParameter($"p{i}", NpgsqlDbType.Array | NpgsqlDbType.Text)
                { Value = source.Paths.Select(JsonPath).Distinct().ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter($"s{i}", NpgsqlDbType.Array | NpgsqlDbType.Text)
                { Value = source.Paths.Select(SizePath).OfType<string>().Distinct().ToArray() });
        }
        cmd.Parameters.Add(new NpgsqlParameter("lines", NpgsqlDbType.Integer) { Value = window.MaxLines });
        AddWindow(cmd, window);
    }

    /// <summary>
    /// Путь к значениям поля: каждый шаг — по ключу схемы, массив — все его строки, ссылка на запись
    /// (<c>{ $ref }</c>) отсекается на каждом шаге: её значение учтено верхним уровнем её типа. Значение —
    /// только строка: число или объект в поле строки — порча данных, а не подсказка.
    ///
    /// <para>Режим — нестрогий (<c>lax</c>): в строгом недостающий ключ у ОДНОЙ строки обрывал бы разбор
    /// всей записи, а незаполненная ячейка — обычное дело.</para>
    /// </summary>
    public static string JsonPath(SuggestionPath path) =>
        "$" + string.Concat(path.Steps.Select(Step)) + "." + Quote(path.Field) + " ? (@.type() == \"string\")";

    /// <summary>
    /// Счётчик строк пути: длина последнего массива на нём (у вложенных массивов — у каждого вхождения).
    /// null — массивов на пути нет, строк не прибавляется.
    /// </summary>
    public static string? SizePath(SuggestionPath path)
    {
        var last = path.Steps.ToList().FindLastIndex(s => s.IsArray);
        return last < 0 ? null
            : "$" + string.Concat(path.Steps.Take(last).Select(Step)) + "." + Quote(path.Steps[last].Key) + ".size()";
    }

    private static string Step(PathStep step) =>
        "." + Quote(step.Key) + (step.IsArray ? "[*]" : "") + " ? (!exists(@.\"$ref\"))";

    /// <summary>Ключ строкой <c>jsonpath</c>: кавычки и обратная косая — экранированы.</summary>
    private static string Quote(string key) => "\"" + key.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
