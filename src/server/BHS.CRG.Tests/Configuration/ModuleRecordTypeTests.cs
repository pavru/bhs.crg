using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Два сторожа вокруг объявления типов модуля (issue #958). Оба — против дрейфа, а не ради
/// нынешнего состава.
/// </summary>
public class ModuleRecordTypeTests
{
    /// <summary>
    /// Зеркало уровней в проекте контрактов обязано совпадать с доменным по СОСТАВУ.
    ///
    /// Зеркало здесь вынужденное: в проекте контрактов модулей нет ссылок на наши проекты вовсе —
    /// на этом стоит правило «модуль знает ядро, ядро о модуле не знает». Разойдись перечисления,
    /// модуль объявил бы уровень, которого ядро не знает, и узнали бы мы об этом отказом старта в
    /// худшем случае — у заказчика.
    /// </summary>
    [Fact]
    public void Зеркало_уровней_правки_совпадает_с_доменным()
    {
        var mirrored = Enum.GetNames<ModuleSchemaLevel>().OrderBy(x => x, StringComparer.Ordinal);
        var domain = Enum.GetNames<SchemaEditLevel>().OrderBy(x => x, StringComparer.Ordinal);

        Assert.Equal(domain, mirrored);
    }

    /// <summary>
    /// Команду проекции зовёт РОВНО ОДИН файл — тот, что проецирует объявления при старте.
    ///
    /// Она пишет схему мимо политики правки (<c>SchemaEditPolicy</c>) — законно, потому что пишет
    /// модуль, а не администратор. Но та же команда, доступная по HTTP, — это дыра в F2 целиком:
    /// любой администратор снимал бы ею замки и происхождение. Проверка дешёвая, а забыть про неё
    /// легко: новый адрес пишется в другом файле и в другой день.
    ///
    /// ⚠️ Смотрим ВЕСЬ слой Api, а не папку <c>Endpoints</c>. Адреса регистрируются ещё и в
    /// <c>Program.cs</c>, в <c>Mcp</c>, в <c>Auth</c> и в <c>MapEndpoints</c> самих модулей — а
    /// проверка одной папки обещала бы «ни одного адреса», отвечая при этом за четверть мест.
    /// Неполный перечень хуже общей фразы: он выглядит исчерпывающим и вопроса не вызывает
    /// (найдено ревью PR #1010).
    /// </summary>
    [Fact]
    public void Проекцию_типа_модуля_зовёт_только_проектор()
    {
        var callers = Directory
            .EnumerateFiles(Path.Combine(SolutionDir, "BHS.CRG.Api"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Replace('\\', '/').Contains("/obj/", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("ProjectModuleTypeCommand", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(SolutionDir, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            callers is ["BHS.CRG.Api/Modules/ModuleTypeProjector.cs"],
            "Проекцию типа модуля зовёт не только проектор: " + string.Join(", ", callers) +
            ".\nПопав в обработчик адреса, она снимает по HTTP замки и происхождение полей, то есть " +
            "отменяет F2 целиком. Проекция вызывается только при старте.");
    }

    private static string SolutionDir { get; } = FindSolutionDir();

    private static string FindSolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BHS.CRG.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Не найден каталог решения (BHS.CRG.slnx).");
    }
}
