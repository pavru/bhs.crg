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
    /// понадобился. Так устроена любая копия, снятая до исправления: файлов модулей в ней нет.</para>
    ///
    /// <para>Пути вычисляются из восстановленных строк ЗАНОВО, а не берутся из перечня файлов копии: у
    /// прежней копии перечня нет, а предупреждение нужно как раз ей. Хранилище спрашивают только о
    /// путях, которых НЕТ В АРХИВЕ: файл из архива либо положен и на месте, либо не положен — и об
    /// этом отчёт уже сказал. Так у исправной копии вопросов к хранилищу нет вовсе, а у прежней их
    /// столько, сколько файлов она не несла. Файл, уцелевший на этой установке, отказом не считается.</para>
    ///
    /// <para>⚠️ «Не найден» хранилище отвечает и тогда, когда до него не достучаться, — различить это
    /// его ответ не даёт. Поэтому текст говорит «не найдены» и называет вторую причину, а не
    /// утверждает потерю.</para>
    /// </summary>
    public async Task WarnAboutMissingFilesAsync(
        BackupModuleSchema[] restored, IReadOnlySet<string> inArchive, List<string> warnings,
        CancellationToken ct)
    {
        var blobs = scoped.GetRequiredService<IBlobStorage>();
        var present = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var section in restored)
        {
            if (registry.Find(section.Module) is not { } module) continue;

            var found = ModuleFileScan.Scan(section.Tables, module.Files);
            var missing = new List<string>();

            foreach (var (what, paths) in found.Groups.OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var absent = 0;
                foreach (var path in paths)
                {
                    if (inArchive.Contains(path)) continue;
                    if (!present.TryGetValue(path, out var here))
                        present[path] = here = await blobs.GetSizeAsync(path, ct) is not null;
                    if (!here) absent++;
                }

                if (absent > 0) missing.Add($"{what} — {absent} из {paths.Count}");
            }

            if (missing.Count == 0) continue;

            warnings.Add(
                $"Модуль «{module.Code}»: в копии нет файлов, и в хранилище они не найдены: " +
                $"{string.Join("; ", missing)}. Записи вернулись и показывают файл приложенным, но " +
                "открыть его будет нечем." +
                (section.Files is null
                    ? " Копия снята версией, которая файлы модулей не брала: если исходная установка " +
                      "цела, обновите её, снимите копию заново и восстановите из новой."
                    : "") +
                " Если хранилище во время восстановления было недоступно, повторите восстановление: " +
                "на недоступность оно отвечает так же, как на отсутствие файла.");
        }
    }
}
