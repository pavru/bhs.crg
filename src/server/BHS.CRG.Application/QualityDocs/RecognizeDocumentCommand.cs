using BHS.CRG.Application.Common;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Domain.Schema;
using MediatR;

namespace BHS.CRG.Application.QualityDocs;

/// <summary>
/// Скачивает скан из blob и извлекает реквизиты по списку полей через распознаватель.
/// БД не меняет — возвращает извлечённые значения для предзаполнения формы (пользователь подтверждает).
/// </summary>
/// <param name="PromptBuilder">Необязательный кастомный промпт (см. IDocumentRecognizer) — Api-слой
/// выбирает конкретную функцию (например, RecognitionShared.BuildTitleBlockPrompt для штампа
/// чертежа), Application только прокидывает делегат дальше, не зная о его реализации.</param>
public record RecognizeDocumentCommand(
    string BlobPath, string MimeType, IReadOnlyList<RecognitionField> Fields,
    Guid? UserId = null, bool Notify = true,
    Func<IReadOnlyList<RecognitionField>, string>? PromptBuilder = null) : IRequest<RecognitionResult>;

public class RecognizeDocumentHandler(
    IBlobStorage blobStorage,
    IDocumentRecognizer recognizer,
    IMetadataExtractor metadataExtractor,
    INotificationService notifications
) : IRequestHandler<RecognizeDocumentCommand, RecognitionResult>
{
    public async Task<RecognitionResult> Handle(RecognizeDocumentCommand cmd, CancellationToken ct)
    {
        try
        {
            await using var stream = await blobStorage.DownloadAsync(cmd.BlobPath, ct);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            var result = await recognizer.RecognizeAsync(bytes, cmd.MimeType, cmd.Fields, cmd.PromptBuilder, ct: ct);

            // Служебные вызовы (напр. классификация типа) не уведомляют и не считают страницы.
            if (!cmd.Notify) return result;

            // Число страниц берём из файла (надёжнее LLM) — для поля с тэгом doc.pageCount.
            var pageCount = ComputePageCount(bytes, cmd.MimeType);

            // Ноль полей — предупреждение (issue #803): человек принёс скан документа и попросил
            // прочитать его реквизиты; ответ «извлечено 0 из 40» под заголовком «завершено» говорит
            // ему, что всё прошло хорошо. Ни одно поле при этом не заполнено.
            var nothing = result.Values.Count == 0;
            await notifications.PublishAsync(
                nothing ? NotificationSeverity.Warning : NotificationSeverity.Info,
                nothing ? "Реквизиты не распознаны" : "Распознавание завершено",
                nothing
                    ? $"Модель не нашла в документе ни одного из {cmd.Fields.Count} реквизитов. Проверьте качество скана."
                    : $"Извлечено полей: {result.Values.Count} из {cmd.Fields.Count}.",
                "Распознавание", userId: cmd.UserId, ct: ct);
            return result with { PageCount = pageCount };
        }
        // Отмену пользователем не объявляем ошибкой: уведомлять некого (запрос уже брошен), а
        // публиковать его тем же `ct` — значит уронить публикацию и подменить причину (issue #797).
        // Таймаут движка сюда доходит уже нашим RecognitionUnavailableException и уведомляет как надо.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (cmd.Notify)
                await notifications.PublishAsync(NotificationSeverity.Error, "Ошибка распознавания",
                    ex.Message, "Распознавание", userId: cmd.UserId, ct: ct);
            throw;
        }
    }

    private int? ComputePageCount(byte[] bytes, string mimeType)
    {
        if (mimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            var meta = metadataExtractor.Extract(bytes, isPdf: true, null);
            return meta.TryGetValue(FunctionalTag.DocPageCount, out var v) && v is int n ? n : null;
        }
        return mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? 1 : null;
    }
}
