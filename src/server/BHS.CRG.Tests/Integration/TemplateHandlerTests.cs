using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Templates;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

[Collection("Integration")]
public class TemplateHandlerTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private IMediator Mediator(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IMediator>();

    private async Task<Guid> CreateDocTypeAsync(string code)
    {
        using var scope = fixture.Services.CreateScope();
        var dt = await Mediator(scope).Send(
            new CreateDocumentTypeCommand(code, code, DocumentTypeKind.Document,
                null, JsonDocument.Parse(@"{""fields"":[]}")));
        return dt.Id;
    }

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_PersistsTemplate_WithIsActiveTrue()
    {
        var dtId = await CreateDocTypeAsync("DT_C1");

        using var scope = fixture.Services.CreateScope();
        var t = await Mediator(scope).Send(
            new CreateTemplateCommand(dtId, "Шаблон 1", "#set(page(\"a4\"))"));

        Assert.NotEqual(Guid.Empty, t.Id);
        Assert.Equal(dtId, t.DocumentTypeId);
        Assert.Equal("Шаблон 1", t.Name);
        Assert.Equal(1, t.Version);
        Assert.True(t.IsActive);
    }

    // ── Название шаблона в типе не повторяется (#1924) ───────────────────────

    [Fact]
    public async Task Create_TakenName_Refused_AndTemplateUnchanged()
    {
        var dtId = await CreateDocTypeAsync("DT_N1");
        using (var s = fixture.Services.CreateScope())
            await Mediator(s).Send(new CreateTemplateCommand(dtId, "Основной", "v1"));

        using (var s = fixture.Services.CreateScope())
        {
            // Обрезка по краям — та же строка, а не новый шаблон.
            var ex = await Assert.ThrowsAsync<ConflictException>(() =>
                Mediator(s).Send(new CreateTemplateCommand(dtId, "  Основной ", "чужой v1")));
            Assert.Contains("уже есть", ex.Message);
        }

        using var scope = fixture.Services.CreateScope();
        var all = await Mediator(scope).Send(new ListTemplatesQuery(dtId));
        var only = Assert.Single(all);
        Assert.DoesNotContain("чужой", only.Content);
        Assert.True(only.IsActive);
    }

    [Fact]
    public async Task Create_SameNameInOtherType_Created()
    {
        var a = await CreateDocTypeAsync("DT_N2A");
        var b = await CreateDocTypeAsync("DT_N2B");
        using (var s = fixture.Services.CreateScope())
            await Mediator(s).Send(new CreateTemplateCommand(a, "Основной", "x"));

        using var scope = fixture.Services.CreateScope();
        var t = await Mediator(scope).Send(new CreateTemplateCommand(b, "Основной", "x"));
        Assert.Equal(b, t.DocumentTypeId);
        Assert.Equal(1, t.Version);
    }

    [Fact]
    public async Task Duplicate_WithoutName_PicksFirstFreeCopyName()
    {
        var dtId = await CreateDocTypeAsync("DT_N3");
        Template source;
        using (var s = fixture.Services.CreateScope())
            source = await Mediator(s).Send(new CreateTemplateCommand(dtId, "Основной", "x"));

        Template first, second;
        using (var s = fixture.Services.CreateScope())
            first = await Mediator(s).Send(new DuplicateTemplateCommand(source.Id, null));
        using (var s = fixture.Services.CreateScope())
            second = await Mediator(s).Send(new DuplicateTemplateCommand(source.Id, null));

        Assert.Equal("Основной (копия)", first.Name);
        Assert.Equal("Основной (копия 2)", second.Name);
        Assert.Equal(1, first.Version);
        Assert.Equal(1, second.Version);
        Assert.True(first.IsActive && second.IsActive);
    }

    [Fact]
    public async Task Duplicate_TakenExplicitName_Refused()
    {
        var dtId = await CreateDocTypeAsync("DT_N4");
        Template source;
        using (var s = fixture.Services.CreateScope())
        {
            source = await Mediator(s).Send(new CreateTemplateCommand(dtId, "Основной", "x"));
            await Mediator(s).Send(new CreateTemplateCommand(dtId, "Краткий", "y"));
        }

        using (var s = fixture.Services.CreateScope())
            await Assert.ThrowsAsync<ConflictException>(() =>
                Mediator(s).Send(new DuplicateTemplateCommand(source.Id, "Краткий")));

        using var scope = fixture.Services.CreateScope();
        Assert.Equal(2, (await Mediator(scope).Send(new ListTemplatesQuery(dtId))).Count);
    }

    // ── Update (versioning) ───────────────────────────────────────────────────

    [Fact]
    public async Task Update_CreatesNewVersionAndDeactivatesOld()
    {
        var dtId = await CreateDocTypeAsync("DT_U1");

        using var scope = fixture.Services.CreateScope();
        var original = await Mediator(scope).Send(
            new CreateTemplateCommand(dtId, "Шаблон", "v1 content"));

        using var scope2 = fixture.Services.CreateScope();
        var newVersion = (await Mediator(scope2).Send(
            new UpdateTemplateCommand(original.Id, "v2 content"))).Template;

        Assert.Equal(2, newVersion.Version);
        Assert.True(newVersion.IsActive);
        Assert.Equal("v2 content", newVersion.Content);

        // Old version is now inactive
        using var scope3 = fixture.Services.CreateScope();
        var templates = await Mediator(scope3).Send(new ListTemplatesQuery(dtId));
        var old = templates.First(t => t.Id == original.Id);
        Assert.False(old.IsActive);
    }

    [Fact]
    public async Task NewVersion_WithComment_PersistsCommentOnNewVersionOnly()
    {
        var dtId = await CreateDocTypeAsync("DT_UC");

        using var scope = fixture.Services.CreateScope();
        var original = await Mediator(scope).Send(new CreateTemplateCommand(dtId, "Шаблон", "v1"));

        using var scope2 = fixture.Services.CreateScope();
        var newVersion = (await Mediator(scope2).Send(
            new UpdateTemplateCommand(original.Id, "v2 content", "переход на новый штамп"))).Template;

        Assert.Equal(2, newVersion.Version);
        Assert.Equal("переход на новый штамп", newVersion.Comment);

        // Примечание живёт только на новой версии, исходная без него.
        using var scope3 = fixture.Services.CreateScope();
        var templates = await Mediator(scope3).Send(new ListTemplatesQuery(dtId));
        Assert.Null(templates.First(t => t.Id == original.Id).Comment);
    }

    // ── Новая версия от исторической (issue #1920) ───────────────────────────

    /// <summary>Шаблон с версиями v1…v<paramref name="count"/>: каждая — от предыдущей.</summary>
    private async Task<List<Template>> CreateVersionsAsync(Guid dtId, string name, int count)
    {
        using var scope = fixture.Services.CreateScope();
        var list = new List<Template> { await Mediator(scope).Send(new CreateTemplateCommand(dtId, name, "v1")) };
        for (var i = 2; i <= count; i++)
        {
            using var s = fixture.Services.CreateScope();
            list.Add((await Mediator(s).Send(new UpdateTemplateCommand(list[^1].Id, $"v{i}"))).Template);
        }
        return list;
    }

    [Fact]
    public async Task NewVersion_FromHistoricalVersion_TakesNextNumber_AndIsTheOnlyActive()
    {
        var dtId = await CreateDocTypeAsync("DT_HIST");
        var v = await CreateVersionsAsync(dtId, "Шаблон", 4);

        using var scope = fixture.Services.CreateScope();
        var forked = (await Mediator(scope).Send(new UpdateTemplateCommand(v[1].Id, "от v2"))).Template;

        // Номер — наибольший у шаблона плюс один, а не «v2 + 1»: вторая v3 была ошибкой.
        Assert.Equal(5, forked.Version);
        Assert.Equal("от v2", forked.Content);

        using var scope2 = fixture.Services.CreateScope();
        var all = await Mediator(scope2).Send(new ListTemplatesQuery(dtId));
        Assert.Equal([1, 2, 3, 4, 5], all.Select(t => t.Version).Order());
        Assert.Equal(forked.Id, Assert.Single(all, t => t.IsActive).Id); // v4 погашена
    }

    [Fact]
    public async Task NewVersion_FromHistoricalVersion_MovesDefaultFromActiveVersion()
    {
        var dtId = await CreateDocTypeAsync("DT_HIST_DEF");
        var v = await CreateVersionsAsync(dtId, "Шаблон", 1);
        using (var s = fixture.Services.CreateScope())
            await Mediator(s).Send(new SetTemplateDefaultCommand(v[0].Id));
        using (var s = fixture.Services.CreateScope())
            v.Add((await Mediator(s).Send(new UpdateTemplateCommand(v[0].Id, "v2"))).Template);
        Assert.True(v[1].IsDefault); // «по умолчанию» ушло на действующую v2

        using var scope = fixture.Services.CreateScope();
        var result = await Mediator(scope).Send(new UpdateTemplateCommand(v[0].Id, "от v1"));

        // Шаблон типа по умолчанию остаётся этим шаблоном — в его новой версии.
        Assert.Equal(3, result.Template.Version);
        Assert.True(result.Template.IsDefault);
        using var scope2 = fixture.Services.CreateScope();
        var all = await Mediator(scope2).Send(new ListTemplatesQuery(dtId));
        Assert.Equal(result.Template.Id, Assert.Single(all, t => t.IsDefault).Id);
        Assert.Equal(result.Template.Id, Assert.Single(all, t => t.IsActive).Id);
    }

    [Fact]
    public async Task NewVersion_DoesNotTouchOtherTemplatesOfTheSameType()
    {
        var dtId = await CreateDocTypeAsync("DT_HIST_OTHER");
        var a = await CreateVersionsAsync(dtId, "Основной", 2);
        var b = await CreateVersionsAsync(dtId, "Сокращённый", 1);

        using var scope = fixture.Services.CreateScope();
        var forked = (await Mediator(scope).Send(new UpdateTemplateCommand(a[0].Id, "от v1"))).Template;

        // Шаблон — версии с тем же названием; соседний шаблон типа остаётся действующим и v1.
        Assert.Equal(3, forked.Version);
        using var scope2 = fixture.Services.CreateScope();
        var all = await Mediator(scope2).Send(new ListTemplatesQuery(dtId));
        var other = Assert.Single(all, t => t.Id == b[0].Id);
        Assert.True(other.IsActive);
        Assert.Equal(1, other.Version);
        Assert.Equal(2, all.Count(t => t.IsActive));
    }

    // ── SaveContent (in-place, issue #360) ────────────────────────────────────

    [Fact]
    public async Task SaveContent_UpdatesActiveVersionInPlace_NoNewVersion()
    {
        var dtId = await CreateDocTypeAsync("DT_SC");

        using var scope = fixture.Services.CreateScope();
        var original = await Mediator(scope).Send(new CreateTemplateCommand(dtId, "Шаблон", "v1"));

        using var scope2 = fixture.Services.CreateScope();
        var saved = (await Mediator(scope2).Send(new SaveTemplateContentCommand(original.Id, "v1 edited"))).Template;

        // Тот же Id, та же версия — правка на месте.
        Assert.Equal(original.Id, saved.Id);
        Assert.Equal(1, saved.Version);
        Assert.Equal("v1 edited", saved.Content);
        Assert.True(saved.IsActive);

        using var scope3 = fixture.Services.CreateScope();
        var templates = await Mediator(scope3).Send(new ListTemplatesQuery(dtId));
        Assert.Single(templates); // новой версии не появилось
    }

    [Fact]
    public async Task SaveContent_OnInactiveVersion_Throws()
    {
        var dtId = await CreateDocTypeAsync("DT_SCI");

        using var scope = fixture.Services.CreateScope();
        var original = await Mediator(scope).Send(new CreateTemplateCommand(dtId, "Шаблон", "v1"));

        // Форкаем новую версию — исходная становится исторической (неактивной).
        using var scope2 = fixture.Services.CreateScope();
        await Mediator(scope2).Send(new UpdateTemplateCommand(original.Id, "v2"));

        using var scope3 = fixture.Services.CreateScope();
        await Assert.ThrowsAsync<ConflictException>(() =>
            Mediator(scope3).Send(new SaveTemplateContentCommand(original.Id, "hack")));
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RemovesTemplate()
    {
        var dtId = await CreateDocTypeAsync("DT_D1");

        using var scope = fixture.Services.CreateScope();
        var t = await Mediator(scope).Send(
            new CreateTemplateCommand(dtId, "Удаляемый", "content"));

        using var scope2 = fixture.Services.CreateScope();
        await Mediator(scope2).Send(new DeleteTemplateCommand(t.Id));

        using var scope3 = fixture.Services.CreateScope();
        var templates = await Mediator(scope3).Send(new ListTemplatesQuery(dtId));
        Assert.Empty(templates);
    }

    // ── GetActive ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetActive_ReturnsActiveTemplate()
    {
        var dtId = await CreateDocTypeAsync("DT_A1");

        using var scope = fixture.Services.CreateScope();
        await Mediator(scope).Send(new CreateTemplateCommand(dtId, "Шаблон", "v1"));

        using var scope2 = fixture.Services.CreateScope();
        var active = await Mediator(scope2).Send(new GetActiveTemplateQuery(dtId));

        Assert.NotNull(active);
        Assert.True(active.IsActive);
    }

    [Fact]
    public async Task GetActive_ReturnsNull_WhenNoneExist()
    {
        using var scope = fixture.Services.CreateScope();
        var active = await Mediator(scope).Send(new GetActiveTemplateQuery(Guid.NewGuid()));
        Assert.Null(active);
    }

    // ── SetDefault ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetDefault_MarksTemplateAsDefault_AndUnmarksOthers()
    {
        var dtId = await CreateDocTypeAsync("DT_DEF");

        using var scope = fixture.Services.CreateScope();
        var t1 = await Mediator(scope).Send(new CreateTemplateCommand(dtId, "T1", "v1"));
        var t2 = await Mediator(scope).Send(new CreateTemplateCommand(dtId, "T2", "v2"));

        // Make t1 default first
        using var scope2 = fixture.Services.CreateScope();
        await Mediator(scope2).Send(new SetTemplateDefaultCommand(t1.Id));

        // Now make t2 default
        using var scope3 = fixture.Services.CreateScope();
        await Mediator(scope3).Send(new SetTemplateDefaultCommand(t2.Id));

        using var scope4 = fixture.Services.CreateScope();
        var all = await Mediator(scope4).Send(new ListTemplatesQuery(dtId));
        Assert.False(all.First(t => t.Id == t1.Id).IsDefault);
        Assert.True(all.First(t => t.Id == t2.Id).IsDefault);
    }

    // ── TemplateAsset duplication-by-reference (issue #62) ───────────────────────

    [Fact]
    public async Task Update_DuplicatesIndividualAssetsByReference_SameBlobPath()
    {
        var dtId = await CreateDocTypeAsync("DT_ASSET_V");
        using var scope = fixture.Services.CreateScope();
        var m = Mediator(scope);
        var original = await m.Send(new CreateTemplateCommand(dtId, "Шаблон", "v1"));
        await m.Send(new CreateTemplateAssetCommand(
            TemplateAssetScope.Template, original.Id, TemplateAssetKind.Image, "logo", "logo.png", "image/png", "blob/logo.png", null));

        using var scope2 = fixture.Services.CreateScope();
        var newVersion = (await Mediator(scope2).Send(new UpdateTemplateCommand(original.Id, "v2"))).Template;

        using var scope3 = fixture.Services.CreateScope();
        var newVersionAssets = await Mediator(scope3).Send(
            new ListTemplateAssetsQuery(TemplateAssetScope.Template, newVersion.Id));
        var oldVersionAssets = await Mediator(scope3).Send(
            new ListTemplateAssetsQuery(TemplateAssetScope.Template, original.Id));

        Assert.Single(newVersionAssets);
        Assert.Equal("blob/logo.png", newVersionAssets[0].BlobPath); // та же ссылка, без переупаковки
        Assert.Single(oldVersionAssets); // у старой версии свой (исходный) остался нетронутым
    }

    [Fact]
    public async Task ReplaceOnNewVersion_DoesNotAffectSourceVersionAsset()
    {
        var dtId = await CreateDocTypeAsync("DT_ASSET_R");
        using var scope = fixture.Services.CreateScope();
        var m = Mediator(scope);
        var original = await m.Send(new CreateTemplateCommand(dtId, "Шаблон", "v1"));
        await m.Send(new CreateTemplateAssetCommand(
            TemplateAssetScope.Template, original.Id, TemplateAssetKind.Image, "logo", "logo.png", "image/png", "blob/old.png", null));

        using var scope2 = fixture.Services.CreateScope();
        var newVersion = (await Mediator(scope2).Send(new UpdateTemplateCommand(original.Id, "v2"))).Template;

        using var scope3 = fixture.Services.CreateScope();
        var newVersionAsset = (await Mediator(scope3).Send(
            new ListTemplateAssetsQuery(TemplateAssetScope.Template, newVersion.Id))).Single();
        await Mediator(scope3).Send(new ReplaceTemplateAssetCommand(
            newVersionAsset.Id, "logo-new.png", "image/png", "blob/new.png", null));

        using var scope4 = fixture.Services.CreateScope();
        var oldVersionAsset = (await Mediator(scope4).Send(
            new ListTemplateAssetsQuery(TemplateAssetScope.Template, original.Id))).Single();
        Assert.Equal("blob/old.png", oldVersionAsset.BlobPath); // старая версия не затронута заменой на новой
    }

    [Fact]
    public async Task Duplicate_DuplicatesIndividualAssetsByReference()
    {
        var dtId = await CreateDocTypeAsync("DT_ASSET_D");
        using var scope = fixture.Services.CreateScope();
        var m = Mediator(scope);
        var source = await m.Send(new CreateTemplateCommand(dtId, "Шаблон", "v1"));
        await m.Send(new CreateTemplateAssetCommand(
            TemplateAssetScope.Template, source.Id, TemplateAssetKind.Font, "font", "f.ttf", "font/ttf", "blob/f.ttf", "Family"));

        using var scope2 = fixture.Services.CreateScope();
        var copy = await Mediator(scope2).Send(new DuplicateTemplateCommand(source.Id, null));

        using var scope3 = fixture.Services.CreateScope();
        var copyAssets = await Mediator(scope3).Send(new ListTemplateAssetsQuery(TemplateAssetScope.Template, copy.Id));
        Assert.Single(copyAssets);
        Assert.Equal("blob/f.ttf", copyAssets[0].BlobPath);
        Assert.Equal("Family", copyAssets[0].FontFamilyName);
    }
}
