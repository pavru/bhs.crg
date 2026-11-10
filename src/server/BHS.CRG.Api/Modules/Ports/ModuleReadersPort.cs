using BHS.CRG.Api.Auth;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Порт <see cref="IModuleReaders" />: параметр доступа контракта строится из параметра доступа ядра
/// (<see cref="DataAccess" />), тем же счётчиком прав, что у ворот адресов и наборов данных (ТЗ AUTH-6).
///
/// <para>Не второй расчёт ключей, а перенос готового: правило «ключ модуля = модуль открыт» живёт в
/// <see cref="DataAccessResolver" /> одно, и разойтись с воротами здесь нечему.</para>
/// </summary>
public sealed class ModuleReadersPort(DataAccessResolver access, IHttpContextAccessor http) : IModuleReaders
{
    public async Task<ModuleReader> ForRequestAsync(CancellationToken ct = default) =>
        From(await access.ForRequestAsync(http, ct));

    public async Task<ModuleReader> ForJobAsync(ModuleJobRun run, CancellationToken ct = default) =>
        From(await access.ForUserAsync(run.UserId, ct));

    public ModuleReader ForSystem(string reason) => ModuleReader.OfSystem(reason);

    internal static ModuleReader From(DataAccess access) =>
        access.IsSystem
            ? ModuleReader.OfSystem(access.SystemReason ?? "причина не названа")
            : ModuleReader.Of(access.UserId!.Value, access.Who, access.Keys);
}
