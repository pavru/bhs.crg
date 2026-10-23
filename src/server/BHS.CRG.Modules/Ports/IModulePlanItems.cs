using System.Diagnostics.CodeAnalysis;

namespace BHS.CRG.Modules.Ports;

/// <summary>
/// Ключ позиции перечня работ (ТЗ CORE-12): вид работы, стройка, раздел, единица — и ничего больше.
/// </summary>
/// <param name="WorkTypeId">Запись классификатора видов работ.</param>
/// <param name="ConstructionId">Стройка, к перечню которой относится позиция.</param>
/// <param name="SectionId">Раздел стройки — или <c>null</c>: «стройка в целом» (ТЗ CORE-Q2). Пусто здесь —
/// не «неизвестно», а значение: позиция без раздела одна на «вид, стройка, единица».</param>
/// <param name="UnitId">Запись справочника единиц измерения.</param>
public readonly record struct PlanItemKey(Guid WorkTypeId, Guid ConstructionId, Guid? SectionId, Guid UnitId);

/// <summary>Составляющая ключа позиции, из-за которой позиция не заведена.</summary>
public enum PlanItemKeyPart
{
    // Значения с единицы, как у назначений чтения ядра: нулевое — забытое поле, а не «вид работы».
    WorkType = 1,
    Construction = 2,
    Section = 3,
    Unit = 4,
}

/// <summary>Позиция перечня: её идентификатор и признак, что её завёл ЭТОТ вызов, а не нашёл.</summary>
public sealed record PlanItemResult(Guid Id, bool Created);

/// <summary>
/// Отказ по ключу: позиция не заведена, и названо, какая составляющая тому виной.
/// </summary>
/// <param name="Reason">Что сказать человеку — например, «вид работы в архиве».</param>
public sealed record PlanItemRefusal(PlanItemKey Key, PlanItemKeyPart Part, string Reason);

/// <summary>
/// Ответ на один ключ: ровно одно из двух — позиция или отказ. Конструктор закрыт, чтобы «ни того, ни
/// другого» нельзя было собрать: звавший разбирает ответ по <see cref="Found" />, а не проверкой двух полей.
/// </summary>
public sealed record PlanItemOutcome
{
    private PlanItemOutcome(PlanItemResult? item, PlanItemRefusal? refusal) => (Item, Refusal) = (item, refusal);

    public PlanItemResult? Item { get; }
    public PlanItemRefusal? Refusal { get; }

    /// <summary>Позиция найдена или заведена; иначе — отказ.</summary>
    [MemberNotNullWhen(true, nameof(Item))]
    [MemberNotNullWhen(false, nameof(Refusal))]
    public bool Found => Item is not null;

    public static PlanItemOutcome Of(PlanItemResult item) => new(item, null);
    public static PlanItemOutcome Of(PlanItemRefusal refusal) => new(null, refusal);
}

/// <summary>
/// Ответ на пакет ключей: позиции по ключам — и отказы поимённо. Пакет из-за одной плохой строки
/// целиком не отвергается.
/// </summary>
/// <param name="Items">Каждому годному ключу пакета — его позиция (повторы ключа в пакете сливаются).</param>
/// <param name="Refusals">По одному отказу на негодный ключ.</param>
public sealed record PlanItemBatch(
    IReadOnlyDictionary<PlanItemKey, PlanItemResult> Items, IReadOnlyList<PlanItemRefusal> Refusals);

/// <summary>Позиция перечня, прочитанная по идентификатору.</summary>
public sealed record ModulePlanItem(Guid Id, PlanItemKey Key);

/// <summary>
/// Перечень работ стройки — «найди или создай» позиции по ключу (ТЗ CORE-10, CORE-12, STG-6.1; change
/// <c>plan-04-plan-item-port</c>). Позиция — общая точка модулей: смета держит на ней план, график —
/// сроки, учёт работ — факт; каждый модуль хранит своё мнение о ней у себя.
///
/// <para><b>Идемпотентно и без гонок.</b> Повтор с тем же ключом возвращает ту же позицию; два
/// одновременных вызова с одним ключом дают одну позицию — конфликт решает уникальный ключ базы, а не
/// «прочитал — не нашёл — вставил».</para>
///
/// <para><b>Позиции заводятся ДО своих строк модуля и своей транзакцией ядра</b>: общей транзакции у
/// схемы ядра и схемы модуля нет (ТЗ CORE-4). Упади модуль после порта — повтор найдёт те же позиции.
/// Позиция без держателей — допустимое состояние: её найдёт следующий вызов.</para>
///
/// <para><b>Ключ проверяется</b>: вид работы и единица — существующие не архивные записи своих
/// справочников (ТЗ CORE-34.4: новая ссылка на архивную запись — отказ), стройка есть, раздел принадлежит
/// стройке ключа. Нарушение — <see cref="PlanItemRefusal" /> с составляющей, а не позиция.</para>
///
/// <para>⚠️ Архив проверяется и у ключа, позиция которого УЖЕ заведена: строки модуля, которые повесят на
/// неё после ответа, — новые ссылки на архивный вид работы. Поэтому повтор импорта, между попытками которого
/// вид работы отправили в архив, получает отказ, а не прежнюю позицию: человеку надо сказать про архив, а не
/// дописать смету на работу, выведенную из выбора.</para>
///
/// <para>⚠️ Ни адреса API, ни действия в интерфейсе у порта нет и быть не должно (ТЗ CORE-12): телефон и
/// интерфейс позиций не заводят, иначе справочник разошёлся бы в написаниях. Правами порт не отбирает —
/// их проверил адрес модуля, который его зовёт. Удаляется позиция только без держателей — держание
/// объявляется ссылкой модуля на <c>ReferenceTarget.WorkPlanItem</c>.</para>
/// </summary>
public interface IModulePlanItems
{
    /// <summary>Найти позицию по ключу или завести её.</summary>
    Task<PlanItemOutcome> FindOrCreateAsync(PlanItemKey key, CancellationToken ct = default);

    /// <summary>
    /// То же пакетом — для импорта на сотни строк: годные ключи найдены или заведены, негодные названы
    /// отказами. Пакет любого размера: порт режет его на части сам.
    /// </summary>
    Task<PlanItemBatch> FindOrCreateManyAsync(IReadOnlyCollection<PlanItemKey> keys, CancellationToken ct = default);

    /// <summary>Позиции по идентификаторам, с ключом. Неизвестный идентификатор в ответ не попадает.</summary>
    Task<IReadOnlyList<ModulePlanItem>> GetAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
}
