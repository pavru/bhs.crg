namespace BHS.CRG.Modules;

/// <summary>
/// Все права, объявленные этой сборкой: ядро плюс включённые модули.
///
/// Один список, а не «права ядра» и «права модулей» по отдельности: спрашивают его редактор ролей,
/// сверка с базой и проверка прав, и всем троим нужен один и тот же ответ. Права ВЫКЛЮЧЕННОГО
/// модуля сюда не попадают — выдавать их некому и не за что (AUTH-19).
/// </summary>
public sealed class PermissionCatalog
{
    public PermissionCatalog(IReadOnlyList<AppPermission> permissions)
    {
        // Отказ собирается по всем правам разом: чинить объявления по одному на перезапуск —
        // это десять перезапусков там, где хватает одного.
        var broken = permissions.Select(p => p.Validate()).Where(r => r is not null).ToList();
        if (broken.Count > 0)
            throw new InvalidOperationException(
                "Право объявлено без объяснения или с негодным кодом: " + string.Join("; ", broken) + ".\n" +
                "Объяснение обязательно: редактор ролей показывает его рядом с галкой, и без него " +
                "администратор раздаёт доступ вслепую.");

        var duplicates = permissions
            .GroupBy(p => p.Code, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
            throw new InvalidOperationException(
                "Право объявлено дважды: " + string.Join(", ", duplicates) + ". " +
                "Два объявления одного кода означают два разных объяснения у одной галки.");

        All = permissions;
        _codes = new HashSet<string>(permissions.Select(p => p.Code), StringComparer.OrdinalIgnoreCase);
    }

    private readonly HashSet<string> _codes;

    public IReadOnlyList<AppPermission> All { get; }

    /// <summary>Коды прав — для сверки с базой и проверок.</summary>
    public IReadOnlyList<string> Codes => [.. All.Select(p => p.Code)];

    /// <summary>Объявлено ли такое право этой сборкой.</summary>
    public bool Declares(string code) => _codes.Contains(code);
}
