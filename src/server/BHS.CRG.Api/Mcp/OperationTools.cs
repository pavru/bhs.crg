using System.ComponentModel;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Jobs;
using BHS.CRG.Domain.Common;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BHS.CRG.Api.Mcp;

/// <summary>Операция принята в работу. Итог — по <paramref name="JobId" /> инструментом get_job.</summary>
public record JobStarted(Guid JobId, string Title);

/// <summary>
/// Что вышло из запроса на распознавание.
/// </summary>
/// <param name="JobId">Задача поставлена в очередь (альбом ГОСТ — минуты). Итог спрашивать get_job.</param>
/// <param name="Completed">Операция уложилась в вызов и уже выполнена — задачи нет и ждать нечего.</param>
public record RecognitionStarted(Guid? JobId, bool Completed);

/// <summary>
/// Запуск долгих операций из MCP (issue #898) — вторая половина оси ACT: до этого агент умел
/// выпустить один документ, но не мог ни собрать комплект, ни отправить набор на распознавание.
///
/// Слой тонкий, как и остальные: разбор аргументов и вызов <see cref="IOperationLauncher" />. Все
/// проверки цели, состояния набора и готовности движка выполняет общий запускатель операций.
///
/// Отказ называет причину, чтобы клиент мог подсказать следующее действие.
/// </summary>
[McpServerToolType]
public class OperationTools(IOperationLauncher launcher, IHttpContextAccessor http)
{
    // Idempotent НЕ ставим ни одному из трёх: подсказку об идемпотентности клиенты используют для
    // автоматического повтора при обрыве связи, а повтор здесь — вторая задача либо отказ.
    [McpPermission("id.document.generate")]
    [McpServerTool(Name = "assemble_document_set", ReadOnly = false, Destructive = false,
        Title = "Собрать комплект в один PDF")]
    [Description("""
        Запускает сборку комплекта в один PDF: недостающие документы выпускаются, готовые
        переиспользуются, всё склеивается по заданному в комплекте порядку. Долгая операция —
        возвращает jobId, итог спрашивать через get_job.

        Сбой любого документа прерывает сборку целиком («всё или ничего») — комплект не выпускается,
        а в отказе перечислены все не готовые документы. Чтобы не собирать заведомо неготовое,
        проверьте документы через validate_document.

        Повторный запуск, пока сборка этого комплекта идёт, отвергается: две сборки писали бы один и
        тот же выход комплекта.

        Собранный файл заменяет предыдущую сборку комплекта. Введённые данные документов при этом не
        меняются — пересоздаются только выпускаемые файлы.
        """)]
    public async Task<JobStarted> AssembleDocumentSetAsync(
        [Description("Идентификатор комплекта.")] Guid setId,
        CancellationToken ct,
        [Description("""
            Необязательное подмножество документов комплекта. Пусто — собирается весь комплект в его
            собственном порядке.
            """)] Guid[]? documentIds = null)
    {
        try
        {
            var jobId = await launcher.AssembleDocumentSetAsync(setId, JobTools.RequireUserId(http), documentIds, ct)
                ?? throw new McpException("Комплект не найден.");
            return new JobStarted(jobId, "Сборка комплекта");
        }
        catch (DomainException ex) { throw new McpException(ex.Message); }
    }

    // Распознавание обновляет данные набора, поэтому оба инструмента требуют права редактирования.
    //
    // Тем же правом закрыты и адреса запуска — группа `recognize` в DataSetEndpoints (ревью #998).
    // До неё они лежали в группе наборов под правом на чтение, и агент оказался бы прикрыт
    // строже человека: одна и та же операция, два разных ответа на вопрос «можно ли».
    [McpPermission(CorePermissions.DataSetsEdit)]
    [McpServerTool(Name = "recognize_dataset", ReadOnly = false, Destructive = true,
        Title = "Распознать PDF-набор")]
    [Description("""
        Отправляет PDF-набор на распознавание: страницы читает vision-модель, из них извлекаются
        документы и таблицы. Долгая операция — альбом ГОСТ занимает минуты и возвращает jobId (итог
        через get_job); короткий профиль выполняется сразу, и тогда jobId пуст, а completed=true.

        Распознавание заменяет прежний результат. Полное перераспознавание ГОСТ-набора с ручным
        разбиением сотрудник подтверждает в веб-интерфейсе на странице набора данных.

        Отказ «распознавать некому» означает, что ни один движок не настроен или все уличены в
        слепоте — это вопрос к администратору, повторный запуск ничего не изменит.
        """)]
    public async Task<CallToolResult> RecognizeDatasetAsync(
        [Description("Идентификатор набора данных — тот же, что у get_dataset.")] Guid datasetId,
        CancellationToken ct)
    {
        try
        {
            var launch = await launcher.RecognizeFileAsync(datasetId, JobTools.RequireUserId(http), confirm: false, ct);
            return launch is not null ? RecognitionReply(launch) : Refusal("Набор данных не найден.");
        }
        catch (ManualGroupingConflict ex) { return ManualGroupingRefusal(ex); }
        catch (DomainException ex) { return Refusal(ex.Message); }
    }

    private static CallToolResult RecognitionReply(RecognitionLaunch launch)
    {
        if (launch.Blocked is { } block) return Refusal($"[{block.Code}] {block.Message}");
        var started = new RecognitionStarted(launch.JobId, launch.JobId is null);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(started, McpSerialization.ToolOptions) }],
        };
    }

    private static CallToolResult Refusal(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    private static CallToolResult ManualGroupingRefusal(ManualGroupingConflict conflict)
        => Refusal($"[manual_grouping] У набора «{conflict.FileName}» ручное разбиение. " +
            $"Посмотреть разбиение можно в веб-интерфейсе по адресу /datasets/files/{conflict.FileId}/grouping. " +
            "На странице «Наборы данных» соответствующего уровня откройте этот набор, " +
            "нажмите «Распознать заново» и подтвердите действие.");

    [McpPermission(CorePermissions.DataSetsEdit)]
    [McpServerTool(Name = "recognize_source", ReadOnly = false, Destructive = true,
        Title = "Распознать источник набора")]
    [Description("""
        То же распознавание, но по одному источнику набора, а не по всему набору. Годится, когда
        источник помечен устаревшим (stale=true у get_source) и нужно привести его данные в
        соответствие текущему файлу.

        Если операция пересобирает ручное разбиение всего ГОСТ-набора, её подтверждает сотрудник
        в веб-интерфейсе. Перераспознавание отдельной таблицы доступно здесь. Долгая операция
        возвращает jobId.
        """)]
    public async Task<CallToolResult> RecognizeSourceAsync(
        [Description("Идентификатор источника — тот же, что у get_source.")] Guid sourceId,
        CancellationToken ct)
    {
        try
        {
            var launch = await launcher.RecognizeSourceAsync(sourceId, JobTools.RequireUserId(http), confirm: false, ct);
            return launch is not null ? RecognitionReply(launch) : Refusal("Источник не найден.");
        }
        catch (ManualGroupingConflict ex) { return ManualGroupingRefusal(ex); }
        catch (DomainException ex) { return Refusal(ex.Message); }
    }
}
