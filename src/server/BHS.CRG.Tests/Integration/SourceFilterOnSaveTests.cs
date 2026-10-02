using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Common;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Отбор источника проверяется при сохранении (issue #1137): негодный не ложится в базу, а человек
/// узнаёт причину там, где ошибся, — а не из отказа источника при следующем чтении.
///
/// <para>Источник здесь — на таблице счетов: у неё колонки с видами, и проверка обязана быть той же,
/// что при чтении («Итого содержит 1» — отказ, «Срок пусто» — годится). Три решения, которые
/// сторожатся отдельно: неизменённый отбор не перепроверяется (иначе сохранённый раньше негодный
/// запер бы источник), шаблон ложится целиком или не ложится вовсе, а колонка, закрытая человеку
/// правом, для него колонка без значений.</para>
/// </summary>
public sealed class SourceFilterOnSaveTests(InvoiceLineHost host) : ModuleTableSeededTests(host)
{
    private const string Good = """{"type":"group","logic":"and","children":[{"type":"condition","column":"Итого","op":"between","values":["80","110"]}]}""";
    private const string WrongKind = """{"type":"group","logic":"and","children":[{"type":"condition","column":"Итого","op":"contains","value":"1"}]}""";
    private const string UnknownOp = """{"type":"group","logic":"and","children":[{"type":"condition","column":"Номер","op":"betwen","value":"1"}]}""";

    [Fact]
    public async Task Негодный_отбор_не_сохраняется_и_причина_названа()
    {
        var (client, _) = await SignInAsync("Admin");
        var id = await SourceAsync(client);

        var refused = await PutAsync(client, id, WrongKind);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var said = await refused.Content.ReadAsStringAsync();
        Assert.Contains("не сохранён", said);
        Assert.Contains("условие 1 по колонке «Итого»", said);
        Assert.Contains("не применяется", said);
        // В базу не легло ничего: отказ — это отказ, а не «сохранили и предупредили».
        Assert.Null(await StoredAsync(id));

        // Оператора нет вовсе — та же дверь, та же причина словами.
        var unknown = await PutAsync(client, id, UnknownOp);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("«betwen»", await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Годный_отбор_сохраняется_а_сброс_проходит_всегда()
    {
        var (client, _) = await SignInAsync("Admin");
        var id = await SourceAsync(client);

        await OkAsync(await PutAsync(client, id, Good));
        // «Пусто» сервер принимает у колонки любого вида — отборы, сохранённые до #1090, спрашивали
        // его у числа и даты. Проверка при сохранении не должна быть строже чтения.
        await OkAsync(await PutAsync(client, id,
            """{"type":"group","logic":"and","children":[{"type":"condition","column":"Срок","op":"is_empty"}]}"""));
        Assert.NotNull(await StoredAsync(id));

        await OkAsync(await PutAsync(client, id, "null"));
        Assert.Null(await StoredAsync(id));
    }

    /// <summary>
    /// Источник с негодным отбором, сохранённым раньше (до #1137, из копии), нельзя запирать: его
    /// сортировку и вычисляемые колонки правят, не присылая отбора, — и до проверки дело не доходит
    /// (issue #1139). Прежде отбор уезжал с каждой правкой, и пропускали его сравнением «тот же ли».
    /// </summary>
    [Fact]
    public async Task Сохранённый_раньше_негодный_отбор_не_запирает_источник()
    {
        var (client, _) = await SignInAsync("Admin");
        var id = await SourceAsync(client);
        await StoreAsync(id, WrongKind);

        var sorted = await PutPartsAsync(client, id, new { sortSpec = Json("""[{"column":"Номер","direction":"asc"}]""") });
        await OkAsync(sorted);
        Assert.Equal(1, (await sorted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sortSpec").GetArrayLength());
        await OkAsync(await PutPartsAsync(client, id, new { computedColumns = Json("""[{"alias":"К","expr":"1"}]""") }));
        Assert.Contains("contains", await StoredAsync(id));

        // А присланный отбор — новый ввод, даже слово в слово совпавший с сохранённым: человек нажал
        // «Сохранить» в диалоге отбора, и там ему и место узнать, что отбор негоден.
        var same = await PutPartsAsync(client, id, new { rowFilter = Json(WrongKind) });
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);
    }

    [Fact]
    public async Task Шаблон_чей_отбор_источник_не_выполнит_отклоняется_и_источник_не_меняется()
    {
        var (client, _) = await SignInAsync("Admin");
        var id = await SourceAsync(client);
        await OkAsync(await PutAsync(client, id, Good));

        // По форме отбор годен — негоден он именно этому источнику: «Итого» здесь число.
        var name = $"Шаблон {Guid.NewGuid():N}";
        var template = await client.PostAsJsonAsync("/api/datasets/processing-templates", new
        {
            name, rowFilter = JsonDocument.Parse(WrongKind).RootElement,
            sortSpec = JsonDocument.Parse("""[{"column":"Номер","direction":"desc"}]""").RootElement,
        });
        await OkAsync(template);
        var templateId = (await template.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var refused = await client.PostAsync($"/api/datasets/sources/{id}/apply-template/{templateId}", null);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var said = await refused.Content.ReadAsStringAsync();
        Assert.Contains(name, said);
        Assert.Contains("Источник не изменён", said);

        // Шаблон не лёг и частично: сортировка шаблона на источник не попала, отбор остался прежним.
        using var scope = host.Services.CreateScope();
        var source = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .DataSetSources.AsNoTracking().FirstAsync(s => s.Id == id);
        Assert.Null(source.SortSpec);
        Assert.Contains("between", source.RowFilter);
    }

    [Fact]
    public async Task Шаблон_с_негодным_по_форме_отбором_не_сохраняется()
    {
        var (client, _) = await SignInAsync("Admin");
        var name = $"Шаблон {Guid.NewGuid():N}";

        var refused = await client.PostAsJsonAsync("/api/datasets/processing-templates",
            new { name, rowFilter = JsonDocument.Parse(UnknownOp).RootElement });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("«betwen»", await refused.Content.ReadAsStringAsync());
        var templates = await client.GetFromJsonAsync<JsonElement>("/api/datasets/processing-templates");
        Assert.DoesNotContain(templates.EnumerateArray(), t => t.GetProperty("name").GetString() == name);

        // Шаблон, сохранённый раньше с таким отбором, переименовать можно: отбор в запросе тот же.
        var good = await client.PostAsJsonAsync("/api/datasets/processing-templates",
            new { name, rowFilter = JsonDocument.Parse(Good).RootElement });
        await OkAsync(good);
        var templateId = (await good.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.DataSetProcessingTemplates.FirstAsync(t => t.Id == templateId);
            stored.Update(stored.Name, null, null, UnknownOp, null, null);
            await db.SaveChangesAsync();
        }

        await OkAsync(await client.PutAsJsonAsync($"/api/datasets/processing-templates/{templateId}",
            new { name = name + " (2)", rowFilter = JsonDocument.Parse(UnknownOp).RootElement }));
    }

    /// <summary>
    /// Виды колонок — глазами того, кто сохраняет. Человеку без права на суммы «Итого» приходит без
    /// значений, и отбор по ней он не выполнит: чтение ему откажет. Сохранение обязано отказать тем же.
    /// </summary>
    [Fact]
    public async Task Отбор_по_колонке_закрытой_правом_не_сохраняется()
    {
        var (admin, _) = await SignInAsync("Admin");
        var id = await SourceAsync(admin);
        var (_, narrow) = await SignInAsync(await RoleAsync("costs.waybill.read"));

        using var scope = host.Services.CreateScope();
        var access = await scope.ServiceProvider.GetRequiredService<DataAccessResolver>().ForUserAsync(narrow, default);
        var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();

        var refusal = await Assert.ThrowsAsync<InvalidRequestException>(() => svc.SetSourceProcessingAsync(
            id, new SetSourceProcessingInput { RowFilter = ProcessingPart.Of(JsonDocument.Parse(Good).RootElement) }, access, default));

        Assert.Contains("нет права на суммы", refusal.Message);
        Assert.Null(await StoredAsync(id));
    }

    /// <summary>
    /// «Виды узнать не удалось» — не «видов нет». Кому поставщик отказывает в строках (нет доступа к
    /// модулю), тому нечем и проверить отбор: прими мы его по форме, человек сохранил бы «Итого
    /// содержит 1» — отказом для всех, кто источник читает. Отказ — тот же, что на чтении.
    ///
    /// <para>А вот снять отбор и применить шаблон без отбора он может: проверять там нечего, и за
    /// видами служба к поставщику не идёт вовсе — сброс не должен зависеть от того, пускает ли
    /// поставщик этого человека и жив ли он.</para>
    /// </summary>
    [Fact]
    public async Task Кому_поставщик_отказывает_тот_отбор_не_сохраняет_но_снять_может()
    {
        var (admin, _) = await SignInAsync("Admin");
        var id = await SourceAsync(admin);
        await OkAsync(await PutAsync(admin, id, Good));
        var (_, outsider) = await SignInAsync("Installer");

        // По форме отбор годен, негоден он этому источнику — без видов проверка его пропустила бы.
        await Assert.ThrowsAsync<ForbiddenException>(() => AsAsync(outsider, (svc, access) => svc.SetSourceProcessingAsync(
            id, new SetSourceProcessingInput { RowFilter = ProcessingPart.Of(JsonDocument.Parse(WrongKind).RootElement) }, access, default)));
        Assert.Contains("between", await StoredAsync(id));

        // Негодный по форме отбор назван и без поставщика — причина та же, что у всех.
        var named = await Assert.ThrowsAsync<InvalidRequestException>(() => AsAsync(outsider, (svc, access) => svc.SetSourceProcessingAsync(
            id, new SetSourceProcessingInput { RowFilter = ProcessingPart.Of(JsonDocument.Parse(UnknownOp).RootElement) }, access, default)));
        Assert.Contains("«betwen»", named.Message);

        // Шаблон без отбора — только сортировка: проверять нечего, поставщик не нужен.
        var template = await admin.PostAsJsonAsync("/api/datasets/processing-templates", new
        {
            name = $"Шаблон {Guid.NewGuid():N}",
            sortSpec = JsonDocument.Parse("""[{"column":"Номер","direction":"desc"}]""").RootElement,
        });
        await OkAsync(template);
        var templateId = (await template.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.NotNull(await AsAsync(outsider, (svc, access) => svc.ApplyProcessingTemplateAsync(id, templateId, access, default)));
        Assert.Null(await StoredAsync(id));

        // И сброс отбора — тоже.
        await StoreAsync(id, WrongKind);
        Assert.NotNull(await AsAsync(outsider, (svc, access) => svc.SetSourceProcessingAsync(
            id, new SetSourceProcessingInput { RowFilter = ProcessingPart.Cleared }, access, default)));
        Assert.Null(await StoredAsync(id));
    }

    /// <summary>
    /// «null» среди узлов — названная причина, а не падение: разбор такой узел пропускает, и прежде
    /// сохранение отвечало 500 без текста.
    /// </summary>
    [Fact]
    public async Task Пустой_узел_в_отборе_назван_а_не_роняет_сохранение()
    {
        var (client, _) = await SignInAsync("Admin");
        var id = await SourceAsync(client);

        var refused = await PutAsync(client, id, """{"type":"group","logic":"and","children":[null]}""");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("узел 1", await refused.Content.ReadAsStringAsync());
    }

    // ── Помощники ───────────────────────────────────────────────────────────────

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>Правка обработки ровно теми частями, что названы в теле.</summary>
    private static Task<HttpResponseMessage> PutPartsAsync(HttpClient client, Guid id, object parts) =>
        client.PutAsJsonAsync($"/api/datasets/sources/{id}/processing", parts);

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid id, string rowFilter, string sortSpec = "null") =>
        client.PutAsJsonAsync($"/api/datasets/sources/{id}/processing", new
        {
            rowFilter = JsonDocument.Parse(rowFilter).RootElement,
            sortSpec = JsonDocument.Parse(sortSpec).RootElement,
        });

    /// <summary>
    /// Вызов службы от имени пользователя — каждый в СВОЕЙ области: в общей контекст базы отдал бы
    /// источник из памяти, каким тот был до правки мимо службы, и проверка шла бы не по тому отбору.
    /// </summary>
    private async Task<T> AsAsync<T>(Guid user, Func<IDataSetService, DataAccess, Task<T>> call)
    {
        using var scope = host.Services.CreateScope();
        var access = await scope.ServiceProvider.GetRequiredService<DataAccessResolver>().ForUserAsync(user, default);
        return await call(scope.ServiceProvider.GetRequiredService<IDataSetService>(), access);
    }

    /// <summary>Отбор так, как он лежит в базе; null — отбора нет.</summary>
    private async Task<string?> StoredAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .DataSetSources.AsNoTracking().FirstAsync(s => s.Id == id)).RowFilter;
    }

    /// <summary>Отбор — в базу мимо службы: так лежит сохранённое до #1137 и восстановленное из копии.</summary>
    private async Task StoreAsync(Guid id, string rowFilter)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.DataSetSources.FirstAsync(s => s.Id == id)).SetProcessing(rowFilter, null, null);
        await db.SaveChangesAsync();
    }
}
