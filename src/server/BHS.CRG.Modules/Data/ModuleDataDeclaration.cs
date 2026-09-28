using Microsoft.EntityFrameworkCore;
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
    /// <param name="before">Дескрипторы, бывшие в контейнере ДО вызова <c>RegisterServices</c> этого
    /// модуля: всё, чего в этом наборе нет, зарегистрировал он. Считать по всему контейнеру нельзя —
    /// контекст соседнего модуля выглядел бы своим.
    ///
    /// ⚠️ Набор дескрипторов, а НЕ их число: <c>RemoveAll</c> и <c>Replace</c> внутри
    /// <c>RegisterServices</c> — обычное дело (так тестовый хост подменяет хранилище), и они сдвигают
    /// индексы. По границе-индексу удаление чужого дескриптора превращало бы окно «что добавил модуль»
    /// в пустое или чужое, а отказ обвинял бы автора в том, чего он не делал (найдено ревью PR #1107).
    /// Сравнение по ссылке: <c>ServiceDescriptor</c> своего равенства не объявляет, и это здесь
    /// правильно — два одинаковых по смыслу дескриптора остаются разными регистрациями.</param>
    public static void Ensure(
        IAppModule module, IServiceCollection services, IReadOnlySet<ServiceDescriptor> before)
    {
        var contexts = services
            .Where(d => !before.Contains(d))
            .Select(d => ContextOf(d.ServiceType))
            .Where(t => t is not null)
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
                    "пользователя, а не у того, кто собирал поставку. Годятся оба способа: " +
                    "AddDbContext (сам контекст) и AddDbContextFactory (фабрика).");
        }

        var undeclared = contexts.Where(t => t != module.Schema?.ContextType).ToList();
        if (undeclared.Count > 0)
            throw new InvalidOperationException(
                $"Модуль «{module.Code}» зарегистрировал контекст базы, которого не объявил: " +
                string.Join(", ", undeclared.Select(t => t!.Name)) + ". " +
                $"Объявите его в {nameof(IAppModule)}.{nameof(IAppModule.Schema)} — иначе ядро о нём " +
                "не знает и миграций ему не будет никогда. У разработчика это работает: таблицы он " +
                "создал сам; у заказчика их не будет вовсе.");
    }

    /// <summary>
    /// Какой контекст модуля стоит за этой службой: сам контекст или его фабрика
    /// (<see cref="IDbContextFactory{TContext}" />). <c>null</c> — служба не про контекст модуля.
    ///
    /// <para>Фабрика считается регистрацией контекста нарочно. Модулю она нужна там, где области
    /// запроса нет вовсе — в фоновой работе (<c>IModuleJobHandler</c>), — и это не обходной путь, а
    /// обычный приём EF (найдено ревью PR #1107).</para>
    ///
    /// <para>⚠️ Насколько это спасает — сказано по факту, а не на слух: помощники EF
    /// (<c>AddDbContextFactory</c>, <c>AddPooledDbContextFactory</c>) заводят рядом и САМ контекст
    /// (проверено прогоном), то есть они прошли бы сверку и без этой ветки. Остаётся случай, когда
    /// модуль регистрирует фабрику сам — и вот ему отказ «не зарегистрировал контекст» досталось бы
    /// за правильно написанный код.</para>
    /// </summary>
    internal static Type? ContextOf(Type serviceType)
    {
        if (typeof(ModuleDbContext).IsAssignableFrom(serviceType)) return serviceType;

        if (serviceType.IsGenericType
            && serviceType.GetGenericTypeDefinition() == typeof(IDbContextFactory<>)
            && typeof(ModuleDbContext).IsAssignableFrom(serviceType.GetGenericArguments()[0]))
            return serviceType.GetGenericArguments()[0];

        return null;
    }
}
