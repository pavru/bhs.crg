using System.Text.RegularExpressions;
using BHS.CRG.Tests.Common;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Модули не знают справочники ядра ПО ИМЕНАМ (ТЗ CORE-20.4, issue #1101).
///
/// <para>Проверяется каждый проект-модуль решения, а не один названный: следующий модуль читает те же
/// организации и ту же номенклатуру, и соблазн назвать их кодом типа у него тот же.</para>
///
/// <para>Модуль счетов раньше знал: тип «Организация», его поля «ИНН» и «Наименование», тип «Номенклатура».
/// Справочники заводит и называет человек, поэтому на новой установке эти имена надо было угадать
/// буква в букву, а переименование поля на действующей останавливало сопоставление поставщика — без
/// единой ошибки. Теперь справочник находится по роли, поле — по тэгу.</para>
///
/// <para>Сторож ловит возврат: строковый литерал с прежним именем, вернувшийся в код модуля. В
/// комментариях и в текстах для человека слова остаются — «Справочник организаций не назначен»
/// литералом-адресом не является, и точное совпадение литерала с именем его не задевает.</para>
///
/// <para>⚠️ Чего сторож НЕ видит: имя, собранное из частей или пришедшее из настроек. Такое ловит
/// порт: необъявленную роль он не читает вовсе (<c>ModuleNeedsTests</c>, <c>ModulePortsTests</c>).</para>
/// </summary>
public class CostsDirectoryAddressTests
{
    /// <summary>Прежние адреса: коды типов и ключи полей справочников ядра.</summary>
    private static readonly string[] Addresses = ["Организация", "Номенклатура", "ИНН"];

    /// <summary>
    /// «Наименование» — отдельно: это ещё и ключ колонки в профиле распознавания строк счёта, договор
    /// модуля с самим собой. Литерал законен ровно в одном файле.
    /// </summary>
    private const string NameAddress = "Наименование";
    private const string NameAllowedIn = "CostsRecognitionProfiles.cs";

    [Fact]
    public void В_коде_модулей_нет_литералов_с_именами_справочников_ядра()
    {
        var found = new List<string>();
        foreach (var file in Sources())
        {
            var name = Path.GetFileName(file);
            var number = 0;
            foreach (var raw in File.ReadLines(file))
            {
                number++;
                var line = raw.TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('*')) continue;

                foreach (var address in Addresses)
                    if (HasLiteral(line, address)) found.Add($"{name}:{number} — \"{address}\"");
                if (name != NameAllowedIn && HasLiteral(line, NameAddress))
                    found.Add($"{name}:{number} — \"{NameAddress}\"");
            }
        }

        Assert.True(found.Count == 0,
            "В коде модуля снова есть имя справочника ядра строковым литералом:\n" + string.Join("\n", found) +
            "\nСправочник находят по роли, поле — по тэгу (образец — CostsDirectories). Имя типа и ключ поля ведёт " +
            "администратор: с литералом код работает до первого переименования.");
    }

    /// <summary>Сторож обязан срабатывать: на строке с прежней адресацией он её находит.</summary>
    [Theory]
    [InlineData("catalog.ListAsync(\"Организация\", RecordsFor.Display, ct)", "Организация", true)]
    [InlineData("public const string TaxIdField = \"ИНН\";", "ИНН", true)]
    [InlineData("$\"Справочник организаций не назначен, поэтому {x}\"", "Организация", false)]
    [InlineData("\"ИНН в файле не прочитан\"", "ИНН", false)]
    [InlineData("catalog.ListAsync(CostsDirectories.Organization, RecordsFor.Display, ct)", "Организация", false)]
    public void Литерал_адрес_отличается_от_слова_в_тексте(string line, string address, bool expected) =>
        Assert.Equal(expected, HasLiteral(line, address));

    private static bool HasLiteral(string line, string address) =>
        Regex.IsMatch(line, "\"" + Regex.Escape(address) + "\"");

    private static IEnumerable<string> Sources()
    {
        // Общим отбором, а не именем проекта: модуль, названный не по соглашению, находится по ссылке
        // на контракты.
        return SolutionModules.Names
            .SelectMany(project => Directory.EnumerateFiles(
                Path.Combine(SolutionRoot(), project), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));
    }

    private static string SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BHS.CRG.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Каталог решения не найден.");
    }
}
