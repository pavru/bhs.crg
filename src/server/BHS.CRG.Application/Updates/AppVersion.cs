using System.Reflection;

namespace BHS.CRG.Application.Updates;

/// <summary>
/// Номер версии приложения «мажор.минор.патч», его разбор и сравнение (issue #813).
///
/// Свой разбор, а не пакет semver: схема простая, а сравнивать нужно две формы ОДНОГО номера —
/// версию сборки (<c>0.137.1+хеш</c>, как её отдаёт <see cref="AssemblyInformationalVersionAttribute"/>)
/// и тег релиза на GitHub (<c>v0.137.1</c>). Обе приводятся здесь, в единственном месте: знай про
/// эти обёртки два разных куска кода — и «доступна новая версия» однажды сказали бы про ту же самую.
/// </summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch) : IComparable<AppVersion>
{
    /// <summary>
    /// Разбирает номер, прощая обёртки обеих форм: ведущий «v» тега и «+хеш» версии сборки.
    ///
    /// На неразобранной строке возвращает false, а НЕ нули. Тихий 0.0.0 не дал бы ложного
    /// уведомления (ноль никогда не больше), он дал бы вечное молчание — служба перестала бы
    /// работать, и никто бы об этом не узнал.
    /// </summary>
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var plus = s.IndexOf('+');   // 0.137.1+a1b2c3d — метаданные сборки
        if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-');   // 0.137.1-rc.1 — пре-релиз: номер берём, суффикс отбрасываем
        if (dash >= 0) s = s[..dash];

        var parts = s.Split('.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var major) || major < 0) return false;
        if (!int.TryParse(parts[1], out var minor) || minor < 0) return false;
        if (!int.TryParse(parts[2], out var patch) || patch < 0) return false;

        version = new AppVersion(major, minor, patch);
        return true;
    }

    public int CompareTo(AppVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    /// <summary>
    /// Считать ли выпущенную версию новее установленной. Сравнение СТРОГОЕ, и это же правило
    /// закрывает случай «на машине разработчика версия поднята раньше, чем вышел релиз»: там
    /// установленная старше выпущенной, и сообщать не о чем — до выхода релиза со старшим номером,
    /// когда уведомление придёт само.
    /// </summary>
    public static bool IsNewer(string? released, string? installed)
        => TryParse(released, out var r) && TryParse(installed, out var i) && r > i;

    /// <summary>
    /// Номер без обёрток: «v0.138.0» → «0.138.0». Наружу (в интерфейс) номер уходит только таким —
    /// иначе в подвале панели рядом оказываются «v0.137.1» и «доступна v0.138.0», где «v» означает
    /// в одном случае наше оформление, а в другом — форму тега GitHub. Возвращает исходную строку,
    /// если она не разобралась: подменять её на пустоту хуже, чем показать как есть.
    /// </summary>
    public static string? Normalize(string? text)
        => TryParse(text, out var v) ? v.ToString() : text;

    // ── Версия текущей сборки ───────────────────────────────────────────────────

    /// <summary>
    /// Полный InformationalVersion сборки: «0.137.1+хеш» либо «0.0.0», если атрибута нет.
    /// Единственное место, где он читается, — его же использует эндпоинт <c>/api/version</c>.
    /// </summary>
    public static string InformationalOfEntryAssembly()
        => Assembly.GetEntryAssembly()
               ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
               ?.InformationalVersion
           ?? "0.0.0";

    /// <summary>Разбор InformationalVersion на «номер» и «хеш коммита» (пустой, если его нет).</summary>
    public static (string Version, string Commit) SplitInformational(string informational)
    {
        var parts = informational.Split('+', 2);
        return (parts[0], parts.Length > 1 ? parts[1] : "");
    }
}
