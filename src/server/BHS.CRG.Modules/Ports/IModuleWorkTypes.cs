namespace BHS.CRG.Modules.Ports;

/// <summary>Вид работы классификатора ядра (ТЗ CORE-8) — как его видит модуль.</summary>
/// <param name="Code">Код — первый компонент ключа идентичности (<c>identity:1</c>); <c>null</c> — не заполнен.</param>
/// <param name="Name">Наименование — второй компонент ключа (<c>identity:2</c>); <c>null</c> — не заполнено.</param>
/// <param name="DisplayName">Название записи, которым её показывают человеку.</param>
/// <param name="UnitId">Единица классификатора — запись справочника единиц (PLAN-6.1: ключ позиции перечня).
/// <c>null</c> — единица не стоит ссылкой: не заполнена или вписана значением.</param>
/// <param name="UnitName">Название единицы — СЕЙЧАС, по самой записи единицы, а не по снимку в ссылке.</param>
/// <param name="RequiresLocation">Признак <c>work.requiresLocation</c>: работа требует указания места.</param>
/// <param name="RequiresMaterials">Признак <c>work.requiresMaterials</c>: работа требует указания материалов.</param>
/// <param name="ProducesId">Признак <c>work.producesId</c>: работа попадает в исполнительную документацию.</param>
/// <param name="Archived">Вид в архиве (ТЗ CORE-34.4): из выбора убран, сохранённые ссылки целы.</param>
public sealed record ModuleWorkType(
    Guid Id, string? Code, string? Name, string DisplayName, Guid? UnitId, string? UnitName,
    bool RequiresLocation, bool RequiresMaterials, bool ProducesId, bool Archived);

/// <summary>
/// Классификатор видов работ ядра — ЧТЕНИЕ, типизированно (ТЗ CORE-20.4, PLAN-6.1; change
/// <c>plan-03-work-types-door</c>). Пишет его дверь ядра <c>/api/work-types</c>, а не модуль.
///
/// <para><b>Отдельным портом, а не ролью в <see cref="IModuleCatalog" />.</b> Тот отдаёт данные записи
/// строкой, и единицу модулю пришлось бы выковыривать из JSON по ключу, который заказчик вправе
/// переименовать. Здесь части вида работы найдены ядром по смыслу: код и наименование — по ключу
/// идентичности, единица — по цели поля (справочник единиц), признаки — по тэгам <c>work.*</c>.</para>
///
/// <para><b><c>null</c> — справочник не заведён</b>, а не «видов работ нет»: на чистой установке
/// классификатора нет, пока нет справочника единиц (<c>SkipWhenBlocked</c>). Модуль говорит это
/// человеку словами <see cref="NotSetUp" />, а не показывает пустой выбор.</para>
///
/// <para>⚠️ Правами не отбирается, как и <see cref="IModuleCatalog" />: справочники ядра читает любой
/// вошедший (<c>core.catalog.read</c>), привязки данных к пользователю нет (issue #675).</para>
/// </summary>
public interface IModuleWorkTypes
{
    /// <summary>Что сказать человеку, когда порт ответил <c>null</c>.</summary>
    const string NotSetUp =
        "Справочник «Вид работы» не заведён: на экземпляре нет справочника единиц измерения, на который " +
        "ссылается вид работы. Его заводит администратор в общих данных.";

    /// <summary>
    /// Виды работ по идентификаторам — для ПОКАЗА того, что у модуля уже стоит: архивные на месте, с
    /// признаком. Неизвестный идентификатор в ответ не попадает — разбирается звавший.
    /// </summary>
    Task<IReadOnlyList<ModuleWorkType>?> GetAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// Поиск на ВЫБОР по части кода, наименования, названия или псевдонима (без учёта регистра): живые
    /// виды по коду, не больше <paramref name="limit" />, и число архивных под тот же запрос. Пустой
    /// запрос — все живые.
    /// </summary>
    Task<ModuleChoice<ModuleWorkType>?> SearchAsync(string? query, int limit, CancellationToken ct = default);
}
