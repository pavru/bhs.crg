namespace BHS.CRG.Application.Templates;

/// <summary>
/// Занятые названия шаблонов типа — одним запросом названий, без строк шаблонов: общий репозиторий
/// тянул бы ради проверки весь Typst-текст каждой версии, а копия без названия — по разу на кандидата.
/// </summary>
public interface ITemplateNames
{
    /// <summary>Есть ли у типа шаблон с этим названием (точное сравнение — D1).</summary>
    Task<bool> IsTakenAsync(Guid documentTypeId, string name, CancellationToken ct = default);

    /// <summary>Различные занятые названия типа, начинающиеся с <paramref name="prefix" />.</summary>
    Task<IReadOnlySet<string>> StartingWithAsync(Guid documentTypeId, string prefix, CancellationToken ct = default);

    /// <summary>
    /// Создание каждого шаблона типа — наименьший <c>CreatedAt</c> его версий, по названию. Ключ
    /// запасного выбора (D2, <see cref="Generation.TemplateChoice.Order"/>) без чтения исторических
    /// версий: им нужен только возраст шаблона, а не их текст.
    /// </summary>
    Task<IReadOnlyDictionary<string, DateTimeOffset>> FirstCreatedAsync(Guid documentTypeId, CancellationToken ct = default);
}
