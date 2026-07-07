using BHS.CRG.Application.Email;
using BHS.CRG.Application.Settings;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace BHS.CRG.Infrastructure.Email;

/// <summary>
/// Отправка почты через MailKit поверх SMTP-настроек (<see cref="SmtpSettings"/> из БД-настроек).
/// SmtpClient — новый на каждую отправку (MailKit-клиент не потокобезопасен и не переиспользуется).
/// </summary>
public class MailKitEmailSender(IIntegrationSettings settings) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var smtp = (await settings.GetEffectiveAsync(ct)).Smtp;
        if (!smtp.Enabled || string.IsNullOrWhiteSpace(smtp.Host) || string.IsNullOrWhiteSpace(smtp.From))
            throw new EmailNotConfiguredException("SMTP не настроен или выключен (Настройки → Почта).");
        if (message.To.Count == 0)
            throw new ArgumentException("Не указан ни один получатель.");

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(smtp.FromName ?? smtp.From, smtp.From));
        foreach (var to in message.To)
            mime.To.Add(MailboxAddress.Parse(to));
        mime.Subject = message.Subject;

        var builder = new BodyBuilder { TextBody = message.Body };
        if (message.Attachments is not null)
            foreach (var a in message.Attachments)
                builder.Attachments.Add(a.FileName, a.Content, ContentType.Parse(a.ContentType));
        mime.Body = builder.ToMessageBody();

        using var client = new SmtpClient();
        // UseSsl: STARTTLS/автовыбор при 587, неявный SSL при 465; иначе — без шифрования.
        var security = smtp.UseSsl
            ? (smtp.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls)
            : SecureSocketOptions.None;
        await client.ConnectAsync(smtp.Host, smtp.Port, security, ct);
        if (!string.IsNullOrWhiteSpace(smtp.User))
            await client.AuthenticateAsync(smtp.User, smtp.Password, ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
    }
}
