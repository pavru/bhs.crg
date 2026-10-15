using BHS.CRG.Domain.Common;
using Microsoft.AspNetCore.Http;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Серверная ветвь «Избранных строек» (issue #1222, эпик #1217). Список накладных — единственный
/// страничный в первой версии: он отдаёт не больше <see cref="WaybillEndpoints.ListLimit" /> строк, и
/// урезанный на клиенте он показал бы три накладные из трёхсот, а остальные избранные не показал бы
/// вовсе. Поэтому отбирает сервер — по перечню, который прислал клиент.
///
/// <para>Настройку пользователя сервер НЕ читает: избранное — фильтр экранов, а не права (см.
/// <c>FavoriteConstructionValues</c>). Перечень приходит параметром <c>sites</c>, и адрес без него
/// отвечает всем — так его зовут MCP, экспорт и всё, что о фильтре не знает.</para>
///
/// <para>Правило то же, что у клиента (<c>rule.ts</c>): избранное пересекается с живым справочником, и
/// пустое после пересечения означает «фильтра нет»; накладная без стройки и со ссылкой на удалённую
/// видна всегда. Сверяет обе ветви общий файл случаев <c>siteFilterCases.json</c>: две ветви одного
/// правила иначе расходятся молча.</para>
/// </summary>
public static class WaybillSiteFilter
{
    public const string Parameter = "sites";

    /// <summary>
    /// Предел перечня — тот же, что у настройки (<c>FavoriteConstructionValues.MaxFavorites</c>): клиент
    /// больше не пришлёт. Модулю ядро не видно (ModuleBoundaryTests), совпадение сверяет тест.
    /// </summary>
    public const int MaxSites = 20;

    /// <summary>
    /// Перечень из запроса; <c>null</c> — параметра нет, отбора нет. Пустой и негодный — отказ, а не
    /// «отбора нет»: клиент, приславший пустой перечень, ждал урезанного списка и получил бы полный,
    /// неотличимый от урезанного.
    /// </summary>
    public static IReadOnlyList<Guid>? Parse(IQueryCollection query)
    {
        if (!query.TryGetValue(Parameter, out var raw)) return null;
        if (raw.Count != 1 || string.IsNullOrEmpty(raw[0]))
            throw new InvalidRequestException("Перечень строек пуст: без избранного параметр sites не передают.");

        var parts = raw[0]!.Split(',');
        if (parts.Length > MaxSites)
            throw new InvalidRequestException($"В перечне строек больше {MaxSites}: столько избранных не бывает.");

        var ids = new List<Guid>(parts.Length);
        foreach (var part in parts)
            // Строгая форма «D» и длина отдельно — как у настройки избранного: разбор GUID молча режет
            // пробелы по краям, и «a, b» прошло бы.
            if (part.Length != 36 || !Guid.TryParseExact(part, "D", out var id) || ids.Contains(id))
                throw new InvalidRequestException(
                    "Перечень строек негоден: нужны идентификаторы через запятую, без пробелов и повторов.");
            else ids.Add(id);

        return ids;
    }

    /// <summary>
    /// Стройки, чьи накладные скрыты: живые, но не избранные. Пустой перечень — фильтр не действует:
    /// избранное пусто после пересечения со справочником (удалили последнюю избранную стройку), и
    /// список возвращается к «всё», а не пустеет.
    ///
    /// <para>Отбор выражен через СКРЫТЫЕ, а не через видимые: тогда «без стройки» и «стройка удалена»
    /// видны сами собой — их нет среди живых, — и отдельного условия для них нет, которое можно забыть.</para>
    /// </summary>
    public static List<Guid?> Hidden(IReadOnlyList<Guid> favorites, IEnumerable<Guid> live)
    {
        var alive = live.ToHashSet();
        if (!favorites.Any(alive.Contains)) return [];
        return [.. alive.Where(id => !favorites.Contains(id)).Select(id => (Guid?)id)];
    }
}
