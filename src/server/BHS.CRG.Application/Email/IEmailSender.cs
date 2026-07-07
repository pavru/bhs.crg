namespace BHS.CRG.Application.Email;

public record EmailAttachment(string FileName, byte[] Content, string ContentType);

/// <summary>Одно письмо: получатели (To и/или скрытые Bcc) + тема + текст (+ вложения). Тонкий
/// транспортный контракт — оркестрация (резолв получателей, сборка контента/вложений) живёт выше,
/// не в отправителе. Для рассылок адреса кладут в Bcc, чтобы получатели не видели друг друга.</summary>
public record EmailMessage(
    IReadOnlyList<string> To, string Subject, string Body,
    IReadOnlyList<EmailAttachment>? Attachments = null, IReadOnlyList<string>? Bcc = null);

/// <summary>SMTP не сконфигурирован (нет host/from или выключен) — отдаём как понятную ошибку, не 500.</summary>
public class EmailNotConfiguredException(string message) : Exception(message);

/// <summary>Транспорт исходящей почты. Реализация — MailKit поверх SMTP-настроек (Настройки → Почта).</summary>
public interface IEmailSender
{
    /// <summary>Отправляет письмо через настроенный SMTP. Бросает <see cref="EmailNotConfiguredException"/>,
    /// если SMTP не настроен, или иное исключение при ошибке соединения/отправки.</summary>
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}
