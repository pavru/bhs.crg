namespace BHS.CRG.Infrastructure.OfficeConversion;

/// <summary>
/// Где стоит конвертер офисных файлов (issue #1267) — отдельный сервис, который превращает счёт в
/// Excel или Word в PDF с текстовым слоем. Раздел настроек <c>OfficeConverter</c>.
///
/// <para>Адрес может быть не задан: тогда конвертера у экземпляра нет, и это допустимое состояние,
/// а не ошибка запуска. Офисный файл при этом прикладывается и скачивается, а читаемый образ
/// получает отказ «недоступно».</para>
/// </summary>
public sealed class OfficeConverterOptions
{
    public const string Section = "OfficeConverter";

    /// <summary>
    /// Имя клиента фабрики. Клиент внутренний: сервис стоит в своей сети без выхода наружу, и через
    /// прокси к нему не ходят — см. <c>OutboundClientRoutingTests</c>.
    /// </summary>
    public const string ClientName = "office-converter";

    /// <summary>Сколько клиент ждёт ответа сервиса; у самого сервиса срок короче.</summary>
    public static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(60);

    /// <summary>В поставке — <c>http://converter:3000</c>, на стенде разработчика — вход через nginx стенда.</summary>
    public string? BaseUrl { get; set; }

    public bool Configured => !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>Адрес внутри сервиса. Зовут только при <see cref="Configured" />.</summary>
    public Uri At(string path) => new($"{BaseUrl!.Trim().TrimEnd('/')}/{path.TrimStart('/')}");
}
