using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Storage;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Backup;

// Пропавшие файлы — по хозяину, одними словами у копии, оценки и восстановления (issue #1534).
public partial class BackupService
{
    /// <summary>
    /// Выход для частей, которые есть из чего вырезать. Действие — то, что редактор разбиения
    /// показывает, когда у набора есть документы без файла (issue #1534); прежде здесь стояло
    /// «сохраните разбиение», а «Сохранить» неактивна, пока разбиение не правили.
    /// </summary>
    private const string RecutExit =
        "Части вырезаются заново из файла набора: откройте у набора «Разбиение» и нажмите «Вырезать " +
        "документы заново» — разбиение и распознанные таблицы не изменятся.";

    /// <summary>Выход для частей, у набора которых нет и файла.</summary>
    private const string ReturnFileExit =
        "Верните файл набора (загрузите заново), затем откройте у набора «Разбиение» и нажмите " +
        "«Вырезать документы заново».";

    /// <summary>
    /// Чего не оказалось в хранилище при снятии копии — словами паспорта. Одна фраза на экспорт и на
    /// оценку: паспорт оценки обязан весить столько же (<see cref="MeasureAsync" />, issue #1425).
    ///
    /// <para>Прежде фраза была одна на всё: «файлов не оказалось: N из M». Отказ выглядел отказом, но
    /// не говорил, что пропало и что делать, — хотя у частей PDF-наборов выход есть, и восстановление
    /// той же копии его называло (<see cref="SayParts" />). Теперь части названы по набору с тем же
    /// выходом, что у восстановления, файлы наборов — по имени, остальное — счётом по месту в
    /// манифесте, как у полей записей при восстановлении (<see cref="PlaceTally" />).</para>
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
        var lostFiles = LostDataSetFiles(located, manifest);
        var (cuttable, stuck) = MissingPartsBySet(located, lostFiles, selection, manifest);

        if (cuttable.Count > 0)
            warnings.Add(
                $"Вырезанных PDF документов не оказалось в хранилище: {string.Join("; ", cuttable)}. В копию " +
                "они не попали, и документы с этими вложениями будут печататься без них — и здесь, и после " +
                $"восстановления. {RecutExit} После этого снимите копию заново.");
        if (stuck.Count > 0)
            warnings.Add(
                "Вырезанных PDF документов не оказалось в хранилище, и вырезать их заново не из чего — файла " +
                $"набора нет тоже: {string.Join("; ", stuck)}. В копию они не попали, и документы с этими " +
                $"вложениями будут печататься без них. {ReturnFileExit} После этого снимите копию заново.");
        if (lostFiles.Count > 0)
            warnings.Add(
                $"Файлов наборов данных не оказалось в хранилище: {SetNames(lostFiles, manifest)}. В копию они " +
                "не попали: наборы вернутся с разобранным содержимым, но перечитать или разбить их заново будет " +
                "не из чего. Загрузите файл набора заново и снимите копию заново.");

        var lostFilePaths = lostFiles.Select(f => f.BlobPath).ToHashSet(StringComparer.Ordinal);
        var rest = located
            .Where(m => m.Holder.Kind != BlobHolderKind.DataSetPart && !lostFilePaths.Contains(m.Path))
            .Select(m => m.Path)
            .ToList();
        if (rest.Count == 0) return;

