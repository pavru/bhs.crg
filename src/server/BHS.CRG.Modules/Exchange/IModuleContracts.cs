namespace BHS.CRG.Modules.Exchange;

/// <summary>
/// Порт потребителю контракта (ТЗ CORE-34, OVW-12): единственный путь к реализации другого модуля.
///
/// <para><b>«Не подключено» — ответом, а не исключением и не пустотой.</b> Выключенный поставщик не
/// регистрирует ничего (<see cref="IAppModule.RegisterServices" />), и прямой запрос интерфейса из
/// контейнера дал бы исключение разрешения служб, а «на всякий случай» обёрнутый — пустоту, неотличимую
/// от «данных нет» (OVW-9). Здесь же потребитель получает <see cref="ModuleContractAnswer{T}.NotConnected" />
/// с названием поставщика — и обязан его показать.</para>
///
/// <para>Прямой запрос интерфейса контракта из контейнера ловит перепись по исходникам
/// (<c>ModuleContractRequestInventoryTests</c>).</para>
/// </summary>
public interface IModuleContracts
{
    /// <summary>
    /// Контракт одного поставщика. Интерфейс без <see cref="ModuleContractAttribute" /> или контракт
    /// со многими поставщиками — исключение: это дефект вызывающего кода, а не состояние экземпляра.
    /// </summary>
    ModuleContractAnswer<T> Get<T>() where T : class;

    /// <summary>
    /// Контракт со многими поставщиками: реализации ВКЛЮЧЁННЫХ модулей в порядке состава поставки.
    /// Пустой список — законный ответ: вкладов нет ни у одного включённого модуля.
    /// </summary>
    IReadOnlyList<ProvidedContract<T>> GetAll<T>() where T : class;
}

/// <summary>Ответ порта на контракт одного поставщика.</summary>
public abstract record ModuleContractAnswer<T> where T : class
{
    private ModuleContractAnswer() { }

    /// <summary>
    /// Замок иерархии: записи с закрытым конструктором всё равно открыты наследованию — компилятор даёт им
    /// защищённый конструктор копирования, и модуль мог бы завести свой вариант ответа, который
    /// <c>switch</c> потребителя не знает. Переопределить член <c>private protected</c> из другой сборки
    /// нельзя: вариантов ровно столько, сколько объявлено здесь.
    /// </summary>
    private protected abstract void Sealed();

    /// <summary>Поставщик включён: его реализация.</summary>
    public sealed record Connected(T Contract) : ModuleContractAnswer<T>
    {
        private protected override void Sealed() { }
    }

    /// <summary>
    /// Поставщик выключен на этом экземпляре (или его нет в сборке). <paramref name="ProviderTitle" /> —
    /// название модуля для человека; модуля нет в сборке — его код.
    /// </summary>
    public sealed record NotConnected(string Provider, string ProviderTitle, string ContractTitle)
        : ModuleContractAnswer<T>
    {
        private protected override void Sealed() { }

        /// <summary>Что показать на месте ответа: «не подключено: модуль „Планирование“».</summary>
        public string Text => $"не подключено: модуль «{ProviderTitle}»";
    }
}

/// <summary>Реализация контракта со многими поставщиками — и чья она.</summary>
public sealed record ProvidedContract<T>(string Module, T Contract) where T : class;
