using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Modules.Data;

/// <summary>
/// Сверка объявления схемы с тем, что модуль на самом деле зарегистрировал (задача A2a этапа 2,
/// issue #1072).
///
/// <para>Проверяется в момент регистрации служб модуля — там, где видно И объявление, И контейнер, —
/// и отказ здесь останавливает СТАРТ. Обе половины расхождения тихие, и каждая по-своему:</para>
/// <list type="bullet">
///   <item><b>схема объявлена, контекста нет</b> — ядро мигрировало бы схему, которой никто не
///   владеет: приложение поднялось бы, а первый запрос модуля отказал бы «служба не
///   зарегистрирована»;</item>
///   <item><b>контекст есть, схема не объявлена</b> — ядро о нём не знает, миграций ему не будет
///   никогда. Модуль работал бы на таблицах, созданных руками у разработчика, и у заказчика не
///   нашёл бы их вовсе. Это самая тихая из двух: на машине автора всё работает.</item>
/// </list>
/// </summary>
public static class ModuleDataDeclaration
{
    /// <summary>
    /// Сверить объявление модуля с регистрациями, которые он только что сделал.
    /// </summary>
    /// <param name="module">Модуль, чьи службы зарегистрированы.</param>
    /// <param name="services">Контейнер на сборке.</param>
    /// <param name="registeredBefore">Сколько служб было ДО вызова <c>RegisterServices</c> этого
    /// модуля: всё, что появилось после, зарегистрировал он. Считать по всему контейнеру нельзя —
    /// контекст соседнего модуля выглядел бы своим.</param>
    public static void Ensure(IAppModule module, IServiceCollection services, int registeredBefore)
    {
        var contexts = services.Skip(registeredBefore)
            .Select(d => d.ServiceType)
            .Where(t => typeof(ModuleDbContext).IsAssignableFrom(t))
            .Distinct()
            .ToList();

        if (module.Schema is { } schema)
        {
            if (schema.Validate(module.Code) is { } problem)
                throw new InvalidOperationException(
                    $"Модуль «{module.Code}» объявил негодную схему базы: {problem}");

            if (!contexts.Contains(schema.ContextType))
                throw new InvalidOperationException(
                    $"Модуль «{module.Code}» объявил схему «{schema.Name}» с контекстом " +
                    $"«{schema.ContextType.Name}», но не зарегистрировал его в RegisterServices. " +
                    "Схему ядро мигрирует, а работать с ней будет некому: первый же запрос модуля " +
                    "отказал бы словами «служба не зарегистрирована» — то есть поломка вылезла бы у " +
                    "пользователя, а не у того, кто собирал поставку.");
        }

        var undeclared = contexts.Where(t => t != module.Schema?.ContextType).ToList();
        if (undeclared.Count > 0)
            throw new InvalidOperationException(
                $"Модуль «{module.Code}» зарегистрировал контекст базы, которого не объявил: " +
                string.Join(", ", undeclared.Select(t => t.Name)) + ". " +
                $"Объявите его в {nameof(IAppModule)}.{nameof(IAppModule.Schema)} — иначе ядро о нём " +
                "не знает и миграций ему не будет никогда. У разработчика это работает: таблицы он " +
                "создал сам; у заказчика их не будет вовсе.");
    }
}
