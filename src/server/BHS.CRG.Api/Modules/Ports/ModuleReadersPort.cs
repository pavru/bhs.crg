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
///
/// <para>⚠️ Задача такта модуля (work-08, ТЗ WORK-41) — система С НАЗВАНИЕМ ТАКТА, а не «автор задачи
/// удалён»: владельца у неё нет по устройству, и поиск учётной записи <see cref="Guid.Empty" /> дал бы
/// ложную причину отказа.</para>
/// </summary>
public sealed class ModuleReadersPort(DataAccessResolver access, IHttpContextAccessor http, ModuleTickCatalog ticks)
    : IModuleReaders
{
    public async Task<ModuleReader> ForRequestAsync(CancellationToken ct = default) =>
        From(await access.ForRequestAsync(http, ct));

    public async Task<ModuleReader> ForJobAsync(ModuleJobRun run, CancellationToken ct = default) =>
        TickOf(run) is { } tick
            ? ModuleReader.OfSystem(tick.Tick.Title)
            : From(await access.ForUserAsync(run.UserId, ct));

    /// <summary>
    /// Такт ли поставил задачу. Операции в <see cref="ModuleJobRun" /> нет — у такта она одна на цель, и
    /// цель с пустым владельцем узнаётся однозначно.
    /// </summary>
    private ModuleTickEntry? TickOf(ModuleJobRun run) =>
        run.UserId == Guid.Empty ? ticks.All.FirstOrDefault(e => e.TargetId == run.TargetId) : null;

    public ModuleReader ForSystem(string reason) => ModuleReader.OfSystem(reason);

    internal static ModuleReader From(DataAccess access) =>
        access.IsSystem
            ? ModuleReader.OfSystem(access.SystemReason ?? "причина не названа")
            : ModuleReader.Of(access.UserId!.Value, access.Who, access.Keys);
}
