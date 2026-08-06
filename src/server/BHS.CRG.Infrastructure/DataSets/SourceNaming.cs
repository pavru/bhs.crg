using System.Text.RegularExpressions;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Имена источников внутри одного набора (issue #717).
///
/// Имя — единственное, чем источники различаются в селекторе привязки: там нет ни маркера, ни
/// уровня, только «Название · N строк». Пока одна консолидация могла быть занята лишь однажды,
/// совпасть именам было негде; с несколькими источниками на одну консолидацию совпадение стало
/// нормой — два «Документы комплекта» подряд неразличимы, и привязка ставится наугад.
///
/// Отсюда два правила: имя обязано быть свободным (проверяет вызывающий сервис), а предлагаемое по
/// умолчанию — сразу свободным, чтобы диалог не встречал пользователя ошибкой. Нумеруем «— 2»,
/// а не «(копия)»: копия — про происхождение, а различать нужно назначение.
/// </summary>
internal static partial class SourceNaming
{
    [GeneratedRegex(@"\s+—\s+\d+$")]
    private static partial Regex TrailingNumber();

    /// <summary>Имя без нашего же суффикса нумерации: копия «Документы — 2» даёт «Документы — 3»,
    /// а не «Документы — 2 — 2».</summary>
    public static string BaseName(string name) => TrailingNumber().Replace(name.Trim(), string.Empty);

    /// <summary>Ближайшее свободное имя вида «База» / «База — 2» / «База — 3».</summary>
    public static string Next(IEnumerable<string> existingNames, string name)
    {
        var baseName = BaseName(name);
        var taken = new HashSet<string>(existingNames.Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName)) return baseName;

        // Множество занятых имён конечно — очередной номер рано или поздно окажется свободным.
        var n = 2;
        while (taken.Contains($"{baseName} — {n}")) n++;
        return $"{baseName} — {n}";
    }
}
