using BHS.CRG.Application.Common;
using BHS.CRG.Application.Email;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Application.Subscriptions;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Notifications;
using Microsoft.Extensions.Configuration;

namespace BHS.CRG.Infrastructure.Email;

/// <summary>
/// Отправка собранного комплекта (<see cref="DocumentSetOutput"/>) подписчикам комплекта (с учётом
/// наследования). Файл вкладывается, если ≤ порога; иначе — текстовая пометка (+ ссылка на систему,
/// если задан App:PublicUrl). Запускается фоновой задачей (<see cref="Domain.Jobs.JobKind.SendEmail"/>).
/// </summary>
public class DocumentSetEmailService(
    IRepository<DocumentSet> setRepo,
    IRepository<DocumentSetOutput> outputRepo,
    ISubscriptionService subscriptions,
    IBlobStorage blob,
    IEmailSender email,
    INotificationService notifications,
    IConfiguration config)
{
    /// <summary>Порог вложения — крупнее отправляем ссылкой/пометкой (лимиты SMTP реальны).</summary>
    public const long MaxAttachmentBytes = 15L * 1024 * 1024;

    public async Task SendToSubscribersAsync(Guid setId, string? subject, string? body, Guid userId, CancellationToken ct)
    {
        var set = await setRepo.GetByIdAsync(setId, ct) ?? throw new KeyNotFoundException("Комплект не найден.");

        var recipients = await subscriptions.ResolveRecipientsAsync(CatalogScope.Set, setId, ct);
        var emails = recipients.Where(r => r.ValidEmail).Select(r => r.Email!).ToList();
        if (emails.Count == 0)
            throw new InvalidOperationException("Нет подписчиков с валидным email (добавьте подписчиков комплекта/раздела/стройки).");

        var output = (await outputRepo.FindAsync(o => o.SetId == setId, ct)).FirstOrDefault()
            ?? throw new InvalidOperationException("Комплект не собран — соберите его перед отправкой.");

        var bytes = await DownloadAsync(output.BlobPath, ct);
        var attach = bytes.Length <= MaxAttachmentBytes;

        var finalSubject = string.IsNullOrWhiteSpace(subject) ? $"Исполнительная документация — {set.Name}" : subject;
        var finalBody = BuildBody(body, set.Name, attach, bytes.Length);

        var attachments = attach
            ? new[] { new EmailAttachment($"{set.Name}.pdf", bytes, "application/pdf") }
            : null;

        await email.SendAsync(new EmailMessage([], finalSubject, finalBody, attachments, Bcc: emails), ct);

        await notifications.PublishAsync(NotificationSeverity.Info, "Комплект отправлен",
            $"«{set.Name}» отправлен {emails.Count} подписчикам{(attach ? " (вложением)" : " (ссылкой — файл крупный)")}.",
            "Отправка комплекта", userId: userId);
    }

    private string BuildBody(string? body, string setName, bool attach, long size)
    {
        var text = string.IsNullOrWhiteSpace(body)
            ? $"Направляем собранный комплект исполнительной документации «{setName}»."
            : body;

        if (attach)
            return text + "\n\nФайл во вложении.";

        var mb = (size / 1024.0 / 1024.0).ToString("0.0");
        var publicUrl = config["App:PublicUrl"]?.TrimEnd('/');
        var link = string.IsNullOrWhiteSpace(publicUrl) ? "" : $"\nОткрыть систему: {publicUrl}";
        return text + $"\n\nСобранный комплект слишком большой для вложения ({mb} МБ) — откройте комплект «{setName}» в системе и скачайте.{link}";
    }

    private async Task<byte[]> DownloadAsync(string blobPath, CancellationToken ct)
    {
        await using var stream = await blob.DownloadAsync(blobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
