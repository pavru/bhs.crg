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
                if (profile.Targets is { } targets)
                    faults.AddRange(TargetFaults(module, profile.Code, targets));
                declarations.Add(new RecognitionProfileDeclaration(
                    profile.Code, profile.Name, kind,
                    Map(profile.Fields), Map(profile.RowColumns), Map(profile.Shape),
                    module.Code, profile.DocumentTag, Map(profile.Targets, module)));
            }
        }

        return new RecognitionProfileCatalog(declarations, owners, faults);
    }

    private static IReadOnlyList<RecognitionProfileField> Map(IReadOnlyList<ModuleRecognitionField> fields) =>
        [.. fields.Select(f => new RecognitionProfileField(f.Name, f.Description, f.Type, f.Options))];

    private static RecognitionTableShape? Map(ModuleRecognitionShape? shape) =>
        shape is null ? null : new RecognitionTableShape(shape.TwoTierHeader, shape.PairedSections, shape.SkipTotals);

    private static RecognitionTargets? Map(ModuleRecognitionTargets? targets, IAppModule module) =>
        targets is null
            ? null
            : new RecognitionTargets(targets.RecordType, [.. targets.Fields.Select(t => new RecognitionTargetDeclaration(
                    t.Key, Map(t.Use), t.Why, t.LegacyKeys))],
                // Свои поля типа — из объявления модуля: всё прочее в схеме дописал заказчик.
                (module.RecordTypes.FirstOrDefault(t => t.Code == targets.RecordType)?.Fields ?? [])
                    .Select(f => f.Key).ToHashSet(StringComparer.Ordinal));

    // Явным перечнем, а не приведением числа: зеркала разошлись бы порядком молча.
    private static RecognitionTargetUse Map(ModuleTargetUse use) => use switch
    {
        ModuleTargetUse.Field => RecognitionTargetUse.Field,
        ModuleTargetUse.Input => RecognitionTargetUse.Input,
        ModuleTargetUse.Unavailable => RecognitionTargetUse.Unavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(use), use, "Неизвестное назначение цели распознавания."),
    };

    /// <summary>
    /// Изъяны объявления целей (issue #1483). Каждый — отказ старта, а не тихий пропуск: цель, которой нет
    /// среди полей типа, значила бы «номер со скана перестал заполняться» у заказчика, а повтор ключа —
    /// одно значение в двух полях.
    /// </summary>
    private static IEnumerable<string> TargetFaults(IAppModule module, string profile, ModuleRecognitionTargets targets)
    {
        var where = $"цели профиля «{profile}» модуля «{module.Title}»";
        var type = module.RecordTypes.FirstOrDefault(t => t.Code == targets.RecordType);
        if (type is null)
        {
            yield return $"{where} называют тип «{targets.RecordType}», которого модуль не объявляет";
            yield break;
        }

        var fields = type.Fields.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        // Служебный вход полем быть не обязан (ИНН — реквизит организации, а не счёта); поле и недоступное — обязаны.
        foreach (var target in targets.Fields.Where(t => t.Use != ModuleTargetUse.Input && !fields.Contains(t.Key)))
            yield return $"{where}: поля «{target.Key}» у типа «{type.Code}» нет";
        foreach (var target in targets.Fields.Where(t => t.Use == ModuleTargetUse.Unavailable && string.IsNullOrWhiteSpace(t.Why)))
            yield return $"{where}: у недоступного поля «{target.Key}» не названа причина";

        var keys = targets.Fields.Select(t => t.Key).ToList();
        var legacy = targets.Fields.SelectMany(t => t.LegacyKeys).ToList();
        foreach (var g in keys.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1))
            yield return $"{where}: ключ «{g.Key}» объявлен {g.Count()} раза";
        foreach (var g in legacy.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1))
            yield return $"{where}: прежний ключ «{g.Key}» ведёт в {g.Count()} поля";
        foreach (var key in legacy.Where(keys.Contains))
            yield return $"{where}: прежний ключ «{key}» совпадает с ключом цели";
    }
}
