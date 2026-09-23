using BHS.CRG.Application.Schema;
using BHS.CRG.Modules;

namespace BHS.CRG.Api.Modules;

/// <summary>
/// Собирает реестр функциональных тэгов этого экземпляра: тэги ядра плюс тэги ВКЛЮЧЁННЫХ модулей
/// (ТЗ TYPE-22, issue #959).
///
/// <para>Живёт здесь по той же причине, что и <see cref="ModuleTypeProjector" />: это единственный
/// слой, знающий обоих — сборка контрактов модулей не ссылается ни на что наше, а слой приложения
/// о модулях не знает вовсе (ТЗ CORE-2).</para>
/// </summary>
public static class ModuleTagCollector
{
    public static IServiceCollection AddTagCatalog(this IServiceCollection services)
    {
        // Singleton: состав реестра задаётся набором модулей, а набор — настройкой ЗАПУСКА, и за
        // время работы приложения не меняется. Пересобирать его на каждый запрос значило бы
        // притворяться, что он может измениться.
        services.AddSingleton(sp =>
        {
            var registry = sp.GetRequiredService<ModuleRegistry>();
            var fromModules = registry.Enabled
                .SelectMany(m => m.Tags.Select(t => Translate(m.Code, t)))
                .ToList();
            return TagCatalog.Build(TagRegistry.Core, fromModules);
        });
        return services;
    }

    private static TagDefinition Translate(string moduleCode, ModuleTag t) => new(
        t.Code, t.Label, t.Description,
        Scope: Scope(t.Scope),
        AppliesTo: [.. t.AppliesTo],
        Multiple: t.Multiple,
        Owner: moduleCode,
        Group: t.Group);

    /// <summary>
    /// Зеркало уровней тэга. Переключатель, а не приведение по числу, — ровно по той причине, что
    /// и у уровней правки схемы: имена сверяет сторож, а совпадение ПОРЯДКА никто не обещал.
    /// </summary>
    private static TagScope Scope(ModuleTagScope scope) => scope switch
    {
        ModuleTagScope.Field => TagScope.Field,
        ModuleTagScope.Type => TagScope.Type,
        ModuleTagScope.Dataset => TagScope.Dataset,
        ModuleTagScope.GostDocument => TagScope.GostDocument,
        _ => throw new InvalidOperationException(
            $"Неизвестный уровень тэга «{scope}» — зеркало разошлось с доменным перечислением."),
    };
}
