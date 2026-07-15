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

/// <summary>Не задан публичный адрес приложения (App:PublicUrl) — ссылку в письме собрать нельзя.
/// Отдаём как понятную ошибку инициатору действия, а НЕ шлём пользователю письмо без ссылки.</summary>
public class AppUrlNotConfiguredException()
    : Exception("Не задан адрес приложения (App:PublicUrl) — ссылки в письмах недоступны. " +
                "Задайте его в конфигурации (переменная APP_PUBLIC_URL) и повторите.");

/// <summary>Транспорт исходящей почты. Реализация — MailKit поверх SMTP-настроек (Настройки → Почта).</summary>
public interface IEmailSender
{
    /// <summary>Отправляет письмо через настроенный SMTP. Бросает <see cref="EmailNotConfiguredException"/>,
    /// если SMTP не настроен, или иное исключение при ошибке соединения/отправки.</summary>
    Task SendAsync(EmailMessage message, CancellationToken ct = default);

    /// <summary>Проверяет ЗАДАННЫЕ настройки (соединение + аутентификация), НЕ отправляя письмо. Для
    /// проверки формы до сохранения. Бросает при ошибке подключения/аутентификации.</summary>
    Task TestConnectionAsync(Settings.SmtpSettings smtp, CancellationToken ct = default);
}
