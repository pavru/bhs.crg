using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules;
using MediatR;

namespace BHS.CRG.Api.Modules;

/// <summary>
/// Переводит объявления типов включённых модулей в слова ядра и проецирует их в схемы (issue #958,
/// ТЗ CORE-20.1, CORE-20.2).
///
/// <para>Живёт здесь, а не в ядре и не в модуле, потому что это ЕДИНСТВЕННЫЙ слой, знающий обоих:
/// проект контрактов модулей не ссылается ни на что наше, а слой приложения о модулях не знает
/// вовсе — на этом стоит правило «модуль знает ядро, ядро о модуле не знает» (ТЗ CORE-2).</para>
///
/// <para>⚠️ Проекция делается ЯДРОМ за модуль, а не самим модулем в <c>InitializeAsync</c>. Разница
/// не стилистическая: обязанность, оставленная модулю, выполняется ровно теми модулями, которые о
/// ней вспомнили, — и тип без скелета выглядел бы обычным типом заказчика, а не ошибкой.</para>
///
/// <para><b>Расхождение останавливает старт.</b> Исключение отсюда не ловится: приложение не
/// поднимается и называет, что не сошлось. Решение владельца 22.09.2026 — тот же приём, что у прав
/// модуля и у миграции справочников; тихая версия этого отказа кончается печатью, печатающей
/// пустоту.</para>
/// </summary>
public static class ModuleTypeProjector
{
    public static async Task ProjectModuleTypesAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        var registry = services.GetRequiredService<ModuleRegistry>();
        var mediator = services.GetRequiredService<IMediator>();

        foreach (var module in registry.Enabled)
            foreach (var declared in module.RecordTypes)
                await mediator.Send(new ProjectModuleTypeCommand(Translate(module.Code, declared)), ct);
    }

    private static ModuleTypeSpec Translate(string moduleCode, ModuleRecordType declared) => new(
        moduleCode, declared.Code, declared.Name, Level(declared.Level),
        [.. declared.Fields.Select(f => new ModuleFieldSpec(f.Key, f.Title, f.Type, f.Tags, f.Required, f.Locked))],
        declared.Group);

    /// <summary>
    /// Зеркало уровней. Переключатель, а не приведение по числу: значения совпадают по именам, и
    /// сверяет это сторож — но совпадение ПОРЯДКА никто не обещал, а приведение молча опиралось бы
    /// именно на него.
    /// </summary>
    private static SchemaEditLevel Level(ModuleSchemaLevel level) => level switch
    {
        ModuleSchemaLevel.Open => SchemaEditLevel.Open,
        ModuleSchemaLevel.Extendable => SchemaEditLevel.Extendable,
        ModuleSchemaLevel.Closed => SchemaEditLevel.Closed,
        _ => throw new InvalidOperationException(
            $"Неизвестный уровень правки схемы «{level}» — зеркало разошлось с доменным перечислением."),
    };
}
