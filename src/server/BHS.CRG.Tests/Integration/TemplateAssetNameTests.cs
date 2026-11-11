using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Domain.Templates;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Имя ассета-картинки — простое имя файла, пробел допустим (ТЗ CORE-21; change core-template-asset-names, D2): оно
/// становится путём <c>assets/{Name}{ext}</c> в каталоге компиляции. Отказ — 400 с названным правилом,
/// и ДО загрузки файла: отклонённое имя не оставляет в хранилище сироты. Имя шрифта в путь не входит.
/// </summary>
[Collection("Integration")]
public class TemplateAssetNameTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private FakeBlobStorage Store => fixture.Services.GetRequiredService<FakeBlobStorage>();

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string fileName, byte[] bytes, string name)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        return await client.PostAsync(
            $"/api/template-assets?scope=System&name={Uri.EscapeDataString(name)}", form);
    }

    [Theory]
    [InlineData("fields/img_0")]
    [InlineData("logo(2)")]
    [InlineData("a..b")]
    public async Task Негодное_имя_картинки_отклоняется_до_загрузки(string name)
    {
        var admin = await AccessProbe.SignInAsync(fixture, SystemRoles.Admin, "asset");
        var uploads = Store.Uploads;

        var response = await UploadAsync(admin, "logo.png", Png, name);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.Contains(TemplateAsset.ImageNameRule, error);
        Assert.Equal(uploads, Store.Uploads);
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/template-assets?scope=System");
        Assert.DoesNotContain(list.EnumerateArray(), a => a.GetProperty("name").GetString() == name);
    }

    [Fact]
    public async Task Допустимое_имя_картинки_создаёт_ассет()
    {
        var admin = await AccessProbe.SignInAsync(fixture, SystemRoles.Admin, "asset");

        var response = await UploadAsync(admin, "f3.png", Png, "ГОСТ 21.101. Форма 3");

        response.EnsureSuccessStatusCode();
        var asset = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ГОСТ 21.101. Форма 3", asset.GetProperty("name").GetString());
    }

    /// <summary>Имя шрифта — справка, не путь: файл ложится как fonts/font_N, правило к нему не применяется.</summary>
    [Fact]
    public async Task Шрифт_принимает_имя_с_пробелом()
    {
        var admin = await AccessProbe.SignInAsync(fixture, SystemRoles.Admin, "asset");

        var response = await UploadAsync(admin, "gost.ttf", [0, 1, 0, 0, 0, 0], "GOST 2.304 тип А");

        response.EnsureSuccessStatusCode();
    }
}
