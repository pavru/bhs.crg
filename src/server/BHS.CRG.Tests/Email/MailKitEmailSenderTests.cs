using BHS.CRG.Application.Email;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Email;

namespace BHS.CRG.Tests.Email;

/// <summary>Транспорт почты: понятная ошибка при ненастроенном SMTP (не 500), проверка получателей.</summary>
public class MailKitEmailSenderTests
{
    private sealed class FakeSettings(SmtpSettings smtp) : IIntegrationSettings
    {
        public Task<IntegrationSettingsModel> GetEffectiveAsync(CancellationToken ct = default) =>
            Task.FromResult(new IntegrationSettingsModel { Smtp = smtp });
        public Task SaveAsync(IntegrationSettingsModel update, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveSmtpAsync(SmtpSettings s, CancellationToken ct = default) => Task.CompletedTask;
        public void Invalidate() { }
    }

    private static MailKitEmailSender Sender(SmtpSettings smtp) => new(new FakeSettings(smtp));

    [Fact]
    public async Task Send_WhenSmtpDisabled_ThrowsEmailNotConfigured()
    {
        var sender = Sender(new SmtpSettings { Enabled = false, Host = "smtp.test", From = "a@b.c" });
        await Assert.ThrowsAsync<EmailNotConfiguredException>(() =>
            sender.SendAsync(new EmailMessage(["x@y.z"], "тема", "текст")));
    }

    [Fact]
    public async Task Send_WhenHostMissing_ThrowsEmailNotConfigured()
    {
        var sender = Sender(new SmtpSettings { Enabled = true, Host = null, From = "a@b.c" });
        await Assert.ThrowsAsync<EmailNotConfiguredException>(() =>
            sender.SendAsync(new EmailMessage(["x@y.z"], "тема", "текст")));
    }

    [Fact]
    public async Task Send_WhenNoRecipients_ThrowsArgument()
    {
        var sender = Sender(new SmtpSettings { Enabled = true, Host = "smtp.test", From = "a@b.c" });
        await Assert.ThrowsAsync<ArgumentException>(() =>
            sender.SendAsync(new EmailMessage([], "тема", "текст")));
    }
}