        // «N из M» — только когда фраза одна: рядом с названными частями «из M» считало бы и их.
        var named = cuttable.Count + stuck.Count + lostFiles.Count > 0;
        warnings.Add(
            (named
                ? $"Других файлов не оказалось в хранилище: {rest.Count}"
                : $"Файлов не оказалось в хранилище: {rest.Count} из {selection.Paths.Count}") +
            $" ({PlaceTally(rest, selection)}) — в копию они не попали, и после восстановления ссылки на них " +
            "останутся битыми.");
    }

    /// <summary>
    /// Файл не отдался при снятии копии — его нет, или хранилище отказало? (issue #1534)
    ///
    /// <para>Выдача файла «нет» от отказа не отличает: клиент бросает что попало, а подделка и реестр —
    /// свои исключения. Различает это вопрос о размере (<see cref="IBlobStorage.GetSizeAsync" />):
    /// <c>null</c> — ответ «объекта нет», и файл честно пропал. Отказ в размере или размер есть, а файл
    /// не отдался, — это хранилище, а не пропажа: копия отказывает, как отказывает оценка (#1427).
    /// Прежде такой файл ложился в паспорт пропавшим, а с названными выходами паспорт советовал бы
    /// вырезать заново части, которые лежат на месте.</para>
    /// </summary>
    /// <param name="failure">Чем отказала выдача — причина, которую увидит журнал.</param>
    private async Task EnsureAbsentAsync(string path, Exception failure, CancellationToken ct)
    {
        long? size;
        try { size = await blob.GetSizeAsync(path, BlobOwner.Any, ct); }
        catch (BlobStorageUnavailableException refused)
        {
            logger.LogWarning(refused, "Копия: хранилище отказало в выдаче файла {BlobPath}", path);
            throw new BackupStorageRefusedException(refused);
        }
        if (size is null) return;

        logger.LogWarning(failure, "Копия: файл {BlobPath} есть в хранилище, но не отдался", path);
        throw new BackupStorageRefusedException(failure);
    }

    /// <summary>
    /// Файлы наборов, которых нет, — по ПУТИ набора, а не по хозяину пути: хозяин у пути один («кто
    /// первым назвал»), а наборов на одном файле бывает несколько, и пропажа файла касается всех.
    /// </summary>
    /// <param name="missing">Всё пропавшее; хозяева здесь не нужны, нужны пути.</param>
    private static List<BackupDataSetFile> LostDataSetFiles(
        IReadOnlyCollection<(string Path, BlobHolder Holder)> missing, BackupManifest manifest)
    {
        var paths = missing.Select(m => m.Path).ToHashSet(StringComparer.Ordinal);
        return (manifest.DataSetFiles ?? [])
            .Where(f => f.Format != "System" && !string.IsNullOrEmpty(f.BlobPath) && paths.Contains(f.BlobPath))
            .ToList();
    }

    /// <summary>
    /// Пропавшие части PDF-наборов (issue #1399) — по набору: «имя — сколько из скольких». Есть ли
    /// выход «Вырезать документы заново», решает файл набора: части вырезаются из него, и без него
    /// действие ничего не вернёт (ревью PR #1440). Резать есть из чего, только если файл набора в копии и не
    /// пропал сам.
    /// </summary>
    /// <param name="missing">Всё пропавшее, с хозяевами.</param>
    /// <param name="lostFiles">Пропавшие файлы наборов — <see cref="LostDataSetFiles" />.</param>
    /// <returns>Наборы по имени — те, что можно разрезать заново, и те, что нельзя.</returns>
    private static (List<string> Cuttable, List<string> Stuck) MissingPartsBySet(
        IReadOnlyCollection<(string Path, BlobHolder Holder)> missing, IReadOnlyCollection<BackupDataSetFile> lostFiles,
        BlobSelection selection, BackupManifest manifest)
    {
        var parts = missing.Where(m => m.Holder.Kind == BlobHolderKind.DataSetPart).ToList();
        if (parts.Count == 0) return ([], []);

        var totals = selection.Holders.Values
            .Where(h => h.Kind == BlobHolderKind.DataSetPart)
            .CountBy(h => h.DataSetFileId.GetValueOrDefault())
            .ToDictionary(p => p.Key, p => p.Value);
        var sets = parts
            .CountBy(m => m.Holder.DataSetFileId.GetValueOrDefault())
            .Select(p => (Id: p.Key, Text: $"«{SetName(p.Key, manifest)}» — {p.Value} из {totals[p.Key]}"))
            .OrderBy(s => s.Text, StringComparer.Ordinal)
            .ToList();

        var lost = lostFiles.Select(f => f.Id).ToHashSet();
        var cuttable = sets
            .Where(s => !lost.Contains(s.Id) && (manifest.DataSetFiles ?? []).Any(f => f.Id == s.Id))
            .ToList();
        return (
            cuttable.Select(s => s.Text).ToList(),
            sets.Except(cuttable).Select(s => s.Text).ToList());
    }

    /// <summary>Пути — счётом по полю манифеста, где их встретил обход: «адрес (n)».</summary>
    private static string PlaceTally(IEnumerable<string> paths, BlobSelection selection) =>
        string.Join(", ", paths
            .GroupBy(p => selection.Addresses.GetValueOrDefault(p, "путь иной формы"))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key} ({g.Count()})"));

    /// <summary>Имена наборов через запятую, по алфавиту.</summary>
    private static string SetNames(IEnumerable<BackupDataSetFile> files, BackupManifest manifest) =>
        string.Join(", ", files
            .Select(f => $"«{SetName(f.Id, manifest)}»")
            .OrderBy(n => n, StringComparer.Ordinal));

    /// <summary>Имя набора — у файла; реестр, чьего файла в копии нет, назван своим именем.</summary>
    private static string SetName(Guid fileId, BackupManifest manifest) =>
        (manifest.DataSetFiles ?? []).FirstOrDefault(f => f.Id == fileId)?.Name
        ?? (manifest.DataSetSources ?? []).FirstOrDefault(s => s.FileId == fileId)?.Name
        ?? fileId.ToString();
}
