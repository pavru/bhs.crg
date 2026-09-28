using System.Reflection;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись контекстов базы: каждый контекст решения — либо контекст ЯДРА, либо контекст МОДУЛЯ, и
/// каждый контекст модуля объявлен своим модулем (задача A2a этапа 2, issue #1072, ТЗ CORE-4).
///
/// <para>Мета-сторож, а не проверка сегодняшнего состава. Контекст, добавленный третьим — «свой
/// контекст под отчёты», — не получил бы ни защиты дописываемых наборов, ни схемы, ни миграций при
/// старте, и узнать об этом было бы нечем: он бы просто работал у того, кто создал таблицы руками.
/// Ровно так и выглядит самая тихая поломка этой задачи, поэтому сторож стоит по ТИПАМ, а не по
/// контейнеру: до контейнера дело может и не дойти.</para>
///
/// <para>Родня по замыслу — <c>ActivityLogInventoryTests</c> и <c>FixtureResetCoverageTests</c>: у
/// каждого исключения обязана быть причина, написанная рядом.</para>
/// </summary>
public class ModuleDbContextInventoryTests
{
    /// <summary>
    /// Контексты, живущие вне правила, и почему. Добавляя строку, вы принимаете решение — именно этого
    /// сторож и добивается.
    /// </summary>
    private static readonly Dictionary<string, string> Exceptions = new()
    {
        [nameof(AppDbContext)] = "контекст ЯДРА: схема public, Identity и доменные агрегаты, своя история миграций",
    };

    /// <summary>Сборки решения, кроме тестовой: поддельные контексты прогонов правилу не подчиняются.</summary>
    private static Assembly[] Production =>
    [
        typeof(BHS.CRG.Api.Modules.IdModule).Assembly,
        typeof(AppDbContext).Assembly,
        typeof(BHS.CRG.Application.Activity.IActivityLog).Assembly,
        typeof(IAppModule).Assembly,
        typeof(BHS.CRG.Modules.Costs.CostsModule).Assembly,
    ];

    [Fact]
    public void Every_context_is_either_the_core_one_or_a_module_one()
    {
        var strangers = Contexts()
            .Where(t => !typeof(ModuleDbContext).IsAssignableFrom(t))
            .Where(t => !Exceptions.ContainsKey(t.Name))
            .Select(t => t.FullName!)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(strangers.Count == 0,
            "В решении появился контекст базы, который не является ни контекстом ядра, ни контекстом " +
            "модуля: " + string.Join(", ", strangers) + ".\n" +
            $"Контекст модуля обязан наследовать {nameof(ModuleDbContext)} — только так он получает " +
            "свою схему, защиту дописываемых наборов от правки и миграцию при старте. Если это " +
            "контекст ядра, впишите его в Exceptions с причиной.");
    }

    /// <summary>
    /// Каждый контекст модуля объявлен в <see cref="IAppModule.Schema" />.
    ///
    /// Необъявленный контекст — тот самый тихий случай: ядро о нём не знает, миграций ему не будет
    /// никогда, а у разработчика всё работает, потому что таблицы он создал сам. Сборка приложения это
    /// тоже ловит (<c>ModuleDataDeclaration</c>), но только для ВКЛЮЧЁННОГО модуля: у выключенного
    /// <c>RegisterServices</c> не вызывается вовсе, и до проверки дело не доходит.
    /// </summary>
    [Fact]
    public void Every_module_context_is_declared_by_its_module()
    {
        var declared = Modules()
            .Select(m => m.Schema?.ContextType)
            .Where(t => t is not null)
            .ToHashSet()!;

        var undeclared = Contexts()
            .Where(typeof(ModuleDbContext).IsAssignableFrom)
            .Where(t => !declared.Contains(t))
            .Select(t => t.FullName!)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(undeclared.Count == 0,
            "Контекст модуля есть, а объявления нет: " + string.Join(", ", undeclared) + ".\n" +
            $"Объявите его в {nameof(IAppModule)}.{nameof(IAppModule.Schema)}: иначе ядро о схеме не " +
            "знает и не мигрирует её — у заказчика таблиц не будет вовсе.");
    }

    /// <summary>
    /// Схемы модулей не совпадают, и каждое объявление годно.
    ///
    /// Одна схема на два модуля означала бы две истории миграций на один набор таблиц: второй модуль
    /// начал бы с того, что не создавал.
    /// </summary>
    [Fact]
    public void Module_schemas_are_valid_and_unique()
    {
        var problems = new List<string>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var module in Modules())
        {
            if (module.Schema is not { } schema) continue;

            if (schema.Validate(module.Code) is { } problem) problems.Add($"{module.Code}: {problem}");

            if (seen.TryGetValue(schema.Name, out var owner))
                problems.Add($"схему «{schema.Name}» объявили и «{owner}», и «{module.Code}»");
            else
                seen[schema.Name] = module.Code;
        }

        Assert.True(problems.Count == 0, string.Join("\n  ", problems));
    }

    private static IEnumerable<Type> Contexts() => Production
        .SelectMany(a => a.GetTypes())
        .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(DbContext).IsAssignableFrom(t));

    private static IEnumerable<IAppModule> Modules() => Production
        .SelectMany(a => a.GetTypes())
        .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(IAppModule).IsAssignableFrom(t))
        .Where(t => t.GetConstructor(Type.EmptyTypes) is not null)
        .Select(t => (IAppModule)Activator.CreateInstance(t)!);
}
