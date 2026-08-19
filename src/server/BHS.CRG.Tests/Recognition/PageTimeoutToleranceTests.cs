using BHS.CRG.Application.QualityDocs;

namespace BHS.CRG.Tests.Recognition;

/// <summary>
/// Таймаут ОДНОЙ страницы не уносит весь постраничный прогон (issue #797).
///
/// До классификации таймаута эту роль случайно играл сырой <c>TaskCanceledException</c>: он не был
/// ни <c>RecognitionUnavailable</c>, ни <c>RecognitionLimit</c>, проваливался в ветку
/// <c>OperationCanceledException</c> и оставлял поля страницы пустыми, не мешая остальным. Обернув
/// таймаут в <c>RecognitionUnavailableException</c>, эту терпимость легко отобрать: одна медленная
/// страница из тридцати начала бы прекращать перераспознавание, выбрасывая уже сделанную работу.
///
/// Отсюда два инварианта, каждый из которых компилятор НЕ стережёт.
/// </summary>
public class PageTimeoutToleranceTests
{
    [Fact]
    public void Timeout_IsAKindOfUnavailable()
    {
        // Цепочка движков ловит базовый тип: если наследование разорвать, таймаут перестанет
        // означать «пробуй следующий движок» и снова полетит мимо всех обработчиков.
        Assert.IsAssignableFrom<RecognitionUnavailableException>(new RecognitionTimeoutException("x"));
    }

    [Fact]
    public void PerPageRerecognition_CatchesTimeoutBeforeGeneralUnavailability()
    {
        // Ветки написаны с `when`-фильтрами, и на них компилятор про «наследник после базового»
        // не ругается (CS0160 выдаётся только для голых catch). То есть переставить их местами
        // можно молча — а поведение поменяется на противоположное.
        var src = File.ReadAllText(Path.Combine(
            SolutionDir, "BHS.CRG.Infrastructure/DataSets/DataSetPdfRecognitionService.cs"));

        var method = src.IndexOf("public async Task<GostGroupingDto?> RecognizeDocumentAsync", StringComparison.Ordinal);
        Assert.True(method >= 0, "Не найден RecognizeDocumentAsync — метод переименован; проверять инвариант стало негде.");

        var timeout = src.IndexOf("RecognitionTimeoutException", method, StringComparison.Ordinal);
        var general = src.IndexOf("RecognitionUnavailableException or RecognitionLimitException", method, StringComparison.Ordinal);

        Assert.True(timeout >= 0,
            "В перераспознавании нет ветки RecognitionTimeoutException — таймаут страницы снова уносит весь прогон.");
        Assert.True(general >= 0, "Не найдена ветка общей недоступности — структура обработки изменилась.");
        Assert.True(timeout < general,
            "Ветка таймаута должна идти ПЕРЕД общей недоступностью: таймаут — её наследник, "
            + "и снизу он недостижим, то есть одна медленная страница прекратит перераспознавание.");
    }

    private static string SolutionDir { get; } = FindSolutionDir();

    private static string FindSolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BHS.CRG.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Не найден каталог решения (BHS.CRG.slnx) выше " + AppContext.BaseDirectory +
                " — тест читает исходники и без них проверять нечего.");
    }
}
