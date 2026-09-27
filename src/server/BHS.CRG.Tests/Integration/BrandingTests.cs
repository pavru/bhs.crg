using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Branding;
using BHS.CRG.Application.Settings;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Название продукта и логотип компании (ТЗ CORE-25.1, issue #967).
///
/// <para>Проверяется не «настройка сохранилась», а три вещи, без которых она не работает: до
/// настройки экземпляр называет себя нейтрально и без логотипа; после настройки название и логотип
/// видны ДО ВХОДА (иначе страница входа, ради которой всё и делается, останется с чужим именем); и
/// логотип виден ПЕЧАТНОЙ ФОРМЕ как обычный системный ассет — то есть шаблон ставит его сам.</para>
/// </summary>
[Collection("Integration")]
public class BrandingTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private const string Password = "Passw0rd!";

    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public async Task До_настройки_нейтральное_название_и_без_логотипа()
    {
        var anonymous = fixture.CreateClient();

        var branding = await anonymous.GetFromJsonAsync<JsonElement>("/api/branding");

        Assert.Equal(BrandingDefaults.ProductName, branding.GetProperty("productName").GetString());
        Assert.False(branding.GetProperty("isCustom").GetBoolean());
        Assert.False(branding.GetProperty("hasLogo").GetBoolean());
        // Картинки нет — и это 404, а не пустой ответ: пустой файл браузер покажет битым значком.
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/branding/logo")).StatusCode);
    }

    [Fact]
    public async Task Название_и_логотип_видны_БЕЗ_входа()
    {
        var admin = await SignInAsync(SystemRoles.Admin);
        (await admin.PutAsJsonAsync("/api/branding", new { productName = "Пример-Строй" })).EnsureSuccessStatusCode();
        (await UploadLogoAsync(admin, "logo.png", Png)).EnsureSuccessStatusCode();

        // Страница входа открыта до входа — и оформление на ней обязано читаться так же.
        var anonymous = fixture.CreateClient();
        var branding = await anonymous.GetFromJsonAsync<JsonElement>("/api/branding");

        Assert.Equal("Пример-Строй", branding.GetProperty("productName").GetString());
        Assert.True(branding.GetProperty("isCustom").GetBoolean());
        Assert.True(branding.GetProperty("hasLogo").GetBoolean());

        var logo = await anonymous.GetAsync("/api/branding/logo");
        logo.EnsureSuccessStatusCode();
        Assert.Equal("image/png", logo.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await logo.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Логотип_доступен_печатной_форме_системным_ассетом()
    {
        // Требование ТЗ CORE-25.1 дословно: «логотип доступен шаблонам Typst как ассет уровня
        // системы». Проверяем не таблицу, а РЕЗОЛВЕР ассетов — тем же путём идёт генерация.
        var admin = await SignInAsync(SystemRoles.Admin);
        (await UploadLogoAsync(admin, "logo.png", Png)).EnsureSuccessStatusCode();

        using var scope = fixture.Services.CreateScope();
        var (templateId, typeId) = await SeedTemplateAsync(scope);
        var assets = await scope.ServiceProvider.GetRequiredService<ITemplateAssetResolver>()
            .ResolveAsync(templateId, typeId);

        var image = Assert.Single(assets.Images, i => i.Name == BrandingDefaults.LogoAssetName);
        Assert.Equal("image/png", image.MimeType);
        // Имя и расширение — это и есть путь в шаблоне: image("/assets/company-logo.png").
        Assert.Equal(".png", Path.GetExtension(image.FileName));
    }

    [Fact]
    public async Task Замена_логотипа_меняет_метку_версии()
    {
        // Метка едет в адрес картинки (?v=): без неё заменённый логотип остался бы прежним у всех,
        // кто уже заходил, — то есть ровно у тех, ради кого замену и делали.
        var admin = await SignInAsync(SystemRoles.Admin);
        var first = await (await UploadLogoAsync(admin, "logo.png", Png)).Content.ReadFromJsonAsync<JsonElement>();

        await Task.Delay(5);
        var second = await (await UploadLogoAsync(admin, "logo.png", Png)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.NotEqual(first.GetProperty("logoVersion").GetString(), second.GetProperty("logoVersion").GetString());

        // Ассет при этом ОДИН: замена не плодит строки, иначе в шаблонах стало бы два «company-logo».
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.TemplateAssets.CountAsync(a => a.Name == BrandingDefaults.LogoAssetName));
    }

    [Fact]
    public async Task Пустое_название_возвращает_умолчание_а_не_пустую_шапку()
    {
        var admin = await SignInAsync(SystemRoles.Admin);
        (await admin.PutAsJsonAsync("/api/branding", new { productName = "Пример-Строй" })).EnsureSuccessStatusCode();

        var cleared = await admin.PutAsJsonAsync("/api/branding", new { productName = "   " });
        var branding = await cleared.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(BrandingDefaults.ProductName, branding.GetProperty("productName").GetString());
        Assert.False(branding.GetProperty("isCustom").GetBoolean());

        // И строки настройки не остаётся: пустое значение уехало бы в резервную копию и выглядело
        // бы там заданной настройкой — «название есть, и оно никакое».
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.AppSettings.AnyAsync(x => x.Key == AppSettingKeys.ProductName));
    }

    [Fact]
    public async Task Правка_оформления_требует_права_обслуживания()
    {
        // Чтение открыто всем, правка — нет. Без этой половины «анонимный адрес» означал бы, что
        // название экземпляра может сменить кто угодно.
        var engineer = await SignInAsync(SystemRoles.IdEngineer);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await engineer.PutAsJsonAsync("/api/branding", new { productName = "Чужое" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadLogoAsync(engineer, "logo.png", Png)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.DeleteAsync("/api/branding/logo")).StatusCode);

        var anonymous = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PutAsJsonAsync("/api/branding", new { productName = "Чужое" })).StatusCode);
    }

    [Fact]
    public async Task Формат_не_для_показа_отвергается_при_загрузке()
    {
        // Настройка, сохранённая файлом, который браузер не покажет, — это «сохранено» с пустым
        // местом на экране: отказ при загрузке говорит об этом там, где ещё можно исправить.
        var admin = await SignInAsync(SystemRoles.Admin);

        var refused = await UploadLogoAsync(admin, "logo.pdf", Encoding.UTF8.GetBytes("%PDF-1.4"));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.False((await admin.GetFromJsonAsync<JsonElement>("/api/branding")).GetProperty("hasLogo").GetBoolean());
    }

    [Fact]
    public async Task Убранный_логотип_исчезает_и_у_шаблонов()
    {
        var admin = await SignInAsync(SystemRoles.Admin);
        (await UploadLogoAsync(admin, "logo.png", Png)).EnsureSuccessStatusCode();

        (await admin.DeleteAsync("/api/branding/logo")).EnsureSuccessStatusCode();

        Assert.False((await admin.GetFromJsonAsync<JsonElement>("/api/branding")).GetProperty("hasLogo").GetBoolean());
        using var scope = fixture.Services.CreateScope();
        var (templateId, typeId) = await SeedTemplateAsync(scope);
        var assets = await scope.ServiceProvider.GetRequiredService<ITemplateAssetResolver>()
            .ResolveAsync(templateId, typeId);
        Assert.DoesNotContain(assets.Images, i => i.Name == BrandingDefaults.LogoAssetName);
    }

    [Fact]
    public async Task Название_и_логотип_переживают_резервную_копию()
    {
        // Требование ТЗ CORE-25.1 дословно: «логотип и название попадают в резервную копию».
        // Отдельной дороги для них не заводилось — название едет настройкой экземпляра, логотип
        // системным ассетом, — и проверяется здесь ровно это: обе уже проложенные дороги ведут
        // именно туда, куда обещано, на ПОЛНОМ круге выгрузить → стереть → восстановить.
        var admin = await SignInAsync(SystemRoles.Admin);
        (await admin.PutAsJsonAsync("/api/branding", new { productName = "Пример-Строй" })).EnsureSuccessStatusCode();
        (await UploadLogoAsync(admin, "logo.png", Png)).EnsureSuccessStatusCode();

        byte[] archive;
        using (var scope = fixture.Services.CreateScope())
        {
            var (zip, _) = await BackupOf(scope).ExportAsync();
            await using var _ = zip;
            using var ms = new MemoryStream();
            await zip.CopyToAsync(ms);
            archive = ms.ToArray();
        }

        await fixture.ResetDatabaseAsync();
        using (var scope = fixture.Services.CreateScope())
            Assert.True((await BackupOf(scope).ImportAsync(new MemoryStream(archive))).Success);

        var anonymous = fixture.CreateClient();
        var branding = await anonymous.GetFromJsonAsync<JsonElement>("/api/branding");
        Assert.Equal("Пример-Строй", branding.GetProperty("productName").GetString());
        Assert.True(branding.GetProperty("hasLogo").GetBoolean());

        // И сам файл вернулся, а не одна строка о нём: иначе логотип «есть», но не рисуется.
        var logo = await anonymous.GetAsync("/api/branding/logo");
        logo.EnsureSuccessStatusCode();
        Assert.Equal(Png, await logo.Content.ReadAsByteArrayAsync());
    }

    private static BackupService BackupOf(IServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<AppDbContext>(),
        scope.ServiceProvider.GetRequiredService<BHS.CRG.Application.Common.IBlobStorage>(),
        NullLogger<BackupService>.Instance,
        scope.ServiceProvider.GetRequiredService<BHS.CRG.Application.Activity.IActivityLog>());

    private static async Task<HttpResponseMessage> UploadLogoAsync(
        HttpClient client, string fileName, byte[] bytes)
    {
        // Форма живёт ДО конца отправки: возврат задачи из-под using закрывал поток раньше, чем
        // тело уходило в сокет, и тест падал на чтении закрытого потока, а не на поведении.
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        return await client.PostAsync("/api/branding/logo", form);
    }

    /// <summary>Тип документа и шаблон — резолверу ассетов нужны оба уровня, чтобы дойти до системного.</summary>
    private static async Task<(Guid TemplateId, Guid TypeId)> SeedTemplateAsync(IServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var typeId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        db.DocumentTypes.Add(DocumentType.Restore(
            typeId, "Акт", $"act-{Guid.NewGuid():N}"[..12], DocumentTypeKind.Document, null,
            JsonDocument.Parse("""{"fields":[]}"""), JsonDocument.Parse("{}"), false, now, now, null, false));
        db.Templates.Add(Template.Restore(templateId, typeId, "Шаблон", "#set page()", 1, true, true, now, now));
        await db.SaveChangesAsync();
        return (templateId, typeId);
    }

    private async Task<HttpClient> SignInAsync(string role)
    {
        var email = $"brand_{role.ToLowerInvariant()}_{Guid.NewGuid():N}@test.local";
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = email, Email = email, DisplayName = "Тест", EmailConfirmed = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        }

        var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
