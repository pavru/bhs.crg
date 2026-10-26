namespace BHS.CRG.Api.Configuration;

/// <summary>
/// Предел входов с одного адреса — настройка развёртывания, а не константа (plan-09, решение владельца 26.10.2026).
///
/// <para>Поднимает его стенд живых прогонов CI: все наборы входят с одного адреса подряд, и тридцать входов поставки
/// за пять минут они израсходовали целиком — новый набор (сметы) уже не помещался. Поставка остаётся при прежнем
/// пределе: он против перебора адресов и распыления пароля (подбор пароля к одной учётной записи отдельно
/// останавливает блокировка после пяти неудач).</para>
///
/// <para>В <c>deploy/.env</c> ключа нет нарочно: установке он не нужен, а лишний рычаг у защиты входа — лишний
/// способ её ослабить. Задаётся переменной окружения сервера.</para>
/// </summary>
public sealed class LoginRateLimit
{
    /// <summary>Предел поставки: входов за окно с одного адреса.</summary>
    public const int Default = 30;

    /// <summary>Ниже поставки нельзя: офис за общим адресом утром упёрся бы во вход.</summary>
    public const int Min = Default;

    /// <summary>
    /// Верхняя граница — два входа в секунду с адреса: выше это уже не предел, а его отсутствие, и лишний ноль не
    /// должен выключать защиту молча.
    /// </summary>
    public const int Max = 600;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private const string Key = "RateLimits:Login";
    private const string EnvName = "RateLimits__Login";

    private LoginRateLimit(int permitLimit) => PermitLimit = permitLimit;

    /// <summary>Входов за <see cref="Window" /> с одного адреса.</summary>
    public int PermitLimit { get; }

    public static LoginRateLimit FromConfiguration(IConfiguration cfg) => Parse(cfg[Key]);

    /// <summary>Незаданное — предел поставки; заданное с ошибкой — отказ на старте.</summary>
    public static LoginRateLimit Parse(string? value) => new(RateLimitSetting.Parse(
        value, Key, "Предел входов", Default, Min, Max,
        $"Задайте целое число входов за пять минут с одного адреса в переменной окружения {EnvName}. " +
        $"Пусто — предел поставки, {Default}."));
}
