using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Maintenance;

/// <summary>Что нашло (или нашло бы) дозаполнение имён материалов.</summary>
/// <param name="LinksWithoutLabel">Связок без имени на входе.</param>
/// <param name="Named">Скольким имя нашлось.</param>
/// <param name="NotFound">Скольким не нашлось — материала больше нет ни в одном подключённом наборе.</param>
/// <param name="DocumentsScanned">Сколько документов удалось прочитать (файлы наборов разбираются заново).</param>
/// <param name="DocumentsFailed">Сколько прочитать НЕ удалось — их материалы в поиске не участвовали.</param>
public record MaterialLabelReport(
    int LinksWithoutLabel, int Named, int NotFound, int DocumentsScanned, int DocumentsFailed = 0);

/// <summary>
/// Разовое восстановление человеческих имён материалов у уже заведённых связей (issue #561).
///
/// Метка появилась в #554 и пишется при привязке, поэтому у связок, заведённых раньше, её нет — а это
/// 112 из 113 на рабочей базе. Экран контроля (#555) показывает им машинный ключ (<c>mb15-07-01m-54</c>
/// — это боковая панель ВРУ), то есть инструмент поиска дефекта нечитаем ровно там, где нужнее всего:
/// 41 ключ из 113 — голые артикулы, и неверные связки (#552) сидят именно среди них.
///
/// Имя собирается из тех же полей, что читает вкладка «Документы качества» внутри документа — по
/// тэгу <see cref="FunctionalTag.Identity"/>, — и из тех же ДВУХ источников: строк привязанных
/// наборов данных И массивов материалов в самих реквизитах документа. Порядок склейки значений
/// детерминирован (типы по имени), но может отличаться от клиентского: там он задан порядком типов
/// в ответе API. Для сопоставления это неважно — ключи те же.
///
/// НЕ EF-миграция: читает и разбирает файлы наборов из блоб-хранилища, которого на старте приложения
/// может не быть. Момент выбирает администратор и видит отчёт — как у переноса картинок (#522).
///
/// Идемпотентно: связка с именем пропускается, повторный прогон ничего не меняет.
/// </summary>
public class MaterialLabelBackfill(
    AppDbContext db,
    IDataSetService bindings)
{
    public async Task<MaterialLabelReport> RunAsync(bool dryRun, CancellationToken ct = default)
    {
        var links = await db.MaterialQualityLinks
            .Where(l => l.MaterialLabel == null)
            .ToListAsync(ct);
        if (links.Count == 0) return new MaterialLabelReport(0, 0, 0, 0);

        var byKey = links
            .GroupBy(l => l.MaterialKey)
            .ToDictionary(g => g.Key, g => g.ToList());

        var identityKeys = await IdentityKeysAsync(ct);
        if (identityKeys.Count == 0) return new MaterialLabelReport(links.Count, 0, links.Count, 0);

        var named = 0;
        var scanned = 0;
        var failed = 0;

        // Источник 1 — массивы материалов в самих реквизитах документов. Дешёвый: только чтение БД,
        // без блобов. Пропустить его нельзя: связки, заведённые из этой половины, иначе навсегда
        // остались бы с ключом, да ещё и попали бы в отчёт как «материала больше нет».
        foreach (var data in await db.DomainObjects.AsNoTracking().Select(o => o.Data).ToListAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            named += Apply(MaterialsInRequisites(data, identityKeys), byKey);
        }

        // Источник 2 — строки привязанных наборов данных. Дорогой: файлы скачиваются и разбираются.
        var ownerIds = await db.DataSetBindings.Select(b => b.OwnerId).Distinct().ToListAsync(ct);
        foreach (var ownerId in ownerIds)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<BindingPreviewDto> preview;
            // Файл мог исчезнуть, набор — перестать разбираться: один сломанный документ не должен
            // отменять дозаполнение остальных. Но и молчать нельзя — иначе «материала больше нет»
            // сказали бы про материал, который просто не удалось прочитать.
            try { preview = await bindings.PreviewBindingsAsync(ownerId, ct); scanned++; }
            catch (Exception) { failed++; continue; }

            named += Apply(MaterialsOf(preview, identityKeys), byKey);

            // Сохраняем по документу, а не одним махом в конце: проход длинный, и если параллельно
            // снимут связку (экран массового разрыва — ровно тот инструмент, которым админ только что
            // пользовался), единый SaveChanges упал бы и выбросил ВЕСЬ результат (урок #532).
            if (!dryRun) await db.SaveChangesAsync(ct);
        }

        if (!dryRun) await db.SaveChangesAsync(ct);
        else db.ChangeTracker.Clear();   // сухой прогон не оставляет следов

        return new MaterialLabelReport(links.Count, named, links.Count - named, scanned, failed);
    }

    /// <summary>Проставляет имена связкам, чей ключ совпал с любым из значений идентичности строки.</summary>
    private static int Apply(
        IEnumerable<(IReadOnlyList<string> Keys, string Label)> materials,
        Dictionary<string, List<MaterialQualityLink>> byKey)
    {
        var named = 0;
        foreach (var (keys, label) in materials)
        {
            // Сопоставляем по ЛЮБОМУ значению идентичности — так матчит резолвер связок
            // (QualityLinkResolver.TryMatch). Перебираем ВСЕ совпавшие ключи, а не только первый:
            // связки одного материала могли быть заведены и по артикулу, и по наименованию, и
            // остановка на первом оставила бы вторую группу без имени навсегда.
            foreach (var key in keys)
            {
                if (!byKey.TryGetValue(key, out var found)) continue;
                foreach (var link in found)
                {
                    if (link.MaterialLabel is not null) continue;
                    link.DescribeMaterial(label);
                    named++;
                }
            }
        }
        return named;
    }

    /// <summary>
    /// Ключи полей идентичности по всем составным типам — тем же способом, что и резолвер связок
    /// (<c>SchemaTags.FieldKeysWithTag</c>): по тэгу, а не по именам полей.
    /// </summary>
    private async Task<List<string>> IdentityKeysAsync(CancellationToken ct)
    {
        var composites = await db.DocumentTypes.AsNoTracking()
            .Where(t => t.Kind == DocumentTypeKind.Composite)
            .OrderBy(t => t.Name)   // детерминированный порядок: от него зависит порядок слов в имени
            .ToListAsync(ct);
        return composites
            .SelectMany(t => SchemaTags.FieldKeysWithTag(t.Schema, FunctionalTag.Identity))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Материалы из реквизитов документа: массивы объектов верхнего уровня, у элементов которых есть
    /// поля идентичности. Тем же способом их собирает вкладка привязок.
    /// </summary>
    private static IEnumerable<(IReadOnlyList<string> Keys, string Label)> MaterialsInRequisites(
        JsonDocument? data, IReadOnlyList<string> identityKeys)
    {
        if (data is null) yield break;
        var root = data.RootElement;
        if (root.ValueKind != JsonValueKind.Object) yield break;

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var element in property.Value.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;
                var values = identityKeys
                    .Select(k => element.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                        ? v.GetString() : null)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v!.Trim())
                    .ToList();
                if (values.Count == 0) continue;

                var keys = values.Select(MatchKeyNormalizer.Normalize).Where(k => k.Length > 0).ToList();
                if (keys.Count > 0) yield return (keys, string.Join(" · ", values));
            }
        }
    }

    /// <summary>
    /// Пары «возможные ключи → человеческое имя» из строк превью. Ключей несколько: связка могла быть
    /// заведена по любому полю идентичности, и совпасть должен любой из них.
    /// </summary>
    private static IEnumerable<(IReadOnlyList<string> Keys, string Label)> MaterialsOf(
        IReadOnlyList<BindingPreviewDto> preview, IReadOnlyList<string> identityKeys)
    {
        foreach (var binding in preview)
        {
            // Data — либо одна запись (скалярная привязка), либо список строк (табличная).
            var rows = binding.Data switch
            {
                IEnumerable<Dictionary<string, object?>> list => list,
                Dictionary<string, object?> single => [single],
                _ => [],
            };
            foreach (var row in rows)
            {
                var values = identityKeys
                    .Select(k => row.TryGetValue(k, out var v) ? v as string : null)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v!.Trim())
                    .ToList();
                if (values.Count == 0) continue;

                var keys = values.Select(MatchKeyNormalizer.Normalize).Where(k => k.Length > 0).ToList();
                if (keys.Count == 0) continue;
                yield return (keys, string.Join(" · ", values));
            }
        }
    }
}
