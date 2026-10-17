using BHS.CRG.Application.Backup;
using BHS.CRG.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Backup;

// Какие файлы уезжают с манифестом (issue #1422): список мест ∪ обход манифеста по форме пути.
public partial class BackupService
{
    /// <summary>Файлы копии и то, что о них сказал обход манифеста.</summary>
    /// <param name="Paths">Все пути, которые уезжают в архив.</param>
    /// <param name="TakenByScan">Пути, которые нашёл только обход: список мест о них не знает.</param>
    /// <param name="LeftInText">Строки, где ссылка на файл записана внутри текста, а файла в копии нет;
    /// ключи у каждой — только невзятые.</param>
    private sealed record BlobSelection(
        HashSet<string> Paths, IReadOnlyList<CoreFileHit> TakenByScan, IReadOnlyList<CoreFileHit> LeftInText);

    /// <summary>
    /// Все файлы, которые уезжают с этим манифестом, — одно место для архива, оценки веса и паспорта
    /// (issue #1422).
    ///
    /// <para>Сначала список мест (<see cref="ExtractBlobPaths" />), затем обход манифеста
    /// (<see cref="CoreFileScan" />). Правило одно: <b>файл в копии — значит его ключ взят</b> (дата и
    /// идентификатор пути, <see cref="BlobPathShape.KeyOf" />). Строка точной формы, чей ключ не взят,
    /// уезжает в архив — с предупреждением: список о её поле не знает, а копия без файла обнаружилась
    /// бы только после аварии. Строка с уже взятым ключом — тот же файл (иначе записанное имя), второго
    /// «пути» к нему не будет. Ключ внутри текста, не взятый, — предупреждение без взятия: где в тексте
    /// кончается имя файла, не понять. Поэтому разбиение набора и строки реестра «Документы» молчат
    /// сами: их ключи взял список, и исключений для них не нужно.</para>
    ///
    /// <para>«Ключ взят» для точной строки значит «взят ТОЧНЫМ путём». Путь списка иной формы — без
    /// корзины, с адресом сервера — тоже уходит в архив, но скачать по нему, скорее всего, нечего; точный
    /// путь к тому же файлу из другого поля поэтому берётся рядом, а не отбрасывается (ревью PR #1423).
    /// Для ссылок внутри текста годится любой путь списка: о файле, который список пытался взять, копия
    /// уже скажет сама.</para>
    ///
    /// <para>Ключ не включает корзину: «ключ совпал, а файл другой» возможно лишь в другой корзине, а
    /// она на установку одна. Реестр блобов в сверке не участвует: пересечение с ним спрятало бы
    /// висячую ссылку.</para>
    ///
    /// <para>Предупреждения обхода здесь НЕ пишутся: «взяты» можно сказать только после того, как стало
    /// известно, что файл в хранилище есть (<see cref="WarnAboutScan" />). Журнал не пишется тоже: оценку
    /// зовёт каждое открытие раздела настроек; пишет его экспорт (<see cref="LogScanFindings" />).</para>
    /// </summary>
    /// <param name="warnings">Куда сказать о данных, пути из которых список прочесть не смог.</param>
    private static BlobSelection SelectBlobs(BackupManifest manifest, List<string> warnings)
    {
        var paths = ExtractBlobPaths(manifest, warnings);
        var found = CoreFileScan.Scan(manifest, JsonOptions);

        var exactKeys = new HashSet<string>(StringComparer.Ordinal);
        var anyKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
            if (BlobPathShape.Inspect(path) is { } listed)
            {
                anyKeys.UnionWith(listed.Keys);
                if (listed.Exact) exactKeys.Add(listed.Keys[0]);
            }

        var taken = new List<CoreFileHit>();
        foreach (var hit in found.Exact)
            if (exactKeys.Add(hit.Hit.Keys[0]))
            {
                paths.Add(hit.Text);
                anyKeys.Add(hit.Hit.Keys[0]);
                taken.Add(hit);
            }

        // Остаются только невзятые ключи, каждый по разу: строка разбиения, где взяты все части, кроме
        // одной, говорит об одной; путь, повторённый в строке дважды, — об одном файле.
        var left = found.InText
            .Select(h => h with { Hit = h.Hit with { Keys = [.. h.Hit.Keys.Where(k => !anyKeys.Contains(k)).Distinct()] } })
            .Where(h => h.Hit.Keys.Count > 0)
            .ToList();

        return new BlobSelection(paths, taken, left);
    }

