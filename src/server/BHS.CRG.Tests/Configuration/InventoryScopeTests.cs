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
/// <see cref="SolutionModules.WithCore" />: проект контрактов и модули в него попадают сами, отбором по
/// ссылке на контракты ядра. Вписывать новый модуль некуда — и забыть нечего. Этот сторож держит три
/// конца: что отбор совпадает с поставкой (иначе «модули попадают сами» означало бы «не попадает ни
/// один»), что ни один сторож не назвал проекты ядра мимо него — и что ни один не выбирает проекты
/// модулей по-своему.</para>
///
/// <para>⚠️ <b>Чего он не видит.</b> Имя проекта, собранное не литералом — из переменной, склейкой
/// частей, чтением из файла решения. Сторож читает текст; путь, которого в тексте нет, ему не найти.</para>
/// </summary>
public class InventoryScopeTests
{
    /// <summary>
    /// Кто называет проект ядра, не читая модулей: сколько раз — и почему. Добавляя строку или поднимая
    /// число, вы принимаете решение: правило этого места к коду модуля неприменимо. «Модуля тогда ещё
    /// не было» причиной не является — именно так переписи и ослепли.
    ///
    /// <para>Число, а не признак «файл освобождён»: иначе строка, заведённая ради одного места,
    /// разрешала бы следующему автору этого файла назвать одно ядро ещё раз (ревью PR #1250).</para>
    /// </summary>
    private static readonly Dictionary<string, (int Times, string Why)> CoreOnly = new()
    {
        ["BHS.CRG.Tests/Common/SolutionModules.cs"] =
            (2, "сам отбор: имя проекта контрактов и хост, который на контракты ссылается, а модулем не является"),
        ["BHS.CRG.Tests/Configuration/ModuleBoundaryTests.cs"] =
            (2, "не перепись исходников: файл проекта контрактов и список проектов, на которые модулю " +
                "разрешено ссылаться"),
        ["BHS.CRG.Tests/Configuration/ModuleMoneyStorageTests.cs"] =
            (6, "правило обратное: ядро не называет модуль по имени. Модулю называть себя не запрещено"),
        ["BHS.CRG.Tests/Configuration/DomainExceptionPolicyTests.cs"] =
            (1, "вторая проверка («слой API не бросает доменных отказов») — про адреса ядра: у них логика в " +
                "обработчиках, и адрес отвечает кодом. Адрес модуля несёт логику сам и отказывает доменным " +
                "отказом по замыслу. Первая проверка модули читает"),
        ["BHS.CRG.Tests/Configuration/ModuleRecordTypeTests.cs"] =
            (1, "ищет, кто зовёт команду слоя Application: модулю она недоступна — ссылки на этот слой у " +
                "него нет, и это стережёт ModuleBoundaryTests"),
        ["BHS.CRG.Tests/Schema/TagCatalogTests.cs"] =
            (1, "ищет разрешение реестра тэгов на старте: корень композиции один, и он в хосте"),
        ["BHS.CRG.Tests/Configuration/EffectiveSettingsCoverageTests.cs"] =
            (1, "читает один названный файл службы настроек интеграций — не обход проекта"),
        ["BHS.CRG.Tests/Configuration/ModuleRawSqlTests.cs"] =
            (1, "читает снимок модели ядра ради имён его таблиц; исходники, которые он проверяет, — как раз модулей"),
        ["BHS.CRG.Tests/Recognition/PageTimeoutToleranceTests.cs"] =
            (1, "читает каталог распознавания наборов данных: распознавание — служба ядра, модуль зовёт её портом"),
    };

    /// <summary>
    /// Кто называет проект МОДУЛЯ сам — именем или маской — и почему. Отбор по имени каталога пропускает
    /// модуль, названный не по соглашению; общий отбор находит его по ссылке на контракты.
    /// </summary>
    private static readonly Dictionary<string, string> NamesModule = new()
    {
        ["BHS.CRG.Tests/Configuration/InvoiceWritePathTests.cs"] =
            "сторож одного модуля: пути записи счёта есть только в модуле счетов, и связка записи — его",
    };

