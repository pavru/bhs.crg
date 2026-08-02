using BHS.CRG.Domain.Catalog;

namespace BHS.CRG.Application.DataSets;

/// <summary>
/// Поставщик строк системного набора — консолидация данных самой системы (документы комплекта,
/// общие данные, документы качества) в таблицу, с которой дальше работает обычный пайплайн
/// наборов: фильтр, вычисляемые колонки, сортировка, маппинг в поля, превью, экспорт.
///
/// Объединение объектов РАЗНЫХ типов — задача самого провайдера: он свободно запрашивает БД, а
/// общие колонки разнородных схем сводит через функциональные тэги (doc.number и т.п.), а не по
/// именам полей.
///
/// Контекст консолидации — расположение НАБОРА (scope + scopeId), не владельца привязки: строки
/// нужны и там, где владельца нет вовсе (превью источника, экспорт, MCP-срез, сверка).
/// </summary>
public interface ISystemDataProvider
{
    /// <summary>Отвечает ли провайдер за маркер (маркер может нести параметр после префикса).</summary>
    bool Handles(string marker);

    /// <summary>
    /// Что этот провайдер может предложить на данном уровне — кандидаты диалога создания источника.
    /// Пусто, если консолидация на этом уровне не имеет смысла (например, документы комплекта
    /// у набора уровня «Стройка»).
    /// </summary>
    Task<IReadOnlyList<DataSetSourceInfo>> GetCandidatesAsync(
        CatalogScope scope, Guid? scopeId, CancellationToken ct);

    /// <summary>Строки консолидации на момент вызова — данные живые, не кэшируются.</summary>
    Task<DataSetParseResult> ProvideAsync(
        string marker, CatalogScope scope, Guid? scopeId, CancellationToken ct);
}

/// <summary>Провайдеры системных консолидаций — по образцу <c>DataSetParserFactory</c>.</summary>
public class SystemDataProviderRegistry(IEnumerable<ISystemDataProvider> providers)
{
    public ISystemDataProvider Get(string marker)
        => providers.FirstOrDefault(p => p.Handles(marker))
            ?? throw new InvalidOperationException($"Нет провайдера системных данных для «{marker}»");

    public IReadOnlyList<ISystemDataProvider> All => [.. providers];
}
