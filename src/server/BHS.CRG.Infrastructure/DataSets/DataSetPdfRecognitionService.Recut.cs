using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.DataSets;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// «Вырезать документы заново» — часть <see cref="DataSetPdfRecognitionService" /> (issue #1534).
///
/// <para>Части PDF-набора пропадают из хранилища без единой правки набора: восстановление из копии,
/// снятой до #1399 (части она не брала), или копия источника, не обновлённая при перерезке до #1149.
/// Выход, который называли копия и восстановление, — «откройте Разбиение и сохраните», — был
/// невыполним: «Сохранить» неактивна, пока разбиение не правили. А сохранение после фиктивной правки
/// не безвредно: ставит отметку ручной правки (полное перераспознавание начнёт спрашивать) и не
/// переносит распознанные таблицы групп.</para>
///
/// <para>Здесь режется ровно то разбиение, что лежит на наборе, как есть: те же группы, их id, тэги,
/// профили, распознанные таблицы и отметка ручной правки. Меняются только пути частей — им
/// пересобирают источники-проекции (все с маркером, и копии тоже, #1149), а прежние части уходят как
/// осиротевшие. Тот же путь, что у перераспознавания одного документа
/// (<see cref="MaterializeFileGroupingAsync" />), без самого распознавания.</para>
/// </summary>
public partial class DataSetPdfRecognitionService
{
    public async Task<GostGroupingDto?> RecutGroupingAsync(Guid fileId, CancellationToken ct)
    {
        var file = await db.DataSetFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file == null) return null;
        if (file.Format != DataSetFormat.Pdf)
            throw new InvalidRequestException("Вырезать документы можно только у PDF-набора.");
        // Ворота — первыми, как у сохранения разбиения (ревью PR #1252): иначе отказ пришёл бы после
        // того, как части разрезаны и загружены.
        RequireGost();

        var grouping = ParseGrouping(file.Grouping);
        if (grouping is null || !grouping.Groups.Any(IsCuttable))
            throw new InvalidRequestException("Вырезать нечего: набор не разбит на документы.");
        // «Файла нет» — свой отказ с выходом, а не сбой чтения: без файла резать не из чего, и это
        // единственное, что человеку тут нужно знать.
        if (await blob.GetSizeAsync(file.BlobPath, ct) is null)
            throw new ConflictException(
                "Файла набора нет в хранилище — вырезать документы не из чего. Загрузите файл набора заново.");

        await using var stream = await blob.DownloadAsync(file.BlobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        await MaterializeFileGroupingAsync(file, grouping, bytes, ct);

        var cut = ParseGrouping(file.Grouping)!;
        return new GostGroupingDto(
            cut.Groups.Select(g => new GostGroupingGroupDto(g.Kind, g.Code, g.Name, g.Pages.Select(p => p.PageIndex).ToList(), g.Tags, g.ProfileId,
                g.Pages.Where(p => p.NoAnswer).Select(p => p.PageIndex).ToList())).ToList(),
            cut.ManuallyEdited, GetPdfPageCount(bytes), await CountMissingPartsAsync(file.Id, cut, ct));
    }

    /// <summary>
    /// Сколько документов набора без файла: часть из разбиения или из строк реестра «Документы»,
    /// которой нет в хранилище, и документ, который не удалось вырезать вовсе. Пути — тем же чтением,
    /// что у резервной копии (<see cref="GostGroupingSerialization" />), поэтому редактор и паспорт
    /// копии считают одно и то же. Хранилище спрашивают одним вопросом (<c>FindAbsentAsync</c>).
    /// </summary>
    private async Task<int> CountMissingPartsAsync(Guid fileId, GostGroupingData? grouping, CancellationToken ct)
    {
        if (grouping is null) return 0;

        var unsplit = grouping.Groups.Count(g => IsCuttable(g) && string.IsNullOrEmpty(g.BlobPath));
        var paths = GostGroupingSerialization.PartPaths(grouping);
        var registries = await db.DataSetSources.AsNoTracking()
            .Where(s => s.FileId == fileId && s.SheetOrPath == PdfProfiles.GostDocumentsMarker)
            .Select(s => s.CachedData)
            .ToListAsync(ct);
        foreach (var rows in registries)
            if (GostGroupingSerialization.ReadRowFilePaths(rows) is { } fromRows)
                paths.UnionWith(fromRows);

        return unsplit + (paths.Count == 0 ? 0 : (await blob.FindAbsentAsync(paths, ct)).Count);
    }

    private static bool IsCuttable(GostGroupingGroup g) => g.Kind == GostGroupKind.Document && g.Pages.Count > 0;
}
