namespace BHS.CRG.Application.QualityDocs;

/// <summary>
/// Каноническая нормализация ключа сопоставления «строка→объект» (issue #183) — единая точка для
/// всех путей матчинга (документы качества, резолвер строка→объект). Одинаково применяется при
/// СОЗДАНИИ ключа связи и при матче на генерации/резолве.
///
/// Правило: убрать окружающие/повторяющиеся пробелы → срезать завершающие точки/пробелы → регистр.
/// «Шт.», «шт», «шт » и «Ш т» → «шт»; «Провод ВВГ 3х2.5 » → «провод ввг 3х2.5».
/// (Ранее было два расходящихся нормализатора: MaterialKeyNormalizer без среза точек и
/// DataSetResolver.Normalize без схлопывания пробелов — сведены сюда.)
/// </summary>
public static class MatchKeyNormalizer
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        // схлопываем любые пробельные последовательности (в т.ч. окружающие) в одиночный пробел
        var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        // срезаем завершающие точки/пробелы: «шт.» == «шт»
        return collapsed.TrimEnd('.', ' ').ToLowerInvariant();
    }
}
