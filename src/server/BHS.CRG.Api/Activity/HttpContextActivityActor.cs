using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using BHS.CRG.Application.Activity;

namespace BHS.CRG.Api.Activity;

/// <summary>
/// Кто действует — по токену запроса (ТЗ CORE-28).
///
/// Имя берётся из claim'ов, а не из базы: журнал пишется на каждом действии администратора, и
/// поход в базу за именем стоил бы запроса там, где имя УЖЕ приехало вместе с токеном. Показать
/// журнал обязан то имя, которое было в момент действия, — а в токене именно оно.
///
/// ⚠️ Вне запроса (старт приложения, фоновая задача, тест) <c>HttpContext</c> отсутствует, и
/// автором становится сам экземпляр. Это не запасной вариант на случай сбоя: состав модулей и
/// правда меняет не человек.
/// </summary>
public sealed class HttpContextActivityActor(IHttpContextAccessor http) : IActivityActor
{
    public ActivityActor Current
    {
        get
        {
            var user = http.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true) return ActivityActor.System;

            var id = Guid.TryParse(
                user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier),
                out var g) ? g : (Guid?)null;

            // Имя, потом почта: имя заполнено не у всех, а вот почта есть всегда — она же логин.
            // Пустая строка в журнале читалась бы как «автор неизвестен», хотя он известен.
            var name = Nonblank(user.FindFirstValue("displayName"))
                ?? Nonblank(user.FindFirstValue(JwtRegisteredClaimNames.Email))
                ?? Nonblank(user.FindFirstValue(ClaimTypes.Email))
                ?? id?.ToString()
                ?? ActivityActor.System.Name;

            return new ActivityActor(id, name);
        }
    }

    private static string? Nonblank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
