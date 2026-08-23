namespace BHS.CRG.Application.Settings;

/// <summary>Настройки одного движка (распознавание/поиск).</summary>
public class IntegrationEngine
{
    public bool Enabled { get; set; }
    public string? ApiKey { get; set; }
    public string? Model { get; set; }     // Anthropic/Gemini/Ollama
    public string? BaseUrl { get; set; }   // Ollama
    public string? FolderId { get; set; }  // Yandex
    public string? Host { get; set; }      // Yandex
}

/// <summary>Настройки SMTP для исходящей почты. Пароль хранится в том же JSON-store, что и API-ключи.</summary>
public class SmtpSettings
{
    public bool Enabled { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? User { get; set; }
    public string? Password { get; set; }
    /// <summary>Адрес отправителя (From).</summary>
    public string? From { get; set; }
    /// <summary>Отображаемое имя отправителя.</summary>
    public string? FromName { get; set; }
    /// <summary>true — STARTTLS/SSL (обычно порт 587/465); false — без шифрования.</summary>
    public bool UseSsl { get; set; } = true;

    /// <summary>
    /// Тот же самый почтовый сервер и та же учётная запись на нём?
    ///
    /// От этого зависит судьба СОХРАНЁННОГО пароля: наследовать его можно только на тот же сервер.
    /// Иначе достаточно указать чужой хост с пустым паролем — и сохранённый уедет туда сам.
    /// <c>UseSsl</c> входит в сравнение намеренно: снятие шифрования — тоже смена адресата, только
    /// адресатом становится любой на пути, а MailKit отдаёт пароль по AUTH и без TLS.
    ///
    /// Определение здесь, а не в двух вызывающих (сохранение и проверка связи): разъехавшись, они
    /// оставили бы дыру ровно в том месте, ради которого написаны.
    /// </summary>
    public bool SameServerAs(SmtpSettings other) =>
        string.Equals(Host?.Trim(), other.Host?.Trim(), StringComparison.OrdinalIgnoreCase)
        && Port == other.Port
        && string.Equals(User?.Trim(), other.User?.Trim(), StringComparison.Ordinal)
        && UseSsl == other.UseSsl;
}

/// <summary>Проверка новых версий на GitHub (issue #813).</summary>
public class UpdateCheckSettings
{
    /// <summary>
    /// Выключено — служба НЕ ходит в сеть вовсе, а не просто молчит. Каждый запрос сообщает GitHub
    /// адрес установки и сам факт её существования; «выключил, а оно всё равно стучится» —
    /// поведение, за которое систему справедливо ругают. Для установок без интернета это
    /// единственный способ не видеть в журнале бесконечные неудачи.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Расписание резервного копирования (issue #832).
///
/// Здесь, а не в <c>.env</c>: «когда снимать копию и сколько хранить» — продуктовое решение
/// администратора, которое меняют из интерфейса, а не параметр развёртывания, ради которого
/// пересоздают контейнер. Пределом развёртывания остаётся вместимость каталога
/// (<c>BACKUP_KEEP_COUNT</c>): сколько копий вообще влезет на диск, знает тот, кто этот диск дал.
/// </summary>
public class BackupScheduleSettings
{
    /// <summary>
    /// Включено ПО УМОЛЧАНИЮ — и это главное решение issue #832. Копия, которую забыли настроить, —
    /// самый частый способ потерять данные; установка, где никто ничего не трогал, обязана быть
    /// защищена без единого действия администратора.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Время суток «ЧЧ:ММ» по часам сервера. Ночь: копирование читает всю базу.</summary>
    public string TimeOfDay { get; set; } = "03:00";

    /// <summary>
    /// Сколько ПЛАНОВЫХ копий хранить. Ручные и принесённые в этот счёт не входят и уборкой не
    /// затрагиваются — их клали осознанно.
    /// </summary>
    public int KeepCount { get; set; } = 7;
}

/// <summary>
/// Передача сообщений об ошибках в GitHub (issue #834, часть 2).
///
/// Выключателя здесь нет намеренно: выключатель — сам токен. Флаг «включено» без токена означал бы
/// кнопку, которая обещает работу и отказывает при нажатии, а токен без флага — настройку, которая
/// лежит и ничего не делает. Одно поле не умеет расходиться само с собой.
/// </summary>
public class GithubSettings
{
    /// <summary>
    /// Fine-grained PAT с правом <c>issues: write</c> на ОДИН репозиторий. Секрет: хранится
    /// зашифрованным, наружу отдаётся только признак «задан».
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// Куда заводить issue, в виде «владелец/репозиторий». Умолчание — репозиторий продукта: у
    /// установки, которая ничего не настраивала, но получила токен, не должно быть ещё одного
    /// обязательного поля.
    /// </summary>
    public string Repository { get; set; } = DefaultRepository;

    public const string DefaultRepository = "pavru/bhs.crg";

