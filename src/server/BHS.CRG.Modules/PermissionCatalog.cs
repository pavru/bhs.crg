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
    /// <summary>
    /// Составное право «читать всё» (ТЗ AUTH-5.2). Само оно не стоит ни на одной двери: владельцу
    /// достаются права, помеченные <see cref="ReadAllMark.In" />, — см. <see cref="Expand" />.
    /// </summary>
    public const string ReadAllCode = "*.read.all";

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
        ReadAll = [.. permissions.Where(p => p.ReadAll is { Included: true }).Select(p => p.Code)];
    }

    private readonly HashSet<string> _codes;

    /// <summary>
    /// Права, входящие в «читать всё». Только включённых модулей: справочник собран из них, поэтому
    /// модуль, включённый позже, дойдёт до владельца составного права сам, а выключенный — уйдёт.
    /// </summary>
    public IReadOnlyList<string> ReadAll { get; }

    /// <summary>
    /// Раскрывает составное право: к выданным правам добавляются входящие в «читать всё» — если
    /// составное среди выданных есть. Само составное право в наборе остаётся: по нему страница «Мои
    /// права» объясняет, откуда взялись остальные.
    ///
    /// <para>⚠️ Звать обязан тот, кто СЧИТАЕТ права из ролей, а не тот, кто их проверяет. Доступ к
    /// модулю судят по началу кода права (<see cref="ModuleAccess" />), и нераскрытый набор модуль не
    /// открывает вовсе: проверка права на адресе прошла бы, а ворота модуля перед ней — нет.</para>
    /// </summary>
    public IReadOnlyCollection<string> Expand(IReadOnlyCollection<string> granted)
    {
        if (ReadAll.Count == 0 || !granted.Contains(ReadAllCode, StringComparer.OrdinalIgnoreCase))
            return granted;

        var result = new HashSet<string>(granted, StringComparer.OrdinalIgnoreCase);
        result.UnionWith(ReadAll);
        return result;
    }

    public IReadOnlyList<AppPermission> All { get; }

    /// <summary>Коды прав — для сверки с базой и проверок.</summary>
    public IReadOnlyList<string> Codes => [.. All.Select(p => p.Code)];

    /// <summary>Объявлено ли такое право этой сборкой.</summary>
    public bool Declares(string code) => _codes.Contains(code);
}
