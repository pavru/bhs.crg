using BHS.CRG.Application.Settings;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Каждая секция настроек должна попадать в ЭФФЕКТИВНУЮ модель — ту, которую читают службы.
///
/// Тест написан по следу происшествия, а не для красоты. Эффективные настройки собираются полем за
/// полем (осознанно: у части полей есть fallback на конфигурацию), и рядом стоит предупреждение
/// «забудь здесь секцию — и она молча вернётся к умолчанию». Оно сработало ровно так, как обещало:
/// расписание резервного копирования (issue #832) приехало без своей строки — настройка
/// сохранялась, отвечала 204, показывалась сохранённой, а служба продолжала работать по умолчанию.
/// Отказ был неотличим от успеха на всём пути, и поймала его только живая проверка.
///
/// Проверяем ИСХОДНИК, а не поведение: чтобы поймать это поведением, нужен прогон по каждой секции
/// с базой, а связать «добавил секцию» с «внеси её в сборку» надо в тот момент, когда секцию
/// добавляют, и стоить это должно миллисекунды.
/// </summary>
public class EffectiveSettingsCoverageTests
{
    /// <summary>
    /// Свойства, которых в эффективной модели быть не должно, и почему. Пусто — и хорошо: строка
    /// здесь означает принятое решение, а не умолчание.
    /// </summary>
    private static readonly Dictionary<string, string> DeliberatelyAbsent = new();

    [Fact]
    public void EverySettingsSection_IsBuiltIntoEffectiveModel()
    {
        var body = BuildEffectiveBody();

        var missing = typeof(IntegrationSettingsModel)
            .GetProperties()
            .Where(p => p.CanWrite)
            .Select(p => p.Name)
            .Where(name => !DeliberatelyAbsent.ContainsKey(name))
            .Where(name => !body.Contains(name, StringComparison.Ordinal))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "Секции настроек не собираются в эффективную модель: " + string.Join(", ", missing) + ".\n" +
            "Сохраняться такая настройка будет, действовать — нет, и отказ будет неотличим от " +
            "успеха. Впишите строку в BuildEffective (IntegrationSettingsService) — или в " +
            "DeliberatelyAbsent с причиной.");
    }

    /// <summary>Текст метода <c>BuildEffective</c> — от объявления до следующего метода.</summary>
    private static string BuildEffectiveBody()
    {
        var file = Path.Combine(SolutionDir, "BHS.CRG.Infrastructure", "Settings", "IntegrationSettingsService.cs");
        var text = File.ReadAllText(file);

        var start = text.IndexOf("private IntegrationSettingsModel BuildEffective", StringComparison.Ordinal);
        Assert.True(start >= 0, $"В {file} не найден BuildEffective — тест сторожит то, чего больше нет.");

        var end = text.IndexOf("private IntegrationEngine EffRec", start, StringComparison.Ordinal);
        return end > start ? text[start..end] : text[start..];
    }

    /// <summary>Каталог решения — от папки сборки вверх до файла решения.</summary>
    private static string SolutionDir { get; } = FindSolutionDir();

    private static string FindSolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BHS.CRG.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Не найден каталог решения");
    }
}
