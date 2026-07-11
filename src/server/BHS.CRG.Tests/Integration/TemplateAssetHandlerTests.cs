using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Templates;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>CRUD для TemplateAsset (issue #62) — создание/список/явная замена/удаление.</summary>
[Collection("Integration")]
public class TemplateAssetHandlerTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private IMediator Mediator(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IMediator>();

    [Fact]
    public async Task Create_PersistsSystemImageAsset()
    {
        using var scope = fixture.Services.CreateScope();
        var asset = await Mediator(scope).Send(new CreateTemplateAssetCommand(
            TemplateAssetScope.System, null, TemplateAssetKind.Image,
            "logo", "logo.svg", "image/svg+xml", "blob/path/logo.svg", null));

        Assert.NotEqual(Guid.Empty, asset.Id);
        Assert.Equal(TemplateAssetScope.System, asset.Scope);
        Assert.Null(asset.ScopeId);
        Assert.Equal("logo", asset.Name);
    }

    [Fact]
    public async Task List_FiltersByScopeAndScopeId()
    {
        var typeId = Guid.NewGuid();
        using var scope = fixture.Services.CreateScope();
        var m = Mediator(scope);
        await m.Send(new CreateTemplateAssetCommand(
            TemplateAssetScope.System, null, TemplateAssetKind.Image, "sys-logo", "a.png", "image/png", "b1", null));
        await m.Send(new CreateTemplateAssetCommand(
            TemplateAssetScope.DocumentType, typeId, TemplateAssetKind.Image, "type-logo", "b.png", "image/png", "b2", null));

        using var scope2 = fixture.Services.CreateScope();
        var systemAssets = await Mediator(scope2).Send(new ListTemplateAssetsQuery(TemplateAssetScope.System, null));
        var typeAssets = await Mediator(scope2).Send(new ListTemplateAssetsQuery(TemplateAssetScope.DocumentType, typeId));

        Assert.Single(systemAssets);
        Assert.Equal("sys-logo", systemAssets[0].Name);
        Assert.Single(typeAssets);
        Assert.Equal("type-logo", typeAssets[0].Name);
    }

    [Fact]
    public async Task Replace_UpdatesFileWithoutChangingIdOrName()
    {
        using var scope = fixture.Services.CreateScope();
        var created = await Mediator(scope).Send(new CreateTemplateAssetCommand(
            TemplateAssetScope.System, null, TemplateAssetKind.Font, "brand-font", "old.ttf", "font/ttf", "blob/old.ttf", "Old Family"));

        using var scope2 = fixture.Services.CreateScope();
        var replaced = await Mediator(scope2).Send(new ReplaceTemplateAssetCommand(
            created.Id, "new.ttf", "font/ttf", "blob/new.ttf", "New Family"));

        Assert.Equal(created.Id, replaced.Id);
        Assert.Equal("brand-font", replaced.Name); // Name не меняется явной заменой
        Assert.Equal("blob/new.ttf", replaced.BlobPath);
        Assert.Equal("New Family", replaced.FontFamilyName);
    }

    [Fact]
    public async Task Delete_RemovesAsset_NoUsageCheck()
    {
        using var scope = fixture.Services.CreateScope();
        var created = await Mediator(scope).Send(new CreateTemplateAssetCommand(
            TemplateAssetScope.System, null, TemplateAssetKind.Image, "logo", "logo.png", "image/png", "b", null));

        using var scope2 = fixture.Services.CreateScope();
        await Mediator(scope2).Send(new DeleteTemplateAssetCommand(created.Id));

        using var scope3 = fixture.Services.CreateScope();
        var all = await Mediator(scope3).Send(new ListTemplateAssetsQuery(TemplateAssetScope.System, null));
        Assert.Empty(all);
    }
}
