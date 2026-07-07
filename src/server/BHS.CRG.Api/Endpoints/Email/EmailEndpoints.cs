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
        var g = app.MapGroup("/api/email").RequireAuthorization("Admin");

        g.MapPost("/send", async (SendMessageRequest req, AppDbContext db, IEmailSender email, CancellationToken ct) =>
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
            catch (Exception ex)
            {
                return Results.Ok(new { ok = false, error = ex.Message });
            }
        });
    }

    private record SendMessageRequest(List<Guid>? UserIds, string? Subject, string? Body);
}
