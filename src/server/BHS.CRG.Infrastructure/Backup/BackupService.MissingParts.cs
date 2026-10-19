using BHS.CRG.Application.Backup;

namespace BHS.CRG.Infrastructure.Backup;

// Пропавшие файлы — по хозяину, одними словами у копии, оценки и восстановления.
public partial class BackupService
{
    /// <summary>Выход для частей, которые есть из чего вырезать.</summary>
    private const string RecutExit =
        "Части вырезаются заново из файла набора: откройте у набора «Разбиение» и сохраните его.";

    /// <summary>Выход для частей, у набора которых нет и файла.</summary>
    private const string ReturnFileExit =
        "Верните файл набора (загрузите заново), затем откройте у набора «Разбиение» и сохраните его.";

    /// <summary>
    /// Чего не оказалось в хранилище при снятии копии — словами паспорта. Одна фраза на экспорт и на
    /// оценку: паспорт оценки обязан весить столько же (<see cref="MeasureAsync" />, issue #1425).
    ///
    /// <para>Прежде фраза была одна на всё: «файлов не оказалось: N из M». Отказ выглядел отказом, но
    /// не говорил, что пропало и что делать, — хотя у частей PDF-наборов выход есть, и восстановление
    /// той же копии его называло (<see cref="SayParts" />). Теперь части названы по набору с тем же
    /// выходом, что у восстановления, а остальное — общей фразой, как прежде.</para>
    /// </summary>
    /// <param name="missing">Пути выбора, которых в хранилище нет.</param>
    private static void WarnAboutMissingBlobs(
        BlobSelection selection, IReadOnlySet<string> missing, BackupManifest manifest, List<string> warnings)
    {
        if (missing.Count == 0) return;

        // Порядок — выбора, а не множества пропавших: у копии и у оценки он обязан совпасть.
        var located = selection.Holders
            .Where(h => missing.Contains(h.Key))
            .Select(h => (Path: h.Key, Holder: h.Value))
            .ToList();
        var (cuttable, stuck) = MissingPartsBySet(located, selection, manifest);

        if (cuttable.Count > 0)
            warnings.Add(
                $"Вырезанных PDF документов не оказалось в хранилище: {string.Join("; ", cuttable)}. В копию " +
                "они не попали, и документы с этими вложениями будут печататься без них — и здесь, и после " +
                $"восстановления. {RecutExit} После этого снимите копию заново: в эту они уже не попали.");
        if (stuck.Count > 0)
            warnings.Add(
                "Вырезанных PDF документов не оказалось в хранилище, и вырезать их заново не из чего — файла " +
                $"набора нет тоже: {string.Join("; ", stuck)}. В копию они не попали, и документы с этими " +
                $"вложениями будут печататься без них. {ReturnFileExit} После этого снимите копию заново.");

        var others = located.Count(m => m.Holder.Kind != BlobHolderKind.DataSetPart);
        if (others > 0)
            warnings.Add(
                $"{(cuttable.Count + stuck.Count > 0 ? "Других файлов" : "Файлов")} не оказалось в хранилище: " +
                $"{others} из {selection.Paths.Count} — в копию они не попали, и после восстановления ссылки " +
                "на них останутся битыми.");
    }

    /// <summary>
    /// Пропавшие части PDF-наборов (issue #1399) — по набору: «имя — сколько из скольких». Есть ли
    /// выход «Разбиение», решает файл набора: части вырезаются из него, и без него сохранение разбиения
    /// ничего не вернёт (ревью PR #1440). Резать есть из чего, только если файл набора в копии и не
    /// пропал сам.
    /// </summary>
    /// <param name="missing">Всё пропавшее, с хозяевами: файлы наборов среди них решают выход.</param>
    /// <returns>Наборы по имени — те, что можно разрезать заново, и те, что нельзя.</returns>
    private static (List<string> Cuttable, List<string> Stuck) MissingPartsBySet(
        IReadOnlyCollection<(string Path, BlobHolder Holder)> missing, BlobSelection selection,
        BackupManifest manifest)
    {
        var lostSets = missing
            .Where(m => m.Holder.Kind == BlobHolderKind.DataSetFile)
            .Select(m => m.Holder.DataSetFileId)
            .ToHashSet();
        var totals = selection.Holders.Values
            .Where(h => h.Kind == BlobHolderKind.DataSetPart)
            .CountBy(h => h.DataSetFileId)
            .ToDictionary(p => p.Key, p => p.Value);
        var sets = missing
            .Where(m => m.Holder.Kind == BlobHolderKind.DataSetPart)
            .CountBy(m => m.Holder.DataSetFileId)
            .Select(p => (Id: p.Key, Text: $"«{SetName(p.Key, manifest)}» — {p.Value} из {totals[p.Key]}"))
            .OrderBy(s => s.Text, StringComparer.Ordinal)
            .ToList();

        var cuttable = sets
            .Where(s => !lostSets.Contains(s.Id) && (manifest.DataSetFiles ?? []).Any(f => f.Id == s.Id))
            .ToList();
        return (
            cuttable.Select(s => s.Text).ToList(),
            sets.Except(cuttable).Select(s => s.Text).ToList());
    }

    /// <summary>Имя набора — у файла; реестр, чьего файла в копии нет, назван своим именем.</summary>
    private static string SetName(Guid? fileId, BackupManifest manifest) =>
        (manifest.DataSetFiles ?? []).FirstOrDefault(f => f.Id == fileId)?.Name
        ?? (manifest.DataSetSources ?? []).FirstOrDefault(s => s.FileId == fileId)?.Name
        ?? fileId.ToString()!;
}
