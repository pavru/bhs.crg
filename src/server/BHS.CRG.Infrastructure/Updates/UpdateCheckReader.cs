using BHS.CRG.Application.Settings;
using BHS.CRG.Application.Updates;

namespace BHS.CRG.Infrastructure.Updates;

/// <summary>
/// Отдаёт то, что служба уже узнала, БЕЗ похода в сеть (issue #813).
///
/// Отдельно от самой службы намеренно: страницу настроек и подвал боковой панели рисует каждый
/// заход в систему, и запрос к GitHub на этом пути означал бы секунды пустого экрана — ровно то, от
/// чего в настройках интеграций уже отказались («сюда НЕ добавляем проверок, ходящих в сеть»).
/// </summary>
public class UpdateCheckReader(ServiceStateStore store, IIntegrationSettings settings) : IUpdateCheck
{
    public async Task<UpdateStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var state = await store.LoadAsync<UpdateCheckState>(UpdateCheckStateKeys.UpdateCheck, ct);
        var effective = await settings.GetEffectiveAsync(ct);
        var installed = AppVersion.SplitInformational(AppVersion.InformationalOfEntryAssembly()).Version;

        return new UpdateStatus(
            installed,
            AppVersion.Normalize(state.LatestVersion),
            AppVersion.IsNewer(state.LatestVersion, installed),
            state.ReleaseUrl,
            state.ReleaseNotes,
            state.LastCheckedAt,
            effective.Updates.Enabled);
    }
}

/// <summary>Ключи записей в <c>service_state</c> — в одном месте, чтобы читатель и писатель не
/// разъехались опечаткой (разъехавшись, они молча работали бы каждый со своей строкой).</summary>
public static class UpdateCheckStateKeys
{
    public const string UpdateCheck = "update-check";
}
