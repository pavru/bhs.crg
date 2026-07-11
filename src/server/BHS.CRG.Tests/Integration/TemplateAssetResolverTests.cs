using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Templates;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>ITemplateAssetResolver (issue #62) — приоритет Template &gt; DocumentType &gt; System
/// при совпадении Name (картинки) / FontFamilyName (шрифты, с fallback на Name).</summary>
[Collection("Integration")]
public class TemplateAssetResolverTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();

    [Fact]
    public async Task Image_TemplateLevelWinsOverDocumentTypeAndSystem_SameName()
    {
        var templateId = Guid.NewGuid();
        var docTypeId = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
        {
            var m = M(scope);
            await m.Send(new CreateTemplateAssetCommand(
                TemplateAssetScope.System, null, TemplateAssetKind.Image, "logo", "sys.png", "image/png", "blob/sys.png", null));
            await m.Send(new CreateTemplateAssetCommand(
                TemplateAssetScope.DocumentType, docTypeId, TemplateAssetKind.Image, "logo", "type.png", "image/png", "blob/type.png", null));
            await m.Send(new CreateTemplateAssetCommand(
                TemplateAssetScope.Template, templateId, TemplateAssetKind.Image, "logo", "tmpl.png", "image/png", "blob/tmpl.png", null));
        }

        using var scope2 = fixture.Services.CreateScope();
        var resolver = scope2.ServiceProvider.GetRequiredService<ITemplateAssetResolver>();
        var resolved = await resolver.ResolveAsync(templateId, docTypeId, default);

        var image = Assert.Single(resolved.Images);
        Assert.Equal("blob/tmpl.png", image.BlobPath); // индивидуальный уровень победил
    }

    [Fact]
    public async Task Image_DocumentTypeWinsOverSystem_WhenNoTemplateLevelAsset()
    {
        var templateId = Guid.NewGuid();
        var docTypeId = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
        {
            var m = M(scope);
            await m.Send(new CreateTemplateAssetCommand(
                TemplateAssetScope.System, null, TemplateAssetKind.Image, "logo", "sys.png", "image/png", "blob/sys.png", null));
            await m.Send(new CreateTemplateAssetCommand(
                TemplateAssetScope.DocumentType, docTypeId, TemplateAssetKind.Image, "logo", "type.png", "image/png", "blob/type.png", null));
        }

        using var scope2 = fixture.Services.CreateScope();
        var resolver = scope2.ServiceProvider.GetRequiredService<ITemplateAssetResolver>();
        var resolved = await resolver.ResolveAsync(templateId, docTypeId, default);

        var image = Assert.Single(resolved.Images);
        Assert.Equal("blob/type.png", image.BlobPath);
    }

    [Fact]
    public async Task Image_DifferentNames_BothSurviveIndependently()
    {
        var templateId = Guid.NewGuid();
        var docTypeId = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
        {
            var m = M(scope);
            await m.Send(new CreateTemplateAssetCommand(
                TemplateAssetScope.System, null, TemplateAssetKind.Image, "logo", "logo.png", "image/png", "blob/logo.png", null));
            await m.Send(new CreateTemplateAssetCommand(
                TemplateAssetScope.System, null, TemplateAssetKind.Image, "stamp", "stamp.png", "image/png", "blob/stamp.png", null));
        }

        using var scope2 = fixture.Services.CreateScope();
        var resolver = scope2.ServiceProvider.GetRequiredService<ITemplateAssetResolver>();
        var resolved = await resolver.ResolveAsync(templateId, docTypeId, default);

        Assert.Equal(2, resolved.Images.Count);
    }

    [Fact]
    public async Task Font_ResolvesByFamilyName_TemplateWinsOverSystem()
    {
        var templateId = Guid.NewGuid();
        var docTypeId = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
        {
            var m = M(scope);
            await m.Send(new CreateTemplateAssetCommand(
                TemplateAssetScope.System, null, TemplateAssetKind.Font, "brand", "sys.ttf", "font/ttf", "blob/sys.ttf", "Brand Sans"));
            await m.Send(new CreateTemplateAssetCommand(
                TemplateAssetScope.Template, templateId, TemplateAssetKind.Font, "brand-override", "tmpl.ttf", "font/ttf", "blob/tmpl.ttf", "Brand Sans"));
        }

        using var scope2 = fixture.Services.CreateScope();
        var resolver = scope2.ServiceProvider.GetRequiredService<ITemplateAssetResolver>();
        var resolved = await resolver.ResolveAsync(templateId, docTypeId, default);

        var font = Assert.Single(resolved.Fonts);
        Assert.Equal("blob/tmpl.ttf", font.BlobPath); // совпадение по FontFamilyName, не по Name
    }

    [Fact]
    public async Task Font_FallsBackToName_WhenFamilyNameNotRecognized()
    {
        var templateId = Guid.NewGuid();
        var docTypeId = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
        {
            var m = M(scope);
            // FontFamilyName == null у обоих (парсинг не удался при загрузке) — fallback на Name.
            await m.Send(new CreateTemplateAssetCommand(
                TemplateAssetScope.System, null, TemplateAssetKind.Font, "brand", "sys.ttf", "font/ttf", "blob/sys.ttf", null));
            await m.Send(new CreateTemplateAssetCommand(
                TemplateAssetScope.Template, templateId, TemplateAssetKind.Font, "brand", "tmpl.ttf", "font/ttf", "blob/tmpl.ttf", null));
        }

        using var scope2 = fixture.Services.CreateScope();
        var resolver = scope2.ServiceProvider.GetRequiredService<ITemplateAssetResolver>();
        var resolved = await resolver.ResolveAsync(templateId, docTypeId, default);

        var font = Assert.Single(resolved.Fonts);
        Assert.Equal("blob/tmpl.ttf", font.BlobPath);
    }

    [Fact]
    public async Task NoAssets_ReturnsEmpty()
    {
        using var scope = fixture.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITemplateAssetResolver>();
        var resolved = await resolver.ResolveAsync(Guid.NewGuid(), Guid.NewGuid(), default);

        Assert.Empty(resolved.Images);
        Assert.Empty(resolved.Fonts);
    }
}
