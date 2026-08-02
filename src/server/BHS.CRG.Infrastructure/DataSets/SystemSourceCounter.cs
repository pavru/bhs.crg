using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.DataSets;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Живой счётчик строк системных источников (issue #613). У остальных форматов
/// <c>CachedRowCount</c> пересчитывается при замене файла или правке извлечения — у системного
/// набора нет ни того, ни другого: файла не существует, а определение источника не редактируется.
/// Число, записанное при создании, устаревает молча — добавили документ в комплект, и «8 строк»
/// в списках и в MCP-срезе уже неправда, хотя сами строки живые.
///
/// Поэтому для системных источников число берётся у провайдера на чтении. Считаем ДО обработки
/// (фильтр/вычисляемые колонки/сортировка) — та же семантика, что у <c>CachedRowCount</c>
/// остальных форматов, иначе одна и та же подпись «N строк» значила бы в списке разное.
/// </summary>
public class SystemSourceCounter(SystemDataProviderRegistry providers)
{
    /// <summary>
    /// Число строк по id источника для системных наборов из выборки (источники берутся из
    /// <see cref="DataSetFile.Sources"/>). Обычные форматы не трогаем — у них кэш поддерживается
    /// штатно. Пустой словарь, если системных наборов в выборке нет.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, int>> CountAsync(
        IEnumerable<DataSetFile> files, CancellationToken ct)
    {
        var counts = new Dictionary<Guid, int>();
        foreach (var file in files.Where(f => f.IsSystem))
            await AddAsync(counts, file, file.Sources, ct);
        return counts;
    }

    /// <summary>То же для источников, загруженных отдельно от файла.</summary>
    public async Task<IReadOnlyDictionary<Guid, int>> CountAsync(
        DataSetFile file, IEnumerable<DataSetSource> sources, CancellationToken ct)
    {
        var counts = new Dictionary<Guid, int>();
        if (file.IsSystem) await AddAsync(counts, file, sources, ct);
        return counts;
    }

    /// <summary>Число строк одного источника; null — набор не системный или маркер неизвестен.</summary>
    public async Task<int?> CountAsync(DataSetSource source, DataSetFile file, CancellationToken ct)
        => file.IsSystem ? await CountAsync(source.SheetOrPath, file, ct) : null;

    private async Task AddAsync(Dictionary<Guid, int> counts, DataSetFile file,
        IEnumerable<DataSetSource> sources, CancellationToken ct)
    {
        foreach (var source in sources)
        {
            var count = await CountAsync(source.SheetOrPath, file, ct);
            if (count is not null) counts[source.Id] = count.Value;
        }
    }

    // Пересчёт — удобство, а не обязанность: списки наборов не должны падать из-за одного источника.
    // Маркер без провайдера (консолидацию убрали в новой версии) и источник на уровне, где
    // консолидация неприменима (набор остался от версий до гейта #606), отдают запомненное число —
    // оно хотя бы показывает, чем источник был.
    private async Task<int?> CountAsync(string marker, DataSetFile file, CancellationToken ct)
    {
        var provider = providers.TryGet(marker);
        if (provider is null) return null;
        try
        {
            var provided = await provider.ProvideAsync(marker, file.Scope, file.ScopeId, ct);
            return provided.Rows.Count;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
