using System.Net.Http.Headers;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Вход и загрузка логотипа — общие для двух классов тестов оформления (<see cref="BrandingTests" />
/// и <see cref="BrandIconTests" />). Жили копиями в обоих: правка формы входа или адреса загрузки
/// требовала бы синхронной правки двух мест (ревью PR #1320).
/// </summary>
internal static class BrandingClient
{
    /// <summary>Вход с ролью — общим помощником (<see cref="AccessProbe" />).</summary>
    public static Task<HttpClient> SignInAsync(IntegrationTestFixture fixture, string role) =>
        AccessProbe.SignInAsync(fixture, role, "brand");

    public static async Task<HttpResponseMessage> UploadLogoAsync(HttpClient client, string fileName, byte[] bytes)
    {
        // Форма живёт ДО конца отправки: возврат задачи из-под using закрывал поток раньше, чем
        // тело уходило в сокет, и тест падал на чтении закрытого потока, а не на поведении.
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        return await client.PostAsync("/api/branding/logo", form);
    }
}
