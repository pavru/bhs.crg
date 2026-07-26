namespace BHS.CRG.Infrastructure.Reconciliation;

/// <summary>
/// Сведение ключа к каноническому по подтверждённым алиасам (issue #446).
///
/// Резолв транзитивный: человек неизбежно заведёт цепочку А→Б, а потом Б→В, и остановившись на первом
/// шаге, мы получили бы две позиции там, где он ожидает одну. Цикл при этом не должен вешать прогон,
/// поэтому обход помнит пройденное и на повторе останавливается.
/// </summary>
public sealed class AliasResolver
{
    private readonly IReadOnlyDictionary<string, string> _map;
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);

    /// <param name="confirmed">Только ПОДТВЕРЖДЁННЫЕ пары «вариант → канон». Предложенный алиас в
    /// сравнении участвовать не должен: это была бы модель внутри арифметики.</param>
    public AliasResolver(IReadOnlyDictionary<string, string> confirmed) => _map = confirmed;

    public static AliasResolver Empty { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal));

    public string Resolve(string key)
    {
        if (_map.Count == 0) return key;
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = key;
        while (visited.Add(current) && _map.TryGetValue(current, out var next))
            current = next;

        _cache[key] = current;
        return current;
    }
}
