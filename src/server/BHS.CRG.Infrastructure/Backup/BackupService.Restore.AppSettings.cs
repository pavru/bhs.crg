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
        // Валюта — отдельно и до общего выхода «настроек в копии нет»: копия без этого ключа тоже
        // говорит о валюте (см. RestoreAccountingCurrencyAsync).
        var currencyRow = await RestoreAccountingCurrencyAsync(
            items.LastOrDefault(i => i.Key == AppSettingKeys.AccountingCurrency)?.Value, moduleData, warnings, ct);
        items = [.. items.Where(i => i.Key != AppSettingKeys.AccountingCurrency)];
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
            if (settingKeys.Change(item.Key, row?.Value, value) is { } change)
            {
                // Смена настройки, которую модуль проводит своим обработчиком (допуск расхождения пишет книгу
                // запертым счетам, E2): копия без данных модуля провела бы её над здешними счетами без книги.
                if (await settingGuard.RestoreRefusalAsync(item.Key, row?.Value, value, moduleData, ct) is { } why)
                {
                    warnings.Add($"«{change.Label}»: в копии — {change.After}, на этой установке — {change.Before}. " +
                                 "Значение НЕ изменено: копия не несёт данных модуля, а смена коснулась бы здешних " +
                                 $"документов. {why} Смените её в настройках модулей — там она запишется как положено.");
                    continue;
                }

                changes.Add((item.Key, change));
            }

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
    ///
    /// <para><b>Валюта, которой эта версия не знает.</b> Не «ключа нет»: копия называет валюту, просто
    /// не из нашего списка. Приравнять её к рублю значило бы сбросить местный выбор и положить
    /// документы копии под рубль (ревью PR #1306). Валюта не трогается, предупреждение называет её.</para>
    ///
    /// <para>Саму смену делает <see cref="IAccountingCurrencyLock.TryChangeAsync" /> — та же
    /// операция, что на экране настройки: с замком учётных таблиц и записью в журнал.</para>
    /// </summary>
    /// <param name="inCopy">Значение ключа в копии как есть; <c>null</c> — ключа нет.</param>
    private async Task<(int Created, int Updated)> RestoreAccountingCurrencyAsync(
        string? inCopy, BackupModuleSchema[] moduleData, List<string> warnings, CancellationToken ct)
    {
        // Совет проверить суммы — только когда документы из копии действительно придут: иначе он
        // отправлял бы искать то, чего в копии нет.
        var carries = currencyLock.Carries(moduleData);
        var check = carries ? " Документы из копии заведены в её валюте — проверьте их суммы." : "";

        if (inCopy is not null && !AccountingCurrencies.IsKnown(inCopy))
        {
            warnings.Add(
                $"Валюта учёта в копии — «{inCopy}»; эта версия такой не знает. Валюта на этой установке " +
                $"НЕ изменена.{check}");
            return default;
        }

        if (inCopy is null && !carries) return default;

        switch (await currencyLock.TryChangeAsync(
                    AccountingCurrencies.Effective(inCopy), " (восстановление копии)", ct))
        {
            case CurrencyChangeOutcome.Changed changed:
                return changed.Created ? (1, 0) : (0, 1);

            case CurrencyChangeOutcome.Held held:
                warnings.Add(
                    $"Валюта учёта в копии — {held.Change.After}, на этой установке — {held.Change.Before}. " +
                    "Она НЕ изменена: здесь уже есть документы с суммами " +
                    $"({AccountingCurrencyLockText.Holders(held.Holders)}), а суммы при смене валюты не " +
                    $"пересчитываются.{check}");
                return default;

            case CurrencyChangeOutcome.Busy busy:
                warnings.Add(
                    $"Валюта учёта в копии — {busy.Change.After}, на этой установке — {busy.Change.Before}. " +
                    "Она НЕ изменена: во время восстановления в счета или накладные шла запись. " +
                    $"Смените её в настройках, пока документов нет.{check}");
                return default;

            default:
                return default;
        }
    }
}
