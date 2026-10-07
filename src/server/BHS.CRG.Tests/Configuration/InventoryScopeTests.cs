using System.Text.RegularExpressions;
using BHS.CRG.Tests.Common;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Сторож над сторожами: перепись, читающая исходники ядра, читает и проекты модулей — либо говорит,
/// почему нет (задача M3, issue #1071).
///
/// <para><b>От чего это.</b> Переписи этапа 1 — журнал действий, адресаты уведомлений, охрана записи и
/// ещё восемь — сканировали прибитый список <c>Api</c>/<c>Application</c>/<c>Infrastructure</c>. С
/// появлением модуля каждая из них осталась зелёной, что бы в модуле ни написали: класс сторожей
/// перестал действовать ровно в том коде, который пишут сейчас. Первый же прогон с модулем в списке
/// нашёл шесть мест, которых правила ядра не касались никогда.</para>
///
/// <para><b>Как устроено.</b> Список проектов перепись собирает через
/// <see cref="SolutionModules.WithCore" />: модули в него попадают сами, отбором по ссылке на
/// контракты ядра. Вписывать новый модуль некуда — и забыть нечего. Этот сторож держит два конца:
/// что отбор совпадает с поставкой (иначе «модули попадают сами» означало бы «не попадает ни один»)
/// и что ни одна перепись не назвала проекты ядра мимо него.</para>
/// </summary>
public class InventoryScopeTests
{
    /// <summary>
    /// Кто называет проект ядра, не читая модулей, — и почему. Добавляя строку, вы принимаете решение:
    /// правило этого сторожа к коду модуля неприменимо. «Модуля тогда ещё не было» причиной не
    /// является — именно так переписи и ослепли.
    /// </summary>
    private static readonly Dictionary<string, string> CoreOnly = new()
    {
        ["BHS.CRG.Tests/Common/SolutionModules.cs"] =
            "сам отбор: называет хосты, которые ссылаются на контракты, но модулями не являются",
        ["BHS.CRG.Tests/Configuration/ModuleBoundaryTests.cs"] =
            "не перепись исходников: перечисляет, на какие проекты модулю разрешено ссылаться",
        ["BHS.CRG.Tests/Configuration/ModuleMoneyStorageTests.cs"] =
            "правило обратное: ядро не называет модуль по имени. Модулю называть себя не запрещено",
        ["BHS.CRG.Tests/Configuration/DomainExceptionPolicyTests.cs"] =
            "вторая проверка («слой API не бросает доменных отказов») — про адреса ядра: у них логика в " +
            "обработчиках, и адрес отвечает кодом. Адрес модуля несёт логику сам и отказывает доменным " +
            "отказом по замыслу (ModuleWriteGuard возвращает находки, а не бросает). Первая проверка модули читает",
        ["BHS.CRG.Tests/Configuration/ModuleRecordTypeTests.cs"] =
            "ищет, кто зовёт команду слоя Application: модулю она недоступна — ссылки на этот слой у него " +
            "нет, и это стережёт ModuleBoundaryTests",
        ["BHS.CRG.Tests/Schema/TagCatalogTests.cs"] =
            "ищет разрешение реестра тэгов на старте: корень композиции один, и он в хосте",
        ["BHS.CRG.Tests/Configuration/EffectiveSettingsCoverageTests.cs"] =
            "читает один названный файл службы настроек интеграций — не обход проекта",
        ["BHS.CRG.Tests/Recognition/PageTimeoutToleranceTests.cs"] =
            "читает каталог распознавания наборов данных: распознавание — служба ядра, модуль зовёт её портом",
    };

    /// <summary>Имя проекта ядра строкой целиком — так его называют, собираясь читать исходники.</summary>
    private static readonly Regex CoreProject = new(
        @"""BHS\.CRG\.(?:Api|Application|Infrastructure|Domain|Plugins)""", RegexOptions.Compiled);

    /// <summary>Вызов общего отбора вместе с аргументами: названное внутри него — уже с модулями.</summary>
    private static readonly Regex WithModules = new(@"SolutionModules\.WithCore\([^)]*\)", RegexOptions.Compiled);

