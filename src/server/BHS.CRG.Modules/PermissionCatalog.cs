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

    /// <param name="permissions">Права ядра и включённых модулей.</param>
    /// <param name="faults">
    /// Изъяны объявлений, которые видны только тому, кто собирает справочник: он знает, какое право
    /// чьё (см. <see cref="ReadAllFaults" />). Идут в ТОТ ЖЕ отказ, что и остальные.
    /// </param>
    public PermissionCatalog(IReadOnlyList<AppPermission> permissions, IEnumerable<string>? faults = null)
    {
        // Отказ собирается по всем правам разом: чинить объявления по одному на перезапуск —
        // это десять перезапусков там, где хватает одного.
        var broken = permissions.Select(p => p.Validate()).Where(r => r is not null)
            .Concat(faults ?? []).ToList();
        if (broken.Count > 0)
            throw new InvalidOperationException(
                "Право объявлено без объяснения, с негодным кодом или без пометки: " +
                string.Join("; ", broken) + ".\n" +
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
    ///
    /// <para>Набор дополняется НА МЕСТЕ и возвращается он же: зовут это на каждом подсчёте прав, и
    /// копия ради трёх добавленных строк была бы лишней. Составное право ищется сравнением самого
    /// набора — тот, кто считает права, собирает его без учёта регистра.</para>
    /// </summary>
    public HashSet<string> Expand(HashSet<string> granted)
    {
        if (granted.Contains(ReadAllCode)) granted.UnionWith(ReadAll);
        return granted;
    }

    /// <summary>
    /// Изъяны пометок «читать всё» — по тому, чьё право (задача A3, issue #1074).
    ///
    /// <para>У права МОДУЛЯ пометка обязательна: без неё оно выпало бы из составного молча, и
    /// обнаружилось бы это отсутствием раздела у «Руководителя». Проверяются модули СБОРКИ, а не
    /// включённые — иначе отказ пришёл бы на экземпляре, где модуль включили.</para>
    ///
    /// <para>У права ЯДРА пометки быть не должно: составное право раскрывается по модулям, и это
    /// сказано администратору в руководстве. Пометка на праве ядра молча раздала бы его каждому
    /// владельцу «читать всё» — и подпись в редакторе ролей появилась бы сама (ревью PR #1251).</para>
    /// </summary>
    public static IEnumerable<string> ReadAllFaults(
        IEnumerable<AppPermission> core, IEnumerable<AppPermission> modules) =>
        modules.Where(p => p.ReadAll is null)
            .Select(p => $"«{p.Code}» — не сказано, входит ли право в «читать всё»: поставьте " +
                         "ReadAllMark.In (только читает) или ReadAllMark.Out(\"причина\")")
            .Concat(core.Where(p => p.ReadAll is not null)
                .Select(p => $"«{p.Code}» — право ядра помечено для «читать всё», а оно раскрывается " +
                             "только по модулям: уберите пометку"));

    public IReadOnlyList<AppPermission> All { get; }

    /// <summary>Коды прав — для сверки с базой и проверок.</summary>
    public IReadOnlyList<string> Codes => [.. All.Select(p => p.Code)];

    /// <summary>Объявлено ли такое право этой сборкой.</summary>
    public bool Declares(string code) => _codes.Contains(code);
}
