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

        // Правка — по частям (issue #1139): чего в запросе нет, то остаётся как есть.
        var rowFilter = Part(input.RowFilter, source.RowFilter);

        // Проверяется отбор, только когда он ПРИСЛАН, — и всегда, когда прислан: присланный отбор —
        // новый ввод, даже если совпал с сохранённым. Негодный отбор, сохранённый раньше (до #1137,
        // из копии), источник при этом не запирает: сортировку и вычисляемые колонки правят, не
        // присылая отбора, и до проверки дело не доходит.
        if (input.RowFilter.Sent && await FilterProblemAsync(rowFilter, source, access, ct) is { } problem)
            throw new InvalidRequestException(
                $"Отбор строк источника «{source.Name}» не сохранён: {problem} Источник такой отбор не "
                + "выполнит — исправьте условие и сохраните снова.");

        source.SetProcessing(
            rowFilter, Part(input.ComputedColumns, source.ComputedColumns), Part(input.SortSpec, source.SortSpec));
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(source);
    }

    /// <summary>Часть обработки после правки: присланная — из запроса, остальные — сохранённые.</summary>
    private static string? Part(ProcessingPart part, string? stored) =>
        part.Sent ? DataSetDtoMapper.SerializeJson(part.Value) : stored;

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
        if (await FilterProblemAsync(template.RowFilter, source, access, ct) is { } problem)
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
    /// Почему этот источник отбор не выполнит; null — возражений нет.
    ///
    /// <para>Виды колонок — глазами того, кто сохраняет: их объявляет поставщик системного набора, и
    /// зависят они от прав. Колонка, закрытая человеку, для него колонка без значений, и отбор по
    /// ней он не выполнит — значит, и сохранить не должен. У файла и распознавания видов нет, и
    /// отбор проверяется только по форме.</para>
    ///
    /// <para>За видами идём, только когда без них не обойтись: поставщик собирает ради них
    /// консолидацию ЦЕЛИКОМ (дешевле спросить нечем — см. <c>AutoMapAsync</c>). Отбора нет или он
    /// негоден уже по форме — ответ известен без поставщика, и сброс отбора не должен зависеть от
    /// того, жив ли поставщик и пускает ли он этого человека. Отказ поставщика при этом — отказ
    /// сохранения, а не «видов нет» (<see cref="SystemSourceCounter.TypesAsync" />).</para>
    /// </summary>
    private async Task<string?> FilterProblemAsync(
        string? rowFilter, DataSetSource source, DataAccess access, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rowFilter)) return null;
        return DataSetRowFilterExecutor.Problem(rowFilter)
            ?? DataSetRowFilterExecutor.Problem(
                rowFilter, await systemCounts.TypesAsync(source, source.File, access, ct));
    }
}
