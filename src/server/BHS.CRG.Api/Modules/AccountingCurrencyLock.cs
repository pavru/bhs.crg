using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BHS.CRG.Api.Modules;

/// <summary>
/// Кто держит валюту учёта (issue #1248): документы с суммами по объявлениям
/// <see cref="IAppModule.AccountingDocuments" />, сосчитанные самим ядром.
///
/// <para>Живёт в приложении: только оно видит и базу ядра, и состав модулей. Считает сырым запросом
/// к схеме модуля, без его модели и служб, — тем же приёмом, каким копия ищет данные выключенного
/// модуля (<see cref="ModuleSchemaBackup" />): выключенный модуль со счетами валюту держит так же.</para>
///
/// <para>Запросы идут через контекст ядра и потому встают в его транзакцию, если она открыта.
/// Имена таблиц — в тексте запроса: параметром имя не передать. Они из объявлений в коде, и форму
/// их проверяет старт (<see cref="ModuleAccountingDocuments" />) — кавычке в них взяться неоткуда.</para>
/// </summary>
public sealed class AccountingCurrencyLock(AppDbContext db, ModuleRegistry registry) : IAccountingCurrencyLock
{
    /// <summary>Сколько ждать замка таблиц: дольше пишет разве что восстановление копии.</summary>
    private const string FreezeTimeout = "5s";

    public async Task<IReadOnlyList<CurrencyHolder>> HoldersAsync(CancellationToken ct = default)
    {
        var tables = await ExistingAsync(ct);
        if (tables.Count == 0) return [];

        // Одним запросом на все таблицы (ревью PR #1306): адрес настройки зовёт это при каждом
        // открытии экрана. Число нужно тексту причины — «счетов: 12», поэтому count, а не EXISTS.
        var sql = string.Join(" UNION ALL ", tables.Select((t, i) =>
            $"SELECT {i} AS \"Index\", count(*) AS \"Count\" FROM {Quoted(t.Module, t.Document)}"));
        var counts = (await db.Database.SqlQueryRaw<TableCount>(sql).ToListAsync(ct))
            .ToDictionary(c => c.Index, c => c.Count);

        return [.. tables.Select((t, i) => (t.Module, t.Enabled, t.Document.Noun, Count: counts[i]))
            .Where(t => t.Count > 0)
            .GroupBy(t => (t.Module, t.Enabled))
            .Select(g => new CurrencyHolder(g.Key.Module.Code, g.Key.Module.Title, g.Key.Enabled,
                [.. g.Select(t => new CurrencyHeldCount(t.Noun, t.Count))]))];
    }

    public async Task FreezeAsync(CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "Учётные таблицы запирают внутри транзакции: замок живёт до её конца, а без неё он " +
                "снялся бы сразу и ничего не закрыл.");

        var tables = await ExistingAsync(ct);
        if (tables.Count == 0) return;

        // Срок ожидания — только на этот замок: остальная транзакция живёт с прежним.
        var before = await db.Database
            .SqlQuery<string>($"SELECT current_setting('lock_timeout') AS \"Value\"").SingleAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT set_config('lock_timeout', {FreezeTimeout}, true)", ct);
        try
        {
            // SHARE: читать можно, писать — нет. Вставка, уже идущая в чужой транзакции, заставит
            // подождать её конца — и сверка, сделанная следом, её увидит.
            await db.Database.ExecuteSqlRawAsync("LOCK TABLE " +
                string.Join(", ", tables.Select(t => Quoted(t.Module, t.Document))) + " IN SHARE MODE", ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            throw new AccountingTablesBusyException();
        }

        await db.Database.ExecuteSqlAsync($"SELECT set_config('lock_timeout', {before}, true)", ct);
    }

    public bool Carries(IEnumerable<BackupModuleSchema> data)
    {
        // Только то, что восстановление ПОЛОЖИТ в базу (ревью PR #1306): секцию выключенного модуля
        // и секцию с чужим именем схемы оно пропускает — документы из них не придут, и сбрасывать
        // из-за них выбранную здесь валюту было бы изменением без причины.
        var declared = registry.Enabled
            .Where(m => m.Schema is not null && m.AccountingDocuments.Count > 0)
            .ToDictionary(m => m.Code, StringComparer.OrdinalIgnoreCase);

        return data.Any(section => declared.TryGetValue(section.Module, out var module)
            && string.Equals(module.Schema!.Name, section.Schema, StringComparison.Ordinal)
            && section.Tables.Any(t => t.Rows.Length > 0
                && module.AccountingDocuments.Any(d => string.Equals(d.Table, t.Table, StringComparison.Ordinal))));
    }

    /// <summary>
    /// Объявленные учётные таблицы, которые в базе ЕСТЬ, — у включённых и выключенных модулей.
    ///
    /// <para>Таблицы нет — модуль на этом экземпляре не включали ни разу, его схема не создана. Это
    /// «документов нет» по ответу базы, а не по молчанию: <c>to_regclass</c> отвечает null именно на
    /// отсутствие, а отказ в чтении приходит исключением и смену не разрешает.</para>
    /// </summary>
    private async Task<List<(IAppModule Module, bool Enabled, ModuleAccountingDocument Document)>> ExistingAsync(
        CancellationToken ct)
    {
        var declared = registry.Enabled.Select(m => (Module: m, Enabled: true))
            .Concat(registry.Disabled.Select(m => (Module: m, Enabled: false)))
            .Where(m => m.Module.Schema is not null)
            .SelectMany(m => m.Module.AccountingDocuments.Select(d => (m.Module, m.Enabled, Document: d)))
            .ToList();
        if (declared.Count == 0) return [];

        var names = declared.Select(d => Plain(d.Module, d.Document)).ToArray();
        var present = await db.Database
            .SqlQuery<string>($"SELECT name AS \"Value\" FROM unnest({names}) AS name WHERE to_regclass(name) IS NOT NULL")
            .ToListAsync(ct);

        return [.. declared.Where(d => present.Contains(Plain(d.Module, d.Document)))];
    }

    private static string Plain(IAppModule module, ModuleAccountingDocument document) =>
        $"{module.Schema!.Name}.{document.Table}";

    private static string Quoted(IAppModule module, ModuleAccountingDocument document) =>
        $"\"{module.Schema!.Name}\".\"{document.Table}\"";

    private sealed record TableCount(int Index, long Count);
}
