namespace BHS.CRG.Modules.Ports;

/// <summary>
/// Периодический такт модуля (ТЗ WORK-41, WORK-10; work-08): ядро раз в <see cref="Period" /> ставит
/// задачу операции <see cref="Operation" /> в общую очередь — ту же, что <see cref="IModuleJobs" />.
/// Объявление — в манифесте (<see cref="IAppModule.Ticks" />), поэтому у выключенного модуля такта нет.
///
/// <para><b>Свой фоновый цикл в модуле — нельзя.</b> Он был бы второй очередью: его задач нет ни в
/// индикаторе, ни в остановке приложения, и два экземпляра делали бы одну работу дважды. Такт ставит
/// задачу с постоянной целью, и пока она стоит или идёт, вторая не ставится.</para>
///
/// <para><b>Догон — у модуля, а не у такта.</b> Такт пропущенных сроков не помнит: после недели простоя
/// он ставит ОДНУ задачу, и та выбирает работу по состоянию («все черновики, чьё окно истекло»), а не по
/// числу пропущенных сроков.</para>
///
/// <para>⚠️ Задача такта идёт от имени системы: автор журнала — «Система — <see cref="Title" />», параметр
/// доступа (<c>IModuleReaders.ForJobAsync</c>) — система с названием такта, и данные в правах (контракты,
/// опубликованные наборы) ей отвечают отказом. Владельца у задачи нет — её отказ уходит общесистемным
/// уведомлением.</para>
/// </summary>
/// <param name="Code">Код такта внутри модуля: <c>autosubmit</c>. Пара «модуль, код» — постоянная цель
/// задачи такта.</param>
/// <param name="Title">Название для человека: «Автоподача отчётов». Заголовок задачи в индикаторе, имя
/// в журнале и причина в отказе.</param>
/// <param name="Operation">Код операции очереди модуля — обработчик <see cref="IModuleJobHandler" />
/// с этим кодом модуль регистрирует сам. С префиксом своего модуля: <c>work.autosubmit</c>.</param>
/// <param name="Period">Как часто ставить задачу. Не меньше <see cref="MinPeriod" />.</param>
public sealed record ModuleTick(string Code, string Title, string Operation, TimeSpan Period)
{
    /// <summary>
    /// Шаг службы такта: чаще она на часы не смотрит, и период короче означал бы обещание, которого
    /// служба не держит.
    /// </summary>
    public static readonly TimeSpan MinPeriod = TimeSpan.FromMinutes(1);

    /// <summary>Что не так с объявлением; пусто — годно. Собирает всё, а не первое.</summary>
    /// <param name="module">Код объявившего модуля: операция обязана начинаться с него.</param>
    public IReadOnlyList<string> Problems(string module)
    {
        var problems = new List<string>();
        var name = string.IsNullOrWhiteSpace(Code) ? "без кода" : $"«{Code}»";
        void Add(string problem) => problems.Add($"такт {name}: {problem}");

        if (string.IsNullOrWhiteSpace(Code)) Add("нет кода — цель его задачи не из чего построить");
        if (string.IsNullOrWhiteSpace(Title)) Add("нет названия — в журнале и отказе было бы пусто");
        if (string.IsNullOrWhiteSpace(Operation))
            Add("нет операции — ставить нечего");
        else if (!Operation.StartsWith(module + ".", StringComparison.Ordinal))
            Add($"операция «{Operation}» не модуля «{module}» — такт ставит только свою работу");
        if (Period < MinPeriod)
            Add($"период {Period} короче шага службы ({MinPeriod.TotalMinutes:0} мин)");

        return problems;
    }
}
