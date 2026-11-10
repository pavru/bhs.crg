using BHS.CRG.Modules.Access;

namespace BHS.CRG.Modules.Exchange;

/// <summary>
/// Ответ контракта (или его части) в правах спрашивающего (ТЗ CORE-34): данные, «скрыто правами» или
/// отказ системе.
///
/// <para><b>Общие ответы — одним типом на все контракты</b>, чтобы каждый контракт говорил
/// одинаково, и потребитель рисовал состояние одним компонентом (D17 зонтика plan-00-epic). Свой
/// «нет права» у каждого контракта разошёлся бы на первом же втором: у одного <c>null</c>, у другого
/// пустой список, у третьего нули — и все три неотличимы от «данных нет».</para>
///
/// <para>Контракт с частями под разными правами («Плановые работы»: объёмы, деньги, материалы)
/// отдаёт этим типом КАЖДУЮ часть: скрытая часть называет своё право, остальные приходят.</para>
/// </summary>
public abstract record ContractAnswer<T>
{
    private ContractAnswer() { }

    /// <summary>
    /// Замок иерархии: записи с закрытым конструктором всё равно открыты наследованию — компилятор даёт им
    /// защищённый конструктор копирования, и модуль мог бы завести свой вариант ответа, который
    /// <c>switch</c> потребителя не знает. Переопределить член <c>private protected</c> из другой сборки
    /// нельзя: вариантов ровно столько, сколько объявлено здесь.
    /// </summary>
    private protected abstract void Sealed();

    /// <summary>Данные — в правах спрашивающего.</summary>
    public sealed record Shown(T Value) : ContractAnswer<T>
    {
        private protected override void Sealed() { }
    }

    /// <summary>
    /// «Скрыто правами»: у спрашивающего нет права <paramref name="Permission" />. Данных в ответе нет —
    /// ни нулей, ни пустого списка.
    /// </summary>
    public sealed record Hidden(string Permission) : ContractAnswer<T>
    {
        private protected override void Sealed() { }

        public string Text => $"скрыто правами: нужно право «{Permission}»";
    }

    /// <summary>
    /// Спрашивает система — задание без человека. Контракт ей не отвечает: «под правами системы» стало
    /// бы обходом прав. <paramref name="Reason" /> — причина задания из <see cref="ModuleReader" />.
    /// </summary>
    public sealed record SystemRefused(string Reason) : ContractAnswer<T>
    {
        private protected override void Sealed() { }

        public string Text => $"контракт не отвечает системе: {Reason}";
    }
}

/// <summary>Сборка общих ответов — одна проверка на все контракты.</summary>
public static class ContractAnswer
{
    public static ContractAnswer<T> Shown<T>(T value) => new ContractAnswer<T>.Shown(value);

    public static ContractAnswer<T> Hidden<T>(string permission) => new ContractAnswer<T>.Hidden(permission);

    public static ContractAnswer<T> SystemRefused<T>(string reason) => new ContractAnswer<T>.SystemRefused(reason);

    /// <summary>
    /// Отказ, если он положен, — иначе <c>null</c>, и контракт собирает данные.
    ///
    /// <para>Порядок: сначала система, потом право. У системы ключей нет, и без первой проверки она
    /// получила бы «скрыто правами» — ответ про права там, где дело не в правах.</para>
    ///
    /// <para><paramref name="anyOf" /> — права, любое из которых открывает ответ; «скрыто» называет
    /// первое — главное право ответа.</para>
    /// </summary>
    public static ContractAnswer<T>? Refusal<T>(ModuleReader reader, params string[] anyOf)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (anyOf.Length == 0)
            throw new ArgumentException("Контракт обязан назвать право ответа.", nameof(anyOf));

        if (reader.IsSystem) return SystemRefused<T>(reader.SystemReason ?? "причина не названа");
        return anyOf.Any(reader.Allows) ? null : Hidden<T>(anyOf[0]);
    }
}
