namespace BHS.CRG.Modules.Access;

/// <summary>Вид охвата читателя: свои записи, записи названных строек либо все (ТЗ WORK-39).</summary>
public enum ModuleReachKind
{
    /// <summary>Только свои записи — монтажник и его отчёты.</summary>
    Own,

    /// <summary>Записи названных строек — менеджер и его стройки.</summary>
    Sites,

    /// <summary>Все записи — право «читать всё».</summary>
    All,
}

/// <summary>
/// Охват читателя — что ему видно из изолированных записей модуля: «свои», «стройки» со списком
/// строек или «все» (ТЗ WORK-39, WORK-40; work-07). Общий язык для таблиц модуля, межмодульных
/// контрактов и адресов модуля.
///
/// <para><b>Вычисляет модуль, а не ядро.</b> Поэтому охват — не поле параметра доступа
/// (<see cref="ModuleReader" />), а ответ модуля на вопрос «что этому читателю видно»: стройки менеджера —
/// данные назначений модуля (D5 зонтика <c>work-00-epic</c>), и ядру взять их неоткуда. Положи ядро охват
/// в параметр — оно вычисляло бы его без назначений, то есть неверно, и неверно молча.</para>
///
/// <para><b>Пустой список строек — охват, а не «все».</b> Менеджер без действующих назначений видит
/// ничего, и граница выдачи так и говорит: «отчёты ваших строек (0)». Подменить пустоту «всеми» значило бы
/// открыть всё тому, у кого назначения кончились.</para>
/// </summary>
public sealed record ModuleReach
{
    private ModuleReach(ModuleReachKind kind, IReadOnlySet<Guid> sites)
    {
        Kind = kind;
        Sites = sites;
    }

    /// <summary>Вид охвата.</summary>
    public ModuleReachKind Kind { get; }

    /// <summary>Стройки охвата <see cref="ModuleReachKind.Sites" />; у остальных видов — пусто.</summary>
    public IReadOnlySet<Guid> Sites { get; }

    /// <summary>Только свои записи.</summary>
    public static ModuleReach Own { get; } = new(ModuleReachKind.Own, new HashSet<Guid>());

    /// <summary>Все записи.</summary>
    public static ModuleReach All { get; } = new(ModuleReachKind.All, new HashSet<Guid>());

    /// <summary>Записи этих строек. Пустой список — «ничего», а не «все».</summary>
    public static ModuleReach OfSites(IEnumerable<Guid> sites) =>
        new(ModuleReachKind.Sites, new HashSet<Guid>(sites ?? throw new ArgumentNullException(nameof(sites))));

    /// <summary>
    /// Код охвата — им таблица называет исход отбора (<see cref="Tables.ModuleTableBoundary.Code" />):
    /// <c>own</c>, <c>sites</c>, <c>all</c>.
    /// </summary>
    public string Code => Kind switch
    {
        ModuleReachKind.Own => "own",
        ModuleReachKind.Sites => "sites",
        _ => "all",
    };

    /// <summary>Видна ли этому охвату запись стройки <paramref name="site" />, заведённая <paramref name="own" />.</summary>
    /// <param name="own">Запись — своя для читателя (её автор — он сам).</param>
    public bool Covers(Guid? site, bool own) => Kind switch
    {
        ModuleReachKind.All => true,
        ModuleReachKind.Own => own,
        _ => site is { } id && Sites.Contains(id),
    };
}
