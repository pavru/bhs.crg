namespace BHS.CRG.Modules.Exchange;

/// <summary>
/// Интерфейс — межмодульный контракт (ТЗ CORE-34, OVW-12; plan-22): через него один модуль читает
/// данные другого. Интерфейс лежит здесь, в контрактах ядра, реализацию регистрирует поставщик
/// (<see cref="ModuleContractServices.AddModuleContract{TContract, TImplementation}" />), потребитель
/// получает её портом <see cref="IModuleContracts" />.
///
/// <para><b>Поставщик назван у контракта, а не выведен из регистрации.</b> Иначе «кто отвечает за
/// „Плановые работы“» узнавался бы по тому, кто первым зарегистрировал реализацию, и у выключенного
/// поставщика потребитель не смог бы сказать, ЧЕГО не хватает: «не подключено: модуль
/// „Планирование“» требует знать поставщика тогда, когда его служб в контейнере нет вовсе. А
/// чужая регистрация (модуль реализовал контракт, обещанный другим) без названного поставщика была
/// бы неотличима от законной.</para>
///
/// <para><b>Контракт со многими поставщиками</b> (<see cref="Many" />) — для вкладов в общий экран
/// («Колонка Хода работ», PLAN-21): реализацию даёт каждый включённый модуль, не больше одной, и
/// потребитель получает список. Поставщика у такого контракта нет — его не называют. Остальные
/// контракты строго с одним поставщиком; каталог при старте перечисляет каждый контракт
/// <see cref="Many" />, чтобы признак не стал лазейкой.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class ModuleContractAttribute : Attribute
{
    /// <summary>Контракт одного поставщика: <c>[ModuleContract("plan", "Плановые работы")]</c>.</summary>
    public ModuleContractAttribute(string provider, string title)
    {
        Provider = provider;
        Title = title;
    }

    /// <summary>
    /// Контракт со многими поставщиками: <c>[ModuleContract("Колонка Хода работ", Many = true)]</c>.
    /// Без <see cref="Many" /> такое объявление — отказ при старте: поставщик не назван.
    /// </summary>
    public ModuleContractAttribute(string title) => Title = title;

    /// <summary>Код модуля-поставщика; <c>null</c> у контракта со многими поставщиками.</summary>
    public string? Provider { get; }

    /// <summary>Название контракта для человека: попадает в отказы старта и в «не подключено».</summary>
    public string Title { get; }

    /// <summary>Реализацию даёт каждый включённый модуль — не больше одной на модуль.</summary>
    public bool Many { get; init; }

    /// <summary>Что не так с объявлением; <c>null</c> — годно.</summary>
    public string? Problem() =>
        string.IsNullOrWhiteSpace(Title) ? "у контракта нет названия"
        : Many && Provider is not null ? $"контракт со многими поставщиками называет поставщика «{Provider}»"
        : !Many && string.IsNullOrWhiteSpace(Provider) ? "поставщик не назван, а контракт не объявлен со многими поставщиками"
        : null;
}
