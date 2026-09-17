namespace BHS.CRG.Application.Notifications;

/// <summary>Что произошло с подтверждённым состоянием компонента на этой пробе.</summary>
public enum HealthTransition
{
    /// <summary>Ничего объявлять не нужно.</summary>
    None,

    /// <summary>Отказ подтверждён — объявляем.</summary>
    WentDown,

    /// <summary>Компонент вернулся — объявляем.</summary>
    CameUp,
}

/// <summary>
/// Между пробой и объявлением. Одна неудачная проба внешнего движка не значит ничего: живой случай —
/// 300 уведомлений «недоступен/восстановлен» на исправно работавшем движке, потому что проба падала
/// через раз (issue #917).
///
/// Пороги зависят от класса компонента, а не едины: у базы проверка — <c>CanConnectAsync</c> к
/// локальному сокету, мигать там нечем, и откладывать настоящую аварию на пару минут неприемлемо.
/// У движка за чужой сетью всё наоборот.
///
/// Состояние ведётся по КОДУ компонента. Отображаемое имя для этого не годится: переименование
/// молча обнулило бы счётчики, и это не проявилось бы ничем.
/// </summary>
public sealed class HealthHysteresis
{
    /// <summary>Сколько неудач подряд подтверждают отказ движка. Три пробы при круге в 45 с — около двух минут.</summary>
    public const int EngineDownStreak = 3;

    /// <summary>Сколько удач подряд отменяют отказ движка. Один ответ после отказа ещё ничего не значит.</summary>
    public const int EngineUpStreak = 2;

    private sealed class Counter
    {
        public HealthState? Published;
        public int Fails;
        public int Successes;
    }

    private readonly Dictionary<string, Counter> _counters = [];

    /// <summary>Пороги подтверждения: сколько неудач до объявления отказа и сколько удач до отмены.</summary>
    public static (int Down, int Up) Thresholds(HealthClass @class) => @class switch
    {
        HealthClass.Core => (1, 1),
        _ => (EngineDownStreak, EngineUpStreak),
    };

    /// <summary>
    /// Учитывает пробу и говорит, что объявлять. Первое в жизни удачное наблюдение фиксирует норму
    /// молча — иначе каждый запуск начинался бы с «восстановлен».
    /// </summary>
    public HealthTransition Observe(string code, HealthClass @class, bool ok)
    {
        var counter = _counters.TryGetValue(code, out var existing) ? existing : _counters[code] = new Counter();
        var (down, up) = Thresholds(@class);

        counter.Fails = ok ? 0 : counter.Fails + 1;
        counter.Successes = ok ? counter.Successes + 1 : 0;

        if (counter.Published != HealthState.Down && counter.Fails >= down)
        {
            counter.Published = HealthState.Down;
            return HealthTransition.WentDown;
        }

        if (counter.Published == HealthState.Down && counter.Successes >= up)
        {
            counter.Published = HealthState.Up;
            return HealthTransition.CameUp;
        }

        if (counter.Published is null && ok) counter.Published = HealthState.Up;
        return HealthTransition.None;
    }

    /// <summary>
    /// Состояние для показа. Пока компонент ни разу не подтверждён, показываем саму пробу: иначе
    /// мёртвая при старте база числилась бы «в норме» до конца серии.
    /// </summary>
    public HealthState StateOf(string code, bool ok)
    {
        if (!_counters.TryGetValue(code, out var counter) || counter.Published is not { } published)
            return ok ? HealthState.Up : HealthState.Down;

        if (published == HealthState.Up && !ok) return HealthState.Flapping;
        if (published == HealthState.Down && ok) return HealthState.Flapping;
        return published;
    }

    /// <summary>
    /// Что объявлено по каждому компоненту — то, что надо пережить перезапуск (issue #920).
    ///
    /// Именно ОБЪЯВЛЕННОЕ, а не текущее состояние и не счётчики серий. Текущее описывает мир до
    /// перезапуска и солгало бы на первой же странице после него; счётчики — о пробах, которых
    /// новый процесс не делал. А объявленное нужно, чтобы не повторить уже сказанное и суметь
    /// сказать о возврате.
    /// </summary>
    public IReadOnlyDictionary<string, HealthState> Announced =>
        _counters.Where(c => c.Value.Published is not null)
                 .ToDictionary(c => c.Key, c => c.Value.Published!.Value);

    /// <summary>
    /// Возвращает объявленное прошлым процессом. Без этого после перезапуска уже объявленный отказ
    /// объявлялся заново, а компонент, поднявшийся во время перезапуска, фиксировался в норме молча
    /// — и в колокольчике навсегда оставалось «недоступен» без парного «восстановлен».
    ///
    /// Применяется только к компонентам, по которым этот процесс ещё ничего не решил: если чтение
    /// сохранённого запоздало (база не отвечала на старте), свежее решение важнее старого.
    /// Промежуточное состояние объявлением не бывает и не принимается.
    /// </summary>
    public void Restore(IReadOnlyDictionary<string, HealthState> announced)
    {
        foreach (var (code, state) in announced)
        {
            if (state is not (HealthState.Up or HealthState.Down)) continue;
            var counter = _counters.TryGetValue(code, out var existing) ? existing : _counters[code] = new Counter();
            counter.Published ??= state;
        }
    }

    /// <summary>
    /// Забывает компоненты, которых больше не проверяем. Без этого выключенный и снова включённый
    /// движок наследовал бы счётчики прошлой жизни.
    /// </summary>
    public void Retain(IEnumerable<string> codes)
    {
        var keep = codes.ToHashSet();
        foreach (var code in _counters.Keys.Where(c => !keep.Contains(c)).ToList())
            _counters.Remove(code);
    }
}
