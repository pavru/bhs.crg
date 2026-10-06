using Microsoft.AspNetCore.Http;

namespace BHS.CRG.Modules;

/// <summary>
/// Версия, которую правка называет заголовком <c>If-Match</c>, — та, по которой она собрана: счёт
/// (issue #1176), запись общих данных (issue #1214). Читается одним местом: два чтения со своими
/// правилами разошлись бы на первой же правке одного из них (ревью PR #1231).
///
/// <para>Лежит в контрактах, а не в ядре: счёт правит модуль, а на ядро он не ссылается.</para>
/// </summary>
public static class SeenVersion
{
    public const string Header = "If-Match";

    /// <summary>Отказ на версию, записанную не так, — словами о записи, а не о состоянии.</summary>
    public const string Malformed =
        "Версия в заголовке If-Match записана не так: ожидается значение поля version из ответа чтения — " +
        "число, как есть или в кавычках. «*» и другие отметки не принимаются: правка обязана назвать " +
        "именно ту версию, по которой собрана.";

    /// <summary>
    /// Прочитать названную версию. <c>false</c> — она записана не так; <paramref name="seen" />
    /// равна <c>null</c>, когда заголовка нет вовсе.
    ///
    /// <para>Кавычки и слабая отметка <c>W/"…"</c> снимаются: это запись HTTP для таких значений,
    /// и клиент на типизированных заголовках иначе её не пошлёт. Без этого версия в кавычках не
    /// совпала бы ни с одной лежащей — и на каждую попытку приходил бы ответ «тем временем изменили»,
    /// от которого перечитывание не помогает никогда: отказ, переодетый в другую причину.</para>
    /// </summary>
    public static bool TryRead(HttpRequest request, out string? seen)
    {
        var raw = request.Headers[Header].ToString().Trim();
        if (raw.StartsWith("W/", StringComparison.Ordinal)) raw = raw[2..];
        raw = raw.Trim('"').Trim();
        seen = raw.Length == 0 ? null : raw;
        return seen is null || raw.All(char.IsAsciiDigit);
    }
}
