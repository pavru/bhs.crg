using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Settings;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BHS.CRG.Api.Modules;

/// <summary>
/// Кто держит валюту учёта и как её сменить (issue #1248): документы с суммами по объявлениям
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
public sealed class AccountingCurrencyLock(AppDbContext db, ModuleRegistry registry, IActivityLog journal)
    : IAccountingCurrencyLock
{
    /// <summary>Сколько ждать замков смены: дольше пишет разве что восстановление копии.</summary>
    private const string LockTimeout = "5s";

    public async Task<CurrencyChangeOutcome> TryChangeAsync(
        string next, string? note = null, CancellationToken ct = default)
    {
        if (!AccountingCurrencies.IsKnown(next))
            throw new ArgumentException($"Валюты «{next}» в списке нет.", nameof(next));

        // Замки — первыми, чтение — после них: вторая из двух одновременных смен обязана увидеть
        // результат первой, а не прочитать «было рубль» рядом с ней и записать поверх.
        var locked = await TryLockAsync(ct);

        var row = await db.AppSettings.FirstOrDefaultAsync(a => a.Key == AppSettingKeys.AccountingCurrency, ct);
        if (AppSettingCatalog.CurrencyChange(row?.Value, next) is not { } change)
        {
            // Действует то же самое. Строку правим, только если в базе лежит негодное: это уборка,
            // знак сумм она не меняет — и ни запрет, ни занятые таблицы ей не помеха.
            if (row is not null && row.Value != next)
            {
                row.SetValue(next);
                await db.SaveChangesAsync(ct);
            }

            return new CurrencyChangeOutcome.Unchanged();
        }

        if (!locked) return new CurrencyChangeOutcome.Busy(change);

        var holders = await HoldersAsync(ct);
        if (holders.Count > 0) return new CurrencyChangeOutcome.Held(change, holders);

        if (row is not null) row.SetValue(next);
        else db.AppSettings.Add(AppSetting.Create(AppSettingKeys.AccountingCurrency, next));
        await db.SaveChangesAsync(ct);

        // Журнал — в той же транзакции: значение без записи осталось бы сменой без следа.
        await journal.RecordAsync(ActivityActions.AccountingCurrencyChanged, AppSettingKeys.AccountingCurrency,
            change.Label, before: change.Before, after: change.After + note, ct: ct);
        return new CurrencyChangeOutcome.Changed(change, Created: row is null);
    }

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

    /// <summary>
    /// Взять оба замка смены до конца текущей транзакции; <c>false</c> — не дождались за срок.
    ///
    /// <para><b>Очередь смен</b> (advisory): строки настройки может ещё не быть, запереть её нечем, а
    /// замки таблиц между собой совместимы — без очереди две одновременные смены обе прочли бы
    /// «было рубль» и обе записали, с двумя несходящимися записями журнала (ревью PR #1306).</para>
    ///
    /// <para><b>Учётные таблицы</b> — SHARE: читать можно, писать нет. Вставка, уже идущая в чужой
    /// транзакции, заставит подождать её конца — и сверка, сделанная следом, её увидит. Без этого
    /// счёт, зафиксированный между сверкой и фиксацией смены, получил бы новый знак.</para>
    /// </summary>
    public async Task<bool> TryLockAsync(CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "Валюту меняют внутри транзакции: замки живут до её конца, а без неё они снялись бы " +
                "сразу и ничего не закрыли.");

        var tables = await ExistingAsync(ct);

        // Срок ожидания — только на эти замки: остальная транзакция живёт с прежним. Точка
        // сохранения — потому что отказ в замке обрывает транзакцию, а вызывающему она ещё нужна:
        // восстановление копии после такого отказа продолжается.
        var before = await db.Database
            .SqlQuery<string>($"SELECT current_setting('lock_timeout') AS \"Value\"").SingleAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SAVEPOINT currency_locks", ct);
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT set_config('lock_timeout', {LockTimeout}, true)", ct);
            await db.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_xact_lock(hashtext({AppSettingKeys.AccountingCurrency}))", ct);
            if (tables.Count > 0)
                await db.Database.ExecuteSqlRawAsync("LOCK TABLE " +
                    string.Join(", ", tables.Select(t => Quoted(t.Module, t.Document))) + " IN SHARE MODE", ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            await db.Database.ExecuteSqlRawAsync("ROLLBACK TO SAVEPOINT currency_locks", ct);
            return false;
        }

        await db.Database.ExecuteSqlAsync($"SELECT set_config('lock_timeout', {before}, true)", ct);
        return true;
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
