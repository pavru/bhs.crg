using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Settings;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Backup;

/// <summary>
/// Восстановление настроек ЭКЗЕМПЛЯРА системы (ТЗ CORE-25.3, issue #960): часовой пояс компании и
/// то, что появится рядом.
///
/// <para>Своим файлом, а не строками в <c>BackupService.Restore.cs</c>: тот уже признан неразрывным
/// складом на семьсот строк и стоит в базовом уровне храповика. Дописывать в склад значит двигать
/// его уровень каждой правкой — а правило заведено ровно против этого (ревью PR #1046).</para>
/// </summary>
public partial class BackupService
{
    /// <summary>
    /// Настройки экземпляра (issue #960). Upsert по ключу: копия восстанавливается и поверх живой
    /// системы, и ключ, которого в копии нет, трогать нельзя — его задали здесь, а не там.
    /// </summary>
    private async Task RestoreAppSettingsAsync(
        BackupAppSetting[] items, BackupModuleSchema[] moduleData, RestoreStats stats, List<string> warnings,
        CancellationToken ct)
    {
        // Валюта — до общего выхода «настроек в копии нет»: копия без этого ключа тоже говорит о
        // валюте (см. RestoreAccountingCurrencyAsync).
        var currency = items.LastOrDefault(i => i.Key == AppSettingKeys.AccountingCurrency
            && settingKeys.Accepts(i.Key, i.Value))?.Value;
        var currencyRow = await RestoreAccountingCurrencyAsync(currency, moduleData, warnings, ct);

        // Негодная валюта из копии остаётся в общем разборе — её назовёт общее предупреждение.
        items = [.. items.Where(i => i.Key != AppSettingKeys.AccountingCurrency || currency is null)];
        if (items.Length == 0)
        {
            if (currencyRow != default) stats.Count("Настройки системы", currencyRow.Created, currencyRow.Updated);
            return;
        }

        // Ключ из копии проверяется КАТАЛОГОМ, как и на адресе (ТЗ CORE-25.4): копия приходит от
        // другой сборки, и в ней бывает ключ будущей версии или опечатка. Записать его молча
        // значило бы завести настройку, которую здесь никто не читает, — она выглядела бы
        // действующей. Слишком длинное значение отсекаем по той же причине (ревью PR #1046).
        //
        // Каталог — служба, а не статический список ядра (issue #1070): ключи объявляют ещё и
        // модули, и настройка модуля — в том числе выключенного — обязана вернуться из копии. Ключ
        // модуля, которого в этой сборке нет вовсе, отсекается: читать его некому.
        var (ok, rejected) = Split(items, i => settingKeys.Accepts(i.Key, i.Value));
        Warn(warnings, rejected.Count, "настроек системы",
            "их ключ эта версия не объявляет или значение она не принимает");

        var existing = await db.AppSettings.ToDictionaryAsync(a => a.Key, StringComparer.Ordinal, ct);
        var (created, updated) = currencyRow;
        var changes = new List<(string Key, BHS.CRG.Application.Settings.SettingChange Change)>();
        foreach (var item in ok)
        {
            // В хранимом виде: «1» из копии другой сборки — то же, что «1.00» здесь.
            var value = settingKeys.Normalize(item.Key, item.Value);
            existing.TryGetValue(item.Key, out var row);
            if (settingKeys.Change(item.Key, row?.Value, value) is { } change) changes.Add((item.Key, change));

            if (row is not null) { row.SetValue(value); updated++; }
            else { db.AppSettings.Add(BHS.CRG.Domain.Settings.AppSetting.Create(item.Key, value)); created++; }
        }

        await db.SaveChangesAsync(ct);

        // ⚠️ Смена настройки модуля копией — в журнал, как и смена с экрана (ревью PR #1249): допуск
        // действует на все счета задним числом, и восстановление — штатный путь его поменять. Без
        // записи вопрос «с какого дня изменились суммы» остался бы без ответа. Пишется в той же
        // транзакции, что и само восстановление.
        foreach (var (key, change) in changes)
            await journal.RecordAsync(BHS.CRG.Application.Activity.ActivityActions.ModuleSettingChanged, key,
                change.Label, before: change.Before, after: change.After + " (восстановление копии)", ct: ct);

        db.ChangeTracker.Clear();
        stats.Count("Настройки системы", created, updated);
    }

    /// <summary>
    /// Валюта учёта из копии (issue #1248) — отдельно от остальных настроек, потому что запись её
    /// «как есть» умеет две беды сразу.
    ///
    /// <para><b>Смена поверх живых документов.</b> Восстановление ничего не удаляет: местные счета
    /// остаются, и валюта из копии переодела бы их суммы — тот самый случай, который запрещён на
    /// экране. Поэтому при местных документах валюта остаётся местной, а восстановление говорит об
    /// этом. Спрашиваем ДО восстановления схем модулей — то есть именно о местных документах.</para>
    ///
    /// <para><b>Копия без ключа.</b> Это не «о валюте ничего не сказано», а рубль: его не хранят, пока
    /// не сменили, а версии до настройки другого и не знали. Если такая копия несёт документы, а
    /// здесь выбрана другая валюта, документы легли бы под чужой знак — валюта возвращается к рублю.
    /// Копию без документов и без ключа не трогаем: защищать нечего, а выбор сделан здесь.</para>
    /// </summary>
    /// <param name="inCopy">Валюта из копии; <c>null</c> — ключа нет или значение не из списка.</param>
    private async Task<(int Created, int Updated)> RestoreAccountingCurrencyAsync(
        string? inCopy, BackupModuleSchema[] moduleData, List<string> warnings, CancellationToken ct)
    {
        var row = await db.AppSettings.FirstOrDefaultAsync(a => a.Key == AppSettingKeys.AccountingCurrency, ct);
        var target = AccountingCurrencies.Effective(inCopy);

        if (settingKeys.Change(AppSettingKeys.AccountingCurrency, row?.Value, target) is not { } change)
            return default;

        if (inCopy is null && !currencyLock.Carries(moduleData)) return default;

        var holders = await currencyLock.HoldersAsync(ct);
        if (holders.Count > 0)
        {
            warnings.Add(
                $"Валюта учёта в копии — {change.After}, на этой установке — {change.Before}. Она НЕ " +
                $"изменена: здесь уже есть документы с суммами ({AccountingCurrencyLockText.Holders(holders)}), " +
                "а суммы при смене валюты не пересчитываются. Документы из копии заведены в её валюте " +
                "— проверьте их суммы.");
            return default;
        }

        if (row is not null) row.SetValue(target);
        else db.AppSettings.Add(AppSetting.Create(AppSettingKeys.AccountingCurrency, target));
        await db.SaveChangesAsync(ct);

        await journal.RecordAsync(BHS.CRG.Application.Activity.ActivityActions.AccountingCurrencyChanged,
            AppSettingKeys.AccountingCurrency, change.Label,
            before: change.Before, after: change.After + " (восстановление копии)", ct: ct);
        return row is null ? (1, 0) : (0, 1);
    }
}
