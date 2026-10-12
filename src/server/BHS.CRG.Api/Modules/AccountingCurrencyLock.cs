using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Modules;

/// <summary>
/// Кто держит валюту учёта (issue #1248): документы с суммами по объявлениям
/// <see cref="IAppModule.AccountingDocuments" />, сосчитанные самим ядром.
///
/// <para>Живёт в приложении: только оно видит и базу ядра, и состав модулей. Считает сырым запросом
/// к схеме модуля, без его модели и служб, — тем же приёмом, каким копия ищет данные выключенного
/// модуля (<see cref="ModuleSchemaBackup" />): выключенный модуль со счетами валюту держит так же.</para>
///
/// <para>Запросы идут через контекст ядра и потому встают в его транзакцию, если она открыта: смена
/// валюты спрашивает о документах ПОСЛЕДНИМ оператором перед фиксацией.</para>
/// </summary>
public sealed class AccountingCurrencyLock(AppDbContext db, ModuleRegistry registry) : IAccountingCurrencyLock
{
    public async Task<IReadOnlyList<CurrencyHolder>> HoldersAsync(CancellationToken ct = default)
    {
        var holders = new List<CurrencyHolder>();

        foreach (var (module, enabled) in Declaring(registry))
        {
            var counts = new List<CurrencyHeldCount>();
            foreach (var document in module.AccountingDocuments)
            {
                var table = $"{module.Schema!.Name}.{document.Table}";
                // Таблицы нет — модуль на этом экземпляре не включали ни разу, его схема не создана.
                // Это «документов нет» по ответу базы, а не по молчанию: to_regclass отвечает null
                // именно на отсутствие, а отказ в чтении приходит исключением и смену не разрешает.
                var exists = await db.Database
                    .SqlQuery<bool>($"SELECT to_regclass({table}) IS NOT NULL AS \"Value\"").SingleAsync(ct);
                if (!exists) continue;

                // Имя в тексте запроса: параметром имя таблицы не передать. Оно из объявления в коде
                // и проверено на форму (Declaring) — кавычке в нём взяться неоткуда.
                var count = await db.Database
                    .SqlQueryRaw<long>($"SELECT count(*) AS \"Value\" FROM \"{module.Schema.Name}\".\"{document.Table}\"")
                    .SingleAsync(ct);
                if (count > 0) counts.Add(new CurrencyHeldCount(document.Noun, count));
            }

            if (counts.Count > 0) holders.Add(new CurrencyHolder(module.Code, module.Title, enabled, counts));
        }

        return holders;
    }

    public bool Carries(IEnumerable<BackupModuleSchema> data)
    {
        var declared = Declaring(registry).ToDictionary(
            m => m.Module.Code,
            m => m.Module.AccountingDocuments.Select(d => d.Table).ToHashSet(StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);

        return data.Any(section => declared.TryGetValue(section.Module, out var tables)
            && section.Tables.Any(t => t.Rows.Length > 0 && tables.Contains(t.Table)));
    }

    /// <summary>
    /// Модули сборки, объявившие учётные таблицы, — включённые и выключенные. Негодное объявление —
    /// отказ, а не пропуск: пропущенная таблица читалась бы как «документов нет».
    /// </summary>
    public static IEnumerable<(IAppModule Module, bool Enabled)> Declaring(ModuleRegistry registry)
    {
        foreach (var (module, enabled) in registry.Enabled.Select(m => (m, true))
                     .Concat(registry.Disabled.Select(m => (m, false))))
        {
            if (module.AccountingDocuments.Count == 0) continue;

            var problem = module.Schema is null
                ? "учётные таблицы объявлены, а схема базы — нет"
                : module.Schema.Validate(module.Code)
                  ?? module.AccountingDocuments.Select(d => d.Validate()).FirstOrDefault(p => p is not null);
            if (problem is not null)
                throw new InvalidOperationException(
                    $"Модуль «{module.Code}» объявил учётные таблицы негодно: {problem}.");

            yield return (module, enabled);
        }
    }
}
