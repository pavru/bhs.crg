using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Templates;

public class TemplateAssetResolver(AppDbContext db) : ITemplateAssetResolver
{
    public async Task<ResolvedTemplateAssets> ResolveAsync(Guid templateId, Guid documentTypeId, CancellationToken ct = default)
    {
        // Иерархический OR по трём уровням — по аналогии с DataSetFileService.ListAvailableFilesAsync.
        var candidates = await db.TemplateAssets
            .AsNoTracking()
            .Where(a =>
                (a.Scope == TemplateAssetScope.System && a.ScopeId == null) ||
                (a.Scope == TemplateAssetScope.DocumentType && a.ScopeId == documentTypeId) ||
                (a.Scope == TemplateAssetScope.Template && a.ScopeId == templateId))
            .ToListAsync(ct);

        if (candidates.Count == 0) return ResolvedTemplateAssets.Empty;

        // Приоритет — по возрастанию числового значения enum (Template=1 самый высокий, System=3 низкий),
        // тот же принцип, что уже используется для CatalogScope в проекте.
        var images = candidates
            .Where(a => a.Kind == TemplateAssetKind.Image)
            .GroupBy(a => a.Name)
            .Select(g => g.OrderBy(a => (int)a.Scope).First())
            .Select(a => new ResolvedImageAsset(a.Name, a.FileName, a.MimeType, a.BlobPath))
            .ToList();

        var fonts = candidates
            .Where(a => a.Kind == TemplateAssetKind.Font)
            // Группируем по имени семейства из файла; если не распознано при загрузке — fallback на Name
            // (менее надёжно: два разных файла с одинаковым пользовательским Name, но без FontFamilyName,
            // будут считаться "тем же шрифтом" для целей приоритета — приемлемый edge case).
            .GroupBy(a => a.FontFamilyName ?? a.Name)
            .Select(g => g.OrderBy(a => (int)a.Scope).First())
            .Select(a => new ResolvedFontAsset(a.FileName, a.BlobPath))
            .ToList();

        return new ResolvedTemplateAssets(images, fonts);
    }
}
