using BHS.CRG.Application.Email;
using Microsoft.Extensions.Configuration;

namespace BHS.CRG.Infrastructure.Email;

/// <summary>
/// Письма учётной записи (issue #148): сброс пароля, подтверждение почты. Собирает ссылку из
/// <c>App:PublicUrl</c> + url-encoded токен Identity и отправляет через <see cref="IEmailSender"/>.
/// Оркестрация (генерация токена) — в endpoint'ах, здесь только сборка письма и отправка.
/// </summary>
public class AccountEmailService(IEmailSender email, IConfiguration config)
{
    /// <summary>Письмо со ссылкой сброса пароля (токен действует ~1 час).</summary>
    public async Task SendPasswordResetAsync(string toEmail, string token, CancellationToken ct = default)
    {
        var link = BuildLink("reset-password", toEmail, token);
        var body = link is null
            ? "Вы запросили сброс пароля в BHS.CRG, но адрес приложения не настроен (App:PublicUrl). " +
              "Обратитесь к администратору системы."
            : "Вы запросили сброс пароля в BHS.CRG.\n\n" +
              $"Ссылка для сброса (действует 1 час):\n{link}\n\n" +
              "Если вы не запрашивали сброс — просто проигнорируйте это письмо, пароль останется прежним.";
        await email.SendAsync(new EmailMessage([toEmail], "Сброс пароля — BHS.CRG", body), ct);
    }

    /// <summary>Письмо с подтверждением адреса (токен действует ~24 часа).</summary>
    public async Task SendEmailConfirmationAsync(string toEmail, string token, CancellationToken ct = default)
    {
        var link = BuildLink("confirm-email", toEmail, token);
        var body = link is null
            ? "Для подтверждения адреса в BHS.CRG нужен адрес приложения (App:PublicUrl) — обратитесь к администратору."
            : "Подтвердите адрес электронной почты для BHS.CRG.\n\n" +
              $"Ссылка для подтверждения (действует 24 часа):\n{link}\n\n" +
              "Если вы не заводили учётную запись — просто проигнорируйте это письмо.";
        await email.SendAsync(new EmailMessage([toEmail], "Подтверждение адреса — BHS.CRG", body), ct);
    }

    /// <summary>Письмо на НОВЫЙ адрес для подтверждения смены email (токен действует ~24 часа).</summary>
    public async Task SendEmailChangeAsync(Guid userId, string newEmail, string token, CancellationToken ct = default)
    {
        var publicUrl = config["App:PublicUrl"]?.TrimEnd('/');
        var link = string.IsNullOrEmpty(publicUrl) ? null
            : $"{publicUrl}/confirm-email-change?uid={Uri.EscapeDataString(userId.ToString())}" +
              $"&email={Uri.EscapeDataString(newEmail)}&token={Uri.EscapeDataString(token)}";
        var body = link is null
            ? "Для смены адреса в BHS.CRG нужен адрес приложения (App:PublicUrl) — обратитесь к администратору."
            : "Запрошена смена адреса входа в BHS.CRG на этот email.\n\n" +
              $"Ссылка для подтверждения (действует 24 часа):\n{link}\n\n" +
              "Если вы не запрашивали смену — проигнорируйте это письмо, адрес останется прежним.";
        await email.SendAsync(new EmailMessage([newEmail], "Смена адреса входа — BHS.CRG", body), ct);
    }

    /// <summary>Собирает ссылку на фронт-роут с url-encoded email и токеном, либо null если PublicUrl пуст.</summary>
    private string? BuildLink(string route, string email, string token)
    {
        var publicUrl = config["App:PublicUrl"]?.TrimEnd('/');
        if (string.IsNullOrEmpty(publicUrl)) return null;
        return $"{publicUrl}/{route}?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
    }
}
