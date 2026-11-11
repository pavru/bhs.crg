using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Режим «Сопоставить» (выбор владельца по макету Р1, вариант В): пачка с итогом по строке,
/// предложение по ФИО от сервера, пометка «уволен, но связан» и «Завести» справочник.
/// </summary>
public partial class EmployeeAccountLinkTests
{
    // ── Пачка ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Пачка_удачные_строки_связаны_отказы_названы_построчно()
    {
        var typeId = await DirectoryAsync();
        var ok = await EmployeeAsync(typeId, $"{_surname} Евгений Евгеньевич");
        var busy = await EmployeeAsync(typeId, $"{_surname} Жанна Жановна");
        var stale = await EmployeeAsync(typeId, $"{_surname} Захар Захарович");
        var holder = await EmployeeAsync(typeId, $"{_surname} Держатель");
        var free1 = await AccountAsync($"{_surname} Е. Е.");
        var taken = await AccountAsync($"{_surname} Занятая");
        var free2 = await AccountAsync($"{_surname} З. З.");
        var admin = await AdminAsync();
        await LinkOkAsync(admin, holder, taken);

        var response = await admin.PostAsJsonAsync("/api/employees/account-links", new
        {
            links = new object[]
            {
                new { employeeId = ok, userId = free1, version = Version(await ReadAsync(admin, ok)) },
                new { employeeId = busy, userId = taken, version = Version(await ReadAsync(admin, busy)) },
                new { employeeId = stale, userId = free2, version = "1" },
            },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(1, body.GetProperty("linked").GetInt32());
        Assert.Equal(3, body.GetProperty("total").GetInt32());
        var outcomes = body.GetProperty("results").EnumerateArray()
            .ToDictionary(r => r.GetProperty("employeeId").GetGuid(), r => r);
        Assert.Equal("linked", outcomes[ok].GetProperty("outcome").GetString());
        Assert.Equal("taken", outcomes[busy].GetProperty("outcome").GetString());
        Assert.Contains($"{_surname} Держатель", outcomes[busy].GetProperty("error").GetString());
        Assert.Equal("stale", outcomes[stale].GetProperty("outcome").GetString());

        // Удачная строка не откатилась из-за соседних и записана в журнал.
        Assert.Equal("linked", (await ReadAsync(admin, ok)).GetProperty("account").GetProperty("state").GetString());
        Assert.Single(await JournalAsync(ok));
        Assert.Equal("none", (await ReadAsync(admin, stale)).GetProperty("account").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Пачка_не_по_форме_отвергается_целиком()
    {
        var typeId = await DirectoryAsync();
        var employee = await EmployeeAsync(typeId, $"{_surname} Игорь Игоревич");
        var account = await AccountAsync($"{_surname} И. И.");
        var admin = await AdminAsync();
        var version = Version(await ReadAsync(admin, employee));

        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/employees/account-links", new { links = Array.Empty<object>() })).StatusCode);
        var tooMany = Enumerable.Range(0, 201).Select(_ => new { employeeId = employee, userId = account, version }).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/employees/account-links", new { links = tooMany })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/employees/account-links",
            new { links = new object[] { new { employeeId = employee, userId = account } } })).StatusCode);

        Assert.Equal("none", (await ReadAsync(admin, employee)).GetProperty("account").GetProperty("state").GetString());
    }

    // ── Кандидаты и предложение ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Кандидаты_несут_имя_логин_и_с_кем_связана_и_ничего_сверх()
    {
        var typeId = await DirectoryAsync();
        var employee = await EmployeeAsync(typeId, $"{_surname} Кира Кирилловна");
        var linked = await AccountAsync($"{_surname} К. К.");
        await AccountAsync($"{_surname} Свободная");
        var admin = await AdminAsync();
        await LinkOkAsync(admin, employee, linked);

        var body = await JsonAsync(await admin.GetAsync($"/api/employees/account-candidates?q={Uri.EscapeDataString(_surname)}"));
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        var busy = items.Single(i => i.GetProperty("userId").GetGuid() == linked);
        Assert.Equal($"{_surname} Кира Кирилловна", busy.GetProperty("linkedTo").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, items.Single(i => i.GetProperty("userId").GetGuid() != linked)
            .GetProperty("linkedTo").ValueKind);
        // Узкий ответ: имя, логин, связь — и только.
        Assert.All(items, i => Assert.Equal(["userId", "name", "login", "linkedTo"],
            i.EnumerateObject().Select(p => p.Name)));
    }

    [Fact]
    public async Task Предложение_по_ФИО_одно_совпадение_два_и_только_занятое()
    {
        var typeId = await DirectoryAsync();
        var single = await EmployeeAsync(typeId, $"{_surname} Лев Львович");
        var twice = await EmployeeAsync(typeId, $"{_surname} Мария Максимовна");
        var onlyTaken = await EmployeeAsync(typeId, $"{_surname} Нина Никитична");
        var holder = await EmployeeAsync(typeId, $"{_surname} Держатель Нины");
        var archived = await EmployeeAsync(typeId, $"{_surname} Олег Остапович");
        await AccountAsync($"{_surname} О. О.");
        await SendAsync(new SetRecordArchiveCommand(archived, true));
        var lev = await AccountAsync($"{_surname} Л. Л.");
        await AccountAsync($"{_surname} М. М.");
        await AccountAsync($"{_surname} Мария Максимовна");
        var nina = await AccountAsync($"{_surname} Нина Никитична");
        var admin = await AdminAsync();
        await LinkOkAsync(admin, holder, nina);

        var rows = (await JsonAsync(await admin.GetAsync("/api/employees/account-matches"))).EnumerateArray()
            .ToDictionary(r => r.GetProperty("employee").GetProperty("id").GetGuid(), r => r);

        Assert.Equal(lev, rows[single].GetProperty("suggested").GetProperty("userId").GetGuid());
        Assert.Equal(JsonValueKind.Null, rows[single].GetProperty("reason").ValueKind);
        Assert.Equal(JsonValueKind.Null, rows[twice].GetProperty("suggested").ValueKind);
        Assert.Equal("ambiguous", rows[twice].GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, rows[onlyTaken].GetProperty("suggested").ValueKind);
        Assert.Equal("no-match", rows[onlyTaken].GetProperty("reason").GetString());
        // Связанный сотрудник в сопоставление не попадает, архивный — тоже: это выбор, кого связывать.
        Assert.False(rows.ContainsKey(holder));
        Assert.False(rows.ContainsKey(archived));
    }

    // ── «Уволен, но связан» ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Уволенный_со_связью_помечен_датой_будущее_увольнение_нет()
    {
        var typeId = await DirectoryAsync();
        // ±3 дня: «сегодня» считается по поясу компании, и сутки разницы с UTC не должны решать тест.
        var past = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3);
        var future = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3);
        var gone = await EmployeeAsync(typeId, $"{_surname} Ушедший", $$""","УволенС":"{{past:yyyy-MM-dd}}" """);
        var leaving = await EmployeeAsync(typeId, $"{_surname} Уходящий", $$""","УволенС":"{{future:yyyy-MM-dd}}" """);
        var admin = await AdminAsync();
        await LinkOkAsync(admin, gone, await AccountAsync($"{_surname} У1"));
        await LinkOkAsync(admin, leaving, await AccountAsync($"{_surname} У2"));

