using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Обработка источника правится по частям (issue #1139): отбор, вычисляемые колонки и сортировка —
/// каждая сама по себе. Чего в запросе нет, то остаётся как есть; присланное пустым — сбрасывается.
///
/// <para>До этого запрос нёс обработку целиком, и диалог сортировки досылал отбор из своей копии
/// источника на странице. Копия устаревала — и сохранение сортировки молча затирало отбор, который
/// тем временем поправил другой человек. Отказа при этом не было: ответ «сохранено» был правдой.</para>
/// </summary>
public sealed class SourceProcessingPartsTests(InvoiceLineHost host) : ModuleTableSeededTests(host)
{
    private const string Sort = """[{"column":"Номер","direction":"asc"}]""";
    private const string Computed = """[{"alias":"К","expr":"1"}]""";

    private static string Between(int from, int to) =>
        $$"""{"type":"group","logic":"and","children":[{"type":"condition","column":"Итого","op":"between","values":["{{from}}","{{to}}"]}]}""";

    [Fact]
    public async Task Правка_сортировки_не_трогает_отбор_сменившийся_за_спиной_страницы()
    {
        var (client, _) = await SignInAsync("Admin");
        var id = await SourceAsync(client);
        await OkAsync(await PutAsync(client, id, $$"""{"rowFilter":{{Between(80, 110)}},"computedColumns":{{Computed}}}"""));

        // Страница загружена — на ней отбор «80…110». Тем временем другой человек его поменял.
        await OkAsync(await PutAsync(client, id, $$"""{"rowFilter":{{Between(1, 2)}}}"""));

        // Со страницы правят сортировку: в запросе только она.
        var sorted = await PutAsync(client, id, $$"""{"sortSpec":{{Sort}}}""");
        await OkAsync(sorted);

        var stored = await StoredAsync(id);
        Assert.Contains("\"1\"", stored.RowFilter);
        Assert.DoesNotContain("110", stored.RowFilter);
        Assert.Contains("К", stored.ComputedColumns);
        Assert.Contains("Номер", stored.SortSpec);
        // И в ответе — источник, каким он стал, со всеми тремя частями.
        var answered = await sorted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Object, answered.GetProperty("rowFilter").ValueKind);
        Assert.Equal(1, answered.GetProperty("computedColumns").GetArrayLength());
    }

    [Fact]
    public async Task Часть_присланная_пустой_сбрасывается_а_остальные_остаются()
    {
        var (client, _) = await SignInAsync("Admin");
        var id = await SourceAsync(client);
        await OkAsync(await PutAsync(client, id,
            $$"""{"rowFilter":{{Between(80, 110)}},"computedColumns":{{Computed}},"sortSpec":{{Sort}}}"""));

        await OkAsync(await PutAsync(client, id, """{"rowFilter":null}"""));

        var stored = await StoredAsync(id);
        Assert.Null(stored.RowFilter);
        Assert.NotNull(stored.ComputedColumns);
        Assert.NotNull(stored.SortSpec);

        // Имена полей — без оглядки на регистр, как их читала привязка к записи.
        await OkAsync(await PutAsync(client, id, """{"SortSpec":null,"COMPUTEDCOLUMNS":null}"""));
        stored = await StoredAsync(id);
        Assert.Null(stored.ComputedColumns);
        Assert.Null(stored.SortSpec);
    }

    /// <summary>
    /// При правке по частям опечатка в имени поля дала бы «сохранено», не сохранив ничего, — раньше
    /// она сбрасывала часть, и это было видно. Поэтому запрос, из которого взять нечего, — отказ.
    /// </summary>
    [Theory]
    [InlineData("{}", "менять нечего")]
    [InlineData("""{"rowFiltr":null}""", "«rowFiltr»")]
    [InlineData("""{"sortSpec":null,"filter":null}""", "«filter»")]
    [InlineData("[]", "не объект")]
    public async Task Запрос_из_которого_взять_нечего_отклоняется_и_источник_цел(string body, string named)
    {
        var (client, _) = await SignInAsync("Admin");
        var id = await SourceAsync(client);
        await OkAsync(await PutAsync(client, id, $$"""{"rowFilter":{{Between(80, 110)}},"sortSpec":{{Sort}}}"""));

        var refused = await PutAsync(client, id, body);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains(named, await refused.Content.ReadAsStringAsync());
        var stored = await StoredAsync(id);
        Assert.NotNull(stored.RowFilter);
        Assert.NotNull(stored.SortSpec);
    }

    /// <summary>
    /// Правка без отбора к поставщику не идёт вовсе: проверять нечего. Кому источник закрыт, тот
    /// сортировку всё равно поправит — как и снимет отбор (см. <see cref="SourceFilterOnSaveTests" />).
    /// </summary>
    [Fact]
    public async Task Правка_без_отбора_поставщика_не_спрашивает()
    {
        var (admin, _) = await SignInAsync("Admin");
        var id = await SourceAsync(admin);
        var (_, outsider) = await SignInAsync("Installer");

        using var scope = host.Services.CreateScope();
        var access = await scope.ServiceProvider.GetRequiredService<DataAccessResolver>().ForUserAsync(outsider, default);
        var saved = await scope.ServiceProvider.GetRequiredService<IDataSetService>().SetSourceProcessingAsync(
            id, new SetSourceProcessingInput { SortSpec = ProcessingPart.Of(JsonDocument.Parse(Sort).RootElement) }, access, default);

        Assert.NotNull(saved);
        Assert.Contains("Номер", (await StoredAsync(id)).SortSpec);
    }

    // ── Помощники ───────────────────────────────────────────────────────────────

    /// <summary>Тело — дословно: какие поля в нём есть, а каких нет, здесь и проверяется.</summary>
    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid id, string body) =>
        client.PutAsync($"/api/datasets/sources/{id}/processing", new StringContent(body, Encoding.UTF8, "application/json"));

    private async Task<DataSetSource> StoredAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .DataSetSources.AsNoTracking().FirstAsync(s => s.Id == id);
    }
}
