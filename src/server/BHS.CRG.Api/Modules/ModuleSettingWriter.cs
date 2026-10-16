using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Common;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Settings;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Modules;

/// <summary>
/// Запись настроек модулей (задача M1, issue #1070) вместе с обработчиками их смены (E2, issue #1100,
/// spec v3.3 п. 1.9). Служба, а не тело адреса: порядок здесь — всё, и адресу расти некуда.
///
/// <para><b>Порядок.</b> Транзакция ядра → замки по ключам настроек (исключительные, по порядку ключей) →
/// разделяемый замок записи в учёт, если у смены есть обработчик (закрытие периода ждёт всю смену) →
/// границы периода под ним → «было» → обработчик модуля пишет и ФИКСИРУЕТ своё → значение и журнал →
/// фиксация ядра → замки сняты → выравнивание модуля к действующему значению.</para>
///
/// <para><b>Почему модуль первым.</b> Общей транзакции у ядра и модуля нет (решение 28.09.2026). Сорвись
/// ядро после модуля — модуль окажется впереди настройки, и выравнивание тут же вернёт его к прежнему
/// значению; закрытые дни при этом целы. Наоборот — запертый счёт остался бы без книги при новом допуске,
/// и его закрытый месяц сдвинулся бы молча.</para>
///
/// <para>⚠️ Выравнивание — ПОСЛЕ того, как транзакция ядра закрыта: модуль берёт замок записи в учёт на
/// своём соединении, и при живом замке ядра встал бы в очередь за ждущим закрытием, которое ждёт ядро.</para>
/// </summary>
public sealed class ModuleSettingWriter(
    IEnumerable<IModuleSettingChange> handlers, AppDbContext db, IAppSettingsStore store, IActivityLog journal,
    IModulePeriods periods, ILogger<ModuleSettingWriter> log)
{
    private readonly Dictionary<string, IModuleSettingChange> _handlers =
        handlers.ToDictionary(h => h.Setting.Key, StringComparer.Ordinal);

    /// <summary>
    /// Сторож при старте: обработчик стережёт настройку, объявленную включённым модулем, — тем же объектом,
    /// — и у настройки он один. Обработчик чужой или забытой настройки не позвали бы никогда: смена прошла
    /// бы мимо него, и запертые счета остались бы без книги.
    /// </summary>
    public static void Validate(IEnumerable<IModuleSettingChange> handlers, ModuleSettingCatalog catalog, ModuleRegistry registry)
    {
        var problems = new List<string>();
        foreach (var group in handlers.GroupBy(h => h.Setting.Key, StringComparer.Ordinal))
        {
            var setting = group.First().Setting;
            if (group.Count() > 1)
                problems.Add($"у настройки «{group.Key}» обработчиков смены несколько: " +
                             string.Join(", ", group.Select(h => h.GetType().FullName)));
            if (!catalog.Declares(setting) || !registry.IsEnabled(setting.Module))
                problems.Add($"обработчик {group.First().GetType().FullName} стережёт настройку «{group.Key}», которую " +
                             "не объявляет ни один включённый модуль (или объявляет другим объектом)");
        }

        if (problems.Count > 0)
            throw new InvalidOperationException("Негодные обработчики смены настроек модулей:\n" + string.Join("\n", problems));
    }

    /// <summary>
    /// Что сделает смена с записанными данными — у настроек с обработчиком, чьё действующее значение
    /// меняется. Значения уже проверены.
    /// </summary>
    public async Task<IReadOnlyList<(ModuleSetting Setting, ModuleSettingImpact Impact)>> PreviewAsync(
        IAppModule module, IReadOnlyDictionary<string, string?> values, CancellationToken ct)
    {
        var declared = module.Settings.ToDictionary(s => s.Key, StringComparer.Ordinal);
        var before = await store.GetManyAsync([.. values.Keys], ct);
        var result = new List<(ModuleSetting, ModuleSettingImpact)>();
        foreach (var (key, value) in values)
        {
            var setting = declared[key];
            var (was, now) = (before.GetValueOrDefault(key), value is null ? null : setting.Normalize(value.Trim()));
            if (_handlers.TryGetValue(key, out var handler) && ModuleSettingValues.Change(module.Title, setting, was, now) is not null)
                result.Add((setting, await handler.PreviewAsync(was, now, ct)));
        }

        return result;
    }

    /// <summary>Записать значения — все или ни одного. Значения уже проверены.</summary>
    public async Task SaveAsync(IAppModule module, IReadOnlyDictionary<string, string?> values, CancellationToken ct)
    {
        var declared = module.Settings.ToDictionary(s => s.Key, StringComparer.Ordinal);
        var applied = new List<(IModuleSettingChange Handler, string? Was, string? Now)>();
        var committed = false;

        // ⚠️ Значения и записи журнала — ОДНОЙ транзакцией (ревью PR #1249). Хранилище и журнал сохраняют
        // каждый сам, и без неё значение, записанное до сбоя журнала, уже действовало бы на все счета — без
        // следа и при ответе «не сохранилось». Журнал здесь единственный ответ на вопрос «с какого дня
        // изменились суммы».
        var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Очередь смен по ключу: строки настройки может ещё не быть, запереть её нечем, а без очереди две
            // одновременные смены прочли бы одно «было» — и обработчик провёл бы одну разницу дважды.
            foreach (var key in values.Keys.Order(StringComparer.Ordinal))
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({key}))", ct);
            var boundaries = values.Keys.Any(_handlers.ContainsKey) ? await HoldPeriodAsync(ct) : null;
            var before = await store.GetManyAsync([.. values.Keys], ct);

            foreach (var (key, value) in values)
            {
                var setting = declared[key];
                var was = before.GetValueOrDefault(key);
                var now = value is null ? null : setting.Normalize(value.Trim());

                var change = ModuleSettingValues.Change(module.Title, setting, was, now);
                // Значение не меняется. Строку правим, только когда в базе лежит не то, что должно: негодное
                // или записанное не в хранимом виде. Несохранённую настройку тем же значением, что умолчание,
                // не заводим — иначе экран предложил бы «вернуть умолчание» там, где оно и действует.
                if (change is null && (was is null || string.Equals(was, now, StringComparison.Ordinal))) continue;

                if (change is not null && _handlers.TryGetValue(key, out var handler))
                {
                    await handler.ApplyAsync(new ModuleSettingChange(was, now, boundaries!), ct);
                    applied.Add((handler, was, now));
                }

                // Модуль уже зафиксировал своё: дальше обрыв запроса не должен сорвать запись ядра.
                var tail = applied.Count > 0 ? CancellationToken.None : ct;
                await store.SetAsync(key, now, tail);
                // В журнал — только настоящая смена: «было 1,00, стало 1,00» — шум, за которым теряется
                // запись, ради которой журнал открывали.
                if (change is not null)
                    await journal.RecordAsync(ActivityActions.ModuleSettingChanged, key,
                        change.Label, before: change.Before, after: change.After, ct: tail);
            }

            await tx.CommitAsync(applied.Count > 0 ? CancellationToken.None : ct);
            committed = true;
        }
        finally
        {
            await tx.DisposeAsync();
            foreach (var (handler, was, now) in applied)
                await AlignAsync(handler, committed ? now : was);
        }
    }

    /// <summary>
    /// Выровнять модули к действующим значениям — при старте: так чинится смена, сорвавшаяся между двумя
    /// фиксациями (модуль записал, ядро — нет, и процесс остановился раньше выравнивания).
    /// </summary>
    public async Task AlignAllAsync(CancellationToken ct = default)
    {
        foreach (var (key, handler) in _handlers)
            await AlignAsync(handler, await store.GetAsync(key, ct));
    }

    /// <summary>
    /// Разделяемый замок записи в учёт на соединении ЯДРА — до конца его транзакции — и границы под ним.
    /// Модуль свой не берёт: он передан ему вместе с границами (<see cref="IModuleSettingChange" />).
    /// </summary>
    private async Task<PeriodBoundaries> HoldPeriodAsync(CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock_shared({AdvisoryLockKeys.PeriodWrite})", ct);
        return await periods.BoundariesAsync(ct);
    }

    /// <summary>
    /// Выравнивание не отменяется и не роняет ответ: значение уже записано (или смена уже отказала своим
    /// отказом), а несведённое модуль сам называет в журнале приложения и выровняет при следующем старте.
    /// </summary>
    private async Task AlignAsync(IModuleSettingChange handler, string? current)
    {
        try
        {
            await handler.AlignAsync(current, CancellationToken.None);
        }
        catch (Exception e)
        {
            log.LogError(e, "Модуль не выровнял записанное к настройке {Key}: выровняет при следующем старте",
                handler.Setting.Key);
        }
    }
}
