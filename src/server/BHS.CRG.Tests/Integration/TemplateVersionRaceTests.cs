using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Tests.Support;
using MediatR;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Целостность версий при гонке (id-template-version-integrity, D3–D5, задачи 2.4 и 2.5): проигравший
/// получает 409, а не вторую действующую версию, не второй номер и не 500.
///
/// <para>Гонка воспроизводится детерминированно: в своём хосте запись новой версии и сохранение
/// шаблона ждут «встречи» обоих запросов. Так оба прочитали одно и то же до того, как кто-либо
/// записал, — ровно то окно, в котором на master выходили две v5. Без встречи запросы могли бы пройти
/// по очереди, и тест зеленел бы, ничего не проверив.</para>
/// </summary>
[Collection("Integration")]
public class TemplateVersionRaceTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Встреча двух запросов. Не взведена — пропускает сразу (подготовка данных идёт через неё же).</summary>
    private sealed class Rendezvous
    {
        private int _arrived;
        private TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Armed;

        public void Arm()
        {
            _arrived = 0;
            _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Armed = true;
        }

        public async Task ArriveAsync()
        {
            if (!Armed) return;
            if (Interlocked.Increment(ref _arrived) >= 2) _all.TrySetResult();
            await _all.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private sealed class GatedWriter(ITemplateVersionWriter inner, Rendezvous gate) : ITemplateVersionWriter
    {
        public async Task SaveRetiredThenInsertAsync(IReadOnlyCollection<Template> created, CancellationToken ct = default)
        {
            await gate.ArriveAsync();
            await inner.SaveRetiredThenInsertAsync(created, ct);
        }
    }

    private sealed class GatedRepository(Repository<Template> inner, Rendezvous gate) : IRepository<Template>
    {
        public Task<Template?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);
        public Task<IReadOnlyList<Template>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);
        public Task<IReadOnlyList<Template>> FindAsync(System.Linq.Expressions.Expression<Func<Template, bool>> p, CancellationToken ct = default)
            => inner.FindAsync(p, ct);
        public Task AddAsync(Template entity, CancellationToken ct = default) => inner.AddAsync(entity, ct);
        public void Update(Template entity) => inner.Update(entity);
        public void Remove(Template entity) => inner.Remove(entity);

        public async Task SaveChangesAsync(CancellationToken ct = default)
        {
            await gate.ArriveAsync();
            await inner.SaveChangesAsync(ct);
        }
    }

    private WebApplicationFactory<Program> GatedHost(Rendezvous gate) =>
        fixture.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddScoped<ITemplateVersionWriter>(sp =>
                new GatedWriter(new TemplateVersionWriter(sp.GetRequiredService<AppDbContext>()), gate));
            s.AddScoped<IRepository<Template>>(sp =>
                new GatedRepository(new Repository<Template>(sp.GetRequiredService<AppDbContext>()), gate));
        }));

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }

    private async Task<Guid> TypeAsync(string code)
        => (await SendAsync(new CreateDocumentTypeCommand(code, code, DocumentTypeKind.Document, null,
            JsonDocument.Parse("""{"fields":[]}""")))).Id;

    private async Task<List<Template>> TemplatesAsync(Guid type)
        => [.. await SendAsync(new ListTemplatesQuery(type))];

    private static async Task<string?> ErrorAsync(HttpResponseMessage r)
        => (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    [Fact]
    public async Task Две_новые_версии_разом_одна_v5_другая_409()
    {
        var type = await TypeAsync("RACE_V");
        List<Guid> v = [(await SendAsync(new CreateTemplateCommand(type, "Основной", "v1"))).Id];
        for (var i = 1; i < 4; i++) v.Add((await SendAsync(new UpdateTemplateCommand(v[^1], $"v{i + 1}"))).Template.Id);

        var gate = new Rendezvous();
        await using var host = GatedHost(gate);
        var client = await GrantedSignIn.WithPermissionsAsync(host, "id.config.edit", "id.document.read");
        gate.Arm();

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync($"/api/templates/{v[1]}/versions", new { content = "от v2" }),
            client.PostAsJsonAsync($"/api/templates/{v[3]}/versions", new { content = "от v4" }));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        Assert.Equal("Шаблон тем временем изменили — откройте его заново.",
            await ErrorAsync(responses.Single(r => r.StatusCode == HttpStatusCode.Conflict)));
        var all = await TemplatesAsync(type);
        Assert.Single(all, t => t.IsActive);
        Assert.Single(all, t => t.Version == 5);
        Assert.Equal(5, all.Count);
    }

    [Fact]
    public async Task Два_создания_под_одним_новым_названием_один_шаблон_и_409()
    {
        var type = await TypeAsync("RACE_C");
        var gate = new Rendezvous();
        await using var host = GatedHost(gate);
        var client = await GrantedSignIn.WithPermissionsAsync(host, "id.config.edit", "id.document.read");
        gate.Arm();

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync("/api/templates", new { documentTypeId = type, name = "Основной", content = "a" }),
            client.PostAsJsonAsync("/api/templates", new { documentTypeId = type, name = "Основной", content = "b" }));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        Assert.Equal("Шаблон с таким названием уже есть.",
            await ErrorAsync(responses.Single(r => r.StatusCode == HttpStatusCode.Conflict)));
        var only = Assert.Single(await TemplatesAsync(type));
        Assert.Equal(1, only.Version);
    }

    [Fact]
    public async Task Занятое_название_на_адресах_создания_и_копии_409_а_не_500()
    {
        var type = await TypeAsync("RACE_N");
        var main = (await SendAsync(new CreateTemplateCommand(type, "Основной", "x"))).Id;
        await SendAsync(new CreateTemplateCommand(type, "Краткий", "y"));
        var client = await GrantedSignIn.WithPermissionsAsync(fixture, "id.config.edit", "id.document.read");

        var created = await client.PostAsJsonAsync("/api/templates", new { documentTypeId = type, name = "Основной", content = "z" });
        var copied = await client.PostAsJsonAsync($"/api/templates/{main}/duplicate", new { name = "Краткий" });

        Assert.Equal(HttpStatusCode.Conflict, created.StatusCode);
        Assert.Contains("уже есть", await ErrorAsync(created));
        Assert.Equal(HttpStatusCode.Conflict, copied.StatusCode);
        Assert.Equal(2, (await TemplatesAsync(type)).Count);
    }
}
