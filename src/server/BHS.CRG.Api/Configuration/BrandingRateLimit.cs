namespace BHS.CRG.Api.Configuration;

/// <summary>
/// Предел частоты запросов оформления — настройка развёртывания, а не константа.
///
/// Оформление читает каждая загрузка страницы, а корзина у него одна на адрес клиента. Значит, у
/// предела два потребителя с разными нуждами, и одним числом в коде их не примирить:
/// <list type="bullet">
/// <item>офис за общим внешним адресом — десятки людей в одной корзине. Упрётся в предел — экраны
/// останутся без шапки и логотипа, а починить это без пересборки образа было бы нечем;</item>
/// <item>стенд живых прогонов CI — все наборы с одного адреса подряд, под четыре сотни загрузок
/// страниц за шесть минут. На пределе поставки 429 начинали получать наборы в конце списка, и красным
/// становился тот, что проверяет отказы, а не тот, что добавил загрузок (замер 23.10.2026).</item>
/// </list>
///
/// <para>Значки и манифест (корзина <c>branding-icons</c>) идут с той же частотой — манифест
/// тоже запрашивается при каждой загрузке страницы, — поэтому их предел не настраивается отдельно, а
/// следует за этим с прежним множителем. Иначе поднятое число упёрлось бы во второе, забытое.</para>
/// </summary>
public sealed class BrandingRateLimit
{
    /// <summary>Предел поставки: запросов за окно с одного адреса.</summary>
    public const int Default = 300;

    /// <summary>
    /// Опустить предел ниже поставки нельзя: ключ заведён ради офисов, которым его мало, а меньшее
    /// число оставило бы без шапки как раз их.
    /// </summary>
    public const int Min = Default;

    /// <summary>
    /// Верхняя граница — против описки: сто запросов в секунду с адреса уже не предел, а его
    /// отсутствие, и лишний ноль в <c>deploy/.env</c> не должен выключать защиту молча.
    /// </summary>
    public const int Max = 30_000;

    /// <summary>Во сколько раз щедрее корзина значков и манифеста: они отдаются из памяти.</summary>
    public const int IconsFactor = 4;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private const string Key = "RateLimits:Branding";
    private const string EnvName = "RateLimits__Branding";
    private const string ComposeName = "BRANDING_RATE_LIMIT";

    private BrandingRateLimit(int permitLimit) => PermitLimit = permitLimit;

    /// <summary>Название и логотип: запросов за <see cref="Window" /> с одного адреса.</summary>
    public int PermitLimit { get; }

    /// <summary>Значки вкладки и приложения и манифест: запросов за то же окно.</summary>
    public int IconsPermitLimit => PermitLimit * IconsFactor;

    public static BrandingRateLimit FromConfiguration(IConfiguration cfg) => Parse(cfg[Key]);

    /// <summary>
    /// Незаданное — это <see cref="Default" />: в поставке переменная пустая. Заданное негодно —
    /// отказ на старте, как у прочих проверок конфигурации: описка не должна молча вернуть предел,
    /// который на этой установке специально подняли.
    /// </summary>
    public static BrandingRateLimit Parse(string? value) => new(RateLimitSetting.Parse(
        value, Key, "Предел запросов оформления", Default, Min, Max,
        $"Задайте целое число запросов за пять минут с одного адреса в переменной окружения {EnvName} — " +
        $"в развёртывании compose это {ComposeName} в deploy/.env. Пусто — предел поставки, {Default}."));
}
