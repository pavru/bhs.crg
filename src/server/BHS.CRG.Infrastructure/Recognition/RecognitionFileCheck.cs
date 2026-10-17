using PDFtoImage;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Читается ли файл вообще — ДО похода к движку (issue #1343).
///
/// <para>Раньше битый PDF уходил движку как есть. Облачный отвечал 400, и человек видел
/// «распознавание недоступно» — запрос платный, ответ медленный, а слова про сервис, а не про файл.
/// Ollama называла тот же случай честно, но только потому, что сама растеризует PDF. Проверка здесь —
/// одна для всех движков: бесплатная, быстрая и одними словами.</para>
///
/// <para>Открывает документ тот же PDFium, что растеризует его для Ollama: «открылся» здесь значит
/// «откроется и там». Признака <c>%PDF-</c> в начале мало — его несёт и файл живых прогонов, внутри
/// которого строка текста (#1344). Изображения не проверяются: их разбирает сам движок, и отказ
/// поставщика о негодном изображении назван в движке.</para>
/// </summary>
public static class RecognitionFileCheck
{
    /// <summary>Чем бывает нечитаемый PDF — одна фраза на проверку и на отказ поставщика.</summary>
    public const string Unreadable = "файл повреждён, защищён паролем или это не PDF";

    /// <summary>
    /// Почему файл не прочитать, или <c>null</c>, если читается. Исключение разборщика — в
    /// <paramref name="cause" />: его сообщение чужое и называет внутренности (issue #1050), место ему
    /// в журнале, а не в тексте отказа.
    /// </summary>
    public static string? WhyUnreadable(byte[] file, string mimeType, out Exception? cause)
    {
        cause = null;
        if (file.Length == 0) return "Файл пуст.";
        if (!RecognitionShared.IsPdf(mimeType)) return null;

        int pages;
        try
        {
            pages = Conversion.GetPageCount(file);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            cause = ex;
            return $"PDF не открывается: {Unreadable}.";
        }
        return pages > 0 ? null : "В PDF нет ни одной страницы.";
    }
}
