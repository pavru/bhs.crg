namespace BHS.CRG.Modules.Access;

/// <summary>
/// Параметр доступа контракта: от чьего имени модуль спрашивает данные другого модуля (ТЗ CORE-34,
/// AUTH-6; plan-22).
///
/// <para><b>Параметром, а не «текущим пользователем» из контейнера</b> — по той же причине, что у
/// параметра доступа наборов данных (CORE-24.1): по месту вызова обязано быть видно, в чьих правах
/// отвечает контракт, а первое же чтение из фоновой операции получило бы «никого» молча — то есть
/// либо пустоту, либо всё. Поэтому параметр стоит в подписи каждого метода контракта, и забыть его
/// нельзя: не компилируется.</para>
///
/// <para><b>Ключи</b> — права спрашивающего плюс коды модулей, которые ему открыты, одним набором:
/// зеркало параметра доступа ядра (<c>DataAccess</c>), и сверку имён держит
/// <c>ModulePortMirrorTests</c>. Строит параметр ядро — портом <see cref="Ports.IModuleReaders" />;
/// модуль сам ключей не собирает, иначе у каждого было бы своё правило «что открыто».</para>
///
/// <para><b>Система — не пользователь.</b> Задание без человека спрашивает от имени системы
/// (<see cref="OfSystem" />), и контракт отвечает ей ОТКАЗОМ с причиной
/// (<see cref="Exchange.ContractAnswer{T}.SystemRefused" />), а не данными «под правами системы»: иначе
/// это был бы ещё один обход прав.</para>
///
/// <para><b>Охвата здесь нет — и это решение</b> (<c>work-07</c>). «Свои / стройки / все» (ТЗ WORK-40)
/// вычисляет модуль по своим данным — стройки менеджера живут в его назначениях, — и отвечает им
/// <see cref="ModuleReach" />. Параметр несёт только то, что знает ядро: кто спрашивает и его ключи.
/// ⚠️ Переименовывать поля нельзя — по ним сверено зеркало.</para>
/// </summary>
public sealed record ModuleReader
{
    private ModuleReader(Guid? userId, string who, IReadOnlySet<string> keys, string? systemReason)
    {
        UserId = userId;
        Who = who;
        Keys = keys;
        SystemReason = systemReason;
    }

    /// <summary>Кто спрашивает. <c>null</c> — система (см. <see cref="OfSystem" />).</summary>
    public Guid? UserId { get; }

    /// <summary>Имя для отказа и журнала: «Иванов» или «система».</summary>
    public string Who { get; }

    /// <summary>Права спрашивающего плюс коды открытых ему модулей — одним набором.</summary>
    public IReadOnlySet<string> Keys { get; }

    /// <summary>Чем занята система, если это её параметр: попадает в отказ.</summary>
    public string? SystemReason { get; }

    public bool IsSystem => UserId is null;

    /// <summary>Параметр доступа человека — в запросе или в его фоновой операции.</summary>
    public static ModuleReader Of(Guid userId, string who, IEnumerable<string> keys) =>
        new(userId, string.IsNullOrWhiteSpace(who) ? userId.ToString() : who,
            new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase), null);

    /// <summary>
    /// Параметр доступа задания без человека. <paramref name="reason" /> — чем задание занято
    /// («ночной пересчёт сводки»): без неё отказ читался бы как дефект.
    /// </summary>
    public static ModuleReader OfSystem(string reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? throw new ArgumentException(
                "Система обязана назвать причину: отказ без неё не говорит, кто и зачем пришёл.", nameof(reason))
            : new(null, "система", new HashSet<string>(StringComparer.OrdinalIgnoreCase), reason);

    /// <summary>Есть ли у спрашивающего ключ — право или доступ к модулю.</summary>
    public bool Allows(string key) => Keys.Contains(key);
}
