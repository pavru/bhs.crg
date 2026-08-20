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
        int documents = 4, int pages = 16, int failedSplits = 0, int invalidatedTables = 0, string? engine = null)
        => DataSetPdfRecognitionService.DescribeGostResult(
            documents, pages, failedPages, reason, nothingRecognized, failedSplits, invalidatedTables, engine);

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
    public void NothingRecognized_IsError()
    {
        // Достижимый вид полного провала: первый лист ответил пустотой (лист без штампа — законно),
        // остальные отвалились по отказу. Прежнее условие «failedPages == pageCount» сюда не
        // попадало вовсе: провал ПЕРВОГО листа прекращает прогон раньше уведомления, поэтому
        // счётчик с числом листов не сравняется никогда.
        var (severity, title, _) = Describe(failedPages: 199, pages: 200, nothingRecognized: true);
        Assert.Equal(NotificationSeverity.Error, severity);
        Assert.Equal("Распознавание PDF не удалось", title);
    }

    [Fact]
    public void SomethingRecognized_StaysWarning_EvenIfMostPagesFailed()
    {
        // Граница, которую стоит держать на виду: первый лист распознался, следующие 199 отвалились —
        // это Warning, потому что в наборе что-то появилось. Порог вроде «девяносто процентов
        // отказов» пришлось бы выдумать, а причину отказа текст теперь называет и без него.
        var (severity, _, msg) = Describe(failedPages: 199, pages: 200, nothingRecognized: false,
            reason: "Ollama: модель не принимает изображения");
        Assert.Equal(NotificationSeverity.Warning, severity);
        Assert.Contains("Причина: Ollama: модель не принимает изображения.", msg);
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
    public void EngineIsNamed_WhenKnown()
    {
        // «Почему у меня плохо распозналось» спрашивают после прогона, и ответ начинается с того,
        // кем он делался. При удачном прогоне это справка, при неудачном — первое, что нужно знать.
        Assert.Contains("Распознавал: Ollama · qwen2.5vl:7b.", Describe(engine: "Ollama · qwen2.5vl:7b").Message);
        // Движок неизвестен (ни один лист не удался) — пустой фразы в тексте быть не должно.
        Assert.DoesNotContain("Распознавал", Describe(failedPages: 3).Message);
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