        Assert.Equal(past.ToString("yyyy-MM-dd"),
            (await ReadAsync(admin, gone)).GetProperty("account").GetProperty("dismissedOn").GetString());
        Assert.Equal(JsonValueKind.Null,
            (await ReadAsync(admin, leaving)).GetProperty("account").GetProperty("dismissedOn").ValueKind);
    }

    // ── «Завести» справочник ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Завести_без_Персоны_отказ_с_причиной_с_ней_тип_заведён()
    {
        var admin = await AdminAsync();

        var refused = await admin.PostAsync("/api/employees/directory", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await JsonAsync(refused);
        Assert.Equal("directory-missing", body.GetProperty("code").GetString());
        Assert.Equal("person-missing", body.GetProperty("reason").GetString());

        await PersonAsync();
        var created = await admin.PostAsync("/api/employees/directory", null);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.True((await JsonAsync(created)).GetProperty("created").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/employees")).StatusCode);
    }

    /// <summary>
    /// Существующий тип «Завести» не перепроецирует: иначе адрес снимал бы по HTTP чужую правку схемы
    /// (F2). Правка здесь — подпись системного поля, которую проекция при старте оставляет, а
    /// перепроекция поверх неё всё равно прошла бы — поэтому сравнивается схема целиком.
    /// </summary>
    [Fact]
    public async Task Завести_при_заведённом_справочнике_схему_не_трогает()
    {
        var typeId = await DirectoryAsync();
        string edited;
        using (var scope = fixture.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
            var type = (await repo.GetByIdAsync(typeId))!;
            var schema = System.Text.Json.Nodes.JsonNode.Parse(type.Schema.RootElement.GetRawText())!;
            schema["fields"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(
                """{"key":"Пометка","title":"Пометка","type":"string"}"""));
            edited = schema.ToJsonString();
            type.UpdateSchema(JsonDocument.Parse(edited));
            repo.Update(type);
            await repo.SaveChangesAsync();
        }

        var admin = await AdminAsync();
        var again = await admin.PostAsync("/api/employees/directory", null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.False((await JsonAsync(again)).GetProperty("created").GetBoolean());

        using var check = fixture.Services.CreateScope();
        var stored = (await check.ServiceProvider.GetRequiredService<IRepository<DocumentType>>().GetByIdAsync(typeId))!;
        // По смыслу, а не по тексту: база хранит jsonb и пишет его по-своему.
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(edited),
                System.Text.Json.Nodes.JsonNode.Parse(stored.Schema.RootElement.GetRawText())),
            stored.Schema.RootElement.GetRawText());
    }
}
