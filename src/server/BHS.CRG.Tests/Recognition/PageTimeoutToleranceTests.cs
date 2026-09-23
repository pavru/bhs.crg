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
        // Ищем по ВСЕМ частям сервиса, а не по одному имени файла: класс разнесён на partial-части
        // (issue #1022), и сторож, прибитый к имени файла, ослеп бы молча при следующем разрезе —
        // именно это с ним и случилось. Требуем ровно одно вхождение: ноль значит «проверять негде»,
        // больше одного — что инвариант размножился и проверять надо каждое.
        var parts = Directory.GetFiles(
            Path.Combine(SolutionDir, "BHS.CRG.Infrastructure", "DataSets"),
            "DataSetPdfRecognitionService*.cs");
        const string signature = "public async Task<GostGroupingDto?> RecognizeDocumentAsync";
        var hits = parts.Where(f => File.ReadAllText(f).Contains(signature, StringComparison.Ordinal)).ToArray();

        Assert.True(hits.Length == 1,
            $"RecognizeDocumentAsync найден в {hits.Length} файлах из {parts.Length} частей сервиса "
            + "(ожидалось ровно одно). Ноль — метод переименован и проверять инвариант негде; "
            + "больше одного — инвариант размножился, и стеречь надо каждое вхождение.");

        var src = File.ReadAllText(hits[0]);
        var method = src.IndexOf(signature, StringComparison.Ordinal);

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
