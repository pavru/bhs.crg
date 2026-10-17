using PDFtoImage.Exceptions;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Читается ли файл вообще — ДО похода к движку (issue #1343).
///
/// <para>Раньше битый PDF уходил движку как есть. Облачный отвечал 400, и человек видел
/// «распознавание недоступно» — запрос платный, ответ медленный, а слова про сервис, а не про файл.
/// Ollama называла тот же случай честно, но только потому, что сама растеризует PDF. Проверка здесь —
/// одна для всех движков: бесплатная, быстрая и одними словами.</para>
///
/// <para>Страницы считает <see cref="PdfRasterizer.PageCount" /> — то же место, которым растеризатор
/// сверяет предел: «открылся здесь» значит «откроется и там». Признака <c>%PDF-</c> в начале мало —
/// его несёт и файл живых прогонов, внутри которого строка текста (#1344). Изображения не
/// проверяются: их разбирает сам движок.</para>
///
/// <para>⚠️ Отказом о файле считается только отказ РАЗБОРА документа (<see cref="PdfException" /> и
/// наследники: не тот формат, пароль, неизвестная схема защиты). Сбой окружения — не загрузилась
/// нативная библиотека, не хватило памяти — о файле ничего не говорит (ревью PR #1398): облачным
/// движкам PDFium не нужен вовсе, и назвать им исправный файл битым значило бы выключить
/// распознавание PDF на всей установке, отправив людей искать «исправный файл».</para>
/// </summary>
public static class RecognitionFileCheck
{
    /// <summary>Чем бывает нечитаемый PDF — одна фраза на все места.</summary>
    public const string Unreadable = "файл повреждён, защищён паролем или это не PDF";

    /// <summary>Исход проверки.</summary>
    /// <param name="Why">Почему файл не прочитать; <c>null</c> — читается или проверить не удалось.</param>
    /// <param name="Cause">
    /// Исключение разборщика или окружения — для журнала: его сообщение чужое и называет внутренности
    /// (issue #1050). При <paramref name="Why" /> = <c>null</c> и непустом <c>Cause</c> проверка не
    /// выполнена, и файл уходит движкам как есть — как до неё.
    /// </param>
    public readonly record struct Outcome(string? Why, Exception? Cause);

    /// <summary>
    /// Разбор — на пуле через <c>Task.Run</c>, как у растеризатора: PDFium синхронен, а документ бывает
    /// на десятки мегабайт (ревью PR #1398).
    /// </summary>
    public static async Task<Outcome> CheckAsync(byte[] file, string mimeType, CancellationToken ct)
    {
        if (file.Length == 0) return new("Файл пуст.", null);
        if (!RecognitionShared.IsPdf(mimeType)) return default;

        try
        {
            var pages = await Task.Run(() => PdfRasterizer.PageCount(file), ct);
            return pages > 0 ? default : new(NoPages, null);
        }
        catch (PdfException ex)
        {
            return new($"PDF не открывается: {Unreadable}.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(null, ex);
        }
    }

    /// <summary>Документ открылся, а страниц в нём нет — текст один и для растеризатора Ollama.</summary>
    public const string NoPages = "В PDF нет ни одной страницы.";
}
