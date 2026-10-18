using BHS.CRG.Application.Backup;

namespace BHS.CRG.Api.Modules;

/// <summary>
/// После восстановления копии — проекция типов, как при старте (issue #1400).
///
/// <para>Своя область служб, а не та, в которой шло восстановление: проекция читает типы заново, и
/// контекст, помнящий восстановленное по-своему, был бы ей не помощник.</para>
///
/// <para>При старте расхождение объявления с базой останавливает запуск. Здесь — нет: восстановление
/// уже зафиксировано, и отказ после фиксации отменил бы только сообщение о нём. Расхождение называется
/// в отчёте, а остановит его следующий старт — с тем же текстом.</para>
/// </summary>
public sealed class ModuleTypesRestoreAftercare(IServiceScopeFactory scopes, ILogger<ModuleTypesRestoreAftercare> log)
    : IRestoreAftercare
{
    public async Task RunAsync(List<string> warnings, CancellationToken ct = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.ProjectModuleTypesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Восстановление: типы модулей после восстановления не заведены");
            // Причина — в журнал, а не в отчёт: текст исключения пишет не этот код (правило #1050).
            warnings.Add("Типы модулей после восстановления не сверены — причина в журнале приложения. " +
                         "Данные восстановлены; до исправления причины API не запустится: при старте эта " +
                         "сверка повторяется.");
        }
    }
}
