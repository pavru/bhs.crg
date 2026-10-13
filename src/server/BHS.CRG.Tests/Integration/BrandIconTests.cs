using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Branding;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Infrastructure.Branding;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using static BHS.CRG.Tests.Branding.BrandIconSamples;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Логотип компании как значок вкладки и приложения (issue #1315) — от загрузки до ответа адреса.
///
/// <para>⚠️ Здесь работает НАСТОЯЩИЙ Typst: он растрирует логотип. Подмена сделала бы зелёным
/// именно то, что задача обещает проверить, — что из каждого из четырёх форматов выходит PNG нужного
/// размера с логотипом целиком. Нет Typst — тесты красные, а не пропущенные.</para>
/// </summary>
[Collection("Integration")]
public class BrandIconTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private const string Password = "Passw0rd!";

    private static readonly (string Route, BrandIconKind Kind, int Side)[] Routes =
    [
        ("tab", BrandIconKind.Tab, 32), ("apple", BrandIconKind.Apple, 180),
        ("app-192", BrandIconKind.App192, 192), ("app-512", BrandIconKind.App512, 512),
        ("maskable-512", BrandIconKind.Maskable512, 512),
    ];

    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Без_логотипа_адреса_отдают_стандартные_картинки()
    {
        var anonymous = fixture.CreateClient();

        foreach (var (route, kind, _) in Routes)
        {
            var response = await anonymous.GetAsync($"/api/branding/icons/{route}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var standard = StandardBrandIcons.Get(kind);
            Assert.Equal(standard.MimeType, response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(standard.Content, await response.Content.ReadAsByteArrayAsync());
            // Ссылка в index.html идёт без метки версии — такой ответ обязан проверяться на свежесть.
            Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        }

        var icons = (await BrandingAsync(anonymous)).GetProperty("icons");
        Assert.Equal(BrandIconsState.Standard, icons.GetProperty("state").GetString());
        // Метка стандартных — по содержимому картинок, а не постоянное слово: сборка с новыми
        // картинками обязана дать новый адрес, иначе длинный кеш неделю отдавал бы прежние.
        var version = icons.GetProperty("version").GetString();
        Assert.Equal(StandardBrandIcons.Version, version);
        Assert.Matches("^std-[0-9a-f]{8}$", version);
        Assert.Equal("public, max-age=604800",
            (await anonymous.GetAsync($"/api/branding/icons/app-192?v={version}")).Headers.CacheControl?.ToString());
        // Значок вкладки без логотипа — SVG: он сам светлеет на тёмной вкладке.
        Assert.Equal("image/svg+xml", StandardBrandIcons.Get(BrandIconKind.Tab).MimeType);
    }

    /// <summary>Образец логотипа по имени файла: строки теории видны в отчёте именами, а не байтами.</summary>
    private static byte[] Sample(string fileName) => Path.GetExtension(fileName) switch
    {
        ".png" => Logo(800, 200),
        ".jpg" => Logo(800, 200, SKEncodedImageFormat.Jpeg, background: SKColors.White),
        ".webp" => Logo(800, 200, SKEncodedImageFormat.Webp),
        _ => Svg(800, 200),
    };

    /// <summary>
    /// Сторож задачи: логотип каждого из четырёх принимаемых форматов становится значками — PNG
    /// точного размера, логотип целиком. Один из форматов векторный: его не читает ни одна
    /// библиотека картинок в решении, и «молча оставить стандартный значок» здесь нельзя.
    /// </summary>
    [Theory]
    [InlineData("logo.png")]
    [InlineData("logo.jpg")]
    [InlineData("logo.webp")]
    [InlineData("logo.svg")]
    public async Task Логотип_каждого_формата_становится_значками_нужного_размера(string fileName)
    {
        var admin = await SignInAsync(SystemRoles.Admin);

        var uploaded = await UploadedAsync(admin, fileName, Sample(fileName));

        // Построены уже к ответу загрузки: администратор сразу видит, получились ли.
        var icons = uploaded.GetProperty("icons");
        Assert.Equal(BrandIconsState.Custom, icons.GetProperty("state").GetString());
        // В метке — логотип и отпечаток самих значков: картинки меняются и без смены логотипа
        // (обновление приложения с другими полями), и метка обязана это заметить.
        var version = icons.GetProperty("version").GetString()!;
        Assert.Matches($"^{uploaded.GetProperty("logoVersion").GetString()}-[0-9a-f]{{8}}$", version);
        Assert.Equal(4.0, icons.GetProperty("aspect").GetDouble(), 1);

        var anonymous = fixture.CreateClient();
        foreach (var (route, _, side) in Routes)
        {
            var response = await anonymous.GetAsync($"/api/branding/icons/{route}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal((side, side), Size(await response.Content.ReadAsByteArrayAsync()));
            // Тег следует за меткой, а с ней — за содержимым значков.
            Assert.Equal($"\"{version}-{route}\"", response.Headers.ETag?.Tag);
        }

        // Логотип не обрезан: метки у его краёв стоят там, куда он вписан, — 76 % стороны на четверть.
        var marks = Marks(await anonymous.GetByteArrayAsync("/api/branding/icons/app-512"));
        Assert.InRange(marks.Width, 383, 392);
        Assert.InRange(marks.Height, 92, 100);
    }

    /// <summary>
    /// Высокий логотип — через настоящий Typst. Тесты сборщика получают растр готовым и страницу
    /// растеризатора не видят: без вписывания по высоте она растянула бы логотип 200×600 на ширину
    /// страницы и срезала бы его до средней трети — «успешно» и с неверными пропорциями.
    /// </summary>
    [Fact]
    public async Task Высокий_логотип_вписан_по_высоте_и_не_обрезан()
    {
        var admin = await SignInAsync(SystemRoles.Admin);

        var uploaded = await UploadedAsync(admin, "logo.png", Logo(200, 600));

        Assert.Equal(0.333, uploaded.GetProperty("icons").GetProperty("aspect").GetDouble(), 2);
        var marks = Marks(await fixture.CreateClient().GetByteArrayAsync("/api/branding/icons/app-512"));
        Assert.InRange(marks.Height, 383, 392);
        Assert.InRange(marks.Width, 124, 134);
    }

    /// <summary>
    /// Свой фон логотипа — на растре, который отдал Typst, а не на исходных точках: правило требует
    /// залитого прямоугольника и одноцветных углов, и пересчёт картинки мог бы нарушить оба. Фон
    /// цветной нарочно: белый дал бы белую плитку и без этого правила.
    /// </summary>
    [Fact]
    public async Task Цветной_фон_логотипа_плитка_продолжает_и_после_растеризации()
    {
        var admin = await SignInAsync(SystemRoles.Admin);
        var fill = new SKColor(0x10, 0x9e, 0x92);

        await UploadedAsync(admin, "logo.jpg", Logo(800, 200, SKEncodedImageFormat.Jpeg, background: fill, body: SKColors.White));

        // Угол значка iPhone — плитка: логотип занимает середину. Допуск — на сжатие JPEG.
        var corner = Pixel(await fixture.CreateClient().GetByteArrayAsync("/api/branding/icons/apple"), 4, 4);
        Assert.InRange(corner.Red, fill.Red - 12, fill.Red + 12);
        Assert.InRange(corner.Green, fill.Green - 12, fill.Green + 12);
        Assert.InRange(corner.Blue, fill.Blue - 12, fill.Blue + 12);
    }

    [Fact]
    public async Task Смена_логотипа_меняет_тег_значка_а_неизменный_переспрашивается_одним_304()
    {
        var admin = await SignInAsync(SystemRoles.Admin);
        await UploadedAsync(admin, "logo.png", Logo(800, 200));
        var anonymous = fixture.CreateClient();

        var first = await anonymous.GetAsync("/api/branding/icons/tab");
        var tag = first.Headers.ETag;
        Assert.NotNull(tag);

        var again = new HttpRequestMessage(HttpMethod.Get, "/api/branding/icons/tab");
        again.Headers.IfNoneMatch.Add(tag!);
        Assert.Equal(HttpStatusCode.NotModified, (await anonymous.SendAsync(again)).StatusCode);

        var replacedInfo = await UploadedAsync(admin, "logo.png", Logo(400, 400));
        // Значки — от НОВОГО логотипа. Тег выводится из метки и о ячейке хранилища значков сам по
        // себе не знает: прежний набор под новой меткой (ключ ячейки без версии) прошёл бы
        // проверку тега зелёным. Прежний логотип — 4:1, новый — квадрат.
        Assert.Equal(1.0, replacedInfo.GetProperty("icons").GetProperty("aspect").GetDouble(), 1);

        var replaced = new HttpRequestMessage(HttpMethod.Get, "/api/branding/icons/tab");
        replaced.Headers.IfNoneMatch.Add(tag!);
        var fresh = await anonymous.SendAsync(replaced);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.NotEqual(tag, fresh.Headers.ETag);

        // И сами точки — нового: у квадратного логотипа метки занимают всю высоту вписанного.
        var marks = Marks(await anonymous.GetByteArrayAsync("/api/branding/icons/app-512"));
        Assert.InRange(marks.Height, 383, 392);
    }

    /// <summary>
    /// Длинный кеш — только адресу с ДЕЙСТВУЮЩЕЙ меткой. Устаревшая метка длинного кеша не получает:
    /// он закрепил бы новую картинку за адресом старой.
    /// </summary>
    [Fact]
    public async Task Длинный_кеш_достаётся_только_адресу_с_действующей_меткой()
    {
        var admin = await SignInAsync(SystemRoles.Admin);
        var version = (await BuiltAsync(admin, "logo.png", Logo(800, 200))).GetProperty("version").GetString();
        var anonymous = fixture.CreateClient();

        async Task<string?> CacheOf(string query) =>
            (await anonymous.GetAsync("/api/branding/icons/app-192" + query)).Headers.CacheControl?.ToString();

        Assert.Equal("public, max-age=604800", await CacheOf($"?v={version}"));
        Assert.Equal("no-cache", await CacheOf(""));
        Assert.Equal("no-cache", await CacheOf("?v=1"));
    }

    [Fact]
    public async Task Манифест_несёт_название_продукта_и_значки_с_меткой_версии()
    {
        var admin = await SignInAsync(SystemRoles.Admin);
        var anonymous = fixture.CreateClient();

        // До настройки — общее название и стандартные значки.
        var plain = await ManifestAsync(anonymous);
        Assert.Equal(BrandingDefaults.ProductName, plain.GetProperty("name").GetString());
        Assert.All(plain.GetProperty("icons").EnumerateArray(),
            i => Assert.EndsWith("?v=" + StandardBrandIcons.Version, i.GetProperty("src").GetString()));

        (await admin.PutAsJsonAsync("/api/branding", new { productName = "Пример-Строй" })).EnsureSuccessStatusCode();
        var version = (await BuiltAsync(admin, "logo.png", Logo(800, 200))).GetProperty("version").GetString();

        var response = await anonymous.GetAsync("/api/branding/manifest.webmanifest");
        Assert.Equal(BrandManifest.ContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        var manifest = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("Пример-Строй", manifest.GetProperty("name").GetString());
        Assert.Equal("Пример-Строй", manifest.GetProperty("short_name").GetString());
        Assert.Equal("/", manifest.GetProperty("start_url").GetString());
        Assert.Equal(
            new[] { ("192x192", "any"), ("512x512", "any"), ("512x512", "maskable") },
            manifest.GetProperty("icons").EnumerateArray()
                .Select(i => (i.GetProperty("sizes").GetString()!, i.GetProperty("purpose").GetString()!)));

        // Каждый значок манифеста — живой адрес с действующей меткой: отвечает картинкой и длинным кешем.
        foreach (var icon in manifest.GetProperty("icons").EnumerateArray())
        {
            var src = icon.GetProperty("src").GetString()!;
            Assert.EndsWith("?v=" + version, src);
            var served = await anonymous.GetAsync(src);
            Assert.Equal("image/png", served.Content.Headers.ContentType?.MediaType);
            Assert.Equal("public, max-age=604800", served.Headers.CacheControl?.ToString());
        }
    }

    /// <summary>
    /// Отказ построения — не отказ загрузки. Логотип сохранён и отдаётся; значки остаются
    /// стандартными — и состояние говорит об этом прямо. Стандартный значок, отданный молча вместо
    /// непостроенного, был бы отказом, переодетым в результат.
    /// </summary>
    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 9 9\"><image href=\"https://example.invalid/x.png\" width=\"9\" height=\"9\"/></svg>")]
    public async Task Значки_не_построены_логотип_сохранён_а_состояние_названо(string svg)
    {
        var admin = await SignInAsync(SystemRoles.Admin);

        var uploaded = await UploadedAsync(admin, "logo.svg", Encoding.UTF8.GetBytes(svg));

        Assert.True(uploaded.GetProperty("hasLogo").GetBoolean());
        var icons = uploaded.GetProperty("icons");
        Assert.Equal(BrandIconsState.Failed, icons.GetProperty("state").GetString());
        Assert.Equal(BrandIconProblem.File, icons.GetProperty("problem").GetString());
        // Метка — стандартная: отдаются стандартные картинки.
        Assert.Equal(StandardBrandIcons.Version, icons.GetProperty("version").GetString());

        var anonymous = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/branding/logo")).StatusCode);
        Assert.Equal(StandardBrandIcons.Get(BrandIconKind.Apple).Content,
            await anonymous.GetByteArrayAsync("/api/branding/icons/apple"));
    }

    [Fact]
    public async Task Убранный_логотип_возвращает_стандартные_значки()
    {
        var admin = await SignInAsync(SystemRoles.Admin);
        await UploadedAsync(admin, "logo.png", Logo(800, 200));

        var removed = await (await admin.DeleteAsync("/api/branding/logo")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(BrandIconsState.Standard, removed.GetProperty("icons").GetProperty("state").GetString());
        Assert.Equal(StandardBrandIcons.Get(BrandIconKind.App192).Content,
            await fixture.CreateClient().GetByteArrayAsync("/api/branding/icons/app-192"));
    }

    /// <summary>
    /// Ответ загрузки и следующее чтение называют ОДНУ метку версии. База хранит момент замены
    /// грубее, чем память; без перечитывания строки метки расходились, значки строились бы дважды, а
    /// открытая вкладка видела бы смену логотипа, которой не было.
    /// </summary>
    [Fact]
    public async Task Метка_версии_в_ответе_загрузки_та_же_что_при_следующем_чтении()
    {
        var admin = await SignInAsync(SystemRoles.Admin);

        var uploaded = await UploadedAsync(admin, "logo.png", Logo(800, 200));
        var read = await BrandingAsync(fixture.CreateClient());

        Assert.Equal(uploaded.GetProperty("logoVersion").GetString(), read.GetProperty("logoVersion").GetString());
        Assert.Equal(uploaded.GetProperty("icons").GetRawText(), read.GetProperty("icons").GetRawText());
    }

    /// <summary>
    /// Значки — производное и в резервную копию не входят: после восстановления (и после всякой
    /// замены логотипа мимо службы оформления — экраном ассетов шаблонов) они строятся при первом
    /// обращении. Здесь логотип положен так, как кладёт его восстановление: строкой и файлом.
    /// </summary>
    [Fact]
    public async Task Логотип_положенный_мимо_службы_даёт_значки_при_первом_обращении()
    {
        using (var scope = fixture.Services.CreateScope())
        {
            var blobs = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Имя файла чужое и с неверным расширением: формат читается по содержимому.
            var path = await blobs.UploadAsync("Логотип.GIF", new MemoryStream(Logo(400, 400)), "image/gif");
            db.TemplateAssets.Add(TemplateAsset.Create(
                TemplateAssetScope.System, null, TemplateAssetKind.Image,
                BrandingDefaults.LogoAssetName, "Логотип.GIF", "image/gif", path));
            await db.SaveChangesAsync();
        }

        var anonymous = fixture.CreateClient();
        var icon = await anonymous.GetAsync("/api/branding/icons/apple");
        var bytes = await icon.Content.ReadAsByteArrayAsync();

        Assert.Equal("image/png", icon.Content.Headers.ContentType?.MediaType);
        Assert.Equal((180, 180), Size(bytes));
        // Стандартный значок iPhone — тоже PNG 180×180: без сверки с ним отказ построения (формат
        // выбран по расширению, ассет не найден, Typst не запустился) оставлял бы тест зелёным.
        Assert.NotEqual(StandardBrandIcons.Get(BrandIconKind.Apple).Content, bytes);
        var icons = (await BrandingAsync(anonymous)).GetProperty("icons");
        Assert.Equal(BrandIconsState.Custom, icons.GetProperty("state").GetString());
        // Логотип здесь квадратный, в остальных тестах — 4:1: набор, оставшийся в ячейке от
        // прошлого теста, эту проверку не пройдёт.
        Assert.Equal(1.0, icons.GetProperty("aspect").GetDouble(), 1);
    }

    [Fact]
    public async Task Значка_с_чужим_именем_нет()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            (await fixture.CreateClient().GetAsync("/api/branding/icons/favicon.ico")).StatusCode);
    }

    private static async Task<JsonElement> BrandingAsync(HttpClient client) =>
        await client.GetFromJsonAsync<JsonElement>("/api/branding");

    private static async Task<JsonElement> ManifestAsync(HttpClient client) =>
        JsonDocument.Parse(await client.GetStringAsync("/api/branding/manifest.webmanifest")).RootElement;

    /// <summary>
    /// Загрузить логотип и получить сведения о значках — ПОСТРОЕННЫХ. Без этой сверки тест про
    /// метку и кеш прошёл бы и на стандартных значках, то есть и без Typst вовсе: у стандартных
    /// тоже есть метка, и длинный кеш ей положен так же.
    /// </summary>
    private static async Task<JsonElement> BuiltAsync(HttpClient client, string fileName, byte[] bytes)
    {
        var icons = (await UploadedAsync(client, fileName, bytes)).GetProperty("icons");
        Assert.Equal(BrandIconsState.Custom, icons.GetProperty("state").GetString());
        return icons;
    }

    private static async Task<JsonElement> UploadedAsync(HttpClient client, string fileName, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        var response = await client.PostAsync("/api/branding/logo", form);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<HttpClient> SignInAsync(string role)
    {
        var email = $"icons_{role.ToLowerInvariant()}_{Guid.NewGuid():N}@test.local";
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
