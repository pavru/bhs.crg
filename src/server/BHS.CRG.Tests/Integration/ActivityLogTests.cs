using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Activity;
using BHS.CRG.Domain.Activity;
using BHS.CRG.Infrastructure.Activity;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Журнал действий (issue #950, ТЗ CORE-25, CORE-28).
///
/// ⚠️ Смена роли проверяется ЧЕРЕЗ ЖИВОЙ АДРЕС, а не вызовом службы: запись делает обработчик, и
/// вызов <c>IActivityLog</c> из теста подтвердил бы только то, что служба умеет писать. Забыть
/// позвать её из обработчика — ровно та поломка, ради которой журнал и заводят, и выглядит она как
/// исправно работающая система: роль сменилась, экран показал новую, в журнале пусто.
/// </summary>
[Collection("Integration")]
public class ActivityLogTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string Password = "Passw0rd!";

    /// <summary>
    /// «Готово» из issue: смена роли видна в журнале с автором, временем и прежним значением.
    /// </summary>
    [Fact]
    public async Task Смена_роли_видна_в_журнале_с_автором_временем_и_прежним_значением()
    {
        var (admin, _, adminEmail) = await SignInAsync(SystemRoles.Admin);
        var (_, targetId, targetEmail) = await SignInAsync(SystemRoles.IdEngineer);
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);

        var change = await admin.PutAsJsonAsync($"/api/users/{targetId}/roles",
            new { roles = new[] { SystemRoles.Admin } });
        change.EnsureSuccessStatusCode();

        var record = Assert.Single(await RecordsAsync(ActivityActions.UserRoleChanged));

        Assert.Equal(adminEmail, record.ActorName);           // кто — а не «система» и не пусто
        Assert.Equal(targetEmail, record.TargetLabel);        // кому
        Assert.Equal("Инженер ИД", record.Before);            // что было — то, ради чего журнал и нужен
        Assert.Equal("Администратор", record.After);
        Assert.InRange(record.OccurredAt, before, DateTimeOffset.UtcNow.AddSeconds(5));
    }

    /// <summary>
    /// Удаление пользователя — единственное место, где след остаётся ТОЛЬКО в журнале: самой
    /// учётной записи больше нет, и спросить у неё, какие у неё были права, не у кого.
    /// </summary>
    [Fact]
    public async Task Удаление_пользователя_оставляет_след_с_прежней_ролью()
    {
        var (admin, _, _) = await SignInAsync(SystemRoles.Admin);
        var (_, targetId, targetEmail) = await SignInAsync(SystemRoles.IdEngineer);

        (await admin.DeleteAsync($"/api/users/{targetId}")).EnsureSuccessStatusCode();

        var record = Assert.Single(await RecordsAsync(ActivityActions.UserDeleted));
        Assert.Equal(targetEmail, record.TargetLabel);
        Assert.Equal("Инженер ИД", record.Before);
    }

    /// <summary>
    /// Правка схемы типа: что было (состав полей) и что сделали (добавлено/убрано).
    ///
    /// Тип правится через тот же адрес, что и в работе, — иначе проверялся бы обработчик команды в
    /// отрыве от двери, через которую его зовут.
    /// </summary>
    [Fact]
    public async Task Правка_схемы_типа_записывает_что_было_и_что_изменилось()
    {
        var (admin, _, _) = await SignInAsync(SystemRoles.Admin);

        var created = await admin.PostAsJsonAsync("/api/document-types", new
        {
            name = "Журнальный тип",
            code = $"jt{Guid.NewGuid():N}"[..12],
            kind = "Document",
            schema = """{"fields":[{"key":"номер","type":"string"}]}""",
        });
        created.EnsureSuccessStatusCode();
        var typeId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var updated = await admin.PutAsJsonAsync($"/api/document-types/{typeId}/schema", new
        {
            schema = """{"fields":[{"key":"номер","type":"string"},{"key":"дата","type":"date"}]}""",
        });
        updated.EnsureSuccessStatusCode();

        var record = Assert.Single(await RecordsAsync(ActivityActions.TypeSchemaChanged));
        Assert.Equal("Журнальный тип", record.TargetLabel);
        Assert.Equal("поля: номер", record.Before);
        Assert.Contains("добавлено: дата", record.After);
    }

    /// <summary>
    /// Записи только дописываются (ТЗ CORE-28): правка и удаление отвергаются в единственной точке
    /// сохранения — там, куда приходит ЛЮБОЙ путь записи, а не только наш.
    ///
    /// ⚠️ Проверяется через прямой доступ к набору, то есть тем самым способом, которым запись и
    /// попытались бы поправить в обход службы. Проверка, идущая через службу, не доказала бы
    /// ничего: у службы таких методов нет вовсе.
    /// </summary>
    [Fact]
    public async Task Запись_журнала_нельзя_ни_изменить_ни_удалить()
    {
        using var scope = fixture.Services.CreateScope();
        await Journal(scope).RecordAsync(ActivityActions.ModulesChanged, after: "id");

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await db.ActivityRecords.FirstAsync();

        db.Entry(record).Property(r => r.After).CurrentValue = "подменено";
        db.Entry(record).State = EntityState.Modified;
        var onEdit = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("изменению и удалению не подлежат", onEdit.Message);

        db.ChangeTracker.Clear();
        db.ActivityRecords.Remove(await db.ActivityRecords.FirstAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        db.ChangeTracker.Clear();
        var survivor = await db.ActivityRecords.AsNoTracking().SingleAsync();
        Assert.Equal("id", survivor.After);
    }

    /// <summary>Чтение — по праву (ТЗ CORE-28), и права этого у инженера нет.</summary>
    [Fact]
    public async Task Журнал_читает_только_тот_у_кого_есть_право()
    {
        var (admin, _, _) = await SignInAsync(SystemRoles.Admin);
        var (engineer, _, _) = await SignInAsync(SystemRoles.IdEngineer);

        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.GetAsync("/api/activity")).StatusCode);

        var page = await admin.GetAsync("/api/activity");
        page.EnsureSuccessStatusCode();
        Assert.True((await page.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("items", out _));
    }

    /// <summary>
    /// Состав модулей замечается при старте и записывается ОДИН раз: неизменившийся состав не
    /// должен оставлять строку при каждом перезапуске, иначе журнал заполнится шумом и настоящая
    /// смена в нём потеряется.
    /// </summary>
    [Fact]
    public async Task Состав_модулей_пишется_при_изменении_а_не_при_каждом_старте()
    {
        using var scope = fixture.Services.CreateScope();

        await RecordCompositionAsync(scope);
        await RecordCompositionAsync(scope);

        var first = Assert.Single(await RecordsAsync(ActivityActions.ModulesChanged));
        Assert.Null(first.Before);                      // начало отсчёта, а не «включили сегодня»
        Assert.Equal("id", first.After);
    }

    /// <summary>
    /// Сторож находки 1 (#980): восстановление копии с ДРУГИМ составом модулей не оставляет записи
    /// о смене, которой не было.
    ///
    /// Копия здесь — это записи чужого экземпляра в журнале: именно их старт и принимал за прежнее
    /// состояние, пока состояние жило в журнале. Прежний состав теперь лежит в <c>service_state</c>,
    /// а он в копию не входит — и старт после восстановления сверяется со СВОИМ прошлым.
    /// </summary>
    [Fact]
    public async Task Восстановление_копии_с_другим_составом_не_пишет_смену_модулей()
    {
        using var scope = fixture.Services.CreateScope();

        // Экземпляр уже работал: состав записан и в журнал, и в состояние службы.
        await RecordCompositionAsync(scope);

        // Приехала копия с установки, где был включён ещё один модуль. Её запись — самая свежая.
        var foreign = ActivityRecord.Create(
            ActivityActions.ModulesChanged.Code, null, "Система",
            targetLabel: "Состав модулей экземпляра", after: "costs, id",
            occurredAt: DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(1, await Journal(scope).ImportAsync([foreign]));

        await RecordCompositionAsync(scope);

        var records = await RecordsAsync(ActivityActions.ModulesChanged);
        // Две записи: наша собственная и приехавшая. Третьей — «costs, id → id» — быть не должно.
        Assert.Equal(2, records.Count);
        Assert.DoesNotContain(records, r => r.Before == "costs, id");
    }

    /// <summary>
    /// Сторож находки 2 (#980): длинное отображаемое имя не роняет запрос ПОСЛЕ совершённого
    /// действия. Платой за журнал заявлена потеря записи, а не 500 на удавшейся смене роли.
    /// </summary>
    [Fact]
    public async Task Длинное_имя_автора_не_роняет_удавшееся_действие()
    {
        var (admin, _, _) = await SignInAsync(SystemRoles.Admin, displayName: new string('я', 400));
        var (_, targetId, _) = await SignInAsync(SystemRoles.IdEngineer);

        var change = await admin.PutAsJsonAsync($"/api/users/{targetId}/roles",
            new { roles = new[] { SystemRoles.Admin } });
        change.EnsureSuccessStatusCode();   // действие удалось — и ответ об этом говорит

        var record = Assert.Single(await RecordsAsync(ActivityActions.UserRoleChanged));
        Assert.Equal(ActivityRecord.ActorNameMax, record.ActorName.Length);
        Assert.EndsWith("…", record.ActorName);         // обрез виден, а не выдан за настоящее имя
    }

    /// <summary>
    /// Сторож находки 3 (#980): страницы не едут при совпавшем времени. Записи с одинаковым
    /// <c>OccurredAt</c> без добивки по <c>Id</c> база вправе отдавать в разном порядке — и вторая
    /// страница тогда повторяет строки первой, пряча пограничные.
    /// </summary>
    [Fact]
    public async Task Страницы_не_едут_когда_время_записей_совпадает()
    {
        using var scope = fixture.Services.CreateScope();
        var sameMoment = DateTimeOffset.UtcNow;

        // Идентификаторы заданы явно и различаются ПОСЛЕДНИМ байтом: так их порядок одинаков и в
        // PostgreSQL (сравнение uuid побайтно), и в .NET. Случайные Guid для проверки порядка не
        // годятся — эти два порядка у них расходятся, и тест утверждал бы не то, что проверяет.
        var ids = Enumerable.Range(1, 10)
            .Select(i => new Guid($"00000000-0000-0000-0000-0000000000{i:x2}"))
            .ToList();
        await Journal(scope).ImportAsync(ids
            .Select((id, i) => ActivityRecord.Create(
                ActivityActions.ModulesChanged.Code, null, "Система",
                after: $"набор {i}", id: id, occurredAt: sameMoment))
            .ToList());

        var first = await Journal(scope).ReadAsync(0, 5, ActivityActions.ModulesChanged.Code);
        var second = await Journal(scope).ReadAsync(5, 5, ActivityActions.ModulesChanged.Code);

        // Без добивки по Id порядок при совпавшем времени задаёт база — и это порядок хранения,
        // то есть тот, в котором записи вставляли. Ожидаем обратный ему.
        Assert.Equal(ids.AsEnumerable().Reverse(), first.Concat(second).Select(r => r.Id));
    }

    /// <summary>
    /// Сторож находки 4 (#980): заведение ПЕРВОГО администратора попадает в журнал. Про него потом
    /// и спрашивают «кто завёл» — а заводится он через страницу регистрации, мимо /api/users.
    /// </summary>
    [Fact]
    public async Task Первый_администратор_попадает_в_журнал()
    {
        await ClearUsersAsync();

        var client = fixture.CreateClient();
        var email = $"bootstrap_{Guid.NewGuid():N}@test.local";

        var created = await client.PostAsJsonAsync("/api/auth/register",
            new { email, password = Password, displayName = "Первый" });
        created.EnsureSuccessStatusCode();

        var record = Assert.Single(await RecordsAsync(ActivityActions.UserCreated));
        Assert.Equal(email, record.TargetLabel);
        Assert.Equal("Администратор", record.After);    // выданная роль названа, а не подразумевается
        Assert.Null(record.ActorId);                    // автор — сам экземпляр: войти было некому
        Assert.Equal("Система", record.ActorName);
    }

    /// <summary>
    /// Сторож находки ревью #980: отказ журнала НЕ отменяет заведение первого администратора.
    ///
    /// Эта дверь закрывается навсегда. Учётная запись к моменту записи уже создана, поэтому 500
    /// оставлял бы установку в положении, из которого нет выхода: повторный запрос получает 403
    /// «Регистрация закрыта», а install.sh на ошибку советует завести администратора самому —
    /// совет, выполнить который уже нечем.
    /// </summary>
    [Fact]
    public async Task Отказ_журнала_не_отменяет_заведение_первого_администратора()
    {
        await ClearUsersAsync();

        // Падает ТОЛЬКО на «заведён пользователь»: журнал зовёт и старт (состав модулей), и
        // уронив его целиком, мы проверяли бы не эндпоинт, а невзлетевший хост.
        using var broken = fixture.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddScoped<IActivityLog>(sp => new JournalFailingOnUserCreated(
                sp.GetRequiredService<AppDbContext>(),
                sp.GetRequiredService<IActivityActor>()))));

        var email = $"bootstrap_{Guid.NewGuid():N}@test.local";
        var created = await broken.CreateClient().PostAsJsonAsync("/api/auth/register",
            new { email, password = Password, displayName = "Первый" });

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Empty(await RecordsAsync(ActivityActions.UserCreated));   // след потерян — и только

        using var scope = fixture.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = await users.FindByEmailAsync(email);
        Assert.NotNull(admin);
        Assert.Contains(SystemRoles.Admin, await users.GetRolesAsync(admin));
    }

    /// <summary>Журнал, который отказывает на заведении пользователя и работает во всём остальном.</summary>
    private sealed class JournalFailingOnUserCreated(AppDbContext db, IActivityActor actor) : IActivityLog
    {
        private readonly ActivityLog inner = new(db, actor);

        public Task RecordAsync(ActivityAction action, string? targetId = null, string? targetLabel = null,
            string? before = null, string? after = null, CancellationToken ct = default) =>
            action.Code == ActivityActions.UserCreated.Code
                ? throw new InvalidOperationException("журнал недоступен")
                : inner.RecordAsync(action, targetId, targetLabel, before, after, ct);

        public Task<IReadOnlyList<ActivityRecord>> ReadAsync(int skip, int take, string? action = null,
            CancellationToken ct = default) => inner.ReadAsync(skip, take, action, ct);

        public Task<int> CountAsync(string? action = null, CancellationToken ct = default) =>
            inner.CountAsync(action, ct);

        public Task<ActivityRecord?> LastAsync(ActivityAction action, CancellationToken ct = default) =>
            inner.LastAsync(action, ct);

        public Task<IReadOnlyList<ActivityRecord>> ExportAsync(CancellationToken ct = default) =>
            inner.ExportAsync(ct);

        public Task<int> ImportAsync(IReadOnlyList<ActivityRecord> records, CancellationToken ct = default) =>
            inner.ImportAsync(records, ct);
    }

    /// <summary>
    /// Убирает все учётные записи: регистрация открыта, только пока нет ни одной. Фикстура их НЕ
    /// чистит — «чистят точечно те, кто их заводит» (FixtureResetCoverageTests), поэтому условие
    /// освобождаем сами, а не надеемся на пустую базу.
    /// </summary>
    private async Task ClearUsersAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        // Удаление сверяем: несостоявшееся всплыло бы не здесь, а отказом 403 на регистрации —
        // и отчёт обвинил бы журнал, хотя дело было в неубранной учётной записи.
        foreach (var u in await users.Users.ToListAsync())
            Assert.True((await users.DeleteAsync(u)).Succeeded);
    }

    private static Task RecordCompositionAsync(IServiceScope scope) =>
        BHS.CRG.Api.Activity.ModuleCompositionJournal.RecordIfChangedAsync(
            Journal(scope),
            scope.ServiceProvider.GetRequiredService<BHS.CRG.Modules.ModuleRegistry>(),
            scope.ServiceProvider.GetRequiredService<BHS.CRG.Infrastructure.Updates.ServiceStateStore>());

    /// <summary>
    /// Журнал переносится резервной копией (ТЗ CORE-28) и не удваивает уже известное: восстановление
    /// на работающий экземпляр не должно раздваивать его собственную историю.
    /// </summary>
    [Fact]
    public async Task Записи_из_копии_принимаются_один_раз()
    {
        using var scope = fixture.Services.CreateScope();
        var journal = Journal(scope);

        var fromBackup = ActivityRecord.Create(
            ActivityActions.UserRoleChanged.Code, Guid.NewGuid(), "Пётр с прежней системы",
            targetId: Guid.NewGuid().ToString(), targetLabel: "ivanov@old.local",
            before: "Инженер ИД", after: "Администратор",
            occurredAt: new DateTimeOffset(2025, 3, 4, 10, 0, 0, TimeSpan.Zero));

        Assert.Equal(1, await journal.ImportAsync([fromBackup]));
        Assert.Equal(0, await journal.ImportAsync([fromBackup]));

        var record = Assert.Single(await RecordsAsync(ActivityActions.UserRoleChanged));
        Assert.Equal("Пётр с прежней системы", record.ActorName);
        // Время приехало своё: записи 2025 года не становятся сегодняшними от того, что их
        // восстановили сегодня.
        Assert.Equal(2025, record.OccurredAt.Year);
    }

    private static IActivityLog Journal(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IActivityLog>();

    private async Task<IReadOnlyList<ActivityRecord>> RecordsAsync(ActivityAction action)
    {
        using var scope = fixture.Services.CreateScope();
        return await Journal(scope).ReadAsync(0, 100, action.Code);
    }

    /// <summary>Заводит пользователя с ролью и возвращает клиент с его токеном.</summary>
    private async Task<(HttpClient Client, Guid Id, string Email)> SignInAsync(
        string role, string displayName = "")
    {
        var email = $"log_{role.ToLowerInvariant()}_{Guid.NewGuid():N}@test.local";
        Guid id;
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            // DisplayName пуст ПО УМОЛЧАНИЮ и нарочно: автора журнал тогда берёт из почты, и
            // проверка заодно показывает, что «автор неизвестен» в записи не появляется никогда.
            var user = new ApplicationUser { UserName = email, Email = email, DisplayName = displayName, EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            id = user.Id;
        }

        var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, id, email);
    }
}
