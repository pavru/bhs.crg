using BHS.CRG.Application.Backup;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Backup;

// Файлы ядра после восстановления (issue #1437): о копии без файлов отчёт обязан сказать.
public partial class BackupService
{
    /// <summary>
    /// Записи восстановлены — а файлы, на которые они ссылаются, есть? (issue #1437)
    ///
    /// <para>Отказ обязан выглядеть отказом. Копия, снятая версией, которая какое-то место хранения пути
    /// не обходила, восстанавливалась с «Файлы: восстановлено N из N»: в этом числе только то, что в
    /// архиве, а о файлах, на которые ссылаются вернувшиеся записи, отчёт молчал — и узнавали о них,
    /// когда файл понадобился. Тот же приём, что у файлов модулей
    /// (<see cref="IModuleSchemaBackup.WarnAboutMissingFilesAsync" />); у частей PDF-наборов (#1399) он
    /// был своим, теперь он здесь же.</para>
    ///
    /// <para>Пути — тем же выбором, что у копии (<see cref="SelectBlobs" />: список мест ∪ обход
    /// манифеста), поэтому о составе копия и проверка не разойдутся. У старой копии набор шире взятого
    /// ею — в этом и смысл. Хранилище спрашивают только о путях, которых НЕТ В АРХИВЕ: у исправной
    /// копии вопросов к нему нет вовсе. Файл из архива, который положить не удалось, назван отчётом
    /// раньше, по имени. Файл, уцелевший на этой установке, отказом не считается.</para>
    ///
    /// <para>Слова — по хозяину пути (<see cref="BlobHolder" />): у части набора, сформированного PDF,
    /// скана и ассета свой выход, у прочих — адрес поля. Файлы модулей не наши: о них говорит модуль,
    /// считая пути из своих строк заново. Проверка идёт по манифесту, а не по базе, поэтому называет и
    /// файлы записей, которые восстановление пропустило (неизвестный тип и т.п.), — те названы раньше.</para>
    ///
    /// <para>⚠️ «Не найден» хранилище отвечает и тогда, когда до него не достучаться
    /// (<c>BlobStorage.GetSizeAsync</c>): отказ хранилища выглядит массовым «не найдены», а не отказом
    /// проверки. Поэтому вторая причина называется отдельной строкой.</para>
    ///
    /// <para>Отказ самой проверки восстановление не роняет: база уже зафиксирована.</para>
    /// </summary>
    private async Task WarnAboutMissingFilesAsync(
        BackupManifest manifest, HashSet<string> archived, List<string> warnings, CancellationToken ct)
    {
        try
        {
            // Предупреждения выбора не повторяются: их копия сказала паспортом при снятии.
            var selection = SelectBlobs(manifest, []);
            var missing = new List<(string Path, BlobHolder Holder)>();
            foreach (var (path, holder) in selection.Holders)
            {
                if (holder.Kind == BlobHolderKind.Module || archived.Contains(path)) continue;
                if (await blob.GetSizeAsync(path, ct) is null) missing.Add((path, holder));
            }

            var copyKnew = manifest.KnowsCoreFileScan == true;
            if (!copyKnew && selection.LeftInText.Count > 0)
                logger.LogWarning(
                    "Восстановление: копия снята версией без обхода манифеста, и ссылки на файлы внутри текста " +
                    "ею не названы. Поля: {Addresses}",
                    selection.LeftInText.Select(h => h.Address).Distinct().ToArray());

            if (missing.Count == 0) return;

            var byKind = missing.ToLookup(m => m.Holder.Kind);
            SayRecords(byKind[BlobHolderKind.Record].Select(m => m.Path).ToList(), selection, copyKnew, warnings);
            SayQualityScans(byKind[BlobHolderKind.QualityScan].Select(m => m.Path).ToHashSet(), manifest, warnings);
            SayCount(byKind[BlobHolderKind.TemplateAsset].Count(), warnings,
                "Ассеты шаблонов", "Документы по шаблонам, которые на них ссылаются, выйдут без них (шрифт, " +
                "графика) или не сформируются вовсе — загрузите ассеты заново в редакторе шаблонов.");
            SayCount(byKind[BlobHolderKind.Generated].Count(), warnings,
                "Сформированные PDF документов", "Сами документы вернулись — сформируйте их заново.");
            SayParts(byKind[BlobHolderKind.DataSetPart].Select(m => m.Holder).ToList(), selection, manifest, warnings);

            warnings.Add(
                "О ненайденных файлах: если хранилище во время восстановления было недоступно, повторите " +
                "восстановление — на недоступность оно отвечает так же, как на отсутствие файла.");

            logger.LogWarning(
                "Восстановление: файлов нет ни в архиве, ни в хранилище — {Count}. Пути: {Paths}",
                missing.Count, missing.Take(20).Select(m => m.Path).ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Не удалось проверить файлы ядра после восстановления");
            warnings.Add(
                "Данные восстановлены, но проверить, на месте ли файлы, на которые они ссылаются, не " +
                $"удалось: {ex.Message}. Откройте вложения у нескольких записей из копии.");
        }
    }

    /// <summary>Поля записей — по адресу, как в паспорте копии; причина — по признаку копии.</summary>
    private static void SayRecords(
        List<string> paths, BlobSelection selection, bool copyKnew, List<string> warnings)
    {
        if (paths.Count == 0) return;

        var tally = string.Join(", ", paths
            .GroupBy(p => selection.Addresses.GetValueOrDefault(p, "путь иной формы"))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key} ({g.Count()})"));
        warnings.Add(
            $"В копии нет файлов, на которые ссылаются записи, и в хранилище они не найдены: {paths.Count} " +
            $"({tally}). Записи вернулись и показывают файл приложенным, но открыть его будет нечем. " +
            (copyKnew
                ? "Этих файлов не было в хранилище исходной установки уже при снятии копии — об этом сказал " +
                  "её паспорт."
                : "Копия снята версией, которая брала в архив не все файлы: если исходная установка цела, " +
                  "обновите её, снимите копию заново и восстановите из новой."));
    }

    /// <summary>
    /// Сканы — по документам: скан и есть документ, и счёт идёт карточкам, а не файлам.
    /// </summary>
    private static void SayQualityScans(HashSet<string> paths, BackupManifest manifest, List<string> warnings)
    {
        if (paths.Count == 0) return;

        var documents = (manifest.QualityDocuments ?? [])
            .Count(q => q.ScanBlobPath is { Length: > 0 } scan && paths.Contains(scan));
        warnings.Add(
            $"Документы качества: у {RecordsGenitive(documents)} скана нет ни в копии, ни в хранилище. " +
            "Карточка документа откроется, но сам сертификат по ней не показать: скан нужно загрузить заново.");
    }

    private static void SayCount(int count, List<string> warnings, string what, string exit)
    {
        if (count > 0)
            warnings.Add($"{what}: {count} — нет ни в копии, ни в хранилище. {exit}");
    }

    /// <summary>
    /// Части PDF-наборов (issue #1399) — по имени набора и с выходом «Разбиение»: части вырезаются
    /// заново из файла набора, и это единственные файлы, которые установка умеет вернуть сама.
    /// </summary>
    private static void SayParts(
        List<BlobHolder> holders, BlobSelection selection, BackupManifest manifest, List<string> warnings)
    {
        if (holders.Count == 0) return;

        var totals = selection.Holders.Values
            .Where(h => h.Kind == BlobHolderKind.DataSetPart)
            .CountBy(h => h.DataSetFileId)
            .ToDictionary(p => p.Key, p => p.Value);
        var lacking = holders
            .CountBy(h => h.DataSetFileId)
            .Select(p => $"«{SetName(p.Key, manifest)}» — {p.Value} из {totals[p.Key]}");

        warnings.Add(
            "В копии нет вырезанных PDF документов, и в хранилище они не найдены: " +
            $"{string.Join("; ", lacking)}. Наборы вернулись, но документы с этими вложениями будут " +
            "печататься без них. Так выглядит копия, снятая прежней версией, которая части не брала, " +
            "или копия, при снятии которой их не оказалось в хранилище. Части вырезаются заново из " +
            "файла набора: откройте у набора «Разбиение» и сохраните его.");
    }

    /// <summary>Имя набора — у файла; реестр, чьего файла в копии нет, назван своим именем.</summary>
    private static string SetName(Guid? fileId, BackupManifest manifest) =>
        (manifest.DataSetFiles ?? []).FirstOrDefault(f => f.Id == fileId)?.Name
        ?? (manifest.DataSetSources ?? []).FirstOrDefault(s => s.FileId == fileId)?.Name
        ?? fileId.ToString()!;
}