    /// <summary>
    /// Проект ядра, названный строкой: имя целиком, каталог внутри проекта или начало склейки
    /// (<c>$"BHS.CRG.{…}"</c>). Строка с путём к ФАЙЛУ (<c>….cs</c>) сюда не попадает — так переписи
    /// называют свои исключения и записи перечней, а не то, что читают.
    /// </summary>
    private static readonly Regex CoreProject = new(
        @"""BHS\.CRG\.(?:\{|(?:Api|Application|Infrastructure|Domain|Plugins|Modules)(?:""|[/\\](?![^""\r\n]*\.cs\b)[^""\r\n]*""))",
        RegexOptions.Compiled);

    /// <summary>Проект модуля, названный строкой: именем, маской или каталогом внутри него.</summary>
    private static readonly Regex ModuleProject = new(
        @"""BHS\.CRG\.Modules\.(?!csproj\b)[\w*]+(?:""|[/\\](?![^""\r\n]*\.cs\b)[^""\r\n]*"")", RegexOptions.Compiled);

    /// <summary>Вызов общего отбора вместе с аргументами: названное внутри него — уже с модулями.</summary>
    private static readonly Regex WithModules = new(@"SolutionModules\.WithCore\([^)]*\)", RegexOptions.Compiled);

    /// <summary>
    /// Мета-сторож задачи. Новая перепись со своим списком проектов — красный тест с именем файла,
    /// пока она ещё пуста и решение дёшево. И в обратную сторону: строка, за которой своего списка
    /// больше нет, — след переезда; оставленная, она разрешала бы назвать одно ядро снова.
    /// </summary>
    [Fact]
    public void Сторож_называет_проекты_ядра_только_вместе_с_модулями()
    {
        var bare = Count(CoreProject);

        var wrong = bare.Keys.Union(CoreOnly.Keys)
            .Select(rel => (Rel: rel, Found: bare.GetValueOrDefault(rel), Allowed: CoreOnly.GetValueOrDefault(rel).Times))
            .Where(x => x.Found != x.Allowed)
            .Select(x => $"{x.Rel}: названо {x.Found}, разрешено {x.Allowed}")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(wrong.Count == 0,
            "Проекты ядра названы мимо общего отбора не так, как записано в CoreOnly:\n" +
            string.Join("\n", wrong) + "\n\n" +
            "Соберите список через SolutionModules.WithCore(…) — тогда правило действует и " +
            "на код модулей (сейчас это " + string.Join(", ", SolutionModules.Names) + "). Если к модулю оно " +
            "неприменимо — впишите место в CoreOnly с причиной; если своего списка больше нет — уберите строку.");
    }

    /// <summary>
    /// Проекты модулей выбирает общий отбор. Сторож со своей маской или именем проекта видит только
    /// те модули, что названы по соглашению, — то есть молчит о том, который назван иначе.
    /// </summary>
    [Fact]
    public void Проекты_модулей_сторож_берёт_общим_отбором()
    {
        var own = Count(ModuleProject).Keys.ToList();

        var unlisted = own.Where(rel => !NamesModule.ContainsKey(rel)).ToList();
        Assert.True(unlisted.Count == 0,
            "Сторож выбирает проекты модулей сам — именем или маской:\n" + string.Join("\n", unlisted) + "\n\n" +
            "Возьмите SolutionModules.Names: отбор по ссылке на контракты находит и модуль, названный не по " +
            "соглашению. Если сторож и правда про один модуль — впишите файл в NamesModule с причиной.");

        var stale = NamesModule.Keys.Where(rel => !own.Contains(rel)).ToList();
        Assert.True(stale.Count == 0,
            "В NamesModule файлы, которые проект модуля больше не называют: " + string.Join(", ", stale) +
            ".\nУберите строки — иначе перечень разрешает то, чего уже не делают.");
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

        // И сам общий список: контракты и модули в нём есть, что бы ни назвала перепись.
        var scope = SolutionModules.WithCore("BHS.CRG.Api");
        Assert.Contains(SolutionModules.ContractsProject, scope);
        Assert.All(SolutionModules.Names, name => Assert.Contains(name, scope));
    }

    /// <summary>
    /// Файл тестов → сколько раз в нём встретилось выражение мимо общего отбора. Без строк-комментариев:
    /// <c>cref="BHS.CRG.Modules.…"</c> в пояснении — имя типа, а не проект.
    /// </summary>
    private static Dictionary<string, int> Count(Regex what) =>
        SourceTree.Files("BHS.CRG.Tests")
            .Select(file => (
                Rel: SourceTree.Relative(file),
                Times: what.Matches(WithModules.Replace(Code(File.ReadAllText(file)), string.Empty)).Count))
            .Where(x => x.Times > 0)
            .ToDictionary(x => x.Rel, x => x.Times, StringComparer.Ordinal);

    private static string Code(string text) =>
        string.Join('\n', text.Split('\n').Select(line => line.TrimStart().StartsWith("//") ? string.Empty : line));
}
