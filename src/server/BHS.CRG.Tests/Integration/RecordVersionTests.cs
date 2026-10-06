using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Правка записи общих данных называет версию, по которой собрана (issue #1214).
///
/// <para>Правка заменяет запись ЦЕЛИКОМ. Без версии из двух форм, открытых одновременно, побеждала
/// сохранённая последней, и правка первой пропадала без сообщения — обе получали «сохранено».</para>
/// </summary>
[Collection("Integration")]
public class RecordVersionTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));

    private async Task<T> InScopeAsync<T>(Func<IServiceScope, Task<T>> work)
    {
        using var scope = fixture.Services.CreateScope();
        return await work(scope);
    }

    private Task<DomainObject> EntryAsync(string code) => InScopeAsync(async s =>
    {
        var type = await M(s).Send(new CreateDocumentTypeCommand(
            code, code, DocumentTypeKind.Composite, null, J("{'fields':[{'key':'Адрес','type':'string'}]}")));
        return await M(s).Send(new CreateCommonDataEntryCommand(
            "Ромашка", type.Id, J("{'Адрес':'Москва'}"), CatalogScope.System, null));
    });

    private Task<DomainObject> StoredAsync(Guid id) => InScopeAsync(async s =>
        (await M(s).Send(new GetCommonDataEntryQuery(id)))!);

    private Task<DomainObject> UpdateAsync(Guid id, string name, string seen) => InScopeAsync(s =>
        M(s).Send(new UpdateCommonDataEntryCommand(id, name, J("{'Адрес':'Тверь'}"), TestAccess.All, seen)));

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid id, string name, string? seen)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/common-data/{id}")
        {
            Content = JsonContent.Create(new { displayName = name, data = "{\"Адрес\":\"Тверь\"}" }),
        };
        // Без проверки формата: версия — не ETag в кавычках, а отметка как есть.
        if (seen is not null) request.Headers.TryAddWithoutValidation("If-Match", seen);
        return client.SendAsync(request);
    }

    /// <summary>
    /// Сторож из задачи: пишущий адрес записи без версии — отказ, а не «как раньше». Сервер, молча
    /// принимающий такую правку, оставил бы потерю каждому клиенту, который о версии не знает.
    /// </summary>
    [Fact]
    public async Task Адрес_правки_без_версии_отказывает_и_запись_не_трогает()
    {
        var entry = await EntryAsync("VER_A");
        var client = await SignInAsync();

        var refused = await PutAsync(client, entry.Id, "Подмена", null);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("If-Match", await refused.Content.ReadAsStringAsync());
        Assert.Equal("Ромашка", (await StoredAsync(entry.Id)).DisplayName);
    }

    /// <summary>
    /// Версия в кавычках — запись HTTP для таких значений, и клиент на типизированных заголовках
    /// иначе её не пошлёт (ревью PR #1231). Сравнённая как есть, она не совпала бы ни с одной
    /// лежащей: на каждую попытку — «запись изменили», и перечитывание не помогло бы никогда.
    /// А «*» — отказ о ЗАПИСИ версии, а не о состоянии записи.
    /// </summary>
    [Fact]
    public async Task Версия_в_кавычках_принимается_а_звёздочка_это_отказ_о_записи_версии()
    {
        var entry = await EntryAsync("VER_Q");
        var client = await SignInAsync();

        var quoted = await PutAsync(client, entry.Id, "В кавычках", $"\"{entry.Version}\"");
        Assert.Equal(HttpStatusCode.OK, quoted.StatusCode);
        var moved = (await quoted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(client, entry.Id, "Слабая", $"W/\"{moved}\"")).StatusCode);

        var any = await PutAsync(client, entry.Id, "Любая", "*");
        Assert.Equal(HttpStatusCode.BadRequest, any.StatusCode);
        Assert.Contains("записана не так", await any.Content.ReadAsStringAsync());
        Assert.Equal("Слабая", (await StoredAsync(entry.Id)).DisplayName);
    }

    /// <summary>
    /// Две формы открыты по одной версии. Первая сохраняется, вторая получает 409 — и её правка не
    /// записана. Ответ первой несёт новую версию: по ней та же форма сохраняется ещё раз.
    /// </summary>
    [Fact]
    public async Task Вторая_форма_с_прежней_версией_получает_отказ_а_первая_сохраняется_дальше()
    {
        var entry = await EntryAsync("VER_B");
        var client = await SignInAsync();
        var read = await client.GetFromJsonAsync<JsonElement>($"/api/common-data/{entry.Id}");
        var seen = read.GetProperty("version").GetString()!;

        var first = await PutAsync(client, entry.Id, "Первая", seen);
        var second = await PutAsync(client, entry.Id, "Вторая", seen);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("тем временем изменили", await second.Content.ReadAsStringAsync());
        Assert.Equal("Первая", (await StoredAsync(entry.Id)).DisplayName);

        var moved = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString()!;
        Assert.NotEqual(seen, moved);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(client, entry.Id, "Первая, ещё раз", moved)).StatusCode);
    }

    /// <summary>
    /// Версия — версия СТРОКИ: её двигает и архив. Форма, открытая до архива, получает отказ и узнаёт,
    /// что с записью что-то сделали, — а со свежей версией правка проходит и архива не снимает.
    /// </summary>
    [Fact]
    public async Task Архив_двигает_версию_а_правка_со_свежей_версией_архива_не_снимает()
    {
        var entry = await EntryAsync("VER_C");
        await InScopeAsync(async s =>
            await s.ServiceProvider.GetRequiredService<IRecordArchive>().SetAsync(entry.Id, true));

        await Assert.ThrowsAsync<ConflictException>(() => UpdateAsync(entry.Id, "Поверх", entry.Version));

        var saved = await UpdateAsync(entry.Id, "Правка", (await StoredAsync(entry.Id)).Version);
        Assert.Equal("Правка", saved.DisplayName);
        Assert.True((await StoredAsync(entry.Id)).IsArchived);
    }

    /// <summary>
    /// Сверка при ЗАПИСИ, под блокировкой строки, — отдельно от ранней. Запись прочитана, её версия
    /// совпала, а пока шли проверки, запись сохранил другой запрос: без второй сверки эта правка
    /// легла бы поверх. Раннюю сверку тест обходит нарочно — зовёт хранилище сам.
    /// </summary>
    [Fact]
    public async Task Правка_прочитавшая_запись_до_чужого_сохранения_при_записи_отвергается()
    {
        var entry = await EntryAsync("VER_D");
        using var slow = fixture.Services.CreateScope();
        var objects = slow.ServiceProvider.GetRequiredService<IDomainObjectRepository>();
        var loaded = (await objects.GetByIdAsync(entry.Id))!;

        await UpdateAsync(entry.Id, "Чужая", entry.Version);

        loaded.Update("Поверх", J("{'Адрес':'Поверх'}"));
        objects.Update(loaded);
        await Assert.ThrowsAsync<ConflictException>(() => objects.SaveSeenAsync(loaded, entry.Version));

        Assert.Equal("Чужая", (await StoredAsync(entry.Id)).DisplayName);
    }

    /// <summary>
    /// Сверка и запись НЕРАЗРЫВНЫ: строку держит блокировка. Первая правка сверилась и записала, но
    /// ещё не зафиксирована; вторая по той же версии обязана ДОЖДАТЬСЯ её и получить отказ.
    ///
    /// <para>Без блокировки вторая прочла бы прежнюю версию (чужая запись ещё не видна), прошла бы
    /// сверку и записалась поверх сразу после фиксации первой — обе получили бы «сохранено». Тест
    /// гонки ниже это окно случайно не ловит: оно уже, чем разброс между запросами (проверено
    /// поломкой), — поэтому окно здесь раскрыто руками, незафиксированной транзакцией.</para>
    /// </summary>
    [Fact]
    public async Task Вторая_правка_ждёт_незафиксированную_первую_и_получает_отказ()
    {
        var entry = await EntryAsync("VER_F");
        using var first = fixture.Services.CreateScope();
        var db = first.ServiceProvider.GetRequiredService<AppDbContext>();
        var objects = first.ServiceProvider.GetRequiredService<IDomainObjectRepository>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var loaded = (await objects.GetByIdAsync(entry.Id))!;
        loaded.Update("Первая", J("{'Адрес':'Первая'}"));
        objects.Update(loaded);
        await objects.SaveSeenAsync(loaded, entry.Version);

        var second = Task.Run(() => UpdateAsync(entry.Id, "Вторая", entry.Version));
        await Task.Delay(500);
        Assert.False(second.IsCompleted, "вторая правка не дождалась первой — строка не заблокирована");

        await transaction.CommitAsync();
        await Assert.ThrowsAsync<ConflictException>(() => second);
        Assert.Equal("Первая", (await StoredAsync(entry.Id)).DisplayName);
    }

    /// <summary>
    /// Гонка: правки по одной версии приходят разом. Записана ровно одна, остальные — отказ; без
    /// блокировки строки прошли бы несколько, и каждая получила бы «сохранено».
    /// </summary>
    [Fact]
    public async Task Из_одновременных_правок_по_одной_версии_записывается_ровно_одна()
    {
        var entry = await EntryAsync("VER_E");

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            try { await UpdateAsync(entry.Id, $"Форма {i}", entry.Version); return true; }
            catch (ConflictException) { return false; }
        }));

        Assert.Equal(1, outcomes.Count(won => won));
    }

    private async Task<HttpClient> SignInAsync()
    {
        var email = $"ver_{Guid.NewGuid():N}@example.com";
        const string password = "Passw0rd!Ver";
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.CreateAsync(
                new ApplicationUser { UserName = email, Email = email, DisplayName = "Т", EmailConfirmed = true },
                password)).Succeeded);
            await users.AddToRoleAsync((await users.FindByEmailAsync(email))!, BHS.CRG.Api.Auth.SystemRoles.IdEngineer);
        }
        var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
