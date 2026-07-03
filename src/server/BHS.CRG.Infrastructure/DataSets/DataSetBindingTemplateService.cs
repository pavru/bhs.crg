using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Именованные наборы маппингов колонок → поля типа документа, переиспользуемые при создании
/// новых <see cref="DataSetBinding"/> для документов того же типа.
/// Третий шаг декомпозиции <see cref="DataSetService"/> (см. архитектурный отчёт).
/// </summary>
public class DataSetBindingTemplateService(AppDbContext db)
{
    public async Task<IReadOnlyList<DataSetBindingTemplateDto>> ListAsync(Guid docTypeId, CancellationToken ct)
    {
        var templates = await db.DataSetBindingTemplates
            .Where(t => t.DocumentTypeId == docTypeId)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .AsNoTracking()
            .ToListAsync(ct);
        return templates.Select(DataSetDtoMapper.MapTemplate).ToList();
    }

    public async Task<DataSetBindingTemplateDto> CreateAsync(Guid docTypeId, CreateTemplateInput input, CancellationToken ct)
    {
        var maxOrder = await db.DataSetBindingTemplates
            .Where(t => t.DocumentTypeId == docTypeId)
            .MaxAsync(t => (int?)t.SortOrder, ct) ?? -1;

        var template = DataSetBindingTemplate.Create(
            docTypeId, input.Name, input.TargetFieldKey, DataSetDtoMapper.SerializeMapping(input.ColumnMappings), maxOrder + 1);

        db.DataSetBindingTemplates.Add(template);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapTemplate(template);
    }

    public async Task<DataSetBindingTemplateDto?> UpdateAsync(Guid docTypeId, Guid id, UpdateTemplateInput input, CancellationToken ct)
    {
        var template = await db.DataSetBindingTemplates
            .FirstOrDefaultAsync(t => t.Id == id && t.DocumentTypeId == docTypeId, ct);
        if (template == null) return null;

        template.Update(input.Name, input.TargetFieldKey, DataSetDtoMapper.SerializeMapping(input.ColumnMappings),
            input.SortOrder ?? template.SortOrder);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapTemplate(template);
    }

    public async Task<bool> DeleteAsync(Guid docTypeId, Guid id, CancellationToken ct)
    {
        var template = await db.DataSetBindingTemplates
            .FirstOrDefaultAsync(t => t.Id == id && t.DocumentTypeId == docTypeId, ct);
        if (template == null) return false;
        db.DataSetBindingTemplates.Remove(template);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
