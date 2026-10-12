namespace BHS.CRG.Modules;

/// <summary>
/// Объявления учётных таблиц модулей годны (issue #1248) — проверка при старте, по ВСЕЙ сборке.
///
/// <para>При старте, а не при первом обращении: имя таблицы уходит в запрос текстом, и негодное
/// объявление иначе обнаружилось бы отказом экрана настроек или восстановления копии — у заказчика,
/// а не у того, кто собирал поставку (ревью PR #1306). По всей сборке, потому что документы
/// выключенного модуля держат валюту так же.</para>
/// </summary>
public static class ModuleAccountingDocuments
{
    /// <summary>Что нарушено — по строке на модуль; пусто — нарушений нет.</summary>
    public static IReadOnlyList<string> Problems(IEnumerable<IAppModule> modules) =>
        [.. from module in modules
            where module.AccountingDocuments.Count > 0
            let problem = Problem(module)
            where problem is not null
            select $"модуль «{module.Code}»: {problem}"];

    /// <summary>Отказ старта, называющий модуль.</summary>
    public static void Ensure(IEnumerable<IAppModule> modules)
    {
        var problems = Problems(modules);
        if (problems.Count == 0) return;

        throw new InvalidOperationException(
            "Учётные таблицы модулей объявлены негодно:\n  " + string.Join("\n  ", problems) + "\n" +
            $"По объявлению {nameof(IAppModule)}.{nameof(IAppModule.AccountingDocuments)} ядро решает, " +
            "можно ли сменить валюту учёта: имя таблицы уходит в запрос как есть, и негодное объявление " +
            "отказало бы экрану настроек и восстановлению копии.");
    }

    private static string? Problem(IAppModule module)
    {
        if (module.Schema is null) return "учётные таблицы объявлены, а схема базы — нет";

        if (module.AccountingDocuments.Select(d => d.Validate()).FirstOrDefault(p => p is not null) is { } invalid)
            return invalid;

        return module.AccountingDocuments.GroupBy(d => d.Table, StringComparer.Ordinal).Any(g => g.Count() > 1)
            ? "учётная таблица названа дважды"
            : null;
    }
}
