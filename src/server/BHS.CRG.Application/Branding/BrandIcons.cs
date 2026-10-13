using System.Text.Encodings.Web;
using System.Text.Json;

namespace BHS.CRG.Application.Branding;

/// <summary>
/// Значки экземпляра (issue #1315): вкладка браузера, закладка на экране iPhone и три значка
/// устанавливаемого приложения. Набор — тот же, что у стандартных картинок до этой задачи.
/// </summary>
public enum BrandIconKind
{
    /// <summary>Значок вкладки. Файл 32×32: на экране 16, пополам делится точно.</summary>
    Tab,

    /// <summary>Значок для iPhone, 180×180. Непрозрачный: прозрачность iOS заливает чёрным.</summary>
    Apple,

    App192,
    App512,

    /// <summary>
    /// «Маскируемый» значок приложения, 512×512: система режет его своей маской (круг, капля),
    /// гарантирован только центральный круг диаметром 80 % стороны.
    /// </summary>
    Maskable512,
}

/// <summary>Имена значков в адресе: <c>/api/branding/icons/{имя}</c>.</summary>
public static class BrandIconKinds
{
    public static readonly IReadOnlyDictionary<string, BrandIconKind> ByRoute =
        new Dictionary<string, BrandIconKind>(StringComparer.Ordinal)
        {
            ["tab"] = BrandIconKind.Tab,
            ["apple"] = BrandIconKind.Apple,
            ["app-192"] = BrandIconKind.App192,
            ["app-512"] = BrandIconKind.App512,
            ["maskable-512"] = BrandIconKind.Maskable512,
        };

    public static string Route(BrandIconKind kind) => ByRoute.Single(p => p.Value == kind).Key;

    /// <summary>Адрес значка с меткой версии — таким его называют манифест и открытая вкладка.</summary>
    public static string Url(BrandIconKind kind, string version) =>
        $"/api/branding/icons/{Route(kind)}?v={Uri.EscapeDataString(version)}";
}

/// <summary>Чьи значки сейчас отдаёт экземпляр.</summary>
public static class BrandIconsState
{
    /// <summary>Логотипа нет — стандартные значки.</summary>
    public const string Standard = "standard";

    /// <summary>Значки построены из логотипа.</summary>
    public const string Custom = "custom";

    /// <summary>
    /// Логотип есть, а значки из него построить не удалось: отдаются стандартные. Отдельное
    /// состояние, а не <see cref="Standard" />: стандартный значок, отданный молча вместо
    /// непостроенного, — отказ, переодетый в результат; экран настройки обязан сказать это словами.
    /// </summary>
    public const string Failed = "failed";
}

/// <summary>Почему значки не построены — в виде, по которому экран выбирает совет.</summary>
public static class BrandIconProblem
{
    /// <summary>Файл не разобран как изображение (битый, внешние ссылки в SVG, нет видимых точек).</summary>
    public const string File = "file";

    /// <summary>Картинка слишком велика в точках — разворачивать её в памяти не берёмся.</summary>
    public const string Size = "size";

    /// <summary>Сбой на сервере, а не файл: построитель недоступен, хранилище не отвечает.</summary>
    public const string Builder = "builder";

    /// <summary>
    /// Построитель не уложился в срок. Отдельно от <see cref="Builder" />: виноват может быть и
    /// файл (тяжёлый SVG), и занятый сервер — по одному отказу это не различить, и совет «причина
    /// на сервере, а не в файле» был бы здесь неправдой. Повторяется сам, с растущей паузой.
    /// </summary>
    public const string Timeout = "timeout";
}

/// <param name="State">Одно из <see cref="BrandIconsState" />.</param>
/// <param name="Version">
/// Метка версии значков — адресу как <c>?v=</c>; для клиента это непрозрачная строка. У построенных
/// в ней логотип и отпечаток самих значков, у стандартных — отпечаток стандартных картинок. При
/// отказе построения — тоже стандартная: отдаются стандартные картинки.
/// </param>
/// <param name="Problem">Одно из <see cref="BrandIconProblem" />; <c>null</c>, пока отказа нет.</param>
/// <param name="Aspect">
/// Ширина к высоте видимой части логотипа — того прямоугольника, который вписан в значок.
/// <c>null</c>, пока значки не построены. Считает сервер: у SVG без размеров клиент пропорций не
/// узнает, а предупреждение «логотип вытянутый» обязано говорить о том же, что вписано.
/// </param>
public record BrandIconsInfo(string State, string Version, string? Problem, double? Aspect);

/// <param name="ETag">Сильный тег — по нему браузер переспрашивает одним 304.</param>
/// <param name="Version">Метка версии: запрос с такой же <c>?v=</c> получает длинный кеш.</param>
public record BrandIcon(byte[] Content, string MimeType, string ETag, string Version);

/// <summary>
/// Манифест устанавливаемого приложения (issue #1315). Отдаёт его сервер, а не файл клиента:
/// название и значки — настройка экземпляра, а статический файл о ней не знает.
/// </summary>
public static class BrandManifest
{
    public const string ContentType = "application/manifest+json";

    /// <summary>
    /// Цвета — прежние, из статического манифеста. Решение владельца продукта от 13.10.2026: цвет
    /// строки браузера не трогаем, он остаётся бирюзой знака и при логотипе заказчика. То же
    /// значение стоит в <c>index.html</c> (<c>theme-color</c>).
    /// </summary>
    public const string ThemeColor = "#109e92";
    public const string BackgroundColor = "#f4f2f7";

    private static readonly JsonSerializerOptions Json = new()
    {
        // Кириллица читаемой, а не \uXXXX: манифест смотрят глазами в инструментах браузера.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static string Build(BrandingInfo branding)
    {
        var v = branding.Icons.Version;
        return JsonSerializer.Serialize(new
        {
            // Название и короткое название — одно: под значком на телефоне стоит оно же, что в шапке.
            name = branding.ProductName,
            short_name = branding.ProductName,
            lang = "ru",
            // Личность приложения — явно: адрес манифеста сменился, а установленное приложение
            // обязано узнать себя в новом (без id её определяет start_url — тот же «/»).
            id = "/",
            start_url = "/",
            scope = "/",
            display = "standalone",
            orientation = "any",
            background_color = BackgroundColor,
            theme_color = ThemeColor,
            icons = new[]
            {
                Icon(BrandIconKind.App192, 192, "any", v),
                Icon(BrandIconKind.App512, 512, "any", v),
                Icon(BrandIconKind.Maskable512, 512, "maskable", v),
            },
        }, Json);
    }

    private static object Icon(BrandIconKind kind, int side, string purpose, string version) => new
    {
        src = BrandIconKinds.Url(kind, version),
        sizes = $"{side}x{side}",
        type = "image/png",
        purpose,
    };
}