    /// <summary>
    /// Предупреждения обхода — когда скачивание (у копии) или опрос размеров (у оценки) уже сказали,
    /// каких файлов нет в хранилище. Копия и оценка зовут это в одном месте — между файлами и фразой о
    /// пропавших, — и паспорт у них совпадает байт в байт.
    ///
    /// <para>«Взяты» — только о тех, что в архиве на самом деле. Прежде фраза писалась до скачивания и
    /// обещала полную копию и тогда, когда найденного файла в хранилище не было (ревью PR #1423);
    /// руководство администратора велит такой фразе верить.</para>
    /// </summary>
    private static void WarnAboutScan(BlobSelection selection, IReadOnlySet<string> missing, List<string> warnings)
    {
        if (selection.TakenByScan is { Count: > 0 } taken)
        {
            var absent = taken.Count(h => missing.Contains(h.Text));
            warnings.Add(
                $"Файлы найдены в полях, о которых копия не знает: {Tally(taken, h => [h.Text])}. " +
                (absent == 0
                    ? "Файлы в копию ВЗЯТЫ"
                    : $"В копию ВЗЯТЫ {taken.Count - absent} из {taken.Count}; остальных нет в хранилище — " +
                      "они в числе не оказавшихся там") +
                "; сообщите разработчику — место хранения пути нужно вписать в копию.");
        }

        if (selection.LeftInText is { Count: > 0 } left)
            warnings.Add(
                $"Ссылки на файлы записаны внутри текста: {Tally(left, h => h.Hit.Keys)}. Точный путь из " +
                $"такой записи не извлечь, и эти файлы ({Distinct(left)}) в копию НЕ вошли. Сообщите разработчику.");

        static int Distinct(IEnumerable<CoreFileHit> hits) => hits.SelectMany(h => h.Hit.Keys).Distinct().Count();
    }

    /// <summary>
    /// Поля манифеста так, как их видит обход файлов копии, — для сторожей (issue #1422). Строится тем
    /// же контрактом сериализатора, которым манифест пишется в архив, поэтому незнакомый обходу тип
    /// поля отказывает здесь — в тесте, — а не в копии у заказчика.
    /// </summary>
    public static IReadOnlyList<CoreFileField> ManifestFields() => CoreFileScan.Fields(JsonOptions);

    /// <summary>
    /// «Адрес (число), …» — адреса уже упорядочены обходом. Считаются разные файлы поля, а не строки:
    /// путь, найденный в поле дважды, — один файл (ревью PR #1423).
    /// </summary>
    private static string Tally(IEnumerable<CoreFileHit> hits, Func<CoreFileHit, IEnumerable<string>> files) =>
        string.Join(", ", hits.GroupBy(h => h.Address)
            .Select(g => $"{g.Key} ({g.SelectMany(files).Distinct(StringComparer.Ordinal).Count()})"));

    /// <summary>
    /// Журнал к предупреждениям обхода — только у экспорта: паспорт говорит «где и сколько», журнал —
    /// какие записи и какие пути, чтобы разработчику было с чего начать.
    /// </summary>
    private void LogScanFindings(BlobSelection selection)
    {
        const int shown = 20;
        foreach (var group in selection.TakenByScan.GroupBy(h => h.Address))
            logger.LogWarning(
                "Копия: файлы найдены обходом манифеста в поле {Address}, о котором копия не знает, и взяты: " +
                "{Count}. Записи: {Records}. Пути: {Paths}",
                group.Key, group.Count(), Records(group), group.Take(shown).Select(h => h.Text).ToArray());

        foreach (var group in selection.LeftInText.GroupBy(h => h.Address))
            logger.LogWarning(
                "Копия: ссылки на файлы внутри текста в поле {Address} в копию не вошли. Записи: {Records}. " +
                "Ключи: {Keys}",
                group.Key, Records(group), group.SelectMany(h => h.Hit.Keys).Distinct().Take(shown).ToArray());

        static Guid[] Records(IEnumerable<CoreFileHit> hits) =>
            [.. hits.Select(h => h.RecordId).OfType<Guid>().Distinct().Take(shown)];
    }
}
