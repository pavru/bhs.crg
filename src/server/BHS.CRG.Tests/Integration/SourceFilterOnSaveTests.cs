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
    /// Клиент шлёт обработку целиком — отбор, вычисляемые колонки и сортировку разом. Проверяй мы
    /// отбор при каждом сохранении, источник с негодным отбором, сохранённым до #1137, нельзя было бы
    /// ни пересортировать, ни дополнить колонкой: пришлось бы сначала чинить отбор.
    /// </summary>
    [Fact]
    public async Task Сохранённый_раньше_негодный_отбор_не_запирает_источник()
    {
        var (client, _) = await SignInAsync("Admin");
        var id = await SourceAsync(client);
        await StoreAsync(id, WrongKind);

        // Тот же отбор, ключи в ДРУГОМ порядке (так его и возвращает база), плюс новая сортировка.
        const string reordered = """{"logic":"and","children":[{"value":"1","op":"contains","column":"Итого","type":"condition"}],"type":"group"}""";
        var sorted = await PutAsync(client, id, reordered, """[{"column":"Номер","direction":"asc"}]""");
        await OkAsync(sorted);
        Assert.Equal(1, (await sorted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sortSpec").GetArrayLength());

        // Изменённый негодный отбор — уже новый ввод, и он отклоняется.
        var changed = await PutAsync(client, id, WrongKind.Replace("\"1\"", "\"2\""));
        Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
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
            id, new SetSourceProcessingInput(JsonDocument.Parse(Good).RootElement, null, null), access, default));

        Assert.Contains("нет права на суммы", refusal.Message);
        Assert.Null(await StoredAsync(id));
    }

    // ── Помощники ───────────────────────────────────────────────────────────────

    private static async Task<Guid> SourceAsync(HttpClient client)
    {
        var file = await client.PostAsJsonAsync("/api/datasets/files/system", new { scope = "System", name = "Системные" });
        await OkAsync(file);
        var fileId = (await file.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var created = await client.PostAsJsonAsync($"/api/datasets/files/{fileId}/sources",
            new { name = $"Счета {Guid.NewGuid():N}", sheetOrPath = Marker });
        await OkAsync(created);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid id, string rowFilter, string sortSpec = "null") =>
        client.PutAsJsonAsync($"/api/datasets/sources/{id}/processing", new
        {
            rowFilter = JsonDocument.Parse(rowFilter).RootElement,
            sortSpec = JsonDocument.Parse(sortSpec).RootElement,
        });

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
