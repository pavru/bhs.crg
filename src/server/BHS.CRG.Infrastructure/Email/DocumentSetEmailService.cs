using BHS.CRG.Application.Common;
using BHS.CRG.Application.Email;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Application.Subscriptions;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Domain.Templates;
using Microsoft.Extensions.Configuration;

namespace BHS.CRG.Infrastructure.Email;

/// <summary>
/// Отправка сгенерированных PDF подписчикам комплекта (с учётом наследования): собранного комплекта
/// (<see cref="DocumentSetOutput"/>) или отдельного документа (его <see cref="GeneratedFile"/>).
/// Файлы вкладываются, если суммарно ≤ порога; иначе — текстовая пометка (+ ссылка, если задан
/// App:PublicUrl). Запускается фоновой задачей (<see cref="Domain.Jobs.JobKind.SendEmail"/>).
/// </summary>
public class DocumentSetEmailService(
    IRepository<DocumentSet> setRepo,
    IRepository<DocumentSetOutput> outputRepo,
    IRepository<DocumentInstance> instanceRepo,
    IRepository<DocumentType> docTypeRepo,
    IRepository<Template> templateRepo,
    ISubscriptionService subscriptions,
    IBlobStorage blob,
    IEmailSender email,
    INotificationService notifications,
    IConfiguration config)
{
    /// <summary>Порог суммарного вложения — крупнее отправляем ссылкой/пометкой (лимиты SMTP реальны).</summary>
    public const long MaxAttachmentBytes = 15L * 1024 * 1024;

    private sealed record Attachment(string Name, byte[] Bytes);

    // ── Комплект (собранный файл) ────────────────────────────────────────────────
    public async Task SendSetToSubscribersAsync(Guid setId, string? subject, string? body, Guid userId, CancellationToken ct)
    {
        var set = await setRepo.GetByIdAsync(setId, ct) ?? throw new KeyNotFoundException("Комплект не найден.");
        var output = (await outputRepo.FindAsync(o => o.SetId == setId, ct)).FirstOrDefault()
            ?? throw new InvalidOperationException("Комплект не собран — соберите его перед отправкой.");

        var files = new List<Attachment> { new($"{set.Name}.pdf", await DownloadAsync(output.BlobPath, ct)) };
        await DeliverAsync(setId, subject, body,
            defaultSubject: $"Исполнительная документация — {set.Name}",
            defaultBody: $"Направляем собранный комплект исполнительной документации «{set.Name}».",
            itemName: $"Комплект «{set.Name}»", notifyTitle: "Комплект отправлен",
            openHint: $"откройте комплект «{set.Name}» в системе и скачайте", files, userId, ct);
    }

    // ── Отдельный документ (его сгенерированные PDF) ──────────────────────────────
    public async Task SendDocumentToSubscribersAsync(Guid instanceId, string? subject, string? body, Guid userId, CancellationToken ct)
    {
        var instance = await instanceRepo.GetByIdAsync(instanceId, ct) ?? throw new KeyNotFoundException("Документ не найден.");
        var pdfs = instance.GeneratedFiles.Where(f => f.Format == OutputFormat.Pdf).ToList();
        if (pdfs.Count == 0)
            throw new InvalidOperationException("У документа нет сгенерированных PDF — сначала сгенерируйте.");

        var docType = await docTypeRepo.GetByIdAsync(instance.DocumentTypeId, ct);
        var docName = instance.Name ?? docType?.Name ?? "Документ";

        // Имена вложений: для нескольких PDF (мульти-шаблон) добавляем имя шаблона.
        var files = new List<Attachment>();
        var idx = 0;
        foreach (var f in pdfs)
        {
            var bytes = await DownloadAsync(f.BlobPath, ct);
            string name;
            if (pdfs.Count == 1) name = $"{docName}.pdf";
            else
            {
                var tpl = f.TemplateId is { } tid ? await templateRepo.GetByIdAsync(tid, ct) : null;
                name = $"{docName} - {tpl?.Name ?? $"вариант {++idx}"}.pdf";
            }
            files.Add(new(name, bytes));
        }

        await DeliverAsync(instance.DocumentSetId, subject, body,
            defaultSubject: $"Исполнительная документация — {docName}",
            defaultBody: $"Направляем документ «{docName}» исполнительной документации.",
            itemName: $"Документ «{docName}»", notifyTitle: "Документ отправлен",
            openHint: $"откройте документ «{docName}» в системе и скачайте", files, userId, ct);
    }

    // ── Общая доставка: резолв получателей, вложение/пометка по размеру, отправка, уведомление ──
    private async Task DeliverAsync(Guid recipientsSetId, string? subject, string? body,
        string defaultSubject, string defaultBody, string itemName, string notifyTitle, string openHint,
        List<Attachment> files, Guid userId, CancellationToken ct)
    {
        var recipients = await subscriptions.ResolveRecipientsAsync(CatalogScope.Set, recipientsSetId, ct);
        var emails = recipients.Where(r => r.ValidEmail).Select(r => r.Email!).ToList();
        if (emails.Count == 0)
            throw new InvalidOperationException("Нет подписчиков с валидным email (добавьте подписчиков комплекта/раздела/стройки).");

        var total = files.Sum(f => (long)f.Bytes.Length);
        var attach = total <= MaxAttachmentBytes;

        var text = string.IsNullOrWhiteSpace(body) ? defaultBody : body;
        string finalBody;
        if (attach)
            finalBody = text + (files.Count == 1 ? "\n\nФайл во вложении." : "\n\nФайлы во вложении.");
        else
        {
            var mb = (total / 1024.0 / 1024.0).ToString("0.0");
            var publicUrl = config["App:PublicUrl"]?.TrimEnd('/');
            var link = string.IsNullOrWhiteSpace(publicUrl) ? "" : $"\nОткрыть систему: {publicUrl}";
            finalBody = text + $"\n\nФайлы слишком большие для вложения ({mb} МБ) — {openHint}.{link}";
        }

        var attachments = attach
            ? files.Select(f => new EmailAttachment(f.Name, f.Bytes, "application/pdf")).ToArray()
            : null;

        var finalSubject = string.IsNullOrWhiteSpace(subject) ? defaultSubject : subject;
        await email.SendAsync(new EmailMessage([], finalSubject, finalBody, attachments, Bcc: emails), ct);

        await notifications.PublishAsync(NotificationSeverity.Info, notifyTitle,
            $"{itemName} отправлен {emails.Count} подписчикам{(attach ? " (вложением)" : " (ссылкой — файлы крупные)")}.",
            "Отправка почты", userId: userId);
    }

    private async Task<byte[]> DownloadAsync(string blobPath, CancellationToken ct)
    {
        await using var stream = await blob.DownloadAsync(blobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
