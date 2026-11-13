using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Tests.Support;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Закреплённый документ следует за шаблоном (#1923, вариант А — решение владельца 13.11.2026;
/// id-template-version-integrity, D6, задачи 3.3 и 3.4). Новая версия пересаживает на себя документы,
/// закреплённые за гаснущими версиями, вместе с переопределениями параметров, и возвращает выпущенные
/// в черновик. Прежде документ оставался на погашенной, и генерация отказывала «ни один из выбранных
/// шаблонов не активен», а предпросмотр молча брал другой шаблон.
/// </summary>
[Collection("Integration")]
public class TemplatePinFollowsTemplateTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }

    private async Task<Guid> TypeAsync(string code)
        => (await SendAsync(new CreateDocumentTypeCommand(code, code, DocumentTypeKind.Document, null,
            JsonDocument.Parse("""{"fields":[]}""")))).Id;

    private async Task<Guid> SetAsync()
    {
        var c = await SendAsync(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var s = await SendAsync(new CreateSectionCommand(c.Id, "Раздел"));
        return (await SendAsync(new CreateDocumentSetCommand(s.Id, "Комплект"))).Id;
    }

    /// <summary>Шаблон из <paramref name="count" /> версий — идентификаторы по порядку, действующая последняя.</summary>
    private async Task<List<Guid>> VersionsAsync(Guid type, string name, int count, string content = "= шаблон")
    {
        List<Guid> ids = [(await SendAsync(new CreateTemplateCommand(type, name, content))).Id];
        for (var i = 1; i < count; i++)
            ids.Add((await SendAsync(new UpdateTemplateCommand(ids[^1], content))).Template.Id);
        return ids;
    }

    /// <summary>Документ с выбранным набором шаблонов и переопределениями, отмеченный выпущенным.</summary>
    private async Task<Guid> PinnedDocAsync(Guid setId, Guid type, Guid[] templateIds, string? parameters = null)
    {
        var id = (await SendAsync(new AddDocumentToSetCommand(setId, type))).Id;
        await SendAsync(new SetDocumentTemplatesCommand(id, JsonSerializer.Serialize(templateIds)));
        if (parameters is not null) await SendAsync(new SetDocumentTemplateParamsCommand(id, parameters));
        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDomainObjectRepository>();
        var obj = await repo.GetByIdAsync(id);
        await scope.ServiceProvider.GetRequiredService<IRepository<GeneratedFile>>()
            .AddAsync(obj!.AddGeneratedFile(OutputFormat.Pdf, $"blob/{id}.pdf"));
        await repo.SaveChangesAsync();
        return id;
    }

    private async Task<Domain.Objects.DomainObject> DocAsync(Guid id)
    {
        using var scope = fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IDomainObjectRepository>().GetByIdAsync(id))!;
    }

    private static List<Guid> Ids(Domain.Objects.DomainObject o) => JsonSerializer.Deserialize<List<Guid>>(o.TemplateIds!)!;

    [Fact]
    public async Task Закреплённый_за_v4_переходит_на_v5_с_параметрами_и_в_черновик()
    {
        var type = await TypeAsync("PIN_A");
        var v = await VersionsAsync(type, "Основной", 4);
        var docId = await PinnedDocAsync(await SetAsync(), type, [v[3]], $$$"""{"{{{v[3]}}}":{"orientation":"landscape"}}""");

        var result = await SendAsync(new UpdateTemplateCommand(v[3], "= v5"));

        var doc = await DocAsync(docId);
        Assert.Equal([result.Template.Id], Ids(doc));
        Assert.Contains(result.Template.Id.ToString(), doc.TemplateParams);
        Assert.Contains("landscape", doc.TemplateParams);
        Assert.DoesNotContain(v[3].ToString(), doc.TemplateParams);
        Assert.Equal(DocumentStatus.Draft, doc.Status);
        Assert.Empty(doc.GeneratedFiles);
        Assert.True(result.ResetDocuments >= 1);
    }

    [Fact]
    public async Task Документ_в_генерации_новую_версию_останавливает_и_ничего_не_меняет()
    {
        // Выпуск дописал бы PDF прежней версии уже после пересадки — и документ числился бы
        // выпущенным новой. Ждать его нечем, поэтому отказ: повторить можно, когда выпуск закончится.
        var type = await TypeAsync("PIN_G");
        var v = await VersionsAsync(type, "Основной", 1);
        var docId = await PinnedDocAsync(await SetAsync(), type, [v[0]]);
        using (var scope = fixture.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDomainObjectRepository>();
            (await repo.GetByIdAsync(docId))!.MarkGenerating();
            await repo.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<ConflictException>(() => SendAsync(new UpdateTemplateCommand(v[0], "= v2")));

        Assert.Single(await SendAsync(new ListTemplatesQuery(type)));
        Assert.Equal([v[0]], Ids(await DocAsync(docId)));
    }

    [Fact]
    public async Task Набор_из_двух_шаблонов_сохраняет_порядок()
    {
        var type = await TypeAsync("PIN_B");
        var main = await VersionsAsync(type, "Основной", 4);
        var brief = await VersionsAsync(type, "Краткий", 2);
        var docId = await PinnedDocAsync(await SetAsync(), type, [main[3], brief[1]]);

        var v5 = (await SendAsync(new UpdateTemplateCommand(main[3], "= v5"))).Template.Id;

        Assert.Equal([v5, brief[1]], Ids(await DocAsync(docId)));
    }

    [Fact]
    public async Task Новая_версия_от_исторической_пересаживает_закреплённые_за_действующей()
    {
        var type = await TypeAsync("PIN_C");
        var v = await VersionsAsync(type, "Основной", 4);
        var setId = await SetAsync();
        var onActive = await PinnedDocAsync(setId, type, [v[3]]);
        var onHistoric = await PinnedDocAsync(setId, type, [v[1]]);

        var v5 = (await SendAsync(new UpdateTemplateCommand(v[1], "= от v2"))).Template.Id;

        Assert.Equal([v5], Ids(await DocAsync(onActive)));
        // Гаснет v4, а не v2: закреплённый за историческей v2 и прежде на ней стоял.
        Assert.Equal([v[1]], Ids(await DocAsync(onHistoric)));
        Assert.Equal(DocumentStatus.Generated, (await DocAsync(onHistoric)).Status);
    }

    /// <summary>
    /// Сторож #1923 целиком: закреплён за действующей, новая версия, затем генерация и предпросмотр.
    /// Прежняя версия и шаблон по умолчанию другого названия не компилируются — успешный выпуск
    /// возможен только новой версией. На master генерация отказывала «не активен», а предпросмотр
    /// брал шаблон по умолчанию.
    /// </summary>
    [Fact]
    public async Task После_новой_версии_генерация_и_предпросмотр_берут_её()
    {
        TypstRequired.Ensure();
        var type = await TypeAsync("PIN_D");
        var old = (await SendAsync(new CreateTemplateCommand(type, "Основной", "#panic(\"прежняя версия\")"))).Id;
        var other = (await SendAsync(new CreateTemplateCommand(type, "Краткий", "#panic(\"чужой шаблон\")"))).Id;
        await SendAsync(new SetTemplateDefaultCommand(other));
        var docId = await PinnedDocAsync(await SetAsync(), type, [old]);

        var v2 = (await SendAsync(new UpdateTemplateCommand(old, "= Новая версия"))).Template.Id;

        var client = await GrantedSignIn.WithPermissionsAsync(fixture, "id.document.generate");
        var preview = await client.PostAsJsonAsync($"/api/generate/preview/{docId}", new { });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("application/pdf", preview.Content.Headers.ContentType?.MediaType);

        var generated = await client.PostAsJsonAsync($"/api/generate/{docId}", new { format = "pdf" });
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        var file = Assert.Single((await generated.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Equal(v2, file.GetProperty("templateId").GetGuid());
    }
}
