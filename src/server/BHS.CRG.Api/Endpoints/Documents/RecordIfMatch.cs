namespace BHS.CRG.Api.Endpoints.Documents;

/// <summary>
/// Версия записи общих данных, которую называет правка (issue #1214), — заголовок <c>If-Match</c>,
/// значение приходит в ответе чтения полем <c>version</c>.
///
/// <para>Заголовок, а не поле тела — как у счёта (#1176): версия относится к записи, а не к её
/// содержимому, и адрес без тела сможет назвать её тем же способом.</para>
///
/// <para>Умолчания нет нарочно. Правка без версии — это и есть «сохранить поверх, что бы там ни
/// лежало»: сервер, молча принимающий её, оставил бы потерю чужой правки каждому клиенту, который о
/// версии не знает, — и отвечал бы «сохранено».</para>
/// </summary>
public static class RecordIfMatch
{
    public const string Header = "If-Match";

    public const string Required =
        "Запись не сохранена: правка обязана назвать версию записи, по которой собрана, — заголовком " +
        "If-Match (значение — поле version из ответа чтения). Правка заменяет запись целиком, и без " +
        "версии она затёрла бы изменения, сделанные после того, как запись прочли.";

    /// <summary>Отказ 400, если версию не назвали; иначе <c>null</c> и версия в <paramref name="seen" />.</summary>
    public static IResult? Refuse(HttpRequest request, out string seen)
    {
        seen = request.Headers[Header].ToString().Trim();
        return seen.Length == 0 ? Results.BadRequest(new { error = Required }) : null;
    }
}
