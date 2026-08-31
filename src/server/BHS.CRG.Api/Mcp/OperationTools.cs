using System.ComponentModel;
using BHS.CRG.Application.Jobs;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Common;
using ModelContextProtocol;
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
/// защиты — «по этой цели уже идёт», предполёт движка, подтверждение перезаписи ручной правки —
/// живут в нём, а не здесь: HTTP-эндпоинты зовут то же самое, и агент не получает входа в обход
/// того, чем прикрыт человек.
///
/// Отказы приходят исключениями и превращаются в <see cref="McpException" /> с их собственным
/// текстом: агенту нужно знать, ЧТО не так, а не только что не вышло.
/// </summary>
[McpServerToolType]
public class OperationTools(IOperationLauncher launcher, IHttpContextAccessor http)
{
    // Idempotent НЕ ставим ни одному из трёх: подсказку об идемпотентности клиенты используют для
    // автоматического повтора при обрыве связи, а повтор здесь — вторая задача либо отказ. У
    // распознавания цена выше: повторённый вызов несёт с собой и подтверждение перезаписи.
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

    [McpServerTool(Name = "recognize_dataset", ReadOnly = false, Destructive = true,
        Title = "Распознать PDF-набор")]
    [Description("""
        Отправляет PDF-набор на распознавание: страницы читает vision-модель, из них извлекаются
        документы и таблицы. Долгая операция — альбом ГОСТ занимает минуты и возвращает jobId (итог
        через get_job); короткий профиль выполняется сразу, и тогда jobId пуст, а completed=true.

        Распознавание ЗАМЕНЯЕТ прежний результат по этому набору. Если разбиение на документы
        правил человек вручную, запуск отвергается — пока не передан
        confirmOverwriteManualGrouping. Не передавайте его по своему решению: восстановить стёртую
        ручную правку нельзя, и спросить об этом нужно у человека.

        Отказ «распознавать некому» означает, что ни один движок не настроен или все уличены в
        слепоте — это вопрос к администратору, повторный запуск ничего не изменит.
        """)]
    public async Task<RecognitionStarted> RecognizeDatasetAsync(
        [Description("Идентификатор набора данных — тот же, что у get_dataset.")] Guid datasetId,
        CancellationToken ct,
        [Description("""
            Подтверждение перезаписи РУЧНОЙ правки разбиения на документы. Стирает работу человека
            безвозвратно; по умолчанию false.
            """)] bool confirmOverwriteManualGrouping = false)
    {
        try
        {
            var launch = await launcher.RecognizeFileAsync(
                datasetId, JobTools.RequireUserId(http), confirmOverwriteManualGrouping, ct)
                ?? throw new McpException("Набор данных не найден.");
            if (launch.Blocked is { } blocked) throw Refusal(blocked);
            return new RecognitionStarted(launch.JobId, launch.JobId is null);
        }
        catch (DomainException ex) { throw new McpException(ex.Message); }
    }

    /// <summary>
    /// Отказ движка — вместе с машинным кодом. Без него агенту остаётся разбирать русскую фразу,
    /// чтобы отличить «ни один движок не настроен» (вопрос к администратору) от «модель не видит
    /// картинок» (вопрос к выбору модели), — а по HTTP этот код отдаётся отдельным полем.
    /// </summary>
    private static McpException Refusal(RecognitionBlock block)
        => new($"[{block.Code}] {block.Message}");

    [McpServerTool(Name = "recognize_source", ReadOnly = false, Destructive = true,
        Title = "Распознать источник набора")]
    [Description("""
        То же распознавание, но по одному источнику набора, а не по всему набору. Годится, когда
        источник помечен устаревшим (stale=true у get_source) и нужно привести его данные в
        соответствие текущему файлу.

        Как и recognize_dataset: результат заменяет прежний, ручная правка разбиения защищена
        подтверждением, долгий профиль возвращает jobId.
        """)]
    public async Task<RecognitionStarted> RecognizeSourceAsync(
        [Description("Идентификатор источника — тот же, что у get_source.")] Guid sourceId,
        CancellationToken ct,
        [Description("""
            Подтверждение перезаписи РУЧНОЙ правки разбиения на документы. Стирает работу человека
            безвозвратно; по умолчанию false.
            """)] bool confirmOverwriteManualGrouping = false)
    {
        try
        {
            var launch = await launcher.RecognizeSourceAsync(
                sourceId, JobTools.RequireUserId(http), confirmOverwriteManualGrouping, ct)
                ?? throw new McpException("Источник не найден.");
            if (launch.Blocked is { } blocked) throw Refusal(blocked);
            return new RecognitionStarted(launch.JobId, launch.JobId is null);
        }
        catch (DomainException ex) { throw new McpException(ex.Message); }
    }
}
