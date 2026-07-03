using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Именованные рецепты обработки (Extraction+Filter+Transformation+Sort), переиспользуемые
/// на разных источниках через <see cref="DataSetService.ApplyProcessingTemplateAsync"/>.
/// Второй шаг декомпозиции <see cref="DataSetService"/> (см. архитектурный отчёт).
/// </summary>
public class DataSetProcessingTemplateService(AppDbContext db)
{
    public async Task<IReadOnlyList<DataSetProcessingTemplateDto>> ListAsync(CancellationToken ct)
    {
        var templates = await db.DataSetProcessingTemplates.OrderBy(t => t.Name).AsNoTracking().ToListAsync(ct);
        return templates.Select(DataSetDtoMapper.MapProcessingTemplate).ToList();
    }

    public async Task<DataSetProcessingTemplateDto> CreateAsync(CreateProcessingTemplateInput input, CancellationToken ct)
    {
        var template = DataSetProcessingTemplate.Create(
            input.Name, input.SheetOrPath, DataSetDtoMapper.SerializeColumnExpressions(input.ColumnExpressions),
            DataSetDtoMapper.SerializeJson(input.RowFilter), DataSetDtoMapper.SerializeJson(input.ComputedColumns),
            DataSetDtoMapper.SerializeJson(input.SortSpec));
        db.DataSetProcessingTemplates.Add(template);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapProcessingTemplate(template);
    }

    public async Task<DataSetProcessingTemplateDto?> UpdateAsync(Guid id, UpdateProcessingTemplateInput input, CancellationToken ct)
    {
        var template = await db.DataSetProcessingTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (template == null) return null;

        template.Update(input.Name, input.SheetOrPath, DataSetDtoMapper.SerializeColumnExpressions(input.ColumnExpressions),
            DataSetDtoMapper.SerializeJson(input.RowFilter), DataSetDtoMapper.SerializeJson(input.ComputedColumns),
            DataSetDtoMapper.SerializeJson(input.SortSpec));
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapProcessingTemplate(template);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var template = await db.DataSetProcessingTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (template == null) return false;
        db.DataSetProcessingTemplates.Remove(template);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
