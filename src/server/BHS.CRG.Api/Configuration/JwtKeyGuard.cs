using System.Text;

namespace BHS.CRG.Api.Configuration;

/// <summary>
/// Проверка ключа подписи токенов на старте приложения.
///
/// Ключ обязан быть задан развёртыванием и быть настоящим. Раньше значение бралось как есть: в
/// конфигурации по умолчанию лежала строка-пример, приложение с ней поднималось молча, и отличить
/// «настроено» от «оставлено как в примере» можно было только заглянув в конфиг. Отказ старта здесь
/// лучше рабочего запуска: неверная конфигурация видна сразу и целиком, а не однажды и не тем.
///
/// Проверяем три вещи — задан, достаточной длины, не из примера. Длина считается в БАЙТАХ: алгоритм
/// подписи оперирует ими, и строка из кириллицы короче, чем кажется по числу символов.
/// </summary>
public static class JwtKeyGuard
{
    /// <summary>HMAC-SHA256 — 256 бит; ключ короче не даёт заявленной стойкости.</summary>
    private const int MinBytes = 32;

    /// <summary>
    /// Своя заглушка, кроме общих (<see cref="ConfigPlaceholders" />): встречается только в примерах
    /// ключа подписи. Отдельного «secret» в списке нет намеренно — это обычное английское слово, и
    /// настоящий ключ вроде «secretkey-prod-2026-…» отвергался бы с заведомо неверной причиной.
    /// </summary>
    private const string JwtExamplePrefix = "your-secret";

    /// <summary>Возвращает проверенный ключ либо бросает с указанием, что именно сделать.</summary>
    public static string Require(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw Fail("не задан");

        if (Encoding.UTF8.GetByteCount(key) < MinBytes)
            throw Fail($"короче {MinBytes} байт");

        if (ConfigPlaceholders.LooksLikeExample(key, JwtExamplePrefix))
            throw Fail("совпадает со значением из файла-примера");

        return key;
    }

    private static InvalidOperationException Fail(string reason) => new(
        $"Ключ подписи токенов (Jwt:Key) {reason}. Приложение не запущено намеренно.\n" +
        "Задайте собственное значение — переменной окружения Jwt__Key (в развёртывании compose это " +
        "JWT_KEY в deploy/.env) либо в конфигурации среды.\n" +
        "Сгенерировать: openssl rand -base64 48");
}
