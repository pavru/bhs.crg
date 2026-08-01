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
/// <param name="DocumentsScanned">Сколько документов пришлось прочитать (файлы наборов разбираются заново).</param>
public record MaterialLabelReport(int LinksWithoutLabel, int Named, int NotFound, int DocumentsScanned);

/// <summary>
/// Разовое восстановление человеческих имён материалов у уже заведённых связей (issue #561).
///
/// Метка появилась в #554 и пишется при привязке, поэтому у связок, заведённых раньше, её нет — а это
/// 112 из 113 на рабочей базе. Экран контроля (#555) показывает им машинный ключ (<c>mb15-07-01m-54</c>
/// — это боковая панель ВРУ), то есть инструмент поиска дефекта нечитаем ровно там, где нужнее всего:
/// 41 ключ из 113 — голые артикулы, и неверные связки (#552) сидят именно среди них.
///
/// Имя вычисляется ровно так же, как его считает вкладка «Документы качества» внутри документа:
/// строки привязанных наборов → поля с тэгом <see cref="FunctionalTag.Identity"/> → ключ из ПЕРВОГО
/// значения, имя — склейка всех через « · ».
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

        // Документы, у которых есть привязки наборов: только их и читаем.
        var ownerIds = await db.DataSetBindings.Select(b => b.OwnerId).Distinct().ToListAsync(ct);

        var named = 0;
        foreach (var ownerId in ownerIds)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<BindingPreviewDto> preview;
            // Файл мог исчезнуть, набор — перестать разбираться: один сломанный документ не должен
            // отменять дозаполнение всех остальных.
            try { preview = await bindings.PreviewBindingsAsync(ownerId, ct); }
            catch (Exception) { continue; }

            foreach (var (keys, label) in MaterialsOf(preview, identityKeys))
            {
                // Сопоставляем по ЛЮБОМУ значению идентичности — ровно так матчит резолвер связок
                // (QualityLinkResolver.TryMatch). Хранимый ключ построен по первому непустому полю, а
                // порядок полей мы здесь воспроизвести не можем: он зависит от порядка типов у клиента.
                foreach (var key in keys)
                {
                    if (!byKey.TryGetValue(key, out var found)) continue;
                    foreach (var link in found)
                    {
                        if (link.MaterialLabel is not null) continue;
                        link.DescribeMaterial(label);
                        named++;
                    }
                    break;
                }
            }
        }

        if (!dryRun && named > 0) await db.SaveChangesAsync(ct);
        else db.ChangeTracker.Clear();   // сухой прогон не оставляет следов

        return new MaterialLabelReport(links.Count, named, links.Count - named, ownerIds.Count);
    }

    /// <summary>
    /// Ключи полей идентичности по всем составным типам — тем же способом, что и резолвер связок
    /// (<c>SchemaTags.FieldKeysWithTag</c>): по тэгу, а не по именам полей.
    /// </summary>
    private async Task<HashSet<string>> IdentityKeysAsync(CancellationToken ct)
    {
        var composites = await db.DocumentTypes.AsNoTracking()
            .Where(t => t.Kind == DocumentTypeKind.Composite)
            .ToListAsync(ct);
        return composites
            .SelectMany(t => SchemaTags.FieldKeysWithTag(t.Schema, FunctionalTag.Identity))
            .ToHashSet();
    }

    /// <summary>
    /// Пары «возможные ключи → человеческое имя» из строк превью. Ключей несколько: связка могла быть
    /// заведена по любому полю идентичности, и совпасть должен любой из них.
    /// </summary>
    private static IEnumerable<(IReadOnlyList<string> Keys, string Label)> MaterialsOf(
        IReadOnlyList<BindingPreviewDto> preview, HashSet<string> identityKeys)
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
