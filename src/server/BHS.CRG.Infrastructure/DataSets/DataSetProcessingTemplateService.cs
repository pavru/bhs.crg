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

    /// <summary>
    /// Шаблон из извлечения и обработки источника — «Сохранить как шаблон» (issue #1141). Собирает его
    /// сервер из СОХРАНЁННОГО источника: раньше содержимое присылала страница из своей копии, и
    /// устаревшая копия давала шаблон с обработкой, которой у источника уже нет. Версия сверяется по
    /// той же причине с обратной стороны: человек сохраняет шаблоном то, что видит, — и если источник
    /// тем временем изменили, молча взять новое значило бы сохранить не то, что он просил.
    /// </summary>
    /// <returns><c>null</c> — источника нет.</returns>
    public async Task<DataSetProcessingTemplateDto?> CreateFromSourceAsync(
        Guid sourceId, string name, string? ifMatch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidRequestException("Шаблон обработки не сохранён: не задано название.");

        var source = await db.DataSetSources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;

        if (SourceProcessingVersion.Moved(source, ifMatch))
            throw new ConflictException(
                $"Шаблон «{name.Trim()}» не сохранён: источник «{source.Name}» тем временем изменили, и "
                + "страница показывает его прежнюю обработку. Обновите страницу, проверьте обработку и "
                + "сохраните шаблон снова.");

        EnsureFilter(source.RowFilter, name);
        var template = DataSetProcessingTemplate.Create(
            name, source.SheetOrPath, source.ColumnExpressions,
            source.RowFilter, source.ComputedColumns, source.SortSpec);
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
