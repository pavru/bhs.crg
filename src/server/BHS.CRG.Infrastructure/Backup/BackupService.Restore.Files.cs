using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Storage;
using Microsoft.EntityFrameworkCore;
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
    /// раньше, по имени, — кроме скана: скан и есть документ, и о нём говорится словами документа.
    /// Файл, уцелевший на этой установке, отказом не считается.</para>
    ///
    /// <para>Слова — по хозяину пути (<see cref="BlobHolder" />): у файла и части набора, сформированного
    /// PDF, скана и ассета свой выход, у прочих — адрес поля. Файлы модулей не наши: о них говорит
    /// модуль, считая пути из своих строк заново. Проверка идёт по манифесту, поэтому называет и файлы
    /// записей, которые восстановление пропустило (неизвестный тип и т.п.), — те названы раньше. Сканы
    /// считаются по карточкам в базе: пропущенной карточки нет, и «откроется без скана» о ней неправда.</para>
    ///
    /// <para>«Не найден» — ответ хранилища «объекта нет». Недоступное хранилище с issue #1427 отвечает
    /// отказом (<c>BlobStorageUnavailableException</c>): проверка падает, и отчёт говорит «проверить не
    /// удалось», а не «не найдены». Прежде отказ выглядел массовым «не найдены», и вторую причину
    /// приходилось называть отдельной строкой при каждой находке.</para>
    ///
    /// <para>Ни отказ, ни отмена проверки восстановление не роняют: база уже зафиксирована, и отчёт
    /// говорит, что проверка не прошла, — молчание читалось бы как «файлы на месте».</para>
    /// </summary>
    /// <param name="archived">Всё, что лежит в архиве.</param>
    /// <param name="restored">Из архива положено в хранилище на самом деле.</param>
    private async Task WarnAboutMissingFilesAsync(
        BackupManifest manifest, HashSet<string> archived, HashSet<string> restored, List<string> warnings,
        CancellationToken ct)
    {
        try
        {
            // Предупреждения выбора не повторяются: их копия сказала паспортом при снятии.
            var selection = SelectBlobs(manifest, []);
            var asked = selection.Holders
                .Where(h => h.Value.Kind != BlobHolderKind.Module
                         && !(h.Value.Kind == BlobHolderKind.QualityScan ? restored : archived).Contains(h.Key))
                .ToList();
            // Все пути одним вопросом (issue #1439): по одному у копии, снятой при недоступном
            // хранилище, — тысячи вопросов и десятки секунд внутри запроса восстановления.
            var absent = await blob.FindAbsentAsync(asked.Select(h => h.Key), BlobOwner.Any, ct);
            var missing = asked.Where(h => absent.Contains(h.Key)).Select(h => (Path: h.Key, Holder: h.Value)).ToList();

            var copyKnew = manifest.KnowsCoreFileScan == true;
            if (!copyKnew && selection.LeftInText.Count > 0)
                logger.LogWarning(
                    "Восстановление: копия снята версией без обхода манифеста, и ссылки на файлы внутри текста " +
                    "ею не названы. Поля: {Addresses}",
                    selection.LeftInText.Select(h => h.Address).Distinct().ToArray());

            if (missing.Count == 0) return;

            var byKind = missing.ToLookup(m => m.Holder.Kind);
            SayRecords(byKind[BlobHolderKind.Record].Select(m => m.Path).ToList(), selection, copyKnew, warnings);
            await SayQualityScansAsync(byKind[BlobHolderKind.QualityScan].Select(m => m.Path).ToList(), warnings, ct);
            SayCount(byKind[BlobHolderKind.TemplateAsset].Count(), warnings,
                "Ассеты шаблонов", "Документы по шаблонам, которые на них ссылаются, выйдут без них (шрифт, " +
                "графика) или не сформируются вовсе — загрузите ассеты заново в редакторе шаблонов.");
            SayCount(byKind[BlobHolderKind.Generated].Count(), warnings,
                "Сформированные PDF документов", "Сами документы вернулись — сформируйте их заново.");
            var lostFiles = LostDataSetFiles(missing, manifest);
            SayDataSetFiles(lostFiles, manifest, warnings);
            SayParts(missing, lostFiles, selection, manifest, warnings);

            logger.LogWarning(
                "Восстановление: файлов нет ни в архиве, ни в хранилище — {Count}. Пути: {Paths}",
                missing.Count, missing.Take(20).Select(m => m.Path).ToArray());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Отмена после фиксации — не отказ восстановления: база уже восстановлена, и журнал обязан
            // это сохранить, даже если отчёт отменённому запросу не дойдёт (ревью PR #1440).
            logger.LogWarning("Восстановление: база восстановлена, проверка файлов прервана отменой запроса");
            warnings.Add(
                "Данные восстановлены, но проверка файлов прервана: запрос отменён. Откройте вложения у " +
                "нескольких записей из копии.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Не удалось проверить файлы ядра после восстановления");
            warnings.Add(
                "Данные восстановлены, но проверить, на месте ли файлы, на которые они ссылаются, не " +
                $"удалось: {CheckFailure(ex)}. Откройте вложения у нескольких записей из копии.");
        }
    }

    /// <summary>
    /// Почему не прошла проверка файлов — словами для отчёта. Отказ хранилища (issue #1427) называется
    /// своими словами с выходом: его текст — служебный, с путём объекта, и человеку ничего не говорит.
    /// </summary>
    private static string CheckFailure(Exception ex) => ex is BlobStorageUnavailableException
        ? "хранилище файлов отказало в ответе (причина — в журнале сервера); когда оно будет доступно, " +
          "повторите восстановление"
        : ex.Message;

    /// <summary>
    /// Поля записей — по адресу, как в паспорте копии. Причина — по пути: «версия не брала» верно
    /// только для пути, который нашёл один обход (<see cref="BlobSelection.TakenByScan" />), и только у
    /// копии без признака обхода. Путь из списка мест брала и прежняя версия: его не было в хранилище
    /// при снятии, и новая копия с той же установки его не вернёт (ревью PR #1440).
    /// </summary>
    private static void SayRecords(
        List<string> paths, BlobSelection selection, bool copyKnew, List<string> warnings)
    {
        if (paths.Count == 0) return;

        var tally = PlaceTally(paths, selection);
        var notTaken = copyKnew
            ? 0
            : paths.Intersect(selection.TakenByScan.Select(h => h.Text), StringComparer.Ordinal).Count();

        warnings.Add(
            $"В копии нет файлов, на которые ссылаются записи, и в хранилище они не найдены: {paths.Count} " +
            $"({tally}). Записи вернулись и показывают файл приложенным, но открыть его будет нечем." +
            (notTaken > 0
                ? $" {notTaken} из них лежат в полях, которые версия, снявшая копию, не обходила: если " +
                  "исходная установка цела, обновите её, снимите копию заново и восстановите из новой."
                : "") +
            (notTaken < paths.Count
                ? (notTaken > 0 ? " Остальных" : " Этих файлов") + " не было в хранилище исходной установки " +
                  "уже при снятии копии — об этом сказал её паспорт."
                : ""));
    }

    /// <summary>
    /// Сканы — по карточкам в базе: скан и есть документ, и счёт идёт карточкам, а не файлам. Карточки,
    /// которую восстановление пропустило, в базе нет — о ней сказано раньше, и «откроется без скана» о
    /// ней было бы неправдой.
    /// </summary>
    private async Task SayQualityScansAsync(List<string> paths, List<string> warnings, CancellationToken ct)
    {
        if (paths.Count == 0) return;

        var documents = await db.QualityDocuments.CountAsync(
            d => d.ScanBlobPath != null && paths.Contains(d.ScanBlobPath), ct);
        if (documents == 0) return;

        warnings.Add(
            $"Документы качества: у {RecordsGenitive(documents)} скан не восстановлен, и в хранилище его нет — " +
            "в копии его не было или его не удалось записать. Карточка документа откроется, но сам " +
            "сертификат по ней не показать: скан нужно загрузить заново.");
    }

    private static void SayCount(int count, List<string> warnings, string what, string exit)
    {
        if (count > 0)
            warnings.Add($"{what}: {count} — нет ни в копии, ни в хранилище. {exit}");
    }

    /// <summary>
    /// Файлы наборов — по имени: без файла набор не перечитать и не разрезать заново. Наборы — по пути
    /// файла (<see cref="LostDataSetFiles" />): на одном файле их бывает несколько.
    /// </summary>
    private static void SayDataSetFiles(List<BackupDataSetFile> lost, BackupManifest manifest, List<string> warnings)
    {
        if (lost.Count == 0) return;

        warnings.Add(
            "Файлов наборов данных нет ни в копии, ни в хранилище: " +
            $"{SetNames(lost, manifest)}. Наборы вернулись с " +
            "разобранным содержимым, но перечитать или разбить их заново не из чего — загрузите файл набора " +
            "заново.");
    }

    /// <summary>
    /// Части PDF-наборов (issue #1399) — по имени набора; раскладка и выходы — общие с паспортом копии
    /// (<see cref="MissingPartsBySet" />).
    /// </summary>
    private static void SayParts(
        IReadOnlyCollection<(string Path, BlobHolder Holder)> missing, List<BackupDataSetFile> lostFiles,
        BlobSelection selection, BackupManifest manifest, List<string> warnings)
    {
        var (cuttable, stuck) = MissingPartsBySet(missing, lostFiles, selection, manifest);

        if (cuttable.Count > 0)
            warnings.Add(
                "В копии нет вырезанных PDF документов, и в хранилище они не найдены: " +
                $"{string.Join("; ", cuttable)}. Наборы вернулись, но документы с этими " +
                "вложениями будут печататься без них. Так выглядит копия, снятая прежней версией, которая " +
                "части не брала, или копия, при снятии которой их не оказалось в хранилище. " + RecutExit);
        if (stuck.Count > 0)
            warnings.Add(
                "В копии нет вырезанных PDF документов, и вырезать их заново не из чего — файла набора нет " +
                $"тоже: {string.Join("; ", stuck)}. Документы с этими вложениями будут " +
                "печататься без них. " + ReturnFileExit);
    }
}
