using System.Text.Json.Nodes;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.DataSets;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Обработка источника (отбор, вычисляемые колонки, сортировка): назначение и применение шаблона.
///
/// <para><b>Отбор проверяется на входе</b> (issue #1137) — тем же разбором, каким он исполняется
/// (<see cref="DataSetRowFilterExecutor.Problem" />), и с видами колонок того, кто сохраняет. До этого
/// служба принимала любое дерево, и негодное обнаруживалось чтением: источник отказывал в
/// предпросмотре, выгрузке и печати — не там, где человек ошибся. Диалог отбора держал ради этого
/// собственную копию правил, и копия расходилась с сервером; теперь решает сервер.</para>
///
/// <para>Гарантией проверка на входе не становится: отказ при чтении остаётся в исполнителе — мимо
/// входа идут восстановление из копии, копия источника и всё сохранённое раньше.</para>
///
/// <para>Своим файлом по той же причине, что <c>DataSetSourceService.System.cs</c>: основной стоит в
/// храповике размера, а параметр доступа и проверка добавили бы ему строк.</para>
/// </summary>
public partial class DataSetSourceService
{
    public async Task<DataSetSourceDto?> SetSourceProcessingAsync(
        Guid sourceId, SetSourceProcessingInput input, DataAccess access, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;

        var rowFilter = DataSetDtoMapper.SerializeJson(input.RowFilter);
        // Отбор, который в запросе не изменился, повторно не проверяем. Клиент шлёт обработку целиком,
        // и иначе негодный отбор, сохранённый до #1137, запер бы источник: нельзя было бы поправить
        // ни сортировку, ни вычисляемые колонки. Хуже от такого сохранения не становится — источник
        // с этим отбором уже отказывает на чтении.
        if (!SameJson(rowFilter, source.RowFilter)
            && DataSetRowFilterExecutor.Problem(rowFilter, await TypesAsync(source, access, ct)) is { } problem)
            throw new InvalidRequestException(
                $"Отбор строк источника «{source.Name}» не сохранён: {problem} Источник такой отбор не "
                + "выполнит — исправьте условие и сохраните снова.");

        source.SetProcessing(
            rowFilter, DataSetDtoMapper.SerializeJson(input.ComputedColumns), DataSetDtoMapper.SerializeJson(input.SortSpec));
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(source);
    }

    public async Task<DataSetSourceDto?> ApplyProcessingTemplateAsync(
        Guid sourceId, Guid templateId, DataAccess access, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;

        var template = await db.DataSetProcessingTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId, ct)
            ?? throw new NotFoundException($"DataSetProcessingTemplate {templateId} not found");

        // Проверка — ДО первой правки источника: шаблон ложится целиком или не ложится вовсе. Отбор
        // шаблона писали под другой источник, и здесь он может не выполниться («Итого содержит 1» у
        // таблицы, где «Итого» — число). Применив его, мы получили бы источник, отказывающий на
        // каждом чтении, — и человек искал бы причину в данных, а не в шаблоне.
        if (DataSetRowFilterExecutor.Problem(template.RowFilter, await TypesAsync(source, access, ct)) is { } problem)
            throw new InvalidRequestException(
                $"Шаблон «{template.Name}» не применён к источнику «{source.Name}»: отбор шаблона этот "
                + $"источник не выполнит — {problem} Источник не изменён.");

        // Extraction в шаблоне — опциональна: если задана, пере-парсим файл (имя источника не
        // трогаем — оно своё у каждого источника, не часть рецепта). У системного источника
        // extraction — это ВЫБОР КОНСОЛИДАЦИИ, а не лист файла: подменять его рецептом нельзя
        // (парсера у формата System нет — прежде здесь падало «Нет парсера для формата System»).
        // Обработку при этом переносим: фильтр/колонки/сортировка к живым строкам применимы (#613).
        if (!string.IsNullOrWhiteSpace(template.SheetOrPath) && !source.File.IsSystem)
        {
            var (schema, rowCount) = await ParseForDefinitionAsync(
                source.File.BlobPath, source.File.Format, template.SheetOrPath, template.ColumnExpressions, ct);
            source.UpdateDefinition(source.Name, template.SheetOrPath, template.ColumnExpressions);
            source.UpdateCache(DataSetDtoMapper.SerializeSchema(schema), rowCount);
        }
        source.SetProcessing(template.RowFilter, template.ComputedColumns, template.SortSpec);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(source);
    }

    /// <summary>
    /// Виды колонок источника глазами того, кто сохраняет; null — видов нет (файл, распознавание) и
    /// отбор проверяется только по форме. Виды объявляет поставщик системного набора, и зависят они
    /// от прав: колонка, закрытая человеку, для него колонка без значений, и отбор по ней он не
    /// выполнит — значит, и сохранить не должен.
    /// </summary>
    private async Task<DataSetColumnTypes?> TypesAsync(DataSetSource source, DataAccess access, CancellationToken ct) =>
        (await systemCounts.StateAsync(source, source.File, access, ct))?.Types;

    /// <summary>
    /// Один ли это отбор. Сравниваем значением, а не текстом: в базе отбор лежит в <c>jsonb</c>, и
    /// порядок ключей с пробелами у прочитанного оттуда другой, чем у пришедшего в запросе.
    /// </summary>
    internal static bool SameJson(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);
        return JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right));
    }
}
