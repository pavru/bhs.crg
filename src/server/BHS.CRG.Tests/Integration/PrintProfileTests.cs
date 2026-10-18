using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Api.Renditions;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Storage;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Renditions;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static BHS.CRG.Tests.Renditions.RenditionFixtures;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Профили печати в ядре (issue #1454): системный есть сразу и не удаляется, список открыт праву
/// работы с файлами, запись — обслуживанию экземпляра, а перестроение выбирает профиль по правилам.
///
/// <para>Таблица профилей между классами не усекается (системный создаёт старт), поэтому этот класс
/// сам возвращает её к одному заводскому профилю — до и после себя.</para>
/// </summary>
[Collection("Integration")]
public class PrintProfileTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await fixture.ResetDatabaseAsync();
        await PrintProfilesState.ResetAsync(fixture.Services);
    }

    public Task DisposeAsync() => PrintProfilesState.ResetAsync(fixture.Services);

    private const string Url = "/api/settings/print-profiles";
    private static readonly string[] Words = ["invoice", "supplier", "total"];

    private IBlobStorage Blobs => fixture.Services.GetRequiredService<IBlobStorage>();

    private RenditionStore Store() => new(
        fixture.Services.GetRequiredService<IServiceScopeFactory>(), Blobs,
        Service(StubConverter.Converts(Words).HandleAsync), NullLogger<RenditionStore>.Instance);

    private Task<string> UploadWideBookAsync() => Blobs.UploadAsync("счёт.xlsx", new MemoryStream(
        Workbook(b => b.CreateSheet("Sheet").CreateRow(0).CreateCell(0).SetCellValue(string.Join(" ", Words)), ownLayout: false)),
        "application/octet-stream");

    private static object Body(string name, string paper = "A3", string orientation = "landscape", string fit = "none",
        int margin = 10) => new { name, paper, orientation, fit, margins = new { top = margin, bottom = margin, left = margin, right = margin } };

    private async Task<RenditionLayoutProfile> CreateAsync(string name, int margin = 10)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RenditionLayoutProfiles>().CreateAsync(
            new(name, PaperFormat.A4, PageOrientation.Portrait, PageFit.Width, new(margin, margin, margin, margin)), default);
    }

    private async Task UpdateAsync(Guid id, string name, int margin)
    {
        using var scope = fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RenditionLayoutProfiles>().UpdateAsync(id,
            new(name, PaperFormat.A4, PageOrientation.Portrait, PageFit.Width, new(margin, margin, margin, margin)), default);
    }

    private async Task DeleteAsync(Guid id)
    {
        using var scope = fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RenditionLayoutProfiles>().DeleteAsync(id, default);
    }

    private async Task<RenditionRecord> RecordAsync(string original)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Renditions.AsNoTracking()
            .SingleAsync(r => r.OriginalBlobPath == original);
    }

    // ── Системный профиль ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// На пустой таблице сидер заводит системный — один, под идентификатором-константой и с заводскими
    /// параметрами; второй прогон (второй процесс, второй старт) второго не заводит.
    /// </summary>
    [Fact]
    public async Task Сидер_заводит_один_системный_профиль_и_второго_не_заводит()
    {
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().RenditionLayoutProfiles.ExecuteDeleteAsync();

        for (var i = 0; i < 2; i++)
        {
            using var scope = fixture.Services.CreateScope();
            await RenditionLayoutProfileSeeder.SeedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }

        using (var scope = fixture.Services.CreateScope())
        {
            var profile = Assert.Single(await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .RenditionLayoutProfiles.AsNoTracking().ToListAsync());
            Assert.Equal((RenditionLayoutProfile.SystemId, true, true), (profile.Id, profile.IsSystem, profile.IsFactory));
        }
    }

    /// <summary>Правленый системный сидер не трогает: тихого пересева при старте нет.</summary>
    [Fact]
    public async Task Сидер_правку_системного_профиля_не_затирает()
    {
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.RenditionLayoutProfiles.SingleAsync(p => p.IsSystem)).Update(
                RenditionLayoutProfile.SystemName, PaperFormat.A3, PageOrientation.Landscape, PageFit.None, new(9, 9, 9, 9));
            await db.SaveChangesAsync();
            await RenditionLayoutProfileSeeder.SeedAsync(db);
        }

        using (var scope = fixture.Services.CreateScope())
            Assert.Equal(PaperFormat.A3, (await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .RenditionLayoutProfiles.SingleAsync(p => p.IsSystem)).Paper);
    }

    // ── Адреса и права ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Список нужен меню «Перестроить ▾» у счёта — его видит держатель права работы с файлами, а не
    /// только администратор. Запись — только обслуживанию экземпляра.
    /// </summary>
    [Fact]
    public async Task Список_открыт_праву_работы_с_файлами_а_запись_только_обслуживанию()
    {
        var engineer = await BrandingClient.SignInAsync(fixture, SystemRoles.IdEngineer);
        var system = RenditionLayoutProfile.SystemId;

        var list = await engineer.GetFromJsonAsync<JsonElement>(Url);
        var first = list.EnumerateArray().First();
        Assert.Equal((RenditionLayoutProfile.SystemName, true, "A4", "portrait", "width"), (first.GetProperty("name").GetString(),
            first.GetProperty("isSystem").GetBoolean(), first.GetProperty("paper").GetString(),
            first.GetProperty("orientation").GetString(), first.GetProperty("fit").GetString()));

        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.PostAsJsonAsync(Url, Body("Свой"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.PutAsJsonAsync($"{Url}/{system}", Body(RenditionLayoutProfile.SystemName))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.PostAsync($"{Url}/{system}/reset", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.DeleteAsync($"{Url}/{system}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.CreateClient().GetAsync(Url)).StatusCode);
    }

    [Fact]
    public async Task Администратор_заводит_правит_и_удаляет_свой_профиль_а_системный_не_удаляется()
    {
        var admin = await BrandingClient.SignInAsync(fixture, SystemRoles.Admin);
        var system = RenditionLayoutProfile.SystemId;

        var created = await admin.PostAsJsonAsync(Url, Body("Альбомный A3"));
        created.EnsureSuccessStatusCode();
        var own = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = own.GetProperty("id").GetGuid();
        Assert.Equal(("A3", "landscape", "none", 10), (own.GetProperty("paper").GetString(), own.GetProperty("orientation").GetString(),
            own.GetProperty("fit").GetString(), own.GetProperty("margins").GetProperty("left").GetInt32()));

        // Имя уникально без учёта регистра — и системное тоже занято. Отказы называют поле: экран
        // подсвечивает его, а не показывает баннер.
        Assert.Equal((HttpStatusCode.Conflict, "name"), await RefusedAtAsync(admin.PostAsJsonAsync(Url, Body("альбомный a3"))));
        Assert.Equal((HttpStatusCode.Conflict, "name"), await RefusedAtAsync(admin.PostAsJsonAsync(Url, Body("По умолчанию"))));
        Assert.Equal((HttpStatusCode.BadRequest, "margins.top"),
            await RefusedAtAsync(admin.PostAsJsonAsync(Url, Body("Широкие поля", margin: 41))));
        Assert.Equal((HttpStatusCode.BadRequest, "paper"), await RefusedAtAsync(admin.PostAsJsonAsync(Url, Body("Бумага", paper: "Letter"))));
        Assert.Equal((HttpStatusCode.BadRequest, "name"), await RefusedAtAsync(admin.PostAsJsonAsync(Url, Body(" "))));

        // Остаток называется числом — экран пишет «остаётся N мм» под схемой листа.
        var narrow = await admin.PostAsJsonAsync(Url, Body("Узкий A5", paper: "A5", orientation: "portrait", margin: 25));
        Assert.Equal(HttpStatusCode.BadRequest, narrow.StatusCode);
        var refusal = await narrow.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("По ширине остаётся 98 мм", refusal.GetProperty("error").GetString());
        Assert.Equal(["margins.left", "margins.right"],
            refusal.GetProperty("details").EnumerateArray().Select(d => d.GetProperty("path").GetString()));

        (await admin.PutAsJsonAsync($"{Url}/{id}", Body("Альбомный", paper: "A4"))).EnsureSuccessStatusCode();

        var refused = await admin.DeleteAsync($"{Url}/{system}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("системный", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"{Url}/{system}", Body("Мой"))).StatusCode);

        // Системный правится и сбрасывается к заводским.
        var edited = await (await admin.PutAsJsonAsync($"{Url}/{system}", Body(RenditionLayoutProfile.SystemName, paper: "A5", margin: 5)))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("A5", false), (edited.GetProperty("paper").GetString(), edited.GetProperty("isFactory").GetBoolean()));
        var reset = await (await admin.PostAsync($"{Url}/{system}/reset", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("A4", true), (reset.GetProperty("paper").GetString(), reset.GetProperty("isFactory").GetBoolean()));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"{Url}/{id}/reset", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Url}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"{Url}/{id}")).StatusCode);
        Assert.Equal([system], (await admin.GetFromJsonAsync<JsonElement>(Url)).EnumerateArray().Select(p => p.GetProperty("id").GetGuid()));
    }

    /// <summary>
    /// Заводские параметры знает только сервер — и отдаёт их у системного профиля: экран показывает,
    /// к чему приведёт «Сбросить к заводским». У своего профиля заводских нет.
    /// </summary>
    [Fact]
    public async Task Системный_профиль_несёт_заводские_параметры_а_свой_нет()
    {
        var admin = await BrandingClient.SignInAsync(fixture, SystemRoles.Admin);
        (await admin.PostAsJsonAsync(Url, Body("Свой"))).EnsureSuccessStatusCode();

        var list = (await admin.GetFromJsonAsync<JsonElement>(Url)).EnumerateArray().ToList();

        var factory = list[0].GetProperty("factory");
        Assert.Equal((RenditionLayoutProfile.SystemName, "A4", "portrait", "width", 5), (factory.GetProperty("name").GetString(),
            factory.GetProperty("paper").GetString(), factory.GetProperty("orientation").GetString(),
            factory.GetProperty("fit").GetString(), factory.GetProperty("margins").GetProperty("top").GetInt32()));
        Assert.Equal(JsonValueKind.Null, list[1].GetProperty("factory").ValueKind);
    }

    private static async Task<(HttpStatusCode, string?)> RefusedAtAsync(Task<HttpResponseMessage> sending)
    {
        var response = await sending;
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response.StatusCode, body.GetProperty("details").EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } first
            ? first.GetProperty("path").GetString()
            : null);
    }

    // ── Построение по профилю ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Правила выбора: первое построение — системный; названный — он; не названный — тот, что помнит
    /// запись, пока он есть, иначе системный; названный несуществующий — «не найдено», а не молча
    /// системный, и запись прежняя.
    /// </summary>
    [Fact]
    public async Task Перестроение_выбирает_профиль_по_правилам()
    {
        var store = Store();
        var original = await UploadWideBookAsync();
        var own = await CreateAsync("Узкий");

        Assert.Equal(RenditionLayoutProfile.SystemId, (await store.EnsureAsync(original, default)).LayoutProfileId);

        var named = await store.RebuildAsync(original, own.Id, default);
        Assert.Equal((own.Id, own.Stamp), (named.LayoutProfileId, named.LayoutStamp));
        Assert.Contains("Узкий", named.LayoutApplied);

        Assert.Equal(own.Id, (await store.RebuildAsync(original, null, default)).LayoutProfileId);

        await Assert.ThrowsAsync<NotFoundException>(() => store.RebuildAsync(original, Guid.NewGuid(), default));
        Assert.Equal(own.Id, (await RecordAsync(original)).LayoutProfileId);

        await DeleteAsync(own.Id);
        Assert.Equal(RenditionLayoutProfile.SystemId, (await store.RebuildAsync(original, null, default)).LayoutProfileId);
    }

    /// <summary>
    /// Правка профиля: новые виды строятся по новым параметрам, старая запись не меняется — и при чтении
    /// называется «профиль изменён». Переименование вид не «старит».
    /// </summary>
    [Fact]
    public async Task Правка_профиля_старит_вид_при_чтении_а_запись_не_трогает()
    {
        var store = Store();
        var port = new ModuleRenditionsPort(store, NullLogger<ModuleRenditionsPort>.Instance);
        var original = await UploadWideBookAsync();
        var own = await CreateAsync("Узкий");
        var built = await store.RebuildAsync(original, own.Id, default);

        Assert.Equal(new ModuleRenditionLayout(ModuleLayoutOutcome.Applied, own.Id, "Узкий", ModuleLayoutProfileState.Current),
            Layout(await port.FindAsync(original)));

        await UpdateAsync(own.Id, "Узенький", 10);
        Assert.Equal(new ModuleRenditionLayout(ModuleLayoutOutcome.Applied, own.Id, "Узенький", ModuleLayoutProfileState.Current),
            Layout(await port.FindAsync(original)));

        await UpdateAsync(own.Id, "Узенький", 12);
        Assert.Equal(built.LayoutStamp, (await RecordAsync(original)).LayoutStamp);
        Assert.Equal(ModuleLayoutProfileState.Changed, Layout(await port.FindAsync(original)).ProfileState);

        var rebuilt = Layout(await port.RebuildAsync(original));
        Assert.Equal(ModuleLayoutProfileState.Current, rebuilt.ProfileState);
        Assert.Contains("\"top\": 12", (await RecordAsync(original)).LayoutApplied);
    }

    /// <summary>
    /// Свой профиль удаляется, даже если по нему построены виды: ссылка без внешнего ключа. Запись вида
    /// остаётся как была и при чтении называется «профиль удалён».
    /// </summary>
    [Fact]
    public async Task Удаление_своего_профиля_оставляет_записи_видов()
    {
        var store = Store();
        var port = new ModuleRenditionsPort(store, NullLogger<ModuleRenditionsPort>.Instance);
        var original = await UploadWideBookAsync();
        var own = await CreateAsync("Узкий");
        await store.RebuildAsync(original, own.Id, default);

        await DeleteAsync(own.Id);

        Assert.Equal(own.Id, (await RecordAsync(original)).LayoutProfileId);
        Assert.Equal(new ModuleRenditionLayout(ModuleLayoutOutcome.Applied, own.Id, null, ModuleLayoutProfileState.Deleted),
            Layout(await port.FindAsync(original)));
    }

    /// <summary>
    /// Книга со своей раскладкой профиля не получала — и не помнит его: правка системного не делает её
    /// вид «изменённым», перестроение дало бы тот же (ревью PR #1472).
    /// </summary>
    [Fact]
    public async Task Книга_со_своей_раскладкой_профиля_не_помнит()
    {
        var store = Store();
        var port = new ModuleRenditionsPort(store, NullLogger<ModuleRenditionsPort>.Instance);
        var original = await Blobs.UploadAsync("счёт.xlsx", new MemoryStream(Workbook(Words)), "application/octet-stream");

        var record = await store.EnsureAsync(original, default);
        await UpdateAsync(RenditionLayoutProfile.SystemId, RenditionLayoutProfile.SystemName, 9);

        Assert.Equal((RenditionLayout.BookOwn, (Guid?)null), (record.Layout, record.LayoutProfileId));
        Assert.Equal(new ModuleRenditionLayout(ModuleLayoutOutcome.BookOwn, null, null, null), Layout(await port.FindAsync(original)));
    }

    /// <summary>
    /// Вид, вписанный константой #1453 (исход есть, профиля нет), — «построен до профилей»: перестроение
    /// применит нынешний профиль, и экран обязан это сказать (ревью PR #1472).
    /// </summary>
    [Fact]
    public async Task Вписанный_до_профилей_вид_называется_построенным_до_профилей()
    {
        var port = new ModuleRenditionsPort(Store(), NullLogger<ModuleRenditionsPort>.Instance);
        const string original = "files/2026/10/вписан-константой.xlsx";
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Renditions.Add(RenditionRecord.Built(original, "files/2026/10/rendition.pdf", 1, [], "gotenberg 8.37.0",
                RenditionLayout.Applied, """{"profile":"default","fit":"width"}"""));
            await db.SaveChangesAsync();
        }

        Assert.Equal(new ModuleRenditionLayout(ModuleLayoutOutcome.BeforeProfiles, null, null, null), Layout(await port.FindAsync(original)));
    }

    /// <summary>Профиль помнит только книга: у Word и у файла, который читается сам, его нет.</summary>
    [Fact]
    public async Task Не_книга_профиля_не_помнит()
    {
        var store = Store();
        var port = new ModuleRenditionsPort(store, NullLogger<ModuleRenditionsPort>.Instance);
        var word = await Blobs.UploadAsync("письмо.docx", new MemoryStream(Document()), "application/octet-stream");

        var record = await store.EnsureAsync(word, default);

        Assert.Equal((RenditionLayout.NotBook, (Guid?)null, (string?)null), (record.Layout, record.LayoutProfileId, record.LayoutStamp));
        Assert.Equal(new ModuleRenditionLayout(ModuleLayoutOutcome.NotBook, null, null, null), Layout(await port.FindAsync(word)));
    }

    private static ModuleRenditionLayout Layout(ModuleRendition? rendition) =>
        Assert.IsType<ModuleRendition.Built>(rendition).Layout!;
}

/// <summary>
/// Таблица профилей печати между классами не усекается (системный создаёт старт). Тесты, которые её
/// меняют, возвращают её к одному заводскому системному профилю этим.
/// </summary>
internal static class PrintProfilesState
{
    public static async Task ResetAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Системный не под своей константой тоже уходит: такую строку оставил бы прогон, сломавший
        // константу, — и общая тестовая база несла бы её во все следующие прогоны.
        await db.RenditionLayoutProfiles.Where(p => !p.IsSystem || p.Id != RenditionLayoutProfile.SystemId).ExecuteDeleteAsync();
        await RenditionLayoutProfileSeeder.SeedAsync(db);
        var system = await db.RenditionLayoutProfiles.SingleAsync(p => p.IsSystem);
        if (system.IsFactory) return;
        system.ResetToFactory();
        await db.SaveChangesAsync();
    }
}
