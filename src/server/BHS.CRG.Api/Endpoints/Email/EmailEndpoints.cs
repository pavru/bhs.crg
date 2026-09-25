using BHS.CRG.Modules;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Email;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Endpoints.Email;

/// <summary>
/// Отправка сообщений по запросу (этап 2 почты). Выбранным зарегистрированным пользователям —
/// одним письмом, адреса в Bcc (получатели не видят друг друга). Ненастроенный SMTP / отсутствие
/// валидных адресатов — понятная ошибка, не 500.
/// </summary>
public static class EmailEndpoints
{
    public static void MapEmailEndpoints(this IEndpointRouteBuilder app)
    {
        // Право УПРАВЛЕНИЯ ПОЛЬЗОВАТЕЛЯМИ, а не обслуживания экземпляра (issue #947). Адрес один, и
        // зовут его с экрана пользователей: выбрать получателей из списка учётных записей и
        // написать им. Сначала он ушёл под core.system.manage «за компанию» с настройками почты — и
        // роль ровно с core.users.manage видела кнопку отправки, получая на неё отказ. Настройка
        // SMTP — обслуживание; письмо выбранным пользователям — работа с пользователями.
        var g = app.MapGroup("/api/email")
            .RequireAuthorization(AppPolicies.Permission(CorePermissions.UsersManage));

        g.MapPost("/send", async (SendMessageRequest req, AppDbContext db, IEmailSender email,
            ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Subject) || string.IsNullOrWhiteSpace(req.Body))
                return Results.BadRequest(new { ok = false, error = "Заполните тему и текст." });
            if (req.UserIds is null || req.UserIds.Count == 0)
                return Results.BadRequest(new { ok = false, error = "Выберите хотя бы одного получателя." });

            var ids = req.UserIds.ToHashSet();
            var users = await db.Set<ApplicationUser>().AsNoTracking()
                .Where(u => ids.Contains(u.Id))
                .Select(u => new { u.DisplayName, u.Email })
                .ToListAsync(ct);

            var recipients = users.Where(u => EmailValidation.IsValid(u.Email)).Select(u => u.Email!).ToList();
            var skipped = users.Where(u => !EmailValidation.IsValid(u.Email))
                .Select(u => string.IsNullOrWhiteSpace(u.DisplayName) ? (u.Email ?? "?") : u.DisplayName).ToList();

            if (recipients.Count == 0)
                return Results.BadRequest(new { ok = false, error = "Ни у одного из выбранных нет валидного email." });

            try
            {
                await email.SendAsync(new EmailMessage([], req.Subject, req.Body, Bcc: recipients), ct);
                return Results.Ok(new { ok = true, sent = recipients.Count, skipped });
            }
            // ⚠️ Раньше общего перехвата: НАШИ отказы доходят как есть. «SMTP не настроен или
            // выключен (Настройки → Почта)» написано нами и называет причину, которую человек
            // устранит сам; общий перехват ниже подменил бы его на «сервер не принял письмо» —
            // то есть соврал бы про соединение, которого не было вовсе. Перехват по фильтру, а не
            // общий: тип здесь и есть признак нашего отказа (issue #691).
            catch (Exception ex) when (ex is EmailNotConfiguredException
                or AppUrlNotConfiguredException or DomainException)
            {
                return Results.Ok(new { ok = false, error = ex.Message });
            }
            catch (Exception ex)
            {
                // Сообщение MailKit наружу не уходит (issue #691): оно называет почтовый сервер,
                // учётную запись под ним и то, что ответил чужой SMTP. Разбор отправки — не дело
                // того, кто пишет письмо: право здесь на УПРАВЛЕНИЕ ПОЛЬЗОВАТЕЛЯМИ, а почту
                // настраивает обслуживание экземпляра, и у него для этого есть своя проверка связи.
                loggers.CreateLogger(LogCategory).LogWarning(ex, "Рассылка не отправлена");
                return Results.Ok(new
                {
                    ok = false,
                    error = "Письмо не отправлено: почтовый сервер не принял его или не ответил. "
                        + "Проверку связи и настройки почты ведёт администратор системы.",
                });
            }
        });
    }

    private const string LogCategory = "BHS.CRG.Api.Endpoints.Email";

    private record SendMessageRequest(List<Guid>? UserIds, string? Subject, string? Body);
}
