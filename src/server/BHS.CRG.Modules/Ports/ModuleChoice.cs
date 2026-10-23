namespace BHS.CRG.Modules.Ports;

/// <summary>
/// Ответ поиска справочника ядра на выбор (виды работ, единицы, сотрудники): живые записи — и сколько
/// под тот же запрос лежит в архиве. Как <see cref="ModuleCatalogChoice" />: не найдя запись, человек
/// заведёт её заново, а она есть, в архиве, — кто показывает список, обязан это сказать.
/// </summary>
/// <param name="Items">Живые записи; архивных здесь нет.</param>
/// <param name="InArchive">Сколько архивных записей подходит под тот же запрос.</param>
public sealed record ModuleChoice<T>(IReadOnlyList<T> Items, int InArchive);
