namespace BHS.CRG.Modules.Ports;

/// <summary>Единица измерения единого справочника ядра (ТЗ TYPE-7.1, TYPE-Q3).</summary>
/// <param name="Name">Название записи единицы — «м», «шт», «компл».</param>
/// <param name="Archived">Единица в архиве (ТЗ CORE-34.4): из выбора убрана, сохранённые ссылки целы.</param>
public sealed record ModuleUnit(Guid Id, string Name, bool Archived);

/// <summary>
/// Справочник единиц измерения ядра — ЧТЕНИЕ (ТЗ PLAN-6.1; change <c>plan-03-work-types-door</c>):
/// выбор единицы и её показ в сопоставлении сметы. Кратности у единицы нет — коэффициент пересчёта
/// живёт у того, кто сопоставляет (D8 зонтика <c>plan-00-epic</c>).
///
/// <para><b><c>null</c> — справочник не заведён</b>: ядро его не заводит, его заводит человек
/// (<c>CoreRecordTypes.UnitCode</c>). Модуль говорит это словами <see cref="NotSetUp" />.</para>
///
/// <para>⚠️ Правами не отбирается — как и остальные справочники ядра (issue #675).</para>
/// </summary>
public interface IModuleUnits
{
    /// <summary>Что сказать человеку, когда порт ответил <c>null</c>.</summary>
    const string NotSetUp =
        "Справочник «Единица измерения» не заведён. Его заводит администратор в общих данных.";

    /// <summary>Единицы по идентификаторам — для показа: архивные на месте, с признаком.</summary>
    Task<IReadOnlyList<ModuleUnit>?> GetAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// Поиск на выбор по части названия или псевдонима: живые единицы по названию, не больше
    /// <paramref name="limit" />, и число архивных под тот же запрос.
    /// </summary>
    Task<ModuleChoice<ModuleUnit>?> SearchAsync(string? query, int limit, CancellationToken ct = default);
}
