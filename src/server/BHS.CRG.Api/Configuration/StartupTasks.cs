using System.Text;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Modules;
using BHS.CRG.Modules;
using BHS.CRG.Application.Settings;
using BHS.CRG.Application.Activity;
using BHS.CRG.Domain.Jobs;
using BHS.CRG.Infrastructure.Settings;
using BHS.CRG.Infrastructure.Http;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Plugins;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Configuration;

/// <summary>
/// Работа при старте: миграция базы, разрешение реестра тэгов, первичные данные и журнал состава
/// модулей (вынесено из <c>Program.cs</c>, issue #1030).
///
/// <para>⚠️ Реестр тэгов разрешается ЗДЕСЬ намеренно, а не лениво при первом обращении. Сторожит
/// <c>TagCatalogTests</c>: он ищет <c>GetRequiredService&lt;…TagCatalog&gt;()</c> в исходниках
/// проекта и требует РОВНО ОДНОГО вхождения — переписан в этом же разрезе, потому что прежний читал
/// <c>Program.cs</c> по имени файла.</para>
/// </summary>
internal static class StartupTasks
{
    /// <summary>Выполняется до постановки конвейера: приложение обязано упасть здесь, а не на первом запросе.</summary>
    internal static async Task RunStartupTasksAsync(this WebApplication app)
    {
    // ExcelDataReader требует регистрации кодировок для .xls файлов
    System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Перепись справочника ядра до и после миграции (ТЗ CORE-29/CORE-31, issue #960).
        // Расхождение — отказ старта: см. MigrationCensus, там записано почему.
        //
        // Считается ТОЛЬКО когда есть что применять: без ожидающих миграций терять данные нечему,
        // а шесть агрегатов по справочнику выполнялись бы каждым запуском — в том числе при
        // перезапуске контейнера, когда база не менялась вовсе (ревью PR #1046).
        var pending = await db.Database.GetPendingMigrationsAsync();
        var migrating = pending.Any();
        var censusBefore = migrating
            ? await BHS.CRG.Infrastructure.Persistence.MigrationCensus.ReadAsync(db)
            : null;

        await db.Database.MigrateAsync();

        if (migrating)
        {
            var censusAfter = await BHS.CRG.Infrastructure.Persistence.MigrationCensus.ReadAsync(db);
            BHS.CRG.Infrastructure.Persistence.MigrationCensus.EnsureUnchanged(censusBefore, censusAfter);
            // В журнал — ЧТО именно сошлось. Иначе «сверка прошла» и «сверять было нечем» выглядят
            // одинаково: молчанием, — а на старой базе часть счётчиков может не посчитаться вовсе.
            app.Logger.LogInformation(
                censusBefore is null
                    ? "Миграций применено: {Count}; справочника до миграции не было — сверять было нечего"
                    : "Миграций применено: {Count}; состав справочника сошёлся: {Census}",
                pending.Count(), censusBefore?.Describe() ?? "");
        }

        // Встроенные профили распознавания (issue #406) — идемпотентно; правленые пользователем не трогает.
        await BHS.CRG.Infrastructure.Recognition.RecognitionProfileSeeder.SeedAsync(db);

        // Секреты интеграций до 0.92.0 лежали в БД открытым текстом. Перешифровываем оставшиеся —
        // идемпотентно: уже зашифрованные пропускаются, второй прогон работы не находит.
        var protectedCount = await scope.ServiceProvider
            .GetRequiredService<IntegrationSettingsService>().ProtectStoredSecretsAsync();
        if (protectedCount > 0)
            app.Logger.LogInformation("Секретов настроек интеграций зашифровано при старте: {Count}", protectedCount);

        // Прокси и галки внешних сервисов — в память сразу (issue #936): их читают на каждом запросе, и
        // первый запрос после старта не должен уйти напрямую только потому, что настройки ещё никто не читал.
        await scope.ServiceProvider.GetRequiredService<IIntegrationSettings>().GetEffectiveAsync();

        // Переменные прокси в окружении приложение НЕ использует — и молчать об этом нельзя: администратор,
        // задавший их, ждёт, что они действуют, а отказ пришёл бы диагнозом «сервис недоступен».
        var envProxy = OutboundProxy.EnvironmentVariables.Where(v => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v))).ToList();
        if (envProxy.Count > 0)
            app.Logger.LogWarning(
                "В окружении заданы {Variables}, но приложение их не использует: прокси для внешних сервисов " +
                "задаётся в «Настройки → Прокси», и действует он только у сервисов с галкой «через прокси»",
                string.Join(", ", envProxy));

