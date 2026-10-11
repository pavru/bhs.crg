using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Modules;

namespace BHS.CRG.Api.Modules;

// Файлы модуля в копии (issue #1261): что взять при снятии и о чём сказать при восстановлении.
// Само правило — в ModuleFileScan; здесь только слова и хранилище.
public sealed partial class ModuleSchemaBackup
{
    /// <summary>
    /// Пути файлов, на которые ссылаются снятые строки модуля, — они уезжают в копию вместе со
    /// строками.
    ///
    /// <para>Оба предупреждения — про то, что обход увидел, а назвать или взять не смог. Молчать о
    /// втором нельзя: файл, которого нет в копии, обнаруживают после аварии.</para>
    /// </summary>
    private static string[] FilesOf(IAppModule module, IReadOnlyList<BackupModuleTable> tables, List<string> warnings)
    {
        var found = ModuleFileScan.Scan(tables, module.Files);

        if (found.Undeclared.Count > 0)
            warnings.Add(
                $"Модуль «{module.Code}»: пути файлов найдены в колонках, о которых модуль не сообщил, — " +
                $"{string.Join(", ", found.Undeclared)}. Файлы в копию ВЗЯТЫ; сообщите разработчику — " +
                "колонку нужно объявить, иначе отчёты называют её адресом.");

        if (found.Embedded.Count > 0)
            warnings.Add(
                $"Модуль «{module.Code}»: ссылки на файлы записаны внутри текста — " +
                $"{string.Join(", ", found.Embedded.Select(e => $"{e.Key} ({e.Value})"))}. Точный путь из " +
                "такой записи не извлечь, и эти файлы в копию НЕ вошли. Сообщите разработчику.");

        return found.Paths;
    }

    /// <summary>
    /// Строки модуля восстановлены — а файлы, на которые они ссылаются, есть? (issue #1261)
    ///
    /// <para>Отказ обязан выглядеть отказом. Без этой проверки восстановление отвечало успехом, счёт
    /// показывал файл приложенным, а открыть его было нечем — и узнавали об этом, когда файл
    /// понадобился. Так устроена любая копия, снятая до 1.28.3: файлов модулей в ней нет.</para>
    ///
    /// <para>Пути вычисляются из строк копии ЗАНОВО, а не берутся из её перечня файлов: у старой
    /// копии перечня нет, а предупреждение нужно как раз ей. Спрашивается хранилище, а не архив:
    /// файлы архива положены туда до базы, так что «нет в хранилище» уже значит «нет ни в копии, ни
    /// на этой установке» — а файл, уцелевший на месте, отказом не считается.</para>
    ///
    /// <para>Цена — один вопрос хранилищу на файл; столько же стоит оценка веса копии.</para>
    /// </summary>
    private async Task WarnAboutMissingFilesAsync(
        IAppModule module, BackupModuleSchema section, List<string> warnings, CancellationToken ct)
    {
        var found = ModuleFileScan.Scan(section.Tables, module.Files);
        if (found.Groups.Count == 0) return;

        var blobs = scoped.GetRequiredService<IBlobStorage>();
        var sizes = new Dictionary<string, bool>(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var (what, paths) in found.Groups.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var absent = 0;
            foreach (var path in paths)
            {
                if (!sizes.TryGetValue(path, out var present))
                    sizes[path] = present = await blobs.GetSizeAsync(path, ct) is not null;
                if (!present) absent++;
            }

            if (absent > 0) missing.Add($"{what} — {absent} из {paths.Count}");
        }

        if (missing.Count == 0) return;

        warnings.Add(
            $"Модуль «{module.Code}»: файлов нет ни в копии, ни в хранилище: {string.Join("; ", missing)}. " +
            "Записи вернулись и показывают файл приложенным, но открыть его будет нечем." +
            (section.Files is null
                ? " Копия снята версией, которая файлы модулей не брала: если исходная установка " +
                  "цела, обновите её, снимите копию заново и восстановите из новой."
                : ""));
    }
}
