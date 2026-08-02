using System.Security.Cryptography;
using System.Text;

namespace BHS.CRG.Application.DataSnapshots;

/// <summary>
/// Отпечаток строк источника (issue #598).
///
/// Работа с комплектом итеративна — «поправил реестр, перепроверь», — и каждый повтор выгружал те же
/// данные заново: кабельный журнал за сессию выгружался целиком не менее четырёх раз при двух
/// реальных изменениях. Отпечаток позволяет вызывающему сказать «у меня версия такая-то» и получить
/// в ответ короткое «не изменилось».
///
/// Считается по строкам ПОСЛЕ обработки — по тем, что вызывающий и получил бы. Загрузку это не
/// экономит: отпечаток без загрузки не посчитать, а обещать дешевизну, которой нет, хуже, чем не
/// обещать. Экономится контекст, а он здесь и есть дефицит.
/// </summary>
public static class RowsFingerprint
{
    /// <summary>Разделители — управляющие символы: в данных они не встречаются, поэтому склейка
    /// «аб» и пары «а», «б» не даст одинакового отпечатка.</summary>
    private const char UnitSeparator = '\u001f';

    /// <inheritdoc cref="UnitSeparator" />
    private const char RecordSeparator = '\u001e';

    /// <summary>
    /// Учитываются значения, их порядок и имена колонок: перестановка строк меняет адрес значения
    /// <c>(источник, номер строки, колонка)</c>, а значит для внешнего анализа это уже другие данные.
    /// </summary>
    public static string Of(IReadOnlyList<IReadOnlyDictionary<string, string?>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            // Порядок ключей внутри словаря не обещан — сортируем, иначе отпечаток «менялся» бы сам.
            foreach (var key in row.Keys.OrderBy(k => k, StringComparer.Ordinal))
                sb.Append(key).Append(UnitSeparator).Append(row[key]).Append(UnitSeparator);
            sb.Append(RecordSeparator);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        // Половины хеша хватает: это защита от «данные не менялись», а не от подделки.
        return Convert.ToHexStringLower(hash)[..16];
    }
}
