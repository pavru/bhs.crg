using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Infrastructure.Recognition;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Редактор разбиения на уровне набора — часть <see cref="DataSetPdfRecognitionService" />.
///
/// <para>Всё, что отвечает на вопросы «какие тут страницы» и «чем их читать»: страницы и эскизы,
/// функциональные тэги документа, привязка профилей распознавания к набору и к отдельной группе
/// листов, сборка DTO разбиения.</para>
///
/// <para>Здесь же ЕДИНСТВЕННОЕ место, решающее «таблична ли группа и по каким колонкам её читать»
/// (issue #410). Раньше этот предикат был размазан по пяти точкам, и одна из них на его основании
/// удаляла источники — поэтому он сведён в одно место нарочно, и разводить его обратно нельзя.</para>
/// </summary>
public partial class DataSetPdfRecognitionService
{
    // ── Редактор разбиения — на уровне НАБОРА (issue #38, fileId) ──────────────────

    public async Task<GostGroupingDto?> GetPagesAsync(Guid fileId, CancellationToken ct)
    {
        var file = await db.DataSetFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file == null) return null;
        if (file.Format != DataSetFormat.Pdf)
            throw new InvalidRequestException("Разбиение доступно только для PDF-набора.");

        var pageCount = await GetPdfPageCountAsync(file.BlobPath, ct);
        var grouping = ParseGrouping(file.Grouping);
        var groups = (grouping?.Groups ?? [])
            .Select(g => new GostGroupingGroupDto(g.Kind, g.Code, g.Name, g.Pages.Select(p => p.PageIndex).ToList(), g.Tags, g.ProfileId,
                g.Pages.Where(p => p.NoAnswer).Select(p => p.PageIndex).ToList()))
            .ToList();
        return new GostGroupingDto(groups, grouping?.ManuallyEdited ?? false, pageCount);
    }

    public async Task<byte[]?> GetPageThumbnailAsync(Guid fileId, int pageIndex, CancellationToken ct, int dpi = 96)
    {
        var file = await db.DataSetFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file == null) return null;
        if (file.Format != DataSetFormat.Pdf)
            throw new InvalidRequestException("Миниатюры доступны только для PDF-набора.");

        await using var stream = await blob.DownloadAsync(file.BlobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        try
        {
            return await Task.Run(() => PdfRasterizer.ToPngPage(bytes, pageIndex, dpi), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Сообщение растеризатора — в inner: оно чужое, а тип отказа наш (issue #1050).
            throw new InvalidRequestException(
                $"Не удалось отрисовать страницу {pageIndex + 1} — файл PDF повреждён или защищён.", ex);
        }
    }

    /// <summary>Лёгкая установка функциональных тэгов документа (тип таблицы) в единой группировке —
    /// без пересборки/разрезания PDF и без сброса ManuallyEdited (в отличие от ApplyGroupingAsync).
    /// firstPageIndex — любая страница документа.</summary>
    public async Task<GostGroupingDto?> SetDocumentTagsAsync(Guid fileId, int firstPageIndex, IReadOnlyList<string> tags, CancellationToken ct)
    {
        var file = await db.DataSetFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file == null) return null;
        if (file.Format != DataSetFormat.Pdf)
            throw new InvalidRequestException("Тэги документа доступны только для PDF-набора.");

        var grouping = ParseGrouping(file.Grouping);
        if (grouping is null)
            throw new InvalidRequestException("Группировка ещё не распознана.");
        // Оставляем только известные тэги типа таблицы (не даём проставить произвольные).
        var clean = tags.Where(profiles.IsTableTag).Distinct().ToList();
        var updated = grouping.Groups
            .Select(g => g.Kind == GostGroupKind.Document && g.Pages.Any(p => p.PageIndex == firstPageIndex)
                ? g with { Tags = clean.Count > 0 ? clean : null }
                : g)
            .ToList();
        file.SetGrouping(JsonSerializer.Serialize(new GostGroupingData(updated, grouping.ManuallyEdited)));
        await db.SaveChangesAsync(ct);

        return await ToGroupingDtoAsync(file, updated, grouping.ManuallyEdited, ct);
    }

    /// <summary>Привязка профилей распознавания к НАБОРУ (issue #412): карта {вид: id профиля}.
    /// Значение null снимает привязку конкретного вида. Распознавание не перезапускает — параметры
    /// применятся при следующем запуске (он дорогой, решает пользователь).</summary>
    public async Task<bool> SetFileRecognitionProfilesAsync(
        Guid fileId, IReadOnlyDictionary<string, Guid?> map, CancellationToken ct)
    {
        var file = await db.DataSetFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file == null) return false;

        var current = ParseFileProfileMap(file.RecognitionProfiles);
        // Виды, у которых привязка ДЕЙСТВИТЕЛЬНО изменилась: повторная установка того же профиля
        // ничего не обесценивает, и помечать по ней — то же, что горящая всегда лампа.
        var changedKinds = new List<string>();
        foreach (var (kindName, profileId) in map)
        {
            if (!Enum.TryParse<RecognitionProfileKind>(kindName, out var kind))
                throw new InvalidRequestException($"Неизвестный вид профиля «{kindName}».");
            if (RecognitionKinds.IsGroupScoped(kind))
                throw new InvalidRequestException(
                    $"Профиль вида «{RecognitionKinds.Describe(kind).Label}» привязывается к группе листов, а не к набору.");

            if (profileId is null)
            {
                if (current.Remove(kindName)) changedKinds.Add(kindName);
                continue;
            }

            var profile = await profiles.GetByIdAsync(profileId.Value, ct)
                ?? throw new InvalidRequestException("Профиль распознавания не найден.");
            if (profile.Kind != kind)
                throw new InvalidRequestException(
                    $"Профиль «{profile.Name}» имеет вид «{RecognitionKinds.Describe(profile.Kind).Label}» — он не подходит для «{RecognitionKinds.Describe(kind).Label}».");
            if (current.GetValueOrDefault(kindName) != profileId.Value) changedKinds.Add(kindName);
            current[kindName] = profileId.Value;
        }

        file.SetRecognitionProfiles(current.Count > 0 ? JsonSerializer.Serialize(current) : null);

        // Данные, прочитанные ПРЕЖНИМИ параметрами, устарели — помечаем источники затронутых видов
        // (issue #815). Групповой аналог (SetDocumentProfileAsync) делал это с самого начала, а
        // файловый молчал: штамп, обложка и счёт оставались с прежними значениями, и ни один экран
        // не говорил, что читали их по другим правилам. Распознавание не перезапускаем — оно дорогое,
        // решает пользователь; наше дело сказать, что данные разошлись с настройкой.
        if (changedKinds.Count > 0)
        {
            var markers = changedKinds.SelectMany(PdfProfiles.MarkersForFileProfileKind).ToHashSet();
            var affected = await db.DataSetSources
                .Where(s => s.FileId == fileId && markers.Contains(s.SheetOrPath)).ToListAsync(ct);
            foreach (var src in affected) src.MarkRecognitionStale(DataSetStaleReason.ProfileChanged);
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Профиль НЕ-табличного вида для набора (issue #412): привязанный к файлу → встроенный по виду.
    /// Штамп, обложка/титул и счёт работают на уровне файла целиком, поэтому их профиль живёт на
    /// наборе, а не на группе листов (в отличие от таблиц, issue #410).
    ///
    /// Привязка с чужим или удалённым профилем деградирует к встроенному, а не роняет распознавание:
    /// потерять альбом из-за удалённого профиля хуже, чем распознать его дефолтными параметрами.
    /// </summary>
    private async Task<ResolvedRecognitionProfile> ProfileForFileAsync(
        Domain.DataSets.DataSetFile file, RecognitionProfileKind kind, CancellationToken ct)
    {
        var bound = ParseFileProfileMap(file.RecognitionProfiles).GetValueOrDefault(kind.ToString());
        if (bound is { } id && await profiles.GetByIdAsync(id, ct) is { } p && p.Kind == kind) return p;
        return await profiles.GetBuiltInAsync(BuiltInRecognitionProfiles.CodeForKind(kind), ct);
    }

    /// <summary>Карта {вид: id профиля} набора. Сломанный JSON — пустая карта (не падаем: распознавание
    /// важнее привязки, оно продолжится на встроенных профилях).</summary>
    public static Dictionary<string, Guid> ParseFileProfileMap(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, Guid>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>
    /// Спецификация распознавания таблицы группы — ЕДИНСТВЕННОЕ место, где решается «таблична ли
    /// группа и по каким колонкам её читать» (issue #410). Раньше этот предикат был размазан по пяти
    /// точкам в виде «есть известный табличный тэг», и одна из них (<c>ReprojectTableSourcesAsync</c>)
    /// на его основании УДАЛЯЕТ источники: разъехавшись, они молча сносили бы данные.
    ///
    /// Приоритет: профиль на группе → тип, объявивший тэг (#29) → встроенный профиль по тэгу.
    /// null — группа не табличная (ни профиля, ни тэга).
    /// </summary>
    public async Task<(IReadOnlyList<RecognitionField> Columns, RecognitionProfileKind Kind, RecognitionTableShape? Shape)?>
        ResolveTableSpecAsync(GostGroupingGroup group, CancellationToken ct)
    {
        // 1. Профиль, привязанный к группе — высший приоритет и единственный путь для произвольных
        //    таблиц (тэга у них нет). Если профиль удалён — деградируем к остальной цепочке.
        if (group.ProfileId is { } pid && await profiles.GetByIdAsync(pid, ct) is { } bound
            && RecognitionKinds.Describe(bound.Kind).RowsKey is not null)
            return (bound.ToRowColumns(), bound.Kind, bound.Shape);

        var tag = (group.Tags ?? []).FirstOrDefault(profiles.IsTableTag);
        if (tag is null) return null;

        var builtIn = (await profiles.GetForTagAsync(tag, ct))!;

        // 2. Тип документа, объявивший тэг (#29): таблица распознаётся прямо в ключи полей типа и
        //    материализуется в него (#19).
        var allTypes = await db.DocumentTypes.AsNoTracking().ToListAsync(ct);
        var targetType = allTypes.FirstOrDefault(t => SchemaTags.TypeHasTag(t, allTypes, tag));
        if (targetType is not null)
        {
            var typesById = allTypes.ToDictionary(t => t.Id);
            var typeFields = DocumentTypeSchemaReader.EffectiveFields(targetType.Id, typesById)
                .Where(f => SchemaFieldKinds.IsScalar(f.Type))
                .ToList();
            if (typeFields.Count == 0)
                throw new InvalidRequestException($"У типа «{targetType.Name}» нет скалярных полей для распознавания таблицы.");
            return (
                typeFields.Select(f => new RecognitionField(f.Key, f.Title ?? f.Key, MapRecognitionType(f.Type))).ToList(),
                builtIn.Kind, builtIn.Shape);
        }

        // 3. Встроенный профиль по тэгу.
        return (builtIn.ToRowColumns(), builtIn.Kind, builtIn.Shape);
    }

    /// <summary>Таблична ли группа — тот же единый предикат, без разбора колонок.</summary>
    public Task<bool> IsTableGroupAsync(GostGroupingGroup group, CancellationToken ct)
        => profiles.IsTableGroupAsync(group.ProfileId, group.Tags, ct);

    /// <summary>Привязка профиля распознавания к группе листов (issue #410). Точечно, как и тэги:
    /// <c>g with { … }</c> сохраняет прочие поля группы, включая уже распознанное сырьё таблицы.
    /// Выставляет <c>TableStale</c> — смена параметров обесценивает распознанные строки, но НЕ
    /// перезапускает распознавание: это дорогой LLM-вызов, пользователь решает сам.
    /// profileId = null снимает привязку (возврат к цепочке «тип → встроенный профиль по тэгу»).</summary>
    public async Task<GostGroupingDto?> SetDocumentProfileAsync(
        Guid fileId, int firstPageIndex, Guid? profileId, CancellationToken ct)
    {
        var file = await db.DataSetFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file == null) return null;
        if (file.Format != DataSetFormat.Pdf)
            throw new InvalidRequestException("Профиль распознавания задаётся только для PDF-набора.");

        var grouping = ParseGrouping(file.Grouping)
            ?? throw new InvalidRequestException("Группировка ещё не распознана.");

        if (profileId is { } pid)
        {
            var profile = await profiles.GetByIdAsync(pid, ct)
                ?? throw new InvalidRequestException("Профиль распознавания не найден.");
            // Проверяем ОБЛАСТЬ, а не «есть ли табличная часть»: у счёта она есть, но привязывается
            // он к набору целиком, а не к группе листов.
            if (!RecognitionKinds.IsGroupScoped(profile.Kind))
                throw new InvalidRequestException(
                    $"Профиль «{profile.Name}» привязывается к набору, а не к группе листов.");
        }

        var touched = grouping.Groups.FirstOrDefault(
            g => g.Kind == GostGroupKind.Document && g.Pages.Any(p => p.PageIndex == firstPageIndex));
        var updated = grouping.Groups
            .Select(g => g.Kind == GostGroupKind.Document && g.Pages.Any(p => p.PageIndex == firstPageIndex)
                ? g with { ProfileId = profileId, TableStale = g.TableStale || !string.IsNullOrEmpty(g.TableData) }
                : g)
            .ToList();
        file.SetGrouping(JsonSerializer.Serialize(new GostGroupingData(updated, grouping.ManuallyEdited)));

        // Признак ставим и на САМ ИСТОЧНИК, а не только на группу (issue #815). Раньше `TableStale`
        // доезжал до источника лишь при ближайшей ре-проекции (ReprojectTableSourcesAsync), которую
        // этот путь не запускает: снимок MCP читал устаревание прямо из группировки и видел его, а
        // клиент знает только поле источника — и не показывал ничего. Расхождение двух потребителей
        // на одном признаке лечится тем, что признак заводится в одном месте.
        // Повторное сохранение ТОГО ЖЕ профиля ничего не обесценивает — и метка по нему была бы той
        // самой лампой, что горит всегда (тот же гейт, что у файловых профилей).
        var profileActuallyChanged = touched is not null && touched.ProfileId != profileId;
        if (profileActuallyChanged && !string.IsNullOrEmpty(touched!.TableData))
        {
            var marker = PdfProfiles.GostTableMarkerPrefix + touched!.Id;
            var tableSource = await db.DataSetSources
                .FirstOrDefaultAsync(s => s.FileId == fileId && s.SheetOrPath == marker, ct);
            tableSource?.MarkRecognitionStale(DataSetStaleReason.ProfileChanged);
        }

        await db.SaveChangesAsync(ct);

        return await ToGroupingDtoAsync(file, updated, grouping.ManuallyEdited, ct);
    }

    private async Task<GostGroupingDto> ToGroupingDtoAsync(
        Domain.DataSets.DataSetFile file, IReadOnlyList<GostGroupingGroup> groups, bool manuallyEdited, CancellationToken ct)
    {
        var pageCount = await GetPdfPageCountAsync(file.BlobPath, ct);
        return new GostGroupingDto(
            groups.Select(g => new GostGroupingGroupDto(
                g.Kind, g.Code, g.Name, g.Pages.Select(p => p.PageIndex).ToList(), g.Tags, g.ProfileId,
                g.Pages.Where(p => p.NoAnswer).Select(p => p.PageIndex).ToList())).ToList(),
            manuallyEdited, pageCount);
    }
}
