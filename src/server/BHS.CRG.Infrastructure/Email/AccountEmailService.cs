using BHS.CRG.Application.Email;
using Microsoft.Extensions.Configuration;

namespace BHS.CRG.Infrastructure.Email;

/// <summary>
/// Письма учётной записи (issue #148): сброс пароля, подтверждение/смена почты. Собирает ссылку из
/// <c>App:PublicUrl</c> + url-encoded токен Identity и отправляет через <see cref="IEmailSender"/>.
/// Если <c>App:PublicUrl</c> не задан — бросает <see cref="AppUrlNotConfiguredException"/> и НИЧЕГО
/// не отправляет (иначе пользователь получил бы письмо без ссылки).
/// </summary>
public class AccountEmailService(IEmailSender email, IConfiguration config)
{
    /// <summary>Письмо со ссылкой сброса пароля (токен действует ~1 час).</summary>
    public async Task SendPasswordResetAsync(string toEmail, string token, CancellationToken ct = default)
    {
        var link = BuildLink("reset-password", $"email={Uri.EscapeDataString(toEmail)}&token={Uri.EscapeDataString(token)}");
        var body = "Вы запросили сброс пароля в BHS.CRG.\n\n" +
                   $"Ссылка для сброса (действует 1 час):\n{link}\n\n" +
                   "Если вы не запрашивали сброс — просто проигнорируйте это письмо, пароль останется прежним.";
        await email.SendAsync(new EmailMessage([toEmail], "Сброс пароля — BHS.CRG", body), ct);
    }

    /// <summary>Письмо с подтверждением адреса (токен действует ~24 часа).</summary>
    public async Task SendEmailConfirmationAsync(string toEmail, string token, CancellationToken ct = default)
    {
        var link = BuildLink("confirm-email", $"email={Uri.EscapeDataString(toEmail)}&token={Uri.EscapeDataString(token)}");
        var body = "Подтвердите адрес электронной почты для BHS.CRG.\n\n" +
                   $"Ссылка для подтверждения (действует 24 часа):\n{link}\n\n" +
                   "Если вы не заводили учётную запись — просто проигнорируйте это письмо.";
        await email.SendAsync(new EmailMessage([toEmail], "Подтверждение адреса — BHS.CRG", body), ct);
    }

    /// <summary>Письмо на НОВЫЙ адрес для подтверждения смены email (токен действует ~24 часа).</summary>
    public async Task SendEmailChangeAsync(Guid userId, string newEmail, string token, CancellationToken ct = default)
    {
        var link = BuildLink("confirm-email-change",
            $"uid={Uri.EscapeDataString(userId.ToString())}&email={Uri.EscapeDataString(newEmail)}&token={Uri.EscapeDataString(token)}");
        var body = "Запрошена смена адреса входа в BHS.CRG на этот email.\n\n" +
                   $"Ссылка для подтверждения (действует 24 часа):\n{link}\n\n" +
                   "Если вы не запрашивали смену — проигнорируйте это письмо, адрес останется прежним.";
        await email.SendAsync(new EmailMessage([newEmail], "Смена адреса входа — BHS.CRG", body), ct);
    }

    /// <summary>Ссылка на фронт-роут с уже url-encoded query. Бросает, если App:PublicUrl не задан.</summary>
    private string BuildLink(string route, string query)
    {
        var publicUrl = config["App:PublicUrl"]?.TrimEnd('/');
        if (string.IsNullOrEmpty(publicUrl)) throw new AppUrlNotConfiguredException();
        return $"{publicUrl}/{route}?{query}";
    }
}
