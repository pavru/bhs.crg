using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Infrastructure.Recognition;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Recognition;

namespace BHS.CRG.Api.Modules;

/// <summary>
/// Собирает каталог заводских профилей распознавания: объявления ядра плюс объявления ВСЕХ модулей
/// сборки (ТЗ CORE-Q6, задача B1a, issue #1075).
///
/// <para>Живёт здесь по той же причине, что и <see cref="ModuleTagCollector" />: это единственный
/// слой, знающий обоих — сборка контрактов модулей не ссылается ни на что наше, а слой приложения
/// о модулях не знает вовсе (ТЗ CORE-2).</para>
///
/// <para>В отличие от тэгов, берутся и выключенные модули: тэг выключенного модуля достаточно не
/// предлагать, а у профиля в базе лежит строка, и ядру надо уметь назвать её владельца.</para>
/// </summary>
public static class ModuleRecognitionCollector
{
    public static IServiceCollection AddRecognitionProfileCatalog(this IServiceCollection services)
    {
        // Singleton: состав задаётся набором модулей, а набор — настройкой запуска.
        services.AddSingleton(sp => Build(sp.GetRequiredService<ModuleRegistry>()));
        return services;
    }

    /// <summary>
    /// Каталог по составу модулей. Негодное объявление — исключение: каталог строится при старте
    /// (его требует сидер профилей), и приложение с негодным объявлением не поднимается.
    /// </summary>
    public static RecognitionProfileCatalog Build(ModuleRegistry registry)
    {
        var faults = new List<string>();
        // Своих заводских профилей у ядра нет (issue #1077): последний, «Счёт на оплату», уехал к
        // модулю счетов. Владелец «Общие» остаётся — им подписаны строки прежних копий.
        var declarations = new List<RecognitionProfileDeclaration>();
        var owners = new List<RecognitionProfileOwner>
        {
            new(RecognitionProfileCatalog.CoreOwner, "Общие", Enabled: true),
        };

        foreach (var (module, enabled) in registry.Enabled.Select(m => (m, true))
                     .Concat(registry.Disabled.Select(m => (m, false))))
        {
            owners.Add(new RecognitionProfileOwner(module.Code, module.Title, enabled));
            foreach (var profile in module.RecognitionProfiles)
            {
                // IsDefined и отказ числу — не перестраховка (ревью PR #1252): TryParse принимает «7»
                // и «1», и вид, которого нет, доехал бы до базы, а упал бы список профилей.
                if (int.TryParse(profile.Kind, out _)
                    || !Enum.TryParse<RecognitionProfileKind>(profile.Kind, out var kind) || !Enum.IsDefined(kind))
                {
                    faults.Add($"профиль «{profile.Code}» модуля «{module.Title}» называет вид «{profile.Kind}», " +
                               "которого ядро не знает");
                    continue;
                }
                declarations.Add(new RecognitionProfileDeclaration(
                    profile.Code, profile.Name, kind,
                    Map(profile.Fields), Map(profile.RowColumns), Map(profile.Shape),
                    module.Code, profile.DocumentTag));
            }
        }

        return new RecognitionProfileCatalog(declarations, owners, faults);
    }

    private static IReadOnlyList<RecognitionProfileField> Map(IReadOnlyList<ModuleRecognitionField> fields) =>
        [.. fields.Select(f => new RecognitionProfileField(f.Name, f.Description, f.Type, f.Options))];

    private static RecognitionTableShape? Map(ModuleRecognitionShape? shape) =>
        shape is null ? null : new RecognitionTableShape(shape.TwoTierHeader, shape.PairedSections, shape.SkipTotals);
}
