using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.DataSets;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Живое состояние системных источников: число строк (issue #613) и оговорка к данным (issue #626).
/// У остальных форматов <c>CachedRowCount</c> пересчитывается при замене файла или правке
/// извлечения — у системного набора нет ни того, ни другого: файла не существует, а определение
/// источника не редактируется. Число, записанное при создании, устаревает молча — добавили документ
/// в комплект, и «8 строк» в списках и в MCP-срезе уже неправда, хотя сами строки живые.
///
/// Поэтому и число, и оговорка берутся у провайдера на чтении, ОДНИМ вызовом: провайдер собирает
/// строки целиком, и спрашивать его дважды значило бы удваивать работу на каждый источник в списке.
/// Считаем ДО обработки (фильтр/вычисляемые колонки/сортировка) — та же семантика, что у
/// <c>CachedRowCount</c> остальных форматов, иначе одна и та же подпись «N строк» значила бы в
/// списке разное.
/// </summary>
public class SystemSourceCounter(SystemDataProviderRegistry providers)
{
    /// <summary>Что известно про системный источник на момент чтения.</summary>
    public readonly record struct SystemSourceState(int RowCount, string? Warning);

    /// <summary>
    /// Состояние по id источника для системных наборов из выборки (источники берутся из
    /// <see cref="DataSetFile.Sources"/>). Обычные форматы не трогаем — у них кэш поддерживается
    /// штатно. Пустой словарь, если системных наборов в выборке нет.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, SystemSourceState>> StateAsync(
        IEnumerable<DataSetFile> files, CancellationToken ct)
    {
        var states = new Dictionary<Guid, SystemSourceState>();
        foreach (var file in files.Where(f => f.IsSystem))
            await AddAsync(states, file, file.Sources, ct);
        return states;
    }

    /// <summary>То же для источников, загруженных отдельно от файла.</summary>
    public async Task<IReadOnlyDictionary<Guid, SystemSourceState>> StateAsync(
        DataSetFile file, IEnumerable<DataSetSource> sources, CancellationToken ct)
    {
        var states = new Dictionary<Guid, SystemSourceState>();
        if (file.IsSystem) await AddAsync(states, file, sources, ct);
        return states;
    }

    /// <summary>Состояние одного источника; null — набор не системный или маркер неизвестен.</summary>
    public async Task<SystemSourceState?> StateAsync(DataSetSource source, DataSetFile file, CancellationToken ct)
        => file.IsSystem ? await StateAsync(source.SheetOrPath, file, ct) : null;

    private async Task AddAsync(Dictionary<Guid, SystemSourceState> states, DataSetFile file,
        IEnumerable<DataSetSource> sources, CancellationToken ct)
    {
        foreach (var source in sources)
        {
            var state = await StateAsync(source.SheetOrPath, file, ct);
            if (state is not null) states[source.Id] = state.Value;
        }
    }

    // Пересчёт — удобство, а не обязанность: списки наборов не должны падать из-за одного источника.
    // Маркер без провайдера (консолидацию убрали в новой версии) и источник на уровне, где
    // консолидация неприменима (набор остался от версий до гейта #606), отдают запомненное число —
    // оно хотя бы показывает, чем источник был.
    private async Task<SystemSourceState?> StateAsync(string marker, DataSetFile file, CancellationToken ct)
    {
        var provider = providers.TryGet(marker);
        if (provider is null) return null;
        try
        {
            var provided = await provider.ProvideAsync(marker, file.Scope, file.ScopeId, ct);
            return new SystemSourceState(provided.Rows.Count, provided.Warning);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
