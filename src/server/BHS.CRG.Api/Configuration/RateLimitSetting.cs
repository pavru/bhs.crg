using System.Globalization;

namespace BHS.CRG.Api.Configuration;

/// <summary>
/// Предел частоты, заданный настройкой развёртывания (<see cref="BrandingRateLimit" />, <see cref="LoginRateLimit" />):
/// незаданное — предел поставки, заданное с ошибкой — отказ на старте.
/// </summary>
internal static class RateLimitSetting
{
    /// <param name="what">Что ограничено — первыми словами отказа: «Предел запросов оформления».</param>
    /// <param name="howToSet">Как задать верно — последними словами отказа.</param>
    public static int Parse(string? value, string key, string what, int @default, int min, int max, string howToSet)
    {
        if (string.IsNullOrWhiteSpace(value)) return @default;

        var text = value.Trim();
        if (!text.All(char.IsAsciiDigit))
            throw Fail($"{what} ({key}) не число: «{text}».", howToSet);

        // Цифры, не влезшие в int, — тоже выход за диапазон (лишние нули), и отказ обязан назвать
        // именно его, а не «не число»: человек видит цифры и ищет в строке невидимые знаки.
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var limit)
            || limit < min || limit > max)
            throw Fail($"{what} ({key}) вне допустимого диапазона: {text}. Допустимо от {min} до {max}.", howToSet);

        return limit;
    }

    private static InvalidOperationException Fail(string what, string howToSet) =>
        new($"{what} Приложение не запущено намеренно.\n{howToSet}");
}
