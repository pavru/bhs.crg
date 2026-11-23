using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Printing;
using BHS.CRG.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

public class RecordPrintTests(RecordPrintHost host) : IClassFixture<RecordPrintHost>
{
    private static string PathFor(string code = RecordPrintHost.TypeCode) => $"/api/print/{code}/{Guid.NewGuid()}";

    [Fact]
    public async Task Print_and_preview_check_form_right_module_and_template()
    {
        var path = PathFor();
        var reader = await GrantedSignIn.WithPermissionsAsync(host, RecordPrintHost.Read);
        var outsider = await GrantedSignIn.WithPermissionsAsync(host, "plan.estimate.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsync(path, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsync(path + "/preview", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(path + "/debug-bundle")).StatusCode);

        var disabled = await reader.PostAsync(PathFor(RecordPrintHost.DisabledTypeCode), null);
        Assert.Equal(HttpStatusCode.NotImplemented, disabled.StatusCode);
        Assert.Equal("costs", (await disabled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("module").GetString());

        var (type, _) = await SeedAsync(withTemplate: false);
        var missing = await reader.PostAsync(path, null);
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        Assert.Contains("У типа нет шаблона печати", (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Templates.Add(Template.Create(type, "Основной", "= Печатная форма"));
        await db.SaveChangesAsync();
        var fileCount = await db.Set<GeneratedFile>().CountAsync();

        var download = await reader.PostAsync(path, null);
        var preview = await reader.PostAsync(path + "/preview", null);
        foreach (var response in new[] { download, preview })
        {
            response.EnsureSuccessStatusCode();
            Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString((await response.Content.ReadAsByteArrayAsync())[..4]));
        }
        Assert.NotNull(download.Content.Headers.ContentDisposition);
        Assert.Null(preview.Content.Headers.ContentDisposition);
        Assert.Equal(fileCount, await db.Set<GeneratedFile>().CountAsync());
    }

    [Fact]
    public async Task Debug_archive_respects_record_field_permissions()
    {
        await SeedAsync();
        var path = PathFor() + "/debug-bundle";
        var editor = await GrantedSignIn.WithPermissionsAsync(host, CorePermissions.PrintEdit);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync(path)).StatusCode);

        var reader = await GrantedSignIn.WithPermissionsAsync(host, RecordPrintHost.Read, CorePermissions.PrintEdit);
        var visible = await ReadArchiveAsync(reader, path);
        Assert.Equal("Проба", visible.GetProperty("title").GetString());
        Assert.False(visible.TryGetProperty("restricted", out _));

        var privileged = await GrantedSignIn.WithPermissionsAsync(host, RecordPrintHost.Read,
            CorePermissions.PrintEdit, "plan.estimate.cost");
        Assert.Equal(42, (await ReadArchiveAsync(privileged, path)).GetProperty("restricted").GetInt32());
    }

    [Fact]
    public async Task Template_editor_checks_owner_for_list_content_and_mutation()
    {
        var (type, template) = await SeedAsync();
        var idEditor = await GrantedSignIn.WithPermissionsAsync(host, "id.config.edit");
        Assert.Equal(HttpStatusCode.Forbidden, (await idEditor.GetAsync($"/api/templates?documentTypeId={type}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await idEditor.GetAsync($"/api/templates/{template}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await idEditor.PutAsJsonAsync($"/api/templates/{template}/content", new { content = "= Подмена" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await idEditor.GetAsync($"/api/template-assets?scope=Template&scopeId={template}")).StatusCode);

        var editor = await GrantedSignIn.WithPermissionsAsync(host, CorePermissions.PrintEdit);
        var types = await editor.GetFromJsonAsync<JsonElement>("/api/templates/types");
        Assert.Equal(type, Assert.Single(types.EnumerateArray()).GetProperty("id").GetGuid());
        var access = await editor.GetFromJsonAsync<JsonElement>("/api/account/access");
        Assert.Contains(access.GetProperty("permissions").EnumerateArray(), p => p.GetString() == CorePermissions.PrintEdit);
        Assert.DoesNotContain(access.GetProperty("permissions").EnumerateArray(), p => p.GetString() == "id.config.edit");
        (await editor.PutAsJsonAsync($"/api/templates/{template}/content", new { content = "= Новое оформление" })).EnsureSuccessStatusCode();
        (await editor.GetAsync("/api/typst-userlib")).EnsureSuccessStatusCode();
    }

    private async Task<(Guid TypeId, Guid? TemplateId)> SeedAsync(bool withTemplate = true)
    {
        _ = host.CreateClient();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var type = await db.DocumentTypes.SingleOrDefaultAsync(t => t.Code == RecordPrintHost.TypeCode);
        if (type is null)
        {
            type = DocumentType.Create("Пробная запись", RecordPrintHost.TypeCode, DocumentTypeKind.Composite,
                null, JsonDocument.Parse("""{"fields":[]}"""), "plan", TypeVisibility.Closed);
            db.DocumentTypes.Add(type);
        }
        var templates = db.Templates.Where(t => t.DocumentTypeId == type.Id);
        db.Templates.RemoveRange(await templates.ToListAsync());
        var template = withTemplate ? Template.Create(type.Id, "Основной", "= Печатная форма") : null;
        await db.SaveChangesAsync();
        if (template is not null)
        {
            db.Templates.Add(template);
            await db.SaveChangesAsync();
        }
        return (type.Id, template?.Id);
    }

    private static async Task<JsonElement> ReadArchiveAsync(HttpClient client, string path)
    {
        await using var stream = new MemoryStream(await client.GetByteArrayAsync(path));
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        await using var data = zip.GetEntry("data.json")!.Open();
        using var document = await JsonDocument.ParseAsync(data);
        return document.RootElement.Clone();
    }
}

public sealed class RecordPrintHost : IntegrationTestFixture
{
    internal const string Read = "plan.offer.read";
    internal const string TypeCode = "ПробнаяПечать";
    internal const string DisabledTypeCode = "ПробнаяВыключеннаяПечать";
    protected override string HostConnectionString => TestDatabases.ConnectionString("record_print");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        var overrides = new Dictionary<string, string?>
        {
            ["Modules:Enabled"] = "id,plan",
            ["ConnectionStrings:Postgres"] = HostConnectionString,
        };
        foreach (var (key, value) in overrides) builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(overrides));
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(new ModulePrintFormCatalog([
                new ProbeModule("plan", TypeCode, Read),
                new ProbeModule("costs", DisabledTypeCode, "costs.invoice.read"),
            ]));
            services.AddKeyedScoped<IModulePrintContext, ProbeContext>(TypeCode);
        });
    }

    private sealed class ProbeContext : IModulePrintContext
    {
        public async Task<ModulePrintResult> BuildAsync(Guid recordId, IModuleUser user, CancellationToken ct)
        {
            var data = new Dictionary<string, object> { ["title"] = "Проба" };
            if (await user.HasAsync("plan.estimate.cost", ct)) data["restricted"] = 42;
            return new ModulePrintResult.Found(JsonSerializer.SerializeToElement(data));
        }
    }

    private sealed class ProbeModule(string code, string typeCode, string permission) : IAppModule
    {
        public string Code => code;
        public string Title => code;
        public IReadOnlyList<AppPermission> Permissions => [new(permission, "читать запись", "пробная запись", ReadAll: ReadAllMark.In)];
        public IReadOnlyList<string> RoutePrefixes => [];
        public IReadOnlyList<ModuleRecordType> RecordTypes => [new(typeCode, "Пробная запись", ModuleSchemaLevel.Closed, [])];
        public IReadOnlyList<ModulePrintForm> PrintForms => [new(typeCode, "Пробная печать", permission)];
        public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }
        public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }
}