        // Разовый перенос размеров изображений из схем типов в значения инстансов (issue #246).
        // Идемпотентно: после первого прогона схемы очищены, карта пустеет — обход не запускается.
        await BHS.CRG.Infrastructure.DataFixups.ImageSizeToInstanceFixup.RunAsync(db);

        // Зависшие фоновые задачи (in-process очередь потеряна при рестарте) — помечаем Failed, чтобы
        // индикатор не «висел» вечно (тот же приём восстановления, что и сид ролей ниже).
        var stuckJobs = await db.Jobs.Where(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running).ToListAsync();
        foreach (var job in stuckJobs) job.MarkAbandoned();
        if (stuckJobs.Count > 0) await db.SaveChangesAsync();

        // ── Роли + миграция существующих пользователей ──────────────────────────────
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        await RoleSynchronizer.SyncAsync(roleManager, scope.ServiceProvider.GetRequiredService<PermissionCatalog>(), app.Logger);

        // Существующие аккаунты без роли получают Admin (раньше у всех был полный доступ).
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        foreach (var u in userManager.Users.ToList())
            if ((await userManager.GetRolesAsync(u)).Count == 0)
                await userManager.AddToRoleAsync(u, "Admin");

        // Реестр тэгов — СОБИРАЕМ ЗДЕСЬ, а не ждём первого обращения (issue #959). Он singleton, то
        // есть ленивый: без этой строки два объявления одного кода тэга дали бы зелёный старт и 500 на
        // первом запросе списка тэгов — отказ, который обязан останавливать запуск, приходил бы
        // пользователю в редактор схем. Поймано ревью PR #1012: сторож, который не срабатывает, —
        // не сторож.
        _ = scope.ServiceProvider.GetRequiredService<BHS.CRG.Application.Schema.TagCatalog>();

        // Прогрев плагинов: HTTP-плагины отдают схемы только по запросу (GET /schemas) — best-effort.
        await scope.ServiceProvider.GetRequiredService<IPluginHost>().WarmUpAsync();

        // Права, объявленные кодом, — в базу (AUTH-1). До ролей и до инициализации модулей: роль
        // ссылается на права, и справочник обязан быть на месте раньше, чем кто-то начнёт их раздавать.
        await PermissionSynchronizer.SyncAsync(
            db,
            scope.ServiceProvider.GetRequiredService<PermissionCatalog>(),
            app.Logger);

        // Первичная инициализация включённых модулей — после миграций и сидов ядра: модуль вправе
        // рассчитывать, что схема базы и справочники ядра на месте.
        await scope.ServiceProvider.InitializeAppModulesAsync();

        // Типы, объявленные модулями, — в схемы (issue #958). ПОСЛЕ инициализации: модуль вправе
        // завести к этому моменту свои справочники, на которые скелет ссылается. Исключение отсюда не
        // ловится намеренно: расхождение объявления с базой обязано останавливать старт, а не
        // оставлять систему работать с типом, в котором код модуля не найдёт своих полей.
        await scope.ServiceProvider.ProjectModuleTypesAsync();

        // Состав модулей — в журнал действий (ТЗ CORE-28), если он изменился с прошлого запуска.
        // После инициализации: записывать «включён модуль», который не смог подняться, значит
        // оставить в журнале утверждение, опровергнутое соседней строкой лога.
        await BHS.CRG.Api.Activity.ModuleCompositionJournal.RecordIfChangedAsync(
            scope.ServiceProvider.GetRequiredService<IActivityLog>(),
            scope.ServiceProvider.GetRequiredService<ModuleRegistry>(),
            scope.ServiceProvider.GetRequiredService<BHS.CRG.Infrastructure.Updates.ServiceStateStore>());
    }
    }
}
