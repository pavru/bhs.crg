using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Правка обработки сверяет версию (issue #1141): источник изменили после того, как его загрузила
/// страница, — отказ 409 с причиной, а не «сохранено» поверх чужой правки.
///
/// <para>Правка по частям (issue #1139) закрыла затирание МЕЖДУ частями. Внутри одной оно осталось:
/// диалог заполнен из копии источника на странице, копия устарела — и сохранение молча возвращало
/// прежнее значение туда, где другой человек только что поставил своё.</para>
///
/// <para>«Страница» в этих тестах — версия из ОТВЕТА сервера, какой её получил бы клиент; «только
/// что открытый диалог» — <see cref="SourceProcessingTestBase.PutAsync" />.</para>
/// </summary>
public sealed class SourceProcessingConflictTests(InvoiceLineHost host) : SourceProcessingTestBase(host)
{
    private const string Sort = """[{"column":"Номер","direction":"asc"}]""";
    private const string Computed = """[{"alias":"К","expr":"1"}]""";

    private static string Between(int from, int to) =>
        $$"""{"type":"group","logic":"and","children":[{"type":"condition","column":"Итого","op":"between","values":["{{from}}","{{to}}"]}]}""";

    /// <summary>Версия обработки, какой её видит страница, — из ответа сервера.</summary>
    private static async Task<string> SeenAsync(HttpResponseMessage answered)
    {
        await OkAsync(answered);
        return (await answered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("processingVersion").GetString()!;
    }

    /// <summary>Источник с обработкой во всех трёх частях; отдаёт версию, с которой его загрузила страница.</summary>
    private async Task<(HttpClient Client, Guid Id, string Seen)> LoadedAsync()
    {
        var (client, _) = await SignInAsync("Admin");
        var id = await SourceAsync(client);
        var seen = await SeenAsync(await PutAsync(client, id,
            $$"""{"rowFilter":{{Between(80, 110)}},"computedColumns":{{Computed}},"sortSpec":{{Sort}}}"""));
        return (client, id, seen);
    }

    /// <summary>Значение части с меткой — по метке потом видно, чья правка лежит в базе.</summary>
    private static string Marked(string part, string mark) => part == "rowFilter"
        ? $$"""{"type":"group","logic":"and","children":[{"type":"condition","column":"Номер","op":"eq","value":"{{mark}}"}]}"""
        : $$"""[{"alias":"{{mark}}","expr":"1","column":"{{mark}}","direction":"asc"}]""";

    [Theory]
    [InlineData("rowFilter")]
    [InlineData("computedColumns")]
    [InlineData("sortSpec")]
    public async Task Правка_с_устаревшей_копии_отклоняется_и_чужая_правка_цела(string part)
    {
        var (client, id, seen) = await LoadedAsync();

        // Тем временем ту же часть поправил другой человек — со своей, свежей страницы.
        await OkAsync(await PutAsync(client, id, $$"""{"{{part}}":{{Marked(part, "ЧУЖОЕ")}}}"""));

        // А этот диалог заполнен прежней копией — и версию называет прежнюю.
        var refused = await PutRawAsync(client, id, WithVersion($$"""{"{{part}}":{{Marked(part, "СВОЁ")}}}""", seen));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var said = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.Contains("тем временем изменили", said);
        Assert.Contains("Обновите страницу", said);
        var stored = await StoredAsync(id);
        var kept = part switch
        {
            "rowFilter" => stored.RowFilter,
            "computedColumns" => stored.ComputedColumns,
            _ => stored.SortSpec,
        };
        Assert.Contains("ЧУЖОЕ", kept);
        Assert.DoesNotContain("СВОЁ", kept);
    }

    /// <summary>
    /// Диалог сортировки предлагает колонки по вычисляемым из своей копии. Колонку тем временем
    /// удалили — сортировка по её псевдониму сохранилась бы и указывала бы в никуда. Поэтому версия
    /// одна на всю обработку, а не своя у каждой части.
    /// </summary>
    [Fact]
    public async Task Сортировка_по_вычисляемой_колонке_которую_тем_временем_удалили_отклоняется()
    {
        var (client, id, seen) = await LoadedAsync();
        await OkAsync(await PutAsync(client, id, """{"computedColumns":null}"""));

        var refused = await PutRawAsync(client, id, WithVersion("""{"sortSpec":[{"column":"К","direction":"desc"}]}""", seen));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var stored = await StoredAsync(id);
        Assert.Contains("Номер", stored.SortSpec);
        Assert.DoesNotContain("desc", stored.SortSpec);
    }

    /// <summary>
    /// Обработка лежит в <c>jsonb</c>, и текст после записи другой: ключи переставлены, пробелы свои.
    /// Версия обязана считаться по значению — иначе следующая правка того же человека получила бы
    /// отказ про «изменили», хотя менял только он сам.
    /// </summary>
    [Fact]
    public async Task Версия_из_ответа_на_сохранение_годится_для_следующей_правки()
    {
        var (client, _) = await SignInAsync("Admin");
        var id = await SourceAsync(client);
        const string asSent = """[ { "direction" : "asc",   "column" : "Номер" } ]""";

        var seen = await SeenAsync(await PutAsync(client, id, $$"""{"sortSpec":{{asSent}}}"""));

        // Посылка теста: база вернула не тот текст, что прислали, — иначе проверять было бы нечего.
        Assert.NotEqual(asSent, (await StoredAsync(id)).SortSpec);
        Assert.Equal(seen, await VersionAsync(id));
        await OkAsync(await PutRawAsync(client, id, WithVersion($$"""{"rowFilter":{{Between(80, 110)}}}""", seen)));
    }

    /// <summary>
    /// Почему версия — не <c>UpdatedAt</c>: его двигают и правки, к обработке не относящиеся. Кэш схемы
    /// обновляет распознавание, идущее в фоне минутами, — и человек, поправивший за это время
    /// сортировку, получал бы отказ про изменение, которое его правке не мешает.
    /// </summary>
    [Fact]
    public async Task Правки_не_про_обработку_версию_не_двигают()
    {
        var (client, id, seen) = await LoadedAsync();
        var before = (await StoredAsync(id)).UpdatedAt;

        await OkAsync(await client.PutAsJsonAsync($"/api/datasets/sources/{id}/name", new { name = $"Иначе {Guid.NewGuid():N}" }));
        await ChangeAsync(id, s =>
        {
            s.SetTags("""["dataset.x"]""");
            s.UpdateCache(s.CachedSchema, s.CachedRowCount + 1);
            s.MarkRecognitionStale(DataSetStaleReason.FileReplaced);
            s.SetMaterialization(null, null);
        });

        // Посылка теста: время правки ушло вперёд — версия по нему отказала бы.
        Assert.True((await StoredAsync(id)).UpdatedAt > before);
        Assert.Equal(seen, await VersionAsync(id));
        await OkAsync(await PutRawAsync(client, id, WithVersion("""{"sortSpec":null}""", seen)));
    }

    /// <summary>
    /// Вход HTTP версию требует всегда: за ним страница, и страница без версии — та, чью свежесть
    /// проверить нечем. Так вкладка с прежней сборкой клиента получает отказ с причиной, а не
    /// продолжает затирать молча.
    /// </summary>
    [Theory]
    [InlineData("""{"sortSpec":null}""")]
    [InlineData("""{"sortSpec":null,"ifMatch":null}""")]
    [InlineData("""{"sortSpec":null,"ifMatch":""}""")]
    [InlineData("""{"rowFilter":null,"computedColumns":null,"sortSpec":null}""")]
    public async Task Запрос_не_назвавший_версию_отклоняется_и_источник_цел(string body)
    {
        var (client, id, _) = await LoadedAsync();

        var refused = await PutRawAsync(client, id, body);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var said = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.Contains("ifMatch", said);
        Assert.Contains("обновите страницу", said);
        Assert.NotNull((await StoredAsync(id)).SortSpec);
    }

    /// <summary>
    /// Служба сверяет версию, когда она названа, — и только тогда: вход, задающий обработку заново, на
    /// прежнее состояние не опирается, и сверять ему нечего.
    /// </summary>
    [Fact]
    public async Task Служба_сверяет_версию_когда_она_названа()
    {
        var (client, user) = await SignInAsync("Admin");
        var id = await SourceAsync(client);
        var sort = ProcessingPart.Of(JsonDocument.Parse(Sort).RootElement.Clone());

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => AsAsync(user, (svc, access) =>
            svc.SetSourceProcessingAsync(id, new SetSourceProcessingInput { SortSpec = sort, IfMatch = "не та" }, access, default)));
        Assert.Contains("тем временем изменили", refusal.Message);
        Assert.Null((await StoredAsync(id)).SortSpec);

        Assert.NotNull(await AsAsync(user, (svc, access) =>
            svc.SetSourceProcessingAsync(id, new SetSourceProcessingInput { SortSpec = sort }, access, default)));
        Assert.Contains("Номер", (await StoredAsync(id)).SortSpec);
    }

    /// <summary>
    /// Сверка и запись неразрывны. Два сохранения приходят почти разом: опоздавшее читает источник,
    /// пока чужая правка ещё не закрыта, — то есть видит прежнюю обработку, и версия у него сходится.
    /// Без блокировки строки оно дождалось бы чужой записи и легло поверх неё, а оба получили бы
    /// «сохранено». Чужую правку закрывают, только когда опоздавшее упёрлось в блокировку, — иначе
    /// неизвестно, прочитало ли оно источник прежним (см. <see cref="SourceProcessingTestBase.ChangeWhileAsync" />).
    /// </summary>
    [Fact]
    public async Task Сохранение_пришедшее_вместе_с_чужим_получает_отказ_а_не_затирает()
    {
        var (client, user) = await SignInAsync("Admin");
        var id = await SourceAsync(client);
        var seen = await SeenAsync(await PutAsync(client, id, $$"""{"sortSpec":{{Sort}}}"""));
        var mine = ProcessingPart.Of(JsonDocument.Parse(Marked("sortSpec", "СВОЁ")).RootElement.Clone());

        var late = await ChangeWhileAsync(id, s => s.SetProcessing(s.RowFilter, s.ComputedColumns, Marked("sortSpec", "ЧУЖОЕ")),
            late: () => AsAsync(user, (svc, access) => svc.SetSourceProcessingAsync(
                id, new SetSourceProcessingInput { SortSpec = mine, IfMatch = seen }, access, default)));

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => late);
        Assert.Contains("тем временем изменили", refusal.Message);
        var stored = await StoredAsync(id);
        Assert.Contains("ЧУЖОЕ", stored.SortSpec);
        Assert.DoesNotContain("СВОЁ", stored.SortSpec);
    }

    /// <summary>
    /// То же правило входа — у соседних правок, собранных по копии источника на странице: извлечение
    /// и материализация. Сама сверка у них проверена в <see cref="SourceVersionedEditsTests" />.
    /// </summary>
    [Theory]
    [InlineData("", """{"name":"Иначе","sheetOrPath":"x"}""")]
    [InlineData("/materialization", """{"typeId":null,"mapping":null}""")]
    public async Task Правка_извлечения_и_материализации_без_версии_отклоняется(string path, string body)
    {
        var (client, id, _) = await LoadedAsync();
        var before = (await StoredAsync(id)).UpdatedAt;

        var refused = await client.PutAsync($"/api/datasets/sources/{id}{path}",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var said = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.Contains("ifMatch", said);
        Assert.Contains("обновите страницу", said);
        Assert.Equal(before, (await StoredAsync(id)).UpdatedAt);
    }

    /// <summary>Материализация через HTTP: устаревшая версия — 409, текущая — сохранено.</summary>
    [Fact]
    public async Task Материализация_через_HTTP_сверяет_версию()
    {
        var (client, id, _) = await LoadedAsync();
        Task<HttpResponseMessage> Unset(string version) => client.PutAsJsonAsync(
            $"/api/datasets/sources/{id}/materialization", new { typeId = (Guid?)null, ifMatch = version });

        var stale = await Unset("не та");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("тем временем изменили",
            (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        await OkAsync(await Unset(SourceProcessingVersion.OfMaterialization(await StoredAsync(id))));
    }

    // ── «Сохранить как шаблон» ────────────────────────────────────────────────

    private static Task<HttpResponseMessage> SaveAsTemplateAsync(HttpClient client, Guid id, object body) =>
        client.PostAsJsonAsync($"/api/datasets/sources/{id}/processing-template", body);

    private static async Task<bool> TemplateExistsAsync(HttpClient client, string name)
    {
        var templates = await client.GetFromJsonAsync<JsonElement>("/api/datasets/processing-templates");
        return templates.EnumerateArray().Any(t => t.GetProperty("name").GetString() == name);
    }

    /// <summary>
    /// Шаблон собирает сервер из СОХРАНЁННОГО источника: содержимое с клиента не едет вовсе, и
    /// устаревшей копии на странице попасть в шаблон неоткуда.
    /// </summary>
    [Fact]
    public async Task Шаблон_из_источника_берёт_сохранённые_извлечение_и_обработку()
    {
        var (client, id, seen) = await LoadedAsync();
        var name = $"Шаблон {Guid.NewGuid():N}";

        var saved = await SaveAsTemplateAsync(client, id, new { name, ifMatch = seen });

        await OkAsync(saved);
        var template = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(name, template.GetProperty("name").GetString());
        Assert.Equal(Marker, template.GetProperty("sheetOrPath").GetString());
        Assert.Contains("110", template.GetProperty("rowFilter").GetRawText());
        Assert.Equal("К", template.GetProperty("computedColumns")[0].GetProperty("alias").GetString());
        Assert.Equal("Номер", template.GetProperty("sortSpec")[0].GetProperty("column").GetString());
        Assert.True(await TemplateExistsAsync(client, name));
    }

    [Fact]
    public async Task Шаблон_с_устаревшей_копии_источника_не_создаётся()
    {
        var (client, id, seen) = await LoadedAsync();
        var name = $"Шаблон {Guid.NewGuid():N}";
        await OkAsync(await PutFilterAsync(client, id, Between(1, 2)));

        var refused = await SaveAsTemplateAsync(client, id, new { name, ifMatch = seen });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var said = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.Contains("тем временем изменили", said);
        Assert.False(await TemplateExistsAsync(client, name));
    }

    /// <summary>Запрос, из которого шаблон не собрать, — отказ с причиной, и шаблона нет.</summary>
    [Fact]
    public async Task Шаблон_из_источника_без_версии_или_названия_не_создаётся()
    {
        var (client, id, seen) = await LoadedAsync();
        var name = $"Шаблон {Guid.NewGuid():N}";

        var unversioned = await SaveAsTemplateAsync(client, id, new { name });
        Assert.Equal(HttpStatusCode.BadRequest, unversioned.StatusCode);
        Assert.Contains("ifMatch", (await unversioned.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        var unnamed = await SaveAsTemplateAsync(client, id, new { name = " ", ifMatch = seen });
        Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);

        var missing = await SaveAsTemplateAsync(client, Guid.NewGuid(), new { name, ifMatch = seen });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.False(await TemplateExistsAsync(client, name));
    }
}
