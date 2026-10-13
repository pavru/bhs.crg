using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Вход и загрузка логотипа — общие для двух классов тестов оформления (<see cref="BrandingTests" />
/// и <see cref="BrandIconTests" />). Жили копиями в обоих: правка формы входа или адреса загрузки
/// требовала бы синхронной правки двух мест (ревью PR #1320).
/// </summary>
internal static class BrandingClient
{
    private const string Password = "Passw0rd!";

    public static async Task<HttpClient> SignInAsync(IntegrationTestFixture fixture, string role)
    {
        var email = $"brand_{role.ToLowerInvariant()}_{Guid.NewGuid():N}@test.local";
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