    /// <summary>
    /// Тот же самый репозиторий?
    ///
    /// От этого зависит судьба СОХРАНЁННОГО токена — ровно та же дыра, что и у пароля SMTP
    /// (<see cref="SmtpSettings.SameServerAs" />): без проверки достаточно указать чужой
    /// репозиторий с пустым полем токена, и первое же «Отправить в GitHub» уйдёт туда с нашим
    /// правом записи. Classic-токен таким способом опубликовал бы внутренний текст в чужом месте.
    /// </summary>
    /// <summary>
    /// «владелец/репозиторий» в одном виде: без пробелов и лишних косых, пустое — умолчание.
    ///
    /// В ОДНОМ месте, потому что нормализовали в трёх и по-разному: сохранение обрезало пробелы,
    /// сравнение «тот ли репозиторий» смотрело на сырое значение, а отправка снимала ещё и косые.
    /// Из-за расхождения «pavru/bhs.crg/» и «pavru/bhs.crg» считались разными местами — и повторное
    /// сохранение с пустым полем токена молча стирало токен, хотя репозиторий не менялся.
    /// </summary>
    public static string Normalize(string? repository)
    {
        var text = (repository ?? "").Trim().Trim('/');
        return text.Length == 0 ? DefaultRepository : text;
    }

    /// <summary>
    /// Похоже ли на «владелец/репозиторий». Проверяем при сохранении: негодный адрес иначе всплывёт
    /// ответом 404 при отправке — а его текст подозревает права токена, то есть уводит не туда.
    /// </summary>
    public static bool IsRepositoryWellFormed(string? repository)
    {
        var parts = Normalize(repository).Split('/');
        return parts.Length == 2
               && parts.All(p => p.Length > 0
                                 && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));
    }

    public bool SameRepositoryAs(GithubSettings other)
        => string.Equals(Normalize(Repository), Normalize(other.Repository), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Годится ли токен для заголовка HTTP: только видимые символы ASCII.
    ///
    /// Найдено живой проверкой. Заголовки HTTP не несут ни кириллицы, ни пробелов, ни невидимых
    /// знаков, и .NET на таком значении роняет ЗАПРОС — а выглядит это как «GitHub недоступен,
    /// проверьте сеть», то есть отправляет администратора чинить сеть вместо того, чтобы заново
    /// скопировать токен. Случай будничный: копирование из письма или мессенджера легко приносит
    /// неразрывный пробел, а раскладка — кириллическую «с» вместо латинской.
    /// </summary>
    public static bool IsTokenUsable(string? token)
        => string.IsNullOrEmpty(token) || token.All(c => c > ' ' && c < (char)127);
}

/// <summary>
/// Управляемые из UI настройки ВНЕШНИХ служб: распознавание, веб-поиск, почта, проверка обновлений.
/// Хранятся в БД; пустой ключ движка означает fallback на конфигурацию (user-secrets/appsettings).
///
/// Имя класса уже́ уже своего содержания (issue #813) — переименование задело бы API, клиент и
/// разбор сохранённого JSON, поэтому отложено до собственной причины, а не сделано попутно.
/// </summary>
public class IntegrationSettingsModel
{
    public List<string> RecognitionOrder { get; set; } = [];
    /// <summary>Anthropic / Gemini / Ollama.</summary>
    public Dictionary<string, IntegrationEngine> Recognition { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Serper / Yandex.</summary>
    public Dictionary<string, IntegrationEngine> WebSearch { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> FgisDomains { get; set; } = [];
    public List<string> ManufacturerDomains { get; set; } = [];
    public SmtpSettings Smtp { get; set; } = new();
    /// <summary>Проверка обновлений. Здесь живёт ТОЛЬКО то, что задал человек: след самой службы
    /// («о какой версии уведомляли», «когда проверяли») — в ServiceState, см. issue #813.</summary>
    public UpdateCheckSettings Updates { get; set; } = new();

    /// <summary>Расписание резервного копирования (issue #832). След службы — там же, где у
    /// проверки обновлений: в ServiceState, а не здесь.</summary>
    public BackupScheduleSettings Backup { get; set; } = new();

    /// <summary>Передача сообщений об ошибках в GitHub (issue #834).</summary>
    public GithubSettings Github { get; set; } = new();

    public IntegrationEngine Rec(string name)
        => Recognition.TryGetValue(name, out var e) ? e : new IntegrationEngine();
    public IntegrationEngine Web(string name)
        => WebSearch.TryGetValue(name, out var e) ? e : new IntegrationEngine();
}

/// <summary>
/// Эффективные настройки интеграций (БД поверх конфигурации). Кэшируется, сбрасывается при сохранении.
/// </summary>
public interface IIntegrationSettings
{
    Task<IntegrationSettingsModel> GetEffectiveAsync(CancellationToken ct = default);
    Task SaveAsync(IntegrationSettingsModel update, CancellationToken ct = default);
    /// <summary>Сохраняет только секцию SMTP (не трогая распознавание/поиск) — отдельные формы не затирают друг друга.</summary>
    Task SaveSmtpAsync(SmtpSettings smtp, CancellationToken ct = default);
    /// <summary>Сохраняет только секцию обновлений — по той же причине, что и SMTP.</summary>
    Task SaveUpdatesAsync(UpdateCheckSettings updates, CancellationToken ct = default);

    /// <summary>Сохраняет только расписание копирования — по той же причине, что и SMTP.</summary>
    Task SaveBackupScheduleAsync(BackupScheduleSettings backup, CancellationToken ct = default);

    /// <summary>Сохраняет только настройки GitHub — по той же причине, что и SMTP.</summary>
    Task SaveGithubAsync(GithubSettings github, CancellationToken ct = default);
    void Invalidate();
}
