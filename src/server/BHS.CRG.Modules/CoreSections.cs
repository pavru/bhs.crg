namespace BHS.CRG.Modules;

/// <summary>
/// Разделы ЯДРА, которыми пользуются модули (ТЗ AUTH-16.1, OVW-18.1; issue #1001): стройки,
/// справочники, наборы данных, учётный период, сверка. Модуль называет их в
/// <see cref="IAppModule.UsesSections" />, и рабочее пространство из его модулей показывает вместе с
/// разделами модулей и эти.
///
/// <para>Без объявления пространство «Счета» показало бы одни счета — и человек не нашёл бы стройку,
/// на которую разносит счёт; или показало бы всё ядро — и сужать стало бы нечего. Список разделов в
/// коде модуля, а не в настройке: какие разделы ядра нужны модулю — часть его поведения, как права.</para>
///
/// <para>⚠️ Коды — договор с клиентом: тот же код стоит у пункта навигации (<c>navConfig.ts</c>,
/// поле <c>section</c>). Сервер разделов не рисует и не проверяет по ним ничего — пространство понятие
/// клиента (AUTH-16.2), — поэтому здесь только имена. Раздел клиента, которого никто не объявил, в
/// суженном пространстве не виден и попадает в строку «Ещё N разделов вне пространства»: ошибка
/// заметна, но раздел не теряется.</para>
/// </summary>
public static class CoreSections
{
    public const string Constructions = "constructions";
    public const string Catalog = "catalog";
    public const string Datasets = "datasets";
    public const string Periods = "periods";
    public const string Reconciliations = "reconciliations";

    public static readonly IReadOnlyList<string> All = [Constructions, Catalog, Datasets, Periods, Reconciliations];

    /// <summary>
    /// Объявление модуля годно: только известные коды и без повторов. Негодное — отказ при старте:
    /// опечатка в коде тихо убрала бы раздел из пространства, и выглядело бы это как «пропал раздел».
    /// </summary>
    public static void Validate(IAppModule module)
    {
        var unknown = module.UsesSections.Where(code => !All.Contains(code, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException(
                $"Модуль «{module.Code}» объявляет разделы ядра, которых нет: {string.Join(", ", unknown)}. " +
                $"Известные — {string.Join(", ", All)} (CoreSections).");

        var repeated = module.UsesSections.GroupBy(code => code, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (repeated.Count > 0)
            throw new InvalidOperationException(
                $"Модуль «{module.Code}» объявляет раздел ядра дважды: {string.Join(", ", repeated)}.");
    }
}
