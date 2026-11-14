using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Tests.Support;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Сторож [ID-3] (issue #1401; change id-debug-bundle-template-choice, задачи 1.3 и 1.4): генерация,
/// предпросмотр и отладочный пакет берут один и тот же шаблон. Неверный шаблон в каждом сценарии
/// не компилируется (<c>#panic</c>), верный несёт метку. Генерацию сверяем по шаблону выпущенного
/// файла, предпросмотр — по тому, что PDF вообще получился, пакет — по метке в <c>template.typ</c>.
/// На master пакет не читал выбранный набор, а запасной выбор брал первую строку кучи.
/// </summary>
[Collection("Integration")]
public class TemplateChoiceGuardTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private const string Wrong = "#panic(\"не тот шаблон\")";

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

    private async Task<Guid> DocAsync(Guid type)
    {
        var c = await SendAsync(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var s = await SendAsync(new CreateSectionCommand(c.Id, "Раздел"));
        var set = await SendAsync(new CreateDocumentSetCommand(s.Id, "Комплект"));
        return (await SendAsync(new AddDocumentToSetCommand(set.Id, type))).Id;
    }

    private async Task<Guid> TemplateAsync(Guid type, string name, string content)
        => (await SendAsync(new CreateTemplateCommand(type, name, content))).Id;

    private static string Right(string marker) => $"= Верный шаблон {marker}";

    /// <summary>Создание шаблона — задним числом: так выглядит база, где порядок строк кучи разошёлся
    /// с порядком создания (восстановление копии). UPDATE к тому же переносит строку в конец кучи.</summary>
    private async Task BackdateAsync(Guid templateId, int days)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE templates SET \"CreatedAt\" = \"CreatedAt\" - make_interval(days => {0}) WHERE \"Id\" = {1}",
            days, templateId);
    }

    private async Task<HttpClient> ClientAsync()
        => await GrantedSignIn.WithPermissionsAsync(fixture, "id.document.generate");

    /// <summary>Все трое берут шаблон <paramref name="expected"/> с меткой <paramref name="marker"/>.</summary>
    private static async Task AssertAllTakeAsync(HttpClient client, Guid docId, Guid expected, string marker)
    {
        var bundle = await client.GetAsync($"/api/generate/debug-bundle/{docId}");
        Assert.True(bundle.IsSuccessStatusCode, $"Пакет: {(int)bundle.StatusCode} {await bundle.Content.ReadAsStringAsync()}");
        await using (var stream = await bundle.Content.ReadAsStreamAsync())
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            using var reader = new StreamReader(zip.GetEntry("template.typ")!.Open(), Encoding.UTF8);
            Assert.Contains(marker, await reader.ReadToEndAsync());
        }

        var preview = await client.PostAsJsonAsync($"/api/generate/preview/{docId}", new { });
        Assert.True(preview.StatusCode == HttpStatusCode.OK
                    && preview.Content.Headers.ContentType?.MediaType == "application/pdf",
            $"Предпросмотр: {(int)preview.StatusCode} {await preview.Content.ReadAsStringAsync()}");

        var generated = await client.PostAsJsonAsync($"/api/generate/{docId}", new { format = "pdf" });
        Assert.True(generated.StatusCode == HttpStatusCode.OK,
            $"Генерация: {(int)generated.StatusCode} {await generated.Content.ReadAsStringAsync()}");
        var file = Assert.Single((await generated.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Equal(expected, file.GetProperty("templateId").GetGuid());
    }

    [Fact]
    public async Task Выбран_второй_из_двух_все_трое_берут_его()
    {
        TypstRequired.Ensure();
        var type = await TypeAsync("CHOICE_A");
        // Первый — по умолчанию: пакет на master набор не читал и брал его.
        var first = await TemplateAsync(type, "Основной", Wrong);
        await SendAsync(new SetTemplateDefaultCommand(first));
        var marker = $"m-{Guid.NewGuid():N}";
        var second = await TemplateAsync(type, "Краткий", Right(marker));
        var docId = await DocAsync(type);
        await SendAsync(new SetDocumentTemplatesCommand(docId, JsonSerializer.Serialize(new[] { second })));

        await AssertAllTakeAsync(await ClientAsync(), docId, second, marker);
    }

    [Fact]
    public async Task Без_выбора_и_по_умолчанию_все_трое_берут_ранний_и_его_новую_версию()
    {
        TypstRequired.Ensure();
        var type = await TypeAsync("CHOICE_B");
        // Вставлен первым, создан позже: первая строка кучи — не самый ранний шаблон.
        await TemplateAsync(type, "Поздний", Wrong);
        var marker = $"m-{Guid.NewGuid():N}";
        var early = await TemplateAsync(type, "Ранний", Right(marker));
        await BackdateAsync(early, 30);
        var docId = await DocAsync(type);
        var client = await ClientAsync();

        await AssertAllTakeAsync(client, docId, early, marker);

        // Новая версия моложе всех — выбор остаётся за «Ранним», а не уходит к «Позднему».
        var marker2 = $"m-{Guid.NewGuid():N}";
        var v2 = (await SendAsync(new UpdateTemplateCommand(early, Right(marker2)))).Template.Id;
        await AssertAllTakeAsync(client, docId, v2, marker2);
    }

    [Fact]
    public async Task Все_выбранные_погашены_у_всех_троих_один_отказ()
    {
        var type = await TypeAsync("CHOICE_C");
        var v1 = await TemplateAsync(type, "Основной", "= v1");
        await SendAsync(new UpdateTemplateCommand(v1, "= v2"));
        await TemplateAsync(type, "Запасной", "= запасной");
        var docId = await DocAsync(type);
        // Штатно такого не бывает: новая версия пересаживает выбор, а запись принимает только
        // действующие (#1923, #1960). Остаётся запись мимо адреса — её и делаем.
        using (var scope = fixture.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDomainObjectRepository>();
            (await repo.GetByIdAsync(docId))!.SetTemplateIds(JsonSerializer.Serialize(new[] { v1 }));
            await repo.SaveChangesAsync();
        }
        var client = await ClientAsync();

        var bundle = await client.GetAsync($"/api/generate/debug-bundle/{docId}");
        var preview = await client.PostAsJsonAsync($"/api/generate/preview/{docId}", new { });
        var generated = await client.PostAsJsonAsync($"/api/generate/{docId}", new { format = "pdf" });

        Assert.Equal(HttpStatusCode.Conflict, bundle.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, preview.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, generated.StatusCode);
        foreach (var response in new[] { bundle, preview, generated })
            Assert.Contains(TemplateChoice.NoneSelectedActive, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Список_и_действующий_шаблон_в_порядке_запасного_выбора()
    {
        var type = await TypeAsync("CHOICE_D");
        var late = await TemplateAsync(type, "Поздний", "= поздний");
        var early = await TemplateAsync(type, "Ранний", "= ранний v1");
        await BackdateAsync(early, 30);
        var early2 = (await SendAsync(new UpdateTemplateCommand(early, "= ранний v2"))).Template.Id;

        var list = await SendAsync(new ListTemplatesQuery(type));
        // Внутри шаблона — от новой версии: первая действующая в списке та, что возьмёт сервер.
        Assert.Equal([early2, early, late], list.Select(t => t.Id));

        var active = await SendAsync(new GetActiveTemplateQuery(type));
        Assert.Equal(early2, active?.Id);
    }
}
