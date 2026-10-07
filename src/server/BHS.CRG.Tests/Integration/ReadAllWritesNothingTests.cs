using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Modules;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Составное право «читать всё» раскрыто по модулям — и ничего не даёт изменить (задача A3 этапа 2,
/// issue #1074, ТЗ AUTH-5.2, AUTH-8.1).
///
/// <para><b>От чего это.</b> До раскрытия роль с одним <c>*.read.all</c> не открывала ни одного
/// модуля: доступ к модулю судят по началу кода права, а составное право начинается со звёздочки.
/// Раскрытие чинит это — и открывает новый способ ошибиться: владелец составного права проходит
/// ворота КАЖДОГО включённого модуля, в котором есть хоть одно право чтения. Изменяющий адрес,
/// закрытый только воротами модуля, достаётся ему даром. Пометка у права этого не видит — она про
/// право, а не про адреса; поэтому второй сторож здесь идёт от адресов живого приложения.</para>
///
/// <para>⚠️ <b>Чего сторож адресов не видит.</b> Проверку прав ВНУТРИ обработчика (фильтр адреса,
/// вопрос к <c>IModuleUser</c>): по описанию адреса она не читается. И запрос <c>GET</c>, который
/// что-то меняет, — такой не считается изменяющим.</para>
///
/// <para>Хост — с ОБОИМИ модулями (<see cref="ModulePortsHost" />): в умолчательной сборке модуль
/// счетов выключен, и раскрывать в нём было бы нечего. Коллекция общая с <c>RunResetTests</c> — тот
/// сносит учётные таблицы этой же базы, и идти одновременно с ним нельзя.</para>
/// </summary>
[Collection("Integration")]
public class ReadAllWritesNothingTests(ModulePortsHost fixture) : IClassFixture<ModulePortsHost>
{
    private const string Password = "Passw0rd!";

    /// <summary>
    /// Изменяющие адреса, которые владелец одного «читать всё» проходит по описанию ворот, — с
    /// причиной, почему это не правка. Ключ — «МЕТОД путь».
    /// </summary>
    private static readonly Dictionary<string, string> Deliberate = new();

    /// <summary>
    /// Главный сторож: ни один изменяющий адрес не открывается одним составным правом.
    ///
    /// Считается так же, как считают ворота: набор прав — раскрытое «читать всё», и больше ничего;
    /// адрес открыт, если проходят ВСЕ его политики права и модуля.
    /// </summary>
    [Fact]
    public void Изменяющий_адрес_не_открывается_одним_правом_читать_всё()
    {
        _ = fixture.CreateClient();
        var catalog = fixture.Services.GetRequiredService<PermissionCatalog>();
        var granted = catalog.Expand([PermissionCatalog.ReadAllCode]);

        // Проверять есть чем: без входящих прав набор равен одному составному, ворота модулей
        // закрыты все — и сторож был бы зелёным, ничего не сверив.
        Assert.True(granted.Count > 1,
            "В «читать всё» не входит ни одно право: раскрывать нечего, и проверка ниже не смотрит ни на что.");

        var open = EndpointInventory.Routes(fixture.Services)
            .Where(e => EndpointInventory.IsGated(e) && Writes(e) && Passes(e, granted))
            .Select(e => $"{EndpointInventory.Verbs(e)} {EndpointInventory.Route(e)}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var unlisted = open.Where(key => !Deliberate.ContainsKey(key)).ToList();
        Assert.True(unlisted.Count == 0,
            "Изменяющий адрес открыт владельцу одного составного права «читать всё»:\n  " +
            string.Join("\n  ", unlisted) + "\n\n" +
            "Причина одна из двух. Адрес закрыт только воротами модуля — поставьте на него право на " +
            "правку: ворота модуля проходит и тот, у кого в модуле одно чтение. Либо право на его " +
            "двери помечено ReadAllMark.In — тогда оно не «только читает», и пометку надо снять. Если " +
            "адрес ничего не меняет (поиск телом запроса, предпросмотр) — впишите его в Deliberate с причиной.");

        var stale = Deliberate.Keys.Where(key => !open.Contains(key)).ToList();
        Assert.True(stale.Count == 0,
            "В Deliberate адреса, которые «читать всё» больше не открывает или которых нет: " +
            string.Join(", ", stale) + ".");
    }

    /// <summary>
    /// Пометка стоит у каждого права каждого модуля поставки. Старт приложения отказывает на том же
    /// (см. <c>AddAppModules</c>), но здесь отказ называет право, не роняя с собой весь набор.
    /// </summary>
    [Fact]
    public void У_каждого_права_модуля_есть_пометка_читать_всё()
    {
        var unmarked = DeliveredModules.All()
            .SelectMany(m => m.Permissions)
            .Where(p => p.ReadAll is null)
            .Select(p => p.Code)
            .ToList();

        Assert.True(unmarked.Count == 0,
            "У права модуля не сказано, входит ли оно в «читать всё»: " + string.Join(", ", unmarked) + ".\n" +
            "ReadAllMark.In — право только читает; ReadAllMark.Out(\"причина\") — меняет данные. Без пометки " +
            "владелец составного права («Руководитель») молча не получит этого права вовсе.");
    }

    /// <summary>
    /// Признак готовности задачи: роль с одним «читать всё» видит реестр счетов, накладные и затраты —
    /// и не может ничего изменить.
    /// </summary>
    [Fact]
    public async Task Роль_с_одним_читать_всё_видит_счета_и_затраты_и_ничего_не_меняет()
    {
        var client = await SignInWithAsync(PermissionCatalog.ReadAllCode);

        // Чтение: каждое из трёх входящих прав модуля — на своём адресе.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/costs/invoices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/costs/waybills")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/costs/site-costs")).StatusCode);
        // И модуль ИД: библиотека документов качества читается, а правка её — под своим правом.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/quality-docs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/quality-docs", new { })).StatusCode);

        // Запись: ворота модуля пройдены, право на правку — нет. Отказ именно 403, а не 400 от
        // негодного тела: до разбора тела дело не доходит.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/costs/invoices", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/costs/waybills", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync($"/api/costs/invoices/{Guid.NewGuid()}/paid", new { })).StatusCode);

        // Навигация: модули открыты, а в наборе прав — ни одного, помеченного «не входит».
        var access = await client.GetFromJsonAsync<JsonElement>("/api/account/access");
        var modules = access.GetProperty("modules").EnumerateArray()
            .ToDictionary(m => m.GetProperty("code").GetString()!, m => m.GetProperty("available").GetBoolean());
        Assert.True(modules["costs"], "Модуль счетов владельцу «читать всё» не открылся.");

        var granted = access.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToList();
        var catalog = fixture.Services.GetRequiredService<PermissionCatalog>();
        var excluded = catalog.All.Where(p => p.ReadAll is { Included: false }).Select(p => p.Code).ToList();
        Assert.NotEmpty(excluded);
        Assert.Empty(granted.Intersect(excluded, StringComparer.OrdinalIgnoreCase));
        Assert.Contains("costs.report.read", granted);   // чтение без «.read» у объекта — не выпало
    }

    /// <summary>Без составного права раскрытия нет: чужое право чтения модуль счетов не открывает.</summary>
    [Fact]
    public async Task Без_составного_права_модуль_остаётся_закрытым()
    {
        var client = await SignInWithAsync("core.catalog.read");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/costs/invoices")).StatusCode);
    }

    private static bool Writes(Endpoint endpoint) =>
        (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
        .Any(m => !HttpMethods.IsGet(m) && !HttpMethods.IsHead(m) && !HttpMethods.IsOptions(m));

    /// <summary>
    /// Проходит ли набор прав все ворота адреса. Политика, которая не про право и не про модуль
    /// (роль, своя), считается закрытой: одно «читать всё» её не открывает, а судить о ней по имени
    /// здесь нечем.
    /// </summary>
    private static bool Passes(Endpoint endpoint, IReadOnlyCollection<string> granted) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Where(a => a.Policy is not null || a.Roles is not null)
            .All(a => a.Roles is null && a.Policy switch
            {
                { } p when p.StartsWith(AppPolicies.PermissionPrefix, StringComparison.OrdinalIgnoreCase) =>
                    granted.Contains(p[AppPolicies.PermissionPrefix.Length..].Trim(), StringComparer.OrdinalIgnoreCase),
                { } p when p.StartsWith(AppPolicies.ModulePrefix, StringComparison.OrdinalIgnoreCase) =>
                    ModuleAccess.IsOpen(p[AppPolicies.ModulePrefix.Length..].Trim(), granted),
                _ => false,
            });

    /// <summary>Заводит роль ровно с этими правами, пользователя в ней — и возвращает клиент с его токеном.</summary>
    private async Task<HttpClient> SignInWithAsync(params string[] permissions)
    {
        var roleName = $"ReadAll_{Guid.NewGuid():N}";
        var email = $"readall_{Guid.NewGuid():N}@test.local";

        var client = fixture.CreateClient();
        using (var scope = fixture.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            var role = new IdentityRole<Guid>(roleName);
            Assert.True((await roles.CreateAsync(role)).Succeeded);
            foreach (var permission in permissions)
                Assert.True((await roles.AddClaimAsync(
                    role, new System.Security.Claims.Claim(RoleSynchronizer.PermissionClaim, permission))).Succeeded);

            var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Тест", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
        }

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
