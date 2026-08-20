using BHS.CRG.Domain.Notifications;
using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.Recognition;

/// <summary>
/// Чем прогон отчитывается о себе (issue #801). Проверяется решение, а не текст: назвать прогон
/// провалом или пропусками — единственный вывод, который тут делается, и прежний признак полного
/// провала был НЕДОСТИЖИМ, оставаясь на вид рабочим. Нашли это чтением, потому что смотреть прогоном
/// было не на что; эти тесты и есть «на что смотреть».
/// </summary>
public class GostRecognitionReportTests
{
    private static (NotificationSeverity Severity, string Title, string Message) Describe(
        int failedPages = 0, string? reason = null, bool nothingRecognized = false,
        int documents = 4, int pages = 16, int failedSplits = 0, int invalidatedTables = 0)
        => DataSetPdfRecognitionService.DescribeGostResult(
            documents, pages, failedPages, reason, nothingRecognized, failedSplits, invalidatedTables);

    [Fact]
    public void CleanRun_IsInfo()
    {
        var (severity, title, msg) = Describe();
        Assert.Equal(NotificationSeverity.Info, severity);
        Assert.Equal("Распознавание групп листов PDF завершено", title);
        Assert.Equal("Распознано: 4 документов, 16 листов.", msg);
    }

    [Fact]
    public void SomePagesUnanswered_IsWarning()
    {
        var (severity, _, msg) = Describe(failedPages: 3);
        Assert.Equal(NotificationSeverity.Warning, severity);
        // «Не ответила», а не «не распозналось»: пустой штамп — законный исход, и обвинять модель в
        // нём не за что. Счётчик растёт только на отказах, то есть считает именно отсутствие ответа.
        Assert.Contains("Модель не ответила по листам: 3.", msg);
    }

    [Fact]
    public void NothingRecognized_IsError_EvenWhenFirstPageSucceeded()
    {
        // Случай, который РЕАЛЬНО происходит и который прежнее условие пропускало: первый лист
        // прошёл, остальные отвалились. По существу это провал, а не «завершено с пропусками».
        var (severity, title, _) = Describe(failedPages: 199, pages: 200, nothingRecognized: true);
        Assert.Equal(NotificationSeverity.Error, severity);
        Assert.Equal("Распознавание PDF не удалось", title);
    }

    [Fact]
    public void EmptyResultWithoutFailures_IsNotAFailure()
    {
        // Альбом без штампов вовсе: строки пусты, но отказов не было — модель честно ответила
        // «ничего нет». Объявить это провалом значило бы кричать там, где всё правильно.
        var (severity, _, _) = Describe(failedPages: 0, nothingRecognized: true);
        Assert.Equal(NotificationSeverity.Info, severity);
    }

    [Fact]
    public void FailureReason_IsNamed_AndGetsItsFullStop()
    {
        // Счётчик без причины не говорит ни виновника, ни лекарства — то же молчание, только с числом.
        var withoutDot = Describe(failedPages: 16, reason: "Ollama ответил 500").Message;
        Assert.Contains("Причина: Ollama ответил 500.", withoutDot);

        // Точку не удваиваем: часть причин приходит уже с ней.
        var withDot = Describe(failedPages: 16, reason: "движок не ответил за отведённый срок.").Message;
        Assert.Contains("Причина: движок не ответил за отведённый срок.", withDot);
        Assert.DoesNotContain("..", withDot);
    }

    [Fact]
    public void NoReason_NoEmptyClause()
    {
        Assert.DoesNotContain("Причина", Describe(failedPages: 2, reason: "   ").Message);
    }

    [Fact]
    public void SplitsAndTables_KeepWarning_WithoutFailedPages()
    {
        var (severity, title, msg) = Describe(failedSplits: 2, invalidatedTables: 1);
        Assert.Equal(NotificationSeverity.Warning, severity);
        Assert.Equal("Распознавание групп листов PDF завершено", title);
        Assert.Contains("Документов без файла: 2.", msg);
        Assert.Contains("Табличные источники (1) инвалидированы", msg);
    }
}
