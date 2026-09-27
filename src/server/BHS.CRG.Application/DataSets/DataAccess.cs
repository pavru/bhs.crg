namespace BHS.CRG.Application.DataSets;

/// <summary>
/// Параметр доступа: от чьего имени читаются строки (ТЗ CORE-24.1, WORK-40, issue #965).
///
/// <para>Почему параметром, а не глобальным фильтром запросов и не неявным «текущим пользователем»
/// из контейнера. Глобальный фильтр невидимо обходится фоновыми службами, а ошибка области
/// выглядит как пустой список, неотличимый от «данных нет» (ТЗ OVW-9). Неявный
/// «текущий пользователь» ведёт себя так же: по месту вызова не видно, в чьих правах читаются
/// строки, и первое же чтение из фоновой задачи молча получит «никого» — то есть либо пустоту, либо
/// всё, в зависимости от того, как написана проверка. Поэтому параметр стоит в подписи
/// <see cref="IDataSetRowLoader" /> и <see cref="ISystemDataProvider" />: забыть его нельзя, он не
/// компилируется.</para>
///
/// <para><b>Ключи, а не «права» отдельно от «модулей».</b> Набор требует либо код права, либо код
/// своего модуля — у библиотеки документов качества своего права сегодня нет, и её адреса закрыты
/// воротами модуля (ТЗ AUTH-12.2). Сверять это двумя разными способами значило бы завести второе
/// правило доступа к тем же данным, поэтому проверка идёт ОДНИМ набором ключей: права пользователя
/// плюс коды доступных ему модулей. Тот же приём, что у аудитории уведомлений (ТЗ AUTH-13.1).</para>
///
/// <para><b>Система — не пользователь.</b> Служебное задание без человека (ТЗ WORK-41) идёт от имени
/// системы: у неё нет параметра доступа, и «под правами системы» стало бы ещё одним обходом. Такой
/// параметр объявляется <see cref="OfSystem" /> и опубликованных наборов НЕ читает — отказ с
/// названной причиной, а не пустая таблица.</para>
/// </summary>
public sealed record DataAccess
{
    private DataAccess(Guid? userId, string who, IReadOnlySet<string> keys,
        IReadOnlySet<string> enabledModules, string? systemReason)
    {
        UserId = userId;
        Who = who;
        Keys = keys;
        EnabledModules = enabledModules;
        SystemReason = systemReason;
    }

    /// <summary>Кто спрашивает. null — система (см. <see cref="OfSystem" />).</summary>
    public Guid? UserId { get; }

    /// <summary>Имя для отказа и журнала: «Иванов» или «автоподача отчётов».</summary>
    public string Who { get; }

    /// <summary>Права пользователя плюс коды доступных ему модулей — одним набором.</summary>
    public IReadOnlySet<string> Keys { get; }

    /// <summary>
    /// Коды ВКЛЮЧЁННЫХ модулей экземпляра — независимо от прав спрашивающего.
    ///
    /// Отдельно от <see cref="Keys" /> нарочно: «модуль не подключён» и «модуль есть, а доступа к
    /// нему нет» — разные ответы, и первый обязан звучать своими словами (ТЗ CORE-24.3). Один набор
    /// на оба случая дал бы одну причину на две болезни, и администратор искал бы право там, где
    /// надо включать модуль.
    /// </summary>
    public IReadOnlySet<string> EnabledModules { get; }

    /// <summary>Чем занята система, если это её параметр: попадает в отказ.</summary>
    public string? SystemReason { get; }

    public bool IsSystem => UserId is null;

    /// <summary>Параметр доступа человека.</summary>
    public static DataAccess Of(Guid userId, string who,
        IEnumerable<string> keys, IEnumerable<string> enabledModules) =>
        new(userId, string.IsNullOrWhiteSpace(who) ? userId.ToString() : who,
            new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(enabledModules, StringComparer.OrdinalIgnoreCase),
            null);

    /// <summary>
    /// Параметр доступа служебного задания: человека за ним нет.
    ///
    /// <paramref name="reason" /> — чем задание занято («плановая резервная копия»). Идёт в отказ
    /// при попытке прочитать опубликованный набор, потому что без него отказ читался бы как дефект:
    /// «нет параметра доступа» ничего не говорит о том, кто и зачем пришёл.
    /// </summary>
    public static DataAccess OfSystem(string reason) =>
        new(null, "система", new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), reason);

    /// <summary>Есть ли у спрашивающего ключ — право или доступ к модулю.</summary>
    public bool Allows(string key) => Keys.Contains(key);
}