    /// <summary>
    /// Мета-сторож задачи. Новая перепись со своим списком проектов — красный тест с именем файла,
    /// пока она ещё пуста и решение дёшево.
    /// </summary>
    [Fact]
    public void Перепись_называет_проекты_ядра_только_вместе_с_модулями()
    {
        var bare = Bare().Where(rel => !CoreOnly.ContainsKey(rel)).ToList();

        Assert.True(bare.Count == 0,
            "Сторож называет проекты ядра и не читает проекты модулей:\n" + string.Join("\n", bare) + "\n\n" +
            "Соберите список через SolutionModules.WithCore(\"BHS.CRG.Api\", …) — тогда правило действует и " +
            "на код модулей (сейчас это " + string.Join(", ", SolutionModules.Names) + "). Если к модулю оно " +
            "неприменимо — впишите файл в CoreOnly с причиной.");
    }

    /// <summary>
    /// Строка, за которой больше нет своего списка проектов, — след переезда на общий отбор.
    /// Оставленная, она разрешает следующему автору этого файла снова назвать одно ядро.
    /// </summary>
    [Fact]
    public void Перечень_исключений_не_хранит_умерших_записей()
    {
        var bare = Bare().ToHashSet(StringComparer.Ordinal);
        var stale = CoreOnly.Keys.Where(rel => !bare.Contains(rel)).Order(StringComparer.Ordinal).ToList();

        Assert.True(stale.Count == 0,
            "В CoreOnly файлы, которые проекты ядра мимо общего отбора больше не называют: " +
            string.Join(", ", stale) + ".\nУберите строки — иначе перечень разрешает то, чего уже не делают.");
    }

    /// <summary>
    /// Отбор совпадает с поставкой — в обе стороны. Поставленный модуль, чей проект отбор не нашёл,
    /// выпал бы из всех переписей разом, и все они остались бы зелёными; проект-модуль, которого нет в
    /// поставке, — код, который переписи читают, а приложение не запускает.
    /// </summary>
    [Fact]
    public void Отбор_проектов_модулей_совпадает_с_поставкой()
    {
        var delivered = BHS.CRG.Api.Modules.DeliveredModules.All()
            .Select(m => m.GetType().Assembly.GetName().Name!)
            .Distinct()
            .ToList();

        var unseen = delivered
            .Where(name => !SolutionModules.Names.Contains(name) && !SolutionModules.KnownHosts.ContainsKey(name))
            .ToList();
        Assert.True(unseen.Count == 0,
            "Модуль поставляется, а его проект переписи не читают: " + string.Join(", ", unseen) + ".\n" +
            "Отбор идёт по ссылке проекта на контракты ядра (BHS.CRG.Modules) — см. SolutionModules. Пока " +
            "проекта там нет, ни одно правило ядра на его код не действует.");

        var undelivered = SolutionModules.Names.Where(name => !delivered.Contains(name)).ToList();
        Assert.True(undelivered.Count == 0,
            "Проект ссылается на контракты ядра, то есть является модулем, а в поставке его нет: " +
            string.Join(", ", undelivered) + ".\nВпишите модуль в DeliveredModules либо, если это хост, — " +
            "в SolutionModules.KnownHosts с причиной.");

        // Модуль-обёртка `id` живёт в хосте, и проекта у него нет. Без этой проверки отбор, не нашедший
        // вообще ничего, сошёлся бы с поставкой из одних обёрток.
        Assert.True(SolutionModules.Names.Count > 0,
            "Отбор не нашёл ни одного проекта модуля: все переписи читают одно ядро.");
    }

    /// <summary>Файлы тестов, где проект ядра назван мимо общего отбора.</summary>
    private static IEnumerable<string> Bare() =>
        SourceTree.Files("BHS.CRG.Tests")
            .Where(file => CoreProject.IsMatch(WithModules.Replace(File.ReadAllText(file), string.Empty)))
            .Select(SourceTree.Relative)
            .Order(StringComparer.Ordinal);
}
