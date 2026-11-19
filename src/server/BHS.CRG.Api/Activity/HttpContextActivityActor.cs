using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using BHS.CRG.Application.Activity;
using BHS.CRG.Domain.Activity;

namespace BHS.CRG.Api.Activity;

/// <summary>
/// Кто действует и каким путём — по токену и адресу запроса (ТЗ CORE-28, CORE-28.1).
///
/// Имя берётся из claim'ов, а не из базы: журнал пишется на каждом действии администратора, и
/// поход в базу за именем стоил бы запроса там, где имя УЖЕ приехало вместе с токеном. Показать
/// журнал обязан то имя, которое было в момент действия, — а в токене именно оно.
///
/// <para><b>Канал</b> (issue #1336, design D2) — по метаданным адреса: <c>MapMcp</c> помечен
/// <see cref="ActivityChannel.Mcp" />, всё остальное в запросе — <see cref="ActivityChannel.Ui" />. Не
/// разбором пути строкой: адрес переименуют, и канал молча стал бы <c>ui</c>.</para>
///
/// ⚠️ Вне запроса (старт приложения, фоновая задача, тест) <c>HttpContext</c> отсутствует. Тогда автор —
/// система, названная своей работой (<see cref="SystemActivityScope" />), или просто система. Это не
/// запасной вариант на случай сбоя: состав модулей и правда меняет не человек. Заказчика фоновой задачи
/// здесь нет нарочно — он подписывает только журнал (<see cref="JobActivityScope" />), а этот автор ещё и
/// «кто действует» для портов модулей, чей договор вне запроса — «никого».
/// </summary>
public sealed class HttpContextActivityActor(IHttpContextAccessor http) : IActivityActor
{
    public ActivityActor Current
    {
        get
        {
            var context = http.HttpContext;
            var user = context?.User;
            if (user?.Identity?.IsAuthenticated != true)
                return SystemActivityScope.Actor ?? ActivityActor.System;

            var id = Guid.TryParse(
                user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier),
                out var g) ? g : (Guid?)null;

            // Имя, потом почта: имя заполнено не у всех, а вот почта есть всегда — она же логин.
            var email = user.FindFirstValue(JwtRegisteredClaimNames.Email);
            if (string.IsNullOrWhiteSpace(email)) email = user.FindFirstValue(ClaimTypes.Email);
            var name = ActivityActor.NameOf(id, user.FindFirstValue("displayName"), email);

            var channel = context!.GetEndpoint()?.Metadata.GetMetadata<ActivityChannel>() ?? ActivityChannel.Ui;
            return new ActivityActor(id, name, channel);
        }
    }
}
