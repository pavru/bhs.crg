using System.Text.RegularExpressions;
using BHS.CRG.Tests.Common;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Раздел модуля в диалоге закрытия периода не берёт замок записи (задача E1b, issue #1099).
///
/// <para><b>От чего это.</b> При закрытии ядро зовёт отчёт модуля, держа замок «запись против закрытия»
/// ИСКЛЮЧИТЕЛЬНЫМ. Отчёт, взявший тот же замок разделяемым (сам или через связку записи), ждал бы
/// собственный исключительный: PostgreSQL взаимной блокировки тут не видит — соединения разные, а
/// срока у ожидания нет. Повисло бы закрытие и вместе с ним вся запись в учёт.</para>
///
/// <para>Проверка по исходникам: на живом хосте такой отчёт работает в предпросмотре (замка там нет) и
/// виснет только при самом закрытии — то есть тест с предпросмотром остался бы зелёным.</para>
/// </summary>
public class ClosingReportLockTests
{
    private static readonly Regex Report = new(@":\s*[\w\s,<>.]*\bIModuleClosingReport\b", RegexOptions.Compiled);

    /// <summary>Замок записи и всё, что берёт его внутри себя.</summary>
    private static readonly Regex Lock = new(
        @"\bOpenPeriodWrite\b|\bHoldAsync\(|\bInOpenPeriodAsync\(|\bInvoiceDesk\b|\bWriteAsync\(", RegexOptions.Compiled);

    [Fact]
    public void Отчёт_модуля_к_закрытию_не_берёт_замок_записи()
    {
        var reports = Sources().Where(s => Report.IsMatch(s.Code)).ToList();

        // Ни одного отчёта — не «все чисты», а «проверять было нечего»: сторож ослеп бы молча, смени
        // интерфейс имя.
        Assert.True(reports.Count > 0, "Не найдено ни одной реализации IModuleClosingReport — сторожу нечего проверять.");

        var taking = reports.Where(s => Lock.IsMatch(s.Code))
            .Select(s => $"{s.Rel}: {string.Join(", ", Lock.Matches(s.Code).Select(m => m.Value).Distinct())}")
            .ToList();
        Assert.True(taking.Count == 0,
            "Отчёт модуля к закрытию периода берёт замок записи:\n" + string.Join("\n", taking) + "\n\n" +
            "При закрытии его зовут под исключительным замком ядра — он ждал бы собственный замок, и " +
            "закрытие повисло бы вместе со всей записью в учёт. Отчёт только читает.");
    }

    private static IEnumerable<(string Rel, string Code)> Sources()
    {
        // Проекты модулей — общим отбором, по ссылке на контракты (issue #1071): маска по имени каталога
        // пропустила бы модуль, названный не по соглашению.
        return SolutionModules.Names
            .SelectMany(SourceTree.Files)
            .Select(f => (
                SourceTree.Relative(f),
                string.Join("\n", File.ReadAllLines(f).Where(l => !l.TrimStart().StartsWith("//")))));
    }
}
