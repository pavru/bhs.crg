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
    /// <para>Ключ не включает корзину: «ключ совпал, а файл другой» возможно лишь в другой корзине, а
    /// она на установку одна. Путь, взятый обходом, но в хранилище отсутствующий, называет обычное
    /// «файлов не оказалось» — реестр блобов в сверке не участвует: пересечение с ним спрятало бы
    /// висячую ссылку.</para>
    ///
    /// <para>Порядок предупреждений — построение манифеста, сбор списка, обход, пропавшие файлы — один у
    /// копии и у оценки: паспорт — запись архива, и его текст обязан совпасть байт в байт. Журнал
    /// здесь не пишется: оценку зовёт каждое открытие раздела настроек; пишет его экспорт
    /// (<see cref="LogScanFindings" />).</para>
    /// </summary>
    private static BlobSelection SelectBlobs(BackupManifest manifest, List<string> warnings)
    {
        var paths = ExtractBlobPaths(manifest, warnings);
        var found = CoreFileScan.Scan(manifest, JsonOptions);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
            if (BlobPathShape.KeyOf(path) is { } key) keys.Add(key);

        var taken = new List<CoreFileHit>();
        foreach (var hit in found.Exact)
            if (keys.Add(hit.Hit.Keys[0]))
            {
                paths.Add(hit.Text);
                taken.Add(hit);
            }

        // Остаются только невзятые ключи: строка разбиения, где взяты все части, кроме одной, говорит
        // об одной.
        var left = found.InText
            .Select(h => h with { Hit = h.Hit with { Keys = [.. h.Hit.Keys.Where(k => !keys.Contains(k))] } })
            .Where(h => h.Hit.Keys.Count > 0)
            .ToList();

        if (taken.Count > 0)
            warnings.Add(
                $"Файлы найдены в полях, о которых копия не знает: {Tally(taken, h => 1)}. Файлы в копию " +
                "ВЗЯТЫ; сообщите разработчику — место хранения пути нужно вписать в копию.");
        if (left.Count > 0)
            warnings.Add(
                $"Ссылки на файлы записаны внутри текста: {Tally(left, h => h.Hit.Keys.Count)}. " +
                "Точный путь из такой записи не извлечь, и эти файлы в копию НЕ вошли. Сообщите разработчику.");

        return new BlobSelection(paths, taken, left);
    }

    /// <summary>
    /// Поля манифеста так, как их видит обход файлов копии, — для сторожей (issue #1422). Строится тем
    /// же контрактом сериализатора, которым манифест пишется в архив, поэтому незнакомый обходу тип
    /// поля отказывает здесь — в тесте, — а не в копии у заказчика.
    /// </summary>
    public static IReadOnlyList<CoreFileField> ManifestFields() => CoreFileScan.Fields(JsonOptions);

    /// <summary>«Адрес (число), …» — адреса уже упорядочены обходом.</summary>
    private static string Tally(IEnumerable<CoreFileHit> hits, Func<CoreFileHit, int> count) =>
        string.Join(", ", hits.GroupBy(h => h.Address).Select(g => $"{g.Key} ({g.Sum(count)})"));

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
