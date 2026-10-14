using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Activity;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Costs.Tables;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Сохранённые представления таблицы: свои и общие (задача G3a, issue #1095; ТЗ CORE-33).
///
/// <para>Главные проверки — те, что названы сторожем задачи:
/// <see cref="Запертое_представление_модуля_отвергает_правку_и_предлагает_копию" /> (разрешить правку
/// представления модуля на месте — тест падает) и
/// <see cref="Общее_правит_только_держатель_права_и_только_по_свежей_версии" />.</para>
///
/// <para>База у хоста общая на прогон и не чистится: названия несут метку теста.</para>
/// </summary>
[Collection("Integration")]
public sealed class SavedTableViewTests(InvoiceLineHost host) : ModuleTableSeededTests(host)
{
    private const string Views = $"/api/tables/{Address}/views";

    private readonly InvoiceLineHost host = host;

    [Fact]
    public async Task Своё_представление_видит_и_правит_только_автор()
    {
        var (author, _) = await SignInAsync("Supplier");
        var (stranger, _) = await SignInAsync("Supplier");
        var title = Name("Мои счета");

        var created = await SavedAsync(await author.PostAsJsonAsync(Views, Body(title, columns: ["Номер", "Дата"])));

        Assert.False(created.GetProperty("shared").GetBoolean());
        Assert.True(created.GetProperty("mine").GetBoolean());
        Assert.True(created.GetProperty("canEdit").GetBoolean());
        Assert.Equal(["Номер", "Дата"], Strings(created.GetProperty("state").GetProperty("columns")));
        Assert.Contains(title, await TitlesAsync(author));
        Assert.DoesNotContain(title, await TitlesAsync(stranger));

        // Чужое личное неотличимо от несуществующего: одинаковый код и одинаковые слова. Разные ответы
        // подтверждали бы, что представление с таким идентификатором у кого-то есть.
        var id = created.GetProperty("id").GetGuid();
        var version = created.GetProperty("version").GetString()!;
        var foreign = await SendAsync(stranger, HttpMethod.Put, $"{Views}/{id}", version, Body(title + " 2"));
        var absent = await SendAsync(stranger, HttpMethod.Put, $"{Views}/{Guid.NewGuid()}", version, Body(title + " 2"));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        Assert.Equal(await ErrorAsync(absent), await ErrorAsync(foreign));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(stranger, HttpMethod.Delete, $"{Views}/{id}", version)).StatusCode);

        // Автор правит: колонки заменены целиком, версия сдвинулась.
        var changed = await SavedAsync(await SendAsync(author, HttpMethod.Put, $"{Views}/{id}", version,
            Body(title, columns: ["Номер"], sort: [new { column = "Дата", descending = true }])));
        Assert.Equal(["Номер"], Strings(changed.GetProperty("state").GetProperty("columns")));
        Assert.NotEqual(version, changed.GetProperty("version").GetString());

        var removed = await SendAsync(author, HttpMethod.Delete, $"{Views}/{id}", changed.GetProperty("version").GetString());
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.DoesNotContain(title, await TitlesAsync(author));
    }

    /// <summary>
    /// Сторож задачи. Представление модуля в базе не лежит, и «не найдено» было бы честным ответом
    /// хранилища — но человеку оно сказало бы неправду: представление есть, оно заперто. Отказ обязан
    /// назвать это и выход — сохранить как своё.
    /// </summary>
    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Запертое_представление_модуля_отвергает_правку_и_предлагает_копию(string method)
    {
        var (admin, _) = await SignInAsync("Admin");

        // Версию не называем НАРОЧНО: у представления модуля её нет, в списке его нет, и взять её
        // неоткуда. Требуй адрес заголовок и здесь, вместо «заперто» приходило бы «назовите версию».
        var response = await SendAsync(admin, new HttpMethod(method), $"{Views}/{InvoiceTable.RegistryView}", null,
            method == "PUT" ? Body("Реестр по-моему", columns: ["Номер"]) : null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await ErrorAsync(response);
        Assert.Contains("«Реестр счетов»", error);
        Assert.Contains("заперто", error);
        Assert.Contains("как своё", error);

        // Выход работает: копия заводится и помнит, из чего сделана.
        var copy = await SavedAsync(await admin.PostAsJsonAsync(Views,
            Body(Name("Реестр по-моему"), columns: ["Номер"], basedOn: InvoiceTable.RegistryView.ToUpperInvariant())));
        Assert.Equal(InvoiceTable.RegistryView, copy.GetProperty("basedOn").GetString());

        // Кода, которого модуль не объявляет, нет вовсе — это уже «не найдено», а не «заперто».
        var unknown = await SendAsync(admin, HttpMethod.Delete, $"{Views}/no-such-view", null);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Общее_правит_только_держатель_права_и_только_по_свежей_версии()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (reader, _) = await SignInAsync("Supplier");
        var title = Name("Счета к оплате");

        // Без права общее не заводится — ни созданием, ни правкой своего.
        var refused = await reader.PostAsJsonAsync(Views, Body(title, shared: true));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("core.views.share", await ErrorAsync(refused));
        var own = await SavedAsync(await reader.PostAsJsonAsync(Views, Body(Name("Своё"))));
        var promote = await SendAsync(reader, HttpMethod.Put, $"{Views}/{own.GetProperty("id").GetGuid()}",
            own.GetProperty("version").GetString(), Body(Name("Своё"), shared: true));
        Assert.Equal(HttpStatusCode.Forbidden, promote.StatusCode);

        var shared = await SavedAsync(await admin.PostAsJsonAsync(Views, Body(title, shared: true, columns: ["Номер", "Дата"])));
        var (id, version) = (shared.GetProperty("id").GetGuid(), shared.GetProperty("version").GetString()!);

        // Читатель общее видит, но править не вправе — и список говорит ему это заранее.
        var seen = (await ListAsync(reader)).Single(v => v.GetProperty("id").GetGuid() == id);
        // Автор назван одним способом в ответе создания и в списке: клиент кладёт первый в кеш второго.
        Assert.Equal(shared.GetProperty("author").GetString(), seen.GetProperty("author").GetString());
        Assert.True(seen.GetProperty("shared").GetBoolean());
        Assert.False(seen.GetProperty("mine").GetBoolean());
        Assert.False(seen.GetProperty("canEdit").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(seen.GetProperty("author").GetString()));

        var foreign = await SendAsync(reader, HttpMethod.Put, $"{Views}/{id}", version, Body(title, shared: true));
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Contains("как своё", await ErrorAsync(foreign));
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(reader, HttpMethod.Delete, $"{Views}/{id}", version)).StatusCode);

        // Версия обязательна и обязана быть свежей.
        var unnamed = await SendAsync(admin, HttpMethod.Put, $"{Views}/{id}", null, Body(title, shared: true));
        Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);
        Assert.Contains("If-Match", await ErrorAsync(unnamed));

        var changed = await SavedAsync(await SendAsync(admin, HttpMethod.Put, $"{Views}/{id}", version,
            Body(title, shared: true, columns: ["Номер"])));
        var stale = await SendAsync(admin, HttpMethod.Put, $"{Views}/{id}", version, Body(title, shared: true, columns: ["Дата"]));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("тем временем изменили", await ErrorAsync(stale));
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(admin, HttpMethod.Delete, $"{Views}/{id}", version)).StatusCode);

        // Устаревшая правка не легла: в списке — то, что записала свежая.
        var now = (await ListAsync(reader)).Single(v => v.GetProperty("id").GetGuid() == id);
        Assert.Equal(["Номер"], Strings(now.GetProperty("state").GetProperty("columns")));
        Assert.Equal(changed.GetProperty("version").GetString(), now.GetProperty("version").GetString());
    }

    /// <summary>
    /// Держатель права чужое общее правит и удаляет, но себе не забирает: снятое с общих чужими
    /// руками, оно исчезло бы у всех разом и осталось у человека, который его не заводил.
    /// </summary>
    [Fact]
    public async Task Вернуть_в_свои_может_только_автор()
    {
        var (author, _) = await SignInAsync("Admin");
        var (other, _) = await SignInAsync("Admin");
        var (reader, _) = await SignInAsync("Supplier");
        var title = Name("Общее автора");

        var shared = await SavedAsync(await author.PostAsJsonAsync(Views, Body(title, shared: true)));
        var (id, version) = (shared.GetProperty("id").GetGuid(), shared.GetProperty("version").GetString()!);

        var taken = await SendAsync(other, HttpMethod.Put, $"{Views}/{id}", version, Body(title, shared: false));
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Contains("только автор", await ErrorAsync(taken));
        Assert.Contains(title, await TitlesAsync(reader));

        var back = await SavedAsync(await SendAsync(author, HttpMethod.Put, $"{Views}/{id}", version, Body(title, shared: false)));
        Assert.False(back.GetProperty("shared").GetBoolean());
        Assert.DoesNotContain(title, await TitlesAsync(reader));
        Assert.DoesNotContain(title, await TitlesAsync(other));
        Assert.Contains(title, await TitlesAsync(author));
    }

    [Fact]
    public async Task Имя_уникально_в_своей_группе_а_личное_и_общее_живут_рядом()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (reader, _) = await SignInAsync("Supplier");
        var title = Name("Счета месяца");

        await SavedAsync(await admin.PostAsJsonAsync(Views, Body(title)));

        // Регистр имени не различает — человек в списке его тоже не различит.
        var twice = await admin.PostAsJsonAsync(Views, Body(title.ToUpperInvariant()));
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Contains("уже есть", await ErrorAsync(twice));

        // Общее с тем же именем — другая группа; своё у другого человека — тоже.
        await SavedAsync(await admin.PostAsJsonAsync(Views, Body(title, shared: true)));
        await SavedAsync(await reader.PostAsJsonAsync(Views, Body(title)));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(Views, Body(title, shared: true))).StatusCode);

        // Переименование в занятое имя — тот же отказ, а в своё собственное — нет.
        var second = await SavedAsync(await admin.PostAsJsonAsync(Views, Body(title + " 2")));
        var (id, version) = (second.GetProperty("id").GetGuid(), second.GetProperty("version").GetString()!);
        Assert.Equal(HttpStatusCode.Conflict,
            (await SendAsync(admin, HttpMethod.Put, $"{Views}/{id}", version, Body(title))).StatusCode);
        await SavedAsync(await SendAsync(admin, HttpMethod.Put, $"{Views}/{id}", version, Body(title + " 2", columns: ["Номер"])));
    }

    [Theory]
    [InlineData("""{"title":"","shared":false,"state":{}}""", "нужно название")]
    [InlineData("""{"title":"Т","shared":false,"state":{"columns":[]}}""", "ни одной колонки")]
    [InlineData("""{"title":"Т","shared":false,"state":{"columns":["Номер"],"totals":[{"column":"Дата","aggregate":"count"}]}}""", "в представлении нет")]
    [InlineData("""{"title":"Т","shared":false,"state":{"totals":[{"column":"Номер","aggregate":"median"}]}}""", "не считается")]
    [InlineData("""{"title":"Т","shared":false,"state":{"columns":["Номер"],"pinned":2}}""", "закреплено")]
    [InlineData("""{"title":"Т","shared":false,"state":{"filter":"{не дерево"}}""", "отбор не разобран")]
    [InlineData("""{"title":"Т","shared":false,"state":{},"basedOn":"no-such-view"}""", "модуль не объявляет")]
    public async Task Негодное_представление_не_сохраняется_и_называет_причину(string body, string expected)
    {
        var (admin, _) = await SignInAsync("Admin");

        var response = await admin.PostAsync(Views, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await ErrorAsync(response));
    }

    /// <summary>
    /// Представления открываются тем же ключом, что таблица: у кого модуль закрыт, тот не читает и не
    /// заводит их настроек — иначе названия и отборы общих представлений выдавали бы содержимое таблицы.
    /// </summary>
    [Fact]
    public async Task Без_ключа_таблицы_представлений_нет()
    {
        var (installer, _) = await SignInAsync("Installer");

        Assert.Equal(HttpStatusCode.Forbidden, (await installer.GetAsync(Views)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await installer.PostAsJsonAsync(Views, Body(Name("Чужая таблица")))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await installer.GetAsync("/api/tables/no.such/views")).StatusCode);
    }

    /// <summary>
    /// Удалили учётную запись: личные представления уходят с ней, общие остаются настройкой компании —
    /// без автора. Внешний ключ один на оба вида, поэтому личные убирает адрес удаления; забудь он это —
    /// в базе остались бы строки, которых не видит никто.
    /// </summary>
    [Fact]
    public async Task Удаление_автора_уносит_личные_и_оставляет_общие_без_автора()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (leaving, leavingId) = await SignInAsync("Admin");
        var (own, common) = (Name("Личное ушедшего"), Name("Общее ушедшего"));
        await SavedAsync(await leaving.PostAsJsonAsync(Views, Body(own)));
        var shared = await SavedAsync(await leaving.PostAsJsonAsync(Views, Body(common, shared: true)));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/users/{leavingId}")).StatusCode);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.SavedTableViews.AnyAsync(v => v.Title == own));
            Assert.False(await db.SavedTableViews.AnyAsync(v => !v.Shared && v.OwnerId == null));
        }

        var left = (await ListAsync(admin)).Single(v => v.GetProperty("id").GetGuid() == shared.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, left.GetProperty("author").ValueKind);
        Assert.False(left.GetProperty("mine").GetBoolean());

        // И оно по-прежнему правится тем, у кого есть право.
        await SavedAsync(await SendAsync(admin, HttpMethod.Put, $"{Views}/{left.GetProperty("id").GetGuid()}",
            left.GetProperty("version").GetString(), Body(common, shared: true, columns: ["Номер"])));
    }

    /// <summary>Общее меняет экран всем — след остаётся в журнале; личное в журнал не пишется.</summary>
    [Fact]
    public async Task В_журнал_пишется_только_общее()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (own, common) = (Name("Личное"), Name("Общее"));

        var personal = await SavedAsync(await admin.PostAsJsonAsync(Views, Body(own)));
        Assert.Null(await LastAsync(ActivityActions.TableViewShared, personal));

        var shared = await SavedAsync(await admin.PostAsJsonAsync(Views, Body(common, shared: true)));
        var id = shared.GetProperty("id").GetGuid();
        Assert.Contains(common, (await LastAsync(ActivityActions.TableViewShared, shared))!.TargetLabel);

        var changed = await SavedAsync(await SendAsync(admin, HttpMethod.Put, $"{Views}/{id}",
            shared.GetProperty("version").GetString(), Body(common, shared: true, columns: ["Номер"])));
        // В журнале — что изменено, словами: состояния целиком раздували бы журнал, а он едет в копии.
        var record = await LastAsync(ActivityActions.TableViewChanged, shared);
        Assert.Equal(common, record!.Before);
        Assert.Equal($"{common} — изменено: колонки", record.After);

        await SendAsync(admin, HttpMethod.Delete, $"{Views}/{id}", changed.GetProperty("version").GetString());
        Assert.NotNull(await LastAsync(ActivityActions.TableViewDeleted, shared));
    }

    /// <summary>
    /// Отбор общего представления по колонке, закрытой читателю, из списка ему не отдаётся: значения
    /// условия — порог суммы, перечень поставщиков — выдавали бы содержимое закрытой колонки. И тот,
    /// кто отбора не видит, представление не правит: правка заменяет его целиком и стёрла бы условия.
    /// </summary>
    [Fact]
    public async Task Отбор_по_закрытой_колонке_читателю_не_отдаётся_и_правкой_не_затирается()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (narrow, _) = await SignInAsync(await RoleAsync("costs.waybill.read", "core.views.share"));
        var title = Name("Крупные счета");
        var filter = JsonSerializer.Serialize(new { type = "condition", column = InvoiceTable.AmountKey, op = "gt", value = "1000000" });

        var shared = await SavedAsync(await admin.PostAsJsonAsync(Views,
            new { title, shared = true, state = new { filter } }));
        var id = shared.GetProperty("id").GetGuid();

        // Автору, у которого суммы открыты, отбор приходит как есть.
        Assert.Contains("1000000", shared.GetProperty("state").GetProperty("filter").GetString());
        Assert.Equal(JsonValueKind.Null, shared.GetProperty("withheld").ValueKind);

        var seen = (await ListAsync(narrow)).Single(v => v.GetProperty("id").GetGuid() == id);
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("state").GetProperty("filter").ValueKind);
        Assert.Contains("«Сумма»", seen.GetProperty("withheld").GetString());
        Assert.DoesNotContain("1000000", seen.GetRawText());

        // Право на общие представления у него есть — но отбора он не видит, и правка его стёрла бы.
        Assert.True(seen.GetProperty("canEdit").GetBoolean());
        var wiped = await SendAsync(narrow, HttpMethod.Put, $"{Views}/{id}", seen.GetProperty("version").GetString(),
            Body(title, shared: true));
        Assert.Equal(HttpStatusCode.Forbidden, wiped.StatusCode);
        Assert.Contains("Отбор автора скрыт", await ErrorAsync(wiped));

        var still = (await ListAsync(admin)).Single(v => v.GetProperty("id").GetGuid() == id);
        Assert.Contains("1000000", still.GetProperty("state").GetProperty("filter").GetString());
    }

    /// <summary>
    /// Предел группы — правило ВХОДА в неё. Группу сверх предела может принести копия, и её
    /// представления обязаны остаться правимыми: правка ничего в группу не добавляет.
    /// </summary>
    [Fact]
    public async Task Предел_числа_представлений_проверяется_на_входе_а_не_при_правке()
    {
        var (admin, _) = await SignInAsync("Admin");
        var mark = $"Предел {Guid.NewGuid():N}";
        try
        {
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                // На одно больше предела — как принесла бы копия: правимое в счёт группы не входит, и
                // ровно с пределом проверка при правке прошла бы и без правила «только на входе».
                db.SavedTableViews.AddRange(Enumerable.Range(0, BHS.CRG.Api.Modules.Tables.SavedTableViewService.SharedMax + 1)
                    .Select(i => BHS.CRG.Domain.Tables.SavedTableView.Create(Address, null, null, $"{mark} {i}", true, "{}")));
                await db.SaveChangesAsync();
            }

            var over = await admin.PostAsJsonAsync(Views, Body(Name("Сверх предела"), shared: true));
            Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
            Assert.Contains("предел", await ErrorAsync(over));

            // Правка лежащего в полной группе проходит, своё личное заводится: у него свой счёт.
            var one = (await ListAsync(admin)).First(v => v.GetProperty("title").GetString() == $"{mark} 0");
            await SavedAsync(await SendAsync(admin, HttpMethod.Put, $"{Views}/{one.GetProperty("id").GetGuid()}",
                one.GetProperty("version").GetString(), Body($"{mark} 0", shared: true, columns: ["Номер"])));
            await SavedAsync(await admin.PostAsJsonAsync(Views, Body(Name("Своё при полной группе"))));
        }
        finally
        {
            using var scope = host.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().SavedTableViews
                .Where(v => v.Title.StartsWith(mark)).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// Уникальность имени без учёта регистра держит БАЗА, а не только проверка службы: та не атомарна,
    /// и два одновременных сохранения «Счета» и «счета» прошли бы её оба.
    /// </summary>
    [Fact]
    public async Task Имена_различающиеся_регистром_не_пускает_сама_база()
    {
        var title = Name("Счета");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SavedTableViews.AddRange(
            BHS.CRG.Domain.Tables.SavedTableView.Create(Address, null, null, title, true, "{}"),
            BHS.CRG.Domain.Tables.SavedTableView.Create(Address, null, null, title.ToUpperInvariant(), true, "{}"));

        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.Contains("IX_table_views_shared_title", refused.InnerException!.Message);
    }

    private async Task<BHS.CRG.Domain.Activity.ActivityRecord?> LastAsync(ActivityAction action, JsonElement view)
    {
        using var scope = host.Services.CreateScope();
        var last = await scope.ServiceProvider.GetRequiredService<IActivityLog>().LastAsync(action);
        return last?.TargetId == view.GetProperty("id").GetGuid().ToString() ? last : null;
    }

    private static string Name(string title) => $"{title} {Guid.NewGuid():N}"[..Math.Min(title.Length + 9, 100)];

    private static object Body(
        string title, bool shared = false, string[]? columns = null, object[]? sort = null, string? basedOn = null) =>
        new { title, shared, basedOn, state = new { columns, sort } };

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, string? version, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", version);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> SavedAsync(HttpResponseMessage response)
    {
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

    private static async Task<List<JsonElement>> ListAsync(HttpClient client)
    {
        var response = await client.GetAsync(Views);
        await OkAsync(response);
        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()];
    }

    private static async Task<List<string>> TitlesAsync(HttpClient client) =>
        [.. (await ListAsync(client)).Select(v => v.GetProperty("title").GetString()!)];

    private static List<string> Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];
}
