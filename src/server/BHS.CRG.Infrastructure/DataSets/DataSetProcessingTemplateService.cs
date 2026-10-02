using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Именованные рецепты обработки (Extraction+Filter+Transformation+Sort), переиспользуемые
/// на разных источниках через <see cref="DataSetService.ApplyProcessingTemplateAsync"/>.
/// Второй шаг декомпозиции <see cref="DataSetService"/> (см. архитектурный отчёт).
///
/// <para>Отбор шаблона проверяется при сохранении ПО ФОРМЕ (issue #1137): видов колонок у шаблона
/// нет — он ни к какому источнику не привязан. Годится ли отбор конкретному источнику, решается при
/// применении. Отбор, не изменившийся в запросе, не перепроверяется — иначе шаблон с негодным
/// отбором, сохранённый раньше, нельзя было бы даже переименовать.</para>
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
        var rowFilter = DataSetDtoMapper.SerializeJson(input.RowFilter);
        EnsureFilter(rowFilter, input.Name);
        var template = DataSetProcessingTemplate.Create(
            input.Name, input.SheetOrPath, DataSetDtoMapper.SerializeColumnExpressions(input.ColumnExpressions),
            rowFilter, DataSetDtoMapper.SerializeJson(input.ComputedColumns),
            DataSetDtoMapper.SerializeJson(input.SortSpec));
        db.DataSetProcessingTemplates.Add(template);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapProcessingTemplate(template);
    }

    public async Task<DataSetProcessingTemplateDto?> UpdateAsync(Guid id, UpdateProcessingTemplateInput input, CancellationToken ct)
    {
        var template = await db.DataSetProcessingTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (template == null) return null;

        var rowFilter = DataSetDtoMapper.SerializeJson(input.RowFilter);
        if (!DataSetDtoMapper.SameJson(rowFilter, template.RowFilter)) EnsureFilter(rowFilter, input.Name);
        template.Update(input.Name, input.SheetOrPath, DataSetDtoMapper.SerializeColumnExpressions(input.ColumnExpressions),
            rowFilter, DataSetDtoMapper.SerializeJson(input.ComputedColumns),
            DataSetDtoMapper.SerializeJson(input.SortSpec));
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapProcessingTemplate(template);
    }

    private static void EnsureFilter(string? rowFilter, string templateName)
    {
        if (DataSetRowFilterExecutor.Problem(rowFilter) is { } problem)
            throw new InvalidRequestException(
                $"Шаблон обработки «{templateName}» не сохранён: {problem} Такой отбор не выполнит ни один "
                + "источник — исправьте условие и сохраните снова.");
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
