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
        var bcc = message.Bcc ?? [];
        if (message.To.Count == 0 && bcc.Count == 0)
            throw new ArgumentException("Не указан ни один получатель.");

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(smtp.FromName ?? smtp.From, smtp.From));
        foreach (var to in message.To)
            mime.To.Add(MailboxAddress.Parse(to));
        foreach (var b in bcc)
            mime.Bcc.Add(MailboxAddress.Parse(b));
        // Рассылка (только Bcc) — ставим отправителя в To, чтобы был валидный заголовок, адреса скрыты.
        if (message.To.Count == 0)
            mime.To.Add(new MailboxAddress(smtp.FromName ?? smtp.From, smtp.From));
        mime.Subject = message.Subject;

        var builder = new BodyBuilder { TextBody = message.Body };
        if (message.Attachments is not null)
            foreach (var a in message.Attachments)
                builder.Attachments.Add(a.FileName, a.Content, ContentType.Parse(a.ContentType));
        mime.Body = builder.ToMessageBody();

        using var client = new SmtpClient();
        await ConnectAndAuthAsync(client, smtp, ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
    }

    public async Task TestConnectionAsync(SmtpSettings smtp, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(smtp.Host))
            throw new EmailNotConfiguredException("Не задан SMTP-сервер (host).");
        using var client = new SmtpClient();
        await ConnectAndAuthAsync(client, smtp, ct);
        await client.DisconnectAsync(true, ct); // соединение+аутентификация прошли — письмо не шлём
    }

    private static async Task ConnectAndAuthAsync(SmtpClient client, SmtpSettings smtp, CancellationToken ct)
    {
        // UseSsl: STARTTLS при 587, неявный SSL при 465; иначе — без шифрования.
        var security = smtp.UseSsl
            ? (smtp.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls)
            : SecureSocketOptions.None;
        await client.ConnectAsync(smtp.Host, smtp.Port, security, ct);
        if (!string.IsNullOrWhiteSpace(smtp.User))
            await client.AuthenticateAsync(smtp.User, smtp.Password, ct);
    }
}
